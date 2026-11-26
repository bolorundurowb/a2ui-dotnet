using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

namespace A2UI.Agent;

public sealed partial class MacroExpander
{
    private static readonly string[] EnvelopeMixins = ["catalogId", "accessibility", "metadata", "checks"];

    // The state of one call to Expand.
    private sealed class Expansion
    {
        private readonly MacroExpander _owner;
        private readonly Dictionary<string, SurfaceInfo> _surfaces = new(StringComparer.Ordinal);
        private int _produced;

        internal Expansion(MacroExpander owner, IReadOnlyDictionary<string, string?>? surfaceCatalogs)
        {
            _owner = owner;
            if (surfaceCatalogs is null)
                return;
            foreach (var surface in surfaceCatalogs)
                Track(surface.Key, surface.Value);
        }

        internal WireMessage Process(WireMessage message)
        {
            if (message.Direction != WireDirection.AgentToRenderer ||
                message.Kind is not ("createSurface" or "updateComponents" or "deleteSurface"))
                return message;
            var envelope = (JsonObject)JsonNode.Parse(message.Envelope.GetRawText())!;
            var payload = (JsonObject)envelope[message.Kind]!;
            var surfaceId = payload["surfaceId"]!.GetValue<string>();
            var changed = false;
            if (message.Kind == "deleteSurface")
            {
                _surfaces.Remove(surfaceId);
                return message;
            }

            if (message.Kind == "createSurface")
                changed = Create(surfaceId, payload);
            if (payload["components"] is JsonArray components)
            {
                var surface = _surfaces.TryGetValue(surfaceId, out var known) ? known : SurfaceInfo.Untracked;
                var expanded = ExpandComponents(components, surface, $"/{message.Kind}/components");
                if (expanded is not null)
                {
                    payload["components"] = expanded;
                    changed = true;
                }
            }

            if (!changed)
                return message;
            return WireMessage.TryDecode(Encoding.UTF8.GetBytes(envelope.ToJsonString()), WireDirection.AgentToRenderer,
                out var result, out var problem)
                ? result!
                : throw MacroFailure.Fatal("ValidationError", problem!.Path, $"Expanded message is invalid: {problem.Message}");
        }

        private bool Create(string surfaceId, JsonObject payload)
        {
            var declared = payload["catalogId"] is JsonValue value && value.TryGetValue<string>(out var named) ? named : null;
            var effective = Track(surfaceId, declared);
            if (declared != DefaultCatalogId)
                return false;
            if (effective is null)
                payload.Remove("catalogId");
            else
                payload["catalogId"] = effective;
            return true;
        }

        // Records the surface default. The default macro catalog names no renderer catalog, so a lone base catalog stands in for it.
        private string? Track(string surfaceId, string? declared)
        {
            var effective = declared;
            if (declared == DefaultCatalogId)
            {
                var registered = _owner._catalogs.CatalogIds;
                if (registered.Count == 0)
                    throw MacroFailure.Fatal("CatalogError", "/createSurface/catalogId",
                        "No base catalog is registered for a surface that uses the default macro catalog.");
                effective = registered.Count == 1 ? registered.Single() : null;
            }

            _surfaces[surfaceId] = SurfaceInfo.Tracked(declared, effective);
            return effective;
        }

