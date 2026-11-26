using System.Text.Json;
using System.Text.RegularExpressions;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>
/// Reads a catalog definition to find the properties, argument names, enums and slot types that the Express
/// notation maps positional arguments onto. The properties of a component come in schema order, with the
/// component's own definition before the common mixins it composes.
/// </summary>
internal sealed class CatalogHelper
{
    private static readonly Regex CommonDefinition = new("(?:^|/)common_types\\.json#/(?:\\$defs|definitions)/(\\w+)$", RegexOptions.CultureInvariant);

    private const string StandardCatalogIdDescription =
        "The catalog ID for this component, overriding any surface-level default catalogId.";

    private static readonly string[] EnvelopeNames = ["catalogId", "metadata", "accessibility", "checks"];

    private static readonly Lazy<PlainMap> CommonTypes = new(() => (PlainMap)PlainJson.FromElement(ProtocolSchemas.CommonTypes)!);

    private static readonly Lazy<PlainMap> SystemFunctionSchemas = new(ReadSystemFunctions);

    private static readonly Lazy<HashSet<string>> PreservedTypeReferences = new(() =>
        CommonTypes.Value.Get("$defs") is PlainMap definitions ? [.. definitions.Keys] : []);

    private readonly PlainMap _catalog;
    private readonly Dictionary<string, List<string>> _componentProperties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _componentRequired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _componentCheckable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _customEnvelope = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), List<object?>> _propertyEnums = [];
    private readonly Dictionary<string, List<string>> _functionProperties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _functionRequired = new(StringComparer.Ordinal);

    internal CatalogHelper(string catalogId, JsonElement definition)
    {
        CatalogId = catalogId;
        var root = (PlainMap)PlainJson.FromElement(definition)!;
        _catalog = (PlainMap)InlineLocalReferences(root, root, [])!;
        Components = _catalog.Get("components") as PlainMap ?? new PlainMap();
        Functions = _catalog.Get("functions") as PlainMap ?? new PlainMap();
        foreach (var (name, schema) in Components)
            LoadComponent(name, schema);
        foreach (var (name, schema) in Functions)
        {
            var (properties, required) = FunctionSignature(schema);
            _functionProperties[name] = properties;
            _functionRequired[name] = required;
        }
    }

    internal string CatalogId { get; }

    internal PlainMap Components { get; }

    internal PlainMap Functions { get; }

    internal IReadOnlyList<string> GetComponentProperties(string name) =>
        _componentProperties.TryGetValue(name, out var properties) ? properties : [];

    internal IReadOnlyList<string> GetComponentRequired(string name) =>
        _componentRequired.TryGetValue(name, out var required) ? required : [];

    internal bool IsCheckable(string name) => _componentCheckable.GetValueOrDefault(name);

    /// <summary>The bare envelope names that the component declares as properties of its own.</summary>
    internal ISet<string> GetCustomEnvelope(string name) =>
        _customEnvelope.TryGetValue(name, out var names) ? names : new HashSet<string>();

    internal IReadOnlyList<string> GetFunctionProperties(string name)
    {
        if (_functionProperties.TryGetValue(name, out var properties))
            return properties;
        return SystemFunctionSignature(name).Properties;
    }

    internal IReadOnlyList<string> GetFunctionRequired(string name)
    {
        if (_functionRequired.TryGetValue(name, out var required))
            return required;
        return SystemFunctionSignature(name).Required;
    }

    internal static bool IsSystemFunction(string name) => SystemFunctionSchemas.Value.ContainsKey(name);

    internal static IReadOnlyList<string> SystemFunctionNames => [.. SystemFunctionSchemas.Value.Keys.Order(StringComparer.Ordinal)];

    internal static bool IsActionSlot(object? schema)
    {
        if (schema is not PlainMap map)
            return false;
        if (CommonDefinitionName(map.Get("$ref")) == "Action")
            return true;
        foreach (var key in new[] { "allOf", "oneOf", "anyOf" })
            if (map.Get(key) is List<object?> subs && subs.Any(IsActionSlot))
                return true;
        return false;
    }

    internal PlainMap? GetFunctionPropertySchema(string name, string property)
    {
        var schema = Functions.Get(name) ?? SystemFunctionSchemas.Value.Get(name);
        if (schema is not PlainMap functionSchema)
            return null;
        var subSchemas = new List<object?> { functionSchema };
        if (functionSchema.Get("allOf") is List<object?> all)
            subSchemas.AddRange(all);
        foreach (var sub in subSchemas)
        {
            if (sub is PlainMap { } map && map.Get("properties") is PlainMap properties
                && properties.Get("args") is PlainMap { } arguments
                && arguments.Get("properties") is PlainMap argumentProperties
                && argumentProperties.ContainsKey(property))
                return argumentProperties.Get(property) as PlainMap;
        }

        return null;
    }

    internal List<object?>? GetPropertyEnum(string component, string property) =>
        _propertyEnums.GetValueOrDefault((component, property));

    internal PlainMap? GetPropertySchema(string component, string property)
    {
        if (Components.Get(component) is not PlainMap schema)
            return null;
        if (schema.Get("properties") is PlainMap own && own.ContainsKey(property))
            return own.Get(property) as PlainMap;
        foreach (var sub in OrderedComponentSubSchemas(schema))
            if (sub.Get("properties") is PlainMap properties && properties.ContainsKey(property))
                return properties.Get(property) as PlainMap;
        return null;
    }

    /// <summary>Returns <c>ChildList</c>, <c>Child</c> or <c>Action</c> when the property's schema names that type.</summary>
    internal string? GetPropertyType(string component, string property)
    {
        var schema = GetPropertySchema(component, property);
        return schema is null ? null : ReferenceType(schema);
    }

    internal static string? ReferenceType(object? schema)
    {
        if (schema is not PlainMap map)
            return null;
        if (map.Get("$ref") is string reference)
        {
            if (reference.Contains("ChildList", StringComparison.Ordinal))
                return "ChildList";
            if (reference.Contains("Child", StringComparison.Ordinal) || reference.Contains("ComponentId", StringComparison.Ordinal))
                return "Child";
            if (reference.Contains("Action", StringComparison.Ordinal))
                return "Action";
        }

        foreach (var key in new[] { "oneOf", "anyOf", "allOf" })
        {
            if (map.Get(key) is not List<object?> subs)
                continue;
            foreach (var sub in subs)
                if (ReferenceType(sub) is { } found)
                    return found;
        }

        return null;
    }

    /// <summary>Checks whether a property schema accepts a number, resolving references.</summary>
    internal bool AcceptsNumber(object? schema)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Check(schema, false);

        bool Check(object? candidate, bool inCommonTypes)
        {
            if (candidate is not PlainMap map || !visited.Add(map))
                return false;
            var type = map.Get("type");
            if (type is "number" or "integer")
                return true;
            if (type is List<object?> types && types.Any(t => t is "number" or "integer"))
                return true;
            if (PlainJson.IsNumber(map.Get("const")))
                return true;
            if (map.Get("enum") is List<object?> values && values.Any(PlainJson.IsNumber))
                return true;

            if (map.Get("$ref") is string reference)
            {
                var common = CommonDefinition.Match(reference);
                if (common.Success)
                {
                    var resolved = LookupCommonDefinition(common.Groups[1].Value);
                    if (resolved is null || Check(resolved, true))
                        return true;
                }
                else if (inCommonTypes && (reference.StartsWith("#/$defs/", StringComparison.Ordinal) || reference.StartsWith("#/definitions/", StringComparison.Ordinal)))
                {
                    var name = reference.StartsWith("#/$defs/", StringComparison.Ordinal) ? reference[8..] : reference[14..];
                    var resolved = LookupCommonDefinition(name);
                    if (resolved is null || Check(resolved, true))
                        return true;
                }
                else if (reference.StartsWith("#/", StringComparison.Ordinal))
                {
                    var resolved = ResolvePointer(_catalog, reference);
                    if (resolved is null || Check(resolved, false))
                        return true;
                }
                else if (reference.StartsWith("catalog.json#/", StringComparison.Ordinal))
                {
                    var resolved = ResolvePointer(_catalog, reference[12..]);
                    if (resolved is null || Check(resolved, false))
                        return true;
                }
                else
                {
                    return true;
                }
            }

            foreach (var key in new[] { "allOf", "oneOf", "anyOf" })
                if (map.Get(key) is List<object?> subs && subs.Any(sub => Check(sub, inCommonTypes)))
                    return true;

            string[] typeKeys = ["type", "const", "enum", "$ref", "allOf", "oneOf", "anyOf", "properties", "items", "prefixItems", "not"];
            return !typeKeys.Any(map.ContainsKey);
        }
    }

    internal PlainMap? ResolveItemSchema(object? schema) => ResolveAt(schema, null);

    internal PlainMap? ResolvePropertySchema(object? schema, string name) => ResolveAt(schema, name);

    private PlainMap? ResolveAt(object? schema, string? property)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Resolve(schema, false);

        PlainMap? Resolve(object? candidate, bool inCommonTypes)
        {
            if (candidate is not PlainMap map || !visited.Add(map))
                return null;
            if (property is null)
            {
                if (map.Get("items") is PlainMap items)
                    return items;
            }
            else
            {
                if (map.Get("properties") is PlainMap properties && properties.Get(property) is PlainMap declared)
                    return declared;
                if (map.Get("additionalProperties") is PlainMap additional)
                    return additional;
            }

            if (map.Get("$ref") is string reference)
            {
                PlainMap? found;
                var commonName = CommonDefinitionName(reference);
                if (commonName is not null)
                {
                    found = Resolve(LookupCommonDefinition(commonName), true);
                }
                else if (inCommonTypes && (reference.StartsWith("#/$defs/", StringComparison.Ordinal) || reference.StartsWith("#/definitions/", StringComparison.Ordinal)))
                {
                    var name = reference.StartsWith("#/$defs/", StringComparison.Ordinal) ? reference[8..] : reference[14..];
                    found = Resolve(LookupCommonDefinition(name), true);
                }
                else if (reference.StartsWith("#/", StringComparison.Ordinal))
                {
                    found = Resolve(ResolvePointer(_catalog, reference), false);
                }
                else
                {
                    found = null;
                }

                if (found is { Count: > 0 })
                    return found;
            }

            foreach (var combinator in new[] { "allOf", "oneOf", "anyOf" })
            {
                if (map.Get(combinator) is not List<object?> subs)
                    continue;
                foreach (var sub in subs)
                    if (Resolve(sub, inCommonTypes) is { Count: > 0 } found)
                        return found;
            }

            return null;
        }
    }

    private static object? LookupCommonDefinition(string name)
    {
        var definitions = CommonTypes.Value.Get("$defs") ?? CommonTypes.Value.Get("definitions");
        return (definitions as PlainMap)?.Get(name);
    }

    private static object? ResolvePointer(PlainMap root, string pointer)
    {
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
            return null;
        object? current = root;
        foreach (var part in pointer[2..].Split('/'))
        {
            if (current is not PlainMap map)
                return null;
            current = map.Get(part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }

        return current;
    }

    private static string? CommonDefinitionName(object? reference)
    {
        if (reference is not string text)
            return null;
        var match = CommonDefinition.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static PlainMap ReadSystemFunctions()
    {
        var found = new PlainMap();
        var definitions = CommonTypes.Value.Get("$defs") ?? CommonTypes.Value.Get("definitions");
        if (definitions is not PlainMap map)
            return found;
        foreach (var (_, schema) in map)
        {
            if (schema is PlainMap { } entry && entry.Get("properties") is PlainMap properties
                && properties.Get("@call") is PlainMap call && call.Get("const") is string name
                && name.StartsWith('@'))
                found[name] = entry;
        }

        return found;
    }

    private static (List<string> Properties, List<string> Required) SystemFunctionSignature(string name) =>
        SystemFunctionSchemas.Value.Get(name) is { } schema ? FunctionSignature(schema) : ([], []);

    private static (List<string> Properties, List<string> Required) FunctionSignature(object? schema)
    {
        var subSchemas = new List<object?> { schema };
        if (schema is PlainMap map && map.Get("allOf") is List<object?> all)
            subSchemas.AddRange(all);
        var properties = new PlainMap();
        var required = new List<string>();
        foreach (var sub in subSchemas)
        {
            if (sub is not PlainMap subMap || subMap.Get("properties") is not PlainMap subProperties)
                continue;
            if (subProperties.Get("args") is not PlainMap arguments)
                continue;
            if (arguments.Get("properties") is PlainMap argumentProperties)
                foreach (var pair in argumentProperties)
                    properties[pair.Key] = pair.Value;
            if (arguments.Get("required") is List<object?> names)
                required.AddRange(names.OfType<string>());
        }

        return ([.. properties.Keys], required);
    }

    private static bool IsStandardChecks(object? schema)
    {
        if (schema is not PlainMap map)
            return false;
        if (map.Get("$ref") is string reference && reference.EndsWith("/Checkable", StringComparison.Ordinal))
            return true;
        return map.Get("items") is PlainMap items && items.Get("$ref") is string itemReference
            && itemReference.EndsWith("/CheckRule", StringComparison.Ordinal);
    }

    private static List<PlainMap> OrderedComponentSubSchemas(object? schema)
    {
        if (schema is not PlainMap)
            return [];
        var specific = new List<PlainMap>();
        var common = new List<PlainMap>();
        Visit(schema, true);
        specific.AddRange(common);
        return specific;

        void Visit(object? node, bool isRoot)
        {
            if (node is not PlainMap map)
                return;
            var properties = map.Get("properties") as PlainMap;
            if (properties is not null && properties.ContainsKey("component"))
                specific.Add(map);
            else if (!isRoot || properties is not null || map.ContainsKey("required") || map.ContainsKey("$ref"))
                common.Add(map);
            if (map.Get("allOf") is List<object?> all)
                foreach (var sub in all)
                    Visit(sub, false);
        }
    }

    private void LoadComponent(string name, object? schema)
    {
        var properties = new PlainMap();
        var required = new List<string>();
        var checkable = false;
        foreach (var sub in OrderedComponentSubSchemas(schema))
        {
            if (sub.Get("$ref") is string reference && reference.Contains("Checkable", StringComparison.Ordinal))
                checkable = true;
            if (sub.Get("properties") is PlainMap subProperties)
                foreach (var pair in subProperties)
                    if (!properties.ContainsKey(pair.Key))
                        properties[pair.Key] = pair.Value;
            if (sub.Get("required") is List<object?> names)
                foreach (var item in names.OfType<string>())
                    if (!required.Contains(item))
                        required.Add(item);
        }

        if (properties.ContainsKey("@checks") || IsStandardChecks(properties.Get("checks")))
            checkable = true;

        var custom = new HashSet<string>(EnvelopeNames.Where(properties.ContainsKey), StringComparer.Ordinal);
        if (custom.Contains("checks") && IsStandardChecks(properties.Get("checks")))
            custom.Remove("checks");
        if (custom.Contains("catalogId") && properties.Get("catalogId") is PlainMap catalogId
            && catalogId.Get("description") is string description && description == StandardCatalogIdDescription)
            custom.Remove("catalogId");
        if (properties.Get("metadata") is PlainMap metadata && metadata.Get("properties") is PlainMap metadataProperties
            && metadataProperties.Count == 1 && metadataProperties.ContainsKey("extensions")
            && metadata.Get("additionalProperties") is false)
            custom.Remove("metadata");
        if (properties.Get("accessibility") is PlainMap accessibility)
        {
            var standardReference = accessibility.Get("$ref") is string accessibilityReference
                && accessibilityReference.EndsWith("/AccessibilityAttributes", StringComparison.Ordinal);
            var standardShape = accessibility.Get("properties") is PlainMap accessibilityProperties
                && accessibilityProperties.ContainsKey("label") && accessibilityProperties.ContainsKey("description")
                && accessibility.Get("additionalProperties") is false;
            if (standardReference || standardShape)
                custom.Remove("accessibility");
        }

        var excluded = new HashSet<string>(["component", "id", "@catalogId", "@metadata", "@accessibility", "@checks"], StringComparer.Ordinal);
        foreach (var bare in new[] { "catalogId", "metadata", "accessibility" })
            if (!custom.Contains(bare))
                excluded.Add(bare);
        if (checkable && !custom.Contains("checks"))
            excluded.Add("checks");
        var ordered = properties.Keys.Where(key => !excluded.Contains(key)).ToList();

        foreach (var key in ordered)
        {
            var values = FindEnum(GetPropertySchema(name, key));
            if (values is { Count: > 0 })
                _propertyEnums[(name, key)] = values;
        }

        if (checkable && !custom.Contains("checks"))
            ordered.Add("checks");

        _componentProperties[name] = ordered;
        _componentRequired[name] = required;
        _componentCheckable[name] = checkable;
        _customEnvelope[name] = custom;
    }

    private static List<object?>? FindEnum(object? schema)
    {
        if (schema is not PlainMap map)
            return null;
        if (map.Get("enum") is List<object?> values)
            return values;
        foreach (var key in new[] { "oneOf", "anyOf", "allOf" })
        {
            if (map.Get(key) is not List<object?> subs)
                continue;
            foreach (var sub in subs)
                if (FindEnum(sub) is { Count: > 0 } found)
                    return found;
        }

        return null;
    }

    /// <summary>
    /// Writes the local references of a schema inline. References to common types stay, and so does a
    /// reference that a definition makes to itself.
    /// </summary>
    private static object? InlineLocalReferences(object? node, PlainMap root, HashSet<string> visited)
    {
        if (node is PlainMap map)
        {
            if (map.Get("$ref") is string reference && reference.StartsWith("#/", StringComparison.Ordinal))
            {
                var name = reference[(reference.LastIndexOf('/') + 1)..];
                if (reference.StartsWith("#/$defs/", StringComparison.Ordinal) && PreservedTypeReferences.Value.Contains(name))
                    return map;
                if (visited.Contains(reference))
                    return map;

                var nextVisited = new HashSet<string>(visited, StringComparer.Ordinal) { reference };
                var resolved = ResolvePointer(root, reference);
                if (resolved is not null)
                {
                    resolved = InlineLocalReferences(resolved, root, nextVisited);
                    var merged = new PlainMap();
                    foreach (var pair in map)
                        if (pair.Key != "$ref")
                            merged[pair.Key] = InlineLocalReferences(pair.Value, root, visited);
                    if (resolved is not PlainMap resolvedMap)
                        return resolved;

                    var result = new PlainMap();
                    foreach (var pair in resolvedMap)
                        result[pair.Key] = pair.Value;
                    foreach (var pair in merged)
                    {
                        if (result.Get(pair.Key) is PlainMap existingMap && pair.Value is PlainMap valueMap)
                        {
                            var combined = new PlainMap();
                            foreach (var inner in existingMap)
                                combined[inner.Key] = inner.Value;
                            foreach (var inner in valueMap)
                                combined[inner.Key] = inner.Value;
                            result[pair.Key] = combined;
                        }
                        else if (result.Get(pair.Key) is List<object?> existingList && pair.Value is List<object?> valueList)
                        {
                            var combined = new List<object?>(existingList);
                            combined.AddRange(valueList.Where(item => !existingList.Any(other => PlainJson.DeepEquals(item, other))));
                            result[pair.Key] = combined;
                        }
                        else
                        {
                            result[pair.Key] = pair.Value;
                        }
                    }

                    return result;
                }
            }

            var copy = new PlainMap();
            foreach (var pair in map)
                copy[pair.Key] = InlineLocalReferences(pair.Value, root, visited);
            return copy;
        }

        if (node is List<object?> list)
            return list.Select(item => InlineLocalReferences(item, root, visited)).ToList();
        return node;
    }
}