        private JsonArray? ExpandComponents(JsonArray components, SurfaceInfo surface, string path)
        {
            var output = new List<JsonNode?>(components.Count);
            var changed = false;
            for (var index = 0; index < components.Count; index++)
            {
                var location = $"{path}/{index}";
                if (components[index] is not JsonObject component || !TryResolveCall(component, surface, out var macro))
                {
                    output.Add(components[index]?.DeepClone());
                    continue;
                }

                var produced = new List<JsonObject>();
                try
                {
                    var id = component["id"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
                        ? text : throw MacroFailure.Argument(location + "/id", $"Macro {macro!.Name} needs an id.");
                    ExpandCall(macro!, component, id, surface, 0, produced, location);
                    output.AddRange(produced);
                    changed = true;
                }
                catch (MacroFailure failure) when (failure.Recoverable && _owner._options.ErrorMode == MacroErrorMode.Passthrough)
                {
                    output.Add(component.DeepClone());
                }
            }

            return changed ? new JsonArray(output.ToArray()) : null;
        }

        // A call selects a macro by name and by the catalog of the component. A non-string or empty catalog never selects one.
        private bool TryResolveCall(JsonObject component, SurfaceInfo surface, out MacroDefinition? macro)
        {
            macro = null;
            if (component["component"] is not JsonValue value || !value.TryGetValue<string>(out var name))
                return false;
            string? catalog = null;
            if (component["@catalogId"] is { } declared)
            {
                if (declared is not JsonValue text || !text.TryGetValue<string>(out catalog) || catalog.Length == 0)
                    return false;
            }

            macro = _owner.Resolve(name, catalog, null, surface);
            return macro is not null;
        }

        private JsonObject ExpandCall(MacroDefinition macro, JsonObject call, string id, SurfaceInfo surface,
            int depth, List<JsonObject> output, string path)
        {
            var tree = Evaluate(macro, call, id, depth, path);
            var root = Lower(tree, new Scope(new(id), macro, surface, depth, output, path), isRoot: true);
            MergeEnvelope(root, call);
            return root;
        }

        private JsonObject Evaluate(MacroDefinition macro, JsonObject call, string id, int depth, string path)
        {
            if (depth > _owner._options.MaxDepth)
                throw MacroFailure.Fatal("RecursionError", path,
                    $"Macro expansion is deeper than {_owner._options.MaxDepth}. Check for a macro that expands to itself.");
            var arguments = MacroCoercion.Coerce(macro, call, path);
            var result = macro.Body(new MacroInvocation(macro.Name, id, arguments));
            if (result.Root is null)
                throw MacroFailure.Argument(path, result.Error!);
            return (JsonObject)result.Root.DeepClone();
        }

        private JsonObject Lower(JsonObject node, Scope scope, bool isRoot)
        {
            var name = node["component"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
                ? text : throw MacroFailure.Argument(scope.Path, "Macro template node needs a component name.");
            var selector = node["@catalogId"] ?? node["catalogId"];
            string? explicitCatalog = null;
            if (selector is not null)
                explicitCatalog = selector is JsonValue named && named.TryGetValue<string>(out var catalog) && catalog.Length > 0
                    ? catalog : throw MacroFailure.Argument(scope.Path, "The catalog ID of a template node must be a non-empty string.");

            var macro = _owner.Resolve(name, explicitCatalog, scope.Enclosing, scope.Surface);
            return macro is null
                ? LowerPrimitive(node, name, explicitCatalog, scope, isRoot)
                : LowerMacro(node, macro, explicitCatalog is null, scope, isRoot);
        }

        // A macro of the default catalog that a template names without a catalog expands in place and shares the IDs of its caller.
        // Any other macro keeps its own namespace, because its catalog is resolved separately.
        private JsonObject LowerMacro(JsonObject node, MacroDefinition macro, bool unqualified, Scope scope, bool isRoot)
        {
            var call = (JsonObject)node.DeepClone();
            call.Remove("catalogId");
            call.Remove("@catalogId");
            NormaliseEnvelope(call, key => macro.Parameters.Any(parameter => parameter.Name == key));
            foreach (var parameter in macro.Parameters.Where(parameter => MacroCoercion.IsSlot(parameter.Type)))
                if (call[parameter.Name] is { } slot)
                    call[parameter.Name] = LowerChildren(slot, scope);

            if (unqualified && macro.CatalogId is null)
            {
                var tree = Evaluate(macro, call, scope.Namespace.RootId, scope.Depth + 1, scope.Path);
                var inner = scope with { Enclosing = macro, Depth = scope.Depth + 1 };
                var root = Lower(tree, inner, isRoot);
                MergeEnvelope(root, call);
                return root;
            }

            var id = isRoot ? scope.Namespace.RootId : scope.Namespace.Allocate(macro.Name);
            return ExpandCall(macro, call, id, scope.Surface, scope.Depth + 1, scope.Output, scope.Path);
        }

        private JsonObject LowerPrimitive(JsonObject node, string name, string? explicitCatalog, Scope scope, bool isRoot)
        {
            if (name.Normalize(NormalizationForm.FormKC) == "Surface")
                throw MacroFailure.Fatal("ValidationError", scope.Path, "A macro must not emit the reserved Surface component.");
            var owner = explicitCatalog ?? scope.Surface.Effective;
            // The ID is allocated last, so that the components a node refers to are numbered first.
            var component = new JsonObject { ["component"] = name, ["id"] = null };
            foreach (var member in node)
            {
                if (member.Key is "component" or "id")
                    continue;
                var key = member.Key;
                if (Array.IndexOf(EnvelopeMixins, key) >= 0 && !_owner.Declares(owner, name, key))
                    key = "@" + key;
                component[key] = LowerChildren(member.Value, scope);
            }

            component["id"] = node["id"] is JsonValue declaredId && declaredId.TryGetValue<string>(out var local)
                ? $"{scope.Namespace.RootId}__{local}"
                : isRoot ? scope.Namespace.RootId : scope.Namespace.Allocate(name);

            if (!component.ContainsKey("@catalogId") && Stamp(name, scope) is { } stamped)
                component["@catalogId"] = stamped;
            _owner.ApplyDefaults(component, component["@catalogId"]?.GetValue<string>() ?? scope.Surface.Effective, name);
            if (++_produced > _owner._options.MaxComponents)
                throw MacroFailure.Fatal("ExpansionLimitError", scope.Path,
                    $"Macro expansion produced more than {_owner._options.MaxComponents} components.");
            scope.Output.Add(component);
            return component;
        }

        // Names the catalog of a component that the macro leaves unqualified, where the surface does not already imply it.
        private string? Stamp(string name, Scope scope)
        {
            var surface = scope.Surface;
            if (surface.Kind == SurfaceKind.Untracked || surface.IsForeign(_owner) ||
                surface.Kind == SurfaceKind.Tracked && _owner.Defines(surface.Effective!, name))
                return null;
            var owners = _owner._catalogs.CatalogIds.Where(id => _owner.Defines(id, name)).Take(2).ToArray();
            return owners.Length == 1
                ? owners[0]
                : throw MacroFailure.Fatal("CatalogError", scope.Path, owners.Length == 0
                    ? $"No registered catalog defines {name}."
                    : $"More than one registered catalog defines {name}. Name its catalog.");
        }

        // A nested node becomes its ID, so that a slot holds references and the node is emitted as a separate component.
        private JsonNode? LowerChildren(JsonNode? value, Scope scope)
        {
            switch (value)
            {
                case JsonObject child when child["component"] is JsonValue:
                    return Lower(child, scope, isRoot: false)["id"]!.DeepClone();
                case JsonArray items:
                    var list = new JsonArray();
                    foreach (var item in items)
                        list.Add(item is JsonObject nested && nested["component"] is JsonValue
                            ? Lower(nested, scope, isRoot: false)["id"]!.DeepClone()
                            : item?.DeepClone());
                    return list;
                default:
                    return value?.DeepClone();
            }
        }

        private static void NormaliseEnvelope(JsonObject node, Func<string, bool> declared)
        {
            foreach (var key in EnvelopeMixins)
                if (node.ContainsKey(key) && !declared(key) && !node.ContainsKey("@" + key) &&
                    node.Remove(key, out var moved))
                    node["@" + key] = moved;
        }

        // The call's own envelope members layer over the root that the macro produced. A call never moves its catalog onto the root.
        private static void MergeEnvelope(JsonObject root, JsonObject call)
        {
            foreach (var key in new[] { "@accessibility", "@metadata", "@checks" })
            {
                if (call[key] is not { } incoming)
                    continue;
                if (root[key] is JsonObject existing && incoming is JsonObject overlay)
                    DeepMerge(existing, overlay);
                else if (root[key] is JsonArray rules && incoming is JsonArray more)
                    foreach (var rule in more)
                        rules.Add(rule?.DeepClone());
                else
                    root[key] = incoming.DeepClone();
            }
        }

        private static void DeepMerge(JsonObject target, JsonObject overlay)
        {
            foreach (var member in overlay)
            {
                if (target[member.Key] is JsonObject existing && member.Value is JsonObject nested)
                    DeepMerge(existing, nested);
                else
                    target[member.Key] = member.Value?.DeepClone();
            }
        }
    }

    // The place in an expansion that a template node is lowered.
    private sealed record Scope(IdNamespace Namespace, MacroDefinition? Enclosing, SurfaceInfo Surface, int Depth,
        List<JsonObject> Output, string Path);

    // Allocates the IDs of the components that one macro call produces.
    private sealed class IdNamespace(string rootId)
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        internal string RootId { get; } = rootId;

        internal string Allocate(string component)
        {
            var key = component.ToLowerInvariant();
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
            return $"{RootId}__{key}_{_counts[key]}";
        }
    }
}
