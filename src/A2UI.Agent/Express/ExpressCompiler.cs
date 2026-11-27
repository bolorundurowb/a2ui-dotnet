using System.Globalization;
using System.Text.Json;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>
/// Compiles Express text into A2UI 1.0 messages. Positional arguments are mapped onto the properties
/// that the catalog declares, variable references are flattened into the adjacency list of components, and
/// each component and function call is resolved against the catalog that defines it.
/// </summary>
internal sealed class ExpressCompiler
{
    private const string SurfaceStatement = "surface";
    private const string DeleteSurfaceStatement = "deleteSurface";
    private const string IdKey = "id";
    private const string FunctionCallIdKey = "functionCallId";
    private const string SendDataModelKey = "sendDataModel";
    private const string UpdateKey = "update";
    private const string ExtensionsKey = "extensions";
    private const int MaxArrayIndex = 10_000;

    private static readonly string[] EventKeywords = ["userMessage", "context"];

    private readonly List<CatalogHelper> _helperList = [];
    private readonly Dictionary<string, CatalogHelper> _helpers = new(StringComparer.Ordinal);

    internal ExpressCompiler(IEnumerable<(string Id, JsonElement Definition)> catalogs)
    {
        foreach (var (id, definition) in catalogs)
        {
            var helper = new CatalogHelper(id, definition);
            _helperList.Add(helper);
            _helpers[id] = helper;
        }
    }

    private bool IsMultiCatalog => _helperList.Count > 1;

    /// <summary>Compiles a block of Express text into messages, in the order of the statements that produce them.</summary>
    internal List<PlainMap> Compile(string text, string surfaceId)
    {
        var context = new Context();
        var statements = ExpressSyntax.Parse(text);
        var units = BuildUnits(statements, surfaceId);
        if (units.Count == 0)
            throw UndefinedRoot();

        var messages = new List<PlainMap>();
        foreach (var unit in units)
        {
            switch (unit)
            {
                case DeleteUnit delete:
                    messages.Add(Message("deleteSurface", new PlainMap { ["surfaceId"] = delete.SurfaceId }));
                    break;
                case StandaloneUnit standalone:
                    messages.Add(CompileStandaloneCall(standalone, context));
                    break;
                case SurfaceScope scope:
                    messages.AddRange(CompileScope(scope, context));
                    break;
            }
        }

        return messages;
    }

    private static PlainMap Message(string operation, PlainMap body) =>
        new() { ["version"] = WireMessage.Version, [operation] = body };

    private static ExpressException Invalid(string message) => ExpressException.Validation(message);

    private static ExpressException UndefinedRoot() => ExpressException.Parse("Root target 'root' is not defined.");

    private sealed class Context
    {
        internal List<PlainMap> ExtraComponents { get; set; } = [];

        internal int InlineCounter { get; set; }

        internal int CallCounter { get; set; }

        internal PlainMap? ActiveValuePath { get; set; }

        internal HashSet<string> Resolving { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> UsedIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed class SurfaceScope(string surfaceId)
    {
        internal string SurfaceId { get; } = surfaceId;

        internal bool? SendDataModel { get; init; }

        internal bool IsUpdate { get; init; }

        internal object? Extensions { get; init; }

        internal PlainMap RawSymbols { get; } = new();

        internal PlainMap DataPathAssignments { get; } = new();
    }

    private sealed record DeleteUnit(string SurfaceId);

    private sealed record StandaloneUnit(CallNode Node, SurfaceScope? Scope);

    private sealed record ValueSlot(PlainMap Schema, CatalogHelper Helper);

    private static ValueSlot? ItemSlot(ValueSlot? slot)
    {
        if (slot is null)
            return null;
        var schema = slot.Helper.ResolveItemSchema(slot.Schema);
        return schema is { Count: > 0 } ? new ValueSlot(schema, slot.Helper) : null;
    }

    private static ValueSlot? PropertySlot(ValueSlot? slot, string name)
    {
        if (slot is null)
            return null;
        var schema = slot.Helper.ResolvePropertySchema(slot.Schema, name);
        return schema is { Count: > 0 } ? new ValueSlot(schema, slot.Helper) : null;
    }

    private static string ComponentId(string variableName, CallNode? node)
    {
        if (node is null || !node.Kwargs.TryGetValue(IdKey, out var explicitId))
            return variableName;
        if (explicitId is not string id || id.Length == 0)
            throw Invalid($"Component id must be a non-empty string, got: {Describe(explicitId)}");
        if (!UnicodeIdentifiers.IsIdentifier(id))
            throw Invalid($"Component id '{id}' is not a valid UAX #31 identifier.");
        return id;
    }

    private static string StripPathPrefix(string path)
    {
        if (path.StartsWith("$/", StringComparison.Ordinal))
            return path[2..];
        return path.StartsWith('$') ? path[1..] : path;
    }

    private static string ToJsonPointer(string path) => "/" + StripPathPrefix(path);

    private static bool IsCanonicalIndex(string segment)
    {
        if (segment.Length == 0 || !segment.All(char.IsAsciiDigit))
            return false;
        return segment == "0" || segment[0] != '0';
    }

    private static int ParseArrayIndex(string segment, string path)
    {
        if (!IsCanonicalIndex(segment))
            throw Invalid($"Cannot use non-numeric segment '{segment}' on an array in path '{path}'.");
        if (segment.Length > 5 || int.Parse(segment, CultureInfo.InvariantCulture) > MaxArrayIndex)
            throw Invalid($"Cannot set path '{path}': array index '{segment}' exceeds maximum supported index ({MaxArrayIndex}).");
        return int.Parse(segment, CultureInfo.InvariantCulture);
    }

    private static object StepContainer(object current, string segment, string nextSegment, string path)
    {
        var nextIsIndex = IsCanonicalIndex(nextSegment);
        if (current is PlainMap map)
        {
            var existing = map.Get(segment);
            if (existing is null)
            {
                object child = nextIsIndex ? new List<object?>() : new PlainMap();
                map[segment] = child;
                return child;
            }

            if (existing is PlainMap or List<object?>)
                return existing;
            throw Invalid($"Cannot set path '{path}': segment '{segment}' is a primitive value.");
        }

        var list = (List<object?>)current;
        var index = ParseArrayIndex(segment, path);
        while (list.Count <= index)
            list.Add(null);
        var item = list[index];
        if (item is null)
        {
            object child = nextIsIndex ? new List<object?>() : new PlainMap();
            list[index] = child;
            return child;
        }

        if (item is PlainMap or List<object?>)
            return item;
        throw Invalid($"Cannot set path '{path}': segment '{segment}' is a primitive value.");
    }

    private static void SetNestedPath(PlainMap target, string path, object? value)
    {
        var clean = StripPathPrefix(path);
        if (clean.Length == 0)
        {
            if (value is not PlainMap replacement)
                throw Invalid($"Cannot set root data model '{path}' to a non-map value.");
            target.Clear();
            foreach (var pair in replacement)
                target[pair.Key] = pair.Value;
            return;
        }

        var keys = clean.Split('/');
        object current = target;
        for (var i = 0; i < keys.Length - 1; i++)
            current = StepContainer(current, keys[i], keys[i + 1], path);
        var last = keys[^1];
        if (current is PlainMap map)
        {
            map[last] = value;
            return;
        }

        var list = (List<object?>)current;
        var index = ParseArrayIndex(last, path);
        while (list.Count <= index)
            list.Add(null);
        list[index] = value;
    }

    private static bool SchemaAllowsDataBinding(object? schema)
    {
        if (schema is not PlainMap map)
            return false;
        if (map.Get("$ref") is string reference && (reference.Contains("DataBinding", StringComparison.Ordinal) || reference.Contains("Dynamic", StringComparison.Ordinal)))
            return true;
        if (map.Get("properties") is PlainMap properties && (properties.ContainsKey("path") || properties.ContainsKey("@path"))
            && !properties.ContainsKey("componentId"))
            return true;
        if (map.ContainsKey("items") && SchemaAllowsDataBinding(map.Get("items")))
            return true;
        foreach (var key in new[] { "allOf", "oneOf", "anyOf" })
            if (map.Get(key) is List<object?> subs && subs.Any(SchemaAllowsDataBinding))
                return true;
        return false;
    }

    private static string Describe(object? value) => value switch
    {
        CallNode call => $"a call to {call.Name}(...)",
        CheckNode check => $"a check (?{check.Name})",
        VariableNode variable => $"the variable {variable.Name}",
        PathNode => "a data binding",
        SkippedNode => "'_'",
        PlainMap => "a map literal",
        List<object?> => "a list",
        null => "null",
        bool => "a boolean",
        string => "a string",
        long or double or decimal => "a number",
        _ => $"a {value.GetType().Name}",
    };

    private static string? BindingPath(object? value, string pathKey)
    {
        if (value is not PlainMap map || map.ContainsKey("componentId"))
            return null;
        return map.Get(pathKey) as string;
    }

    private static bool ResolvesToPath(object? node, PlainMap symbols)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (node is VariableNode variable)
        {
            if (!seen.Add(variable.Name) || !symbols.TryGetValue(variable.Name, out var next))
                return false;
            node = next;
        }

        return node is PathNode;
    }

    private static bool HasDataBinding(object? node, PlainMap symbols, ISet<string>? seen = null)
    {
        seen ??= new HashSet<string>(StringComparer.Ordinal);
        switch (node)
        {
            case PathNode:
                return true;
            case VariableNode variable:
                if (seen.Contains(variable.Name) || !symbols.TryGetValue(variable.Name, out var target))
                    return false;
                return HasDataBinding(target, symbols, new HashSet<string>(seen, StringComparer.Ordinal) { variable.Name });
            case PlainMap map:
                return map.Any(pair => HasDataBinding(pair.Value, symbols, seen));
            case List<object?> list:
                return list.Any(item => HasDataBinding(item, symbols, seen));
            default:
                return false;
        }
    }

    private static bool ExpectsOptionObjects(object? schema)
    {
        if (schema is not PlainMap map)
            return false;
        if (map.ContainsKey("items"))
            return HasLabelAndValue(map.Get("items"));
        foreach (var key in new[] { "allOf", "oneOf", "anyOf" })
            if (map.Get(key) is List<object?> subs && subs.Any(ExpectsOptionObjects))
                return true;
        return false;

        static bool HasLabelAndValue(object? candidate)
        {
            if (candidate is not PlainMap sub)
                return false;
            if (sub.Get("properties") is PlainMap properties && properties.ContainsKey("label") && properties.ContainsKey("value"))
                return true;
            foreach (var key in new[] { "allOf", "oneOf", "anyOf" })
                if (sub.Get(key) is List<object?> subs && subs.Any(HasLabelAndValue))
                    return true;
            return false;
        }
    }

    private static bool IsCheckExpression(object? value) => value switch
    {
        CheckNode => true,
        List<object?> { Count: > 0 } list => list.All(IsCheckExpression),
        _ => false,
    };

    private static object? CallArgument(CallNode node, int index, string key)
    {
        if (node.Kwargs.TryGetValue(key, out var named))
            return named;
        return node.Args.Count > index ? node.Args[index] : null;
    }

    private static string? StringCallArgument(CallNode node, int index, string key, string statement)
    {
        var value = CallArgument(node, index, key);
        if (value is null)
            return null;
        if (value is not string text)
            throw Invalid($"{statement}() argument '{key}' must be a string, got: {Describe(value)}");
        return text;
    }

    private static PlainMap CopyMap(PlainMap source)
    {
        var copy = new PlainMap();
        foreach (var pair in source)
            copy[pair.Key] = pair.Value;
        return copy;
    }

    private List<CatalogHelper> Defining(string kind, string name) =>
        _helperList.Where(helper => (kind == "component" ? helper.Components : helper.Functions).ContainsKey(name)).ToList();

    private CatalogHelper HelperFor(string catalogId)
    {
        if (_helpers.TryGetValue(catalogId, out var helper))
            return helper;
        throw Invalid($"Unknown catalog '{catalogId}'. Available catalogs: [{string.Join(", ", _helpers.Keys)}]");
    }

    private bool DefinesComponent(string name, object? catalogId)
    {
        if (catalogId is string id && _helpers.TryGetValue(id, out var helper))
            return helper.Components.ContainsKey(name);
        return Defining("component", name).Count > 0;
    }

    private bool DefinesFunction(string name, object? catalogId)
    {
        if (name.StartsWith('@'))
            return true;
        if (catalogId is not null)
            return catalogId is string id && _helpers.TryGetValue(id, out var helper) && helper.Functions.ContainsKey(name);
        return Defining("function", name).Count > 0;
    }

    /// <summary>
    /// Finds the catalog a component or function resolves against. An explicit catalog identifier wins.
    /// Otherwise exactly one catalog must define the name.
    /// </summary>
    private (CatalogHelper Helper, string? WriteCatalogId) Resolve(string kind, string name, object? catalogId, string target, string label)
    {
        if (catalogId is not null)
        {
            if (catalogId is not string explicitId)
                throw Invalid($"{target} catalogId must be a string, got: {Describe(catalogId)}");
            var helper = HelperFor(explicitId);
            var defined = kind == "component" ? helper.Components : helper.Functions;
            if (!defined.ContainsKey(name))
                throw Invalid($"Unknown {label} '{name}' not defined in catalog '{explicitId}'.");
            return (helper, explicitId);
        }

        var matches = Defining(kind, name);
        if (matches.Count > 1)
        {
            throw Invalid($"{char.ToUpperInvariant(label[0]) + label[1..]} '{name}' is defined in several catalogs: [{string.Join(", ", matches.Select(m => m.CatalogId))}]. " +
                "Name the catalog to use with a catalogId argument.");
        }

        if (matches.Count == 0)
        {
            if (IsMultiCatalog)
                throw Invalid($"Unknown {label} '{name}' not defined in any catalog: [{string.Join(", ", _helpers.Keys)}].");
            throw Invalid($"Unknown {label} '{name}' not defined in catalog '{_helperList[0].CatalogId}'.");
        }

        return (matches[0], IsMultiCatalog ? matches[0].CatalogId : null);
    }

    private List<object> BuildUnits(List<ExpressStatement> statements, string surfaceId)
    {
        var units = new List<object>();
        SurfaceScope? current = null;
        foreach (var statement in statements)
        {
            if (statement.Target is null)
            {
                if (statement.Value is not CallNode call)
                    continue;
                if (call.Name == SurfaceStatement)
                {
                    current = OpenScope(call, surfaceId);
                    units.Add(current);
                }
                else if (call.Name == DeleteSurfaceStatement)
                {
                    var target = StringCallArgument(call, 0, "surfaceId", call.Name)
                        ?? throw Invalid("deleteSurface() requires the id of the surface to delete.");
                    units.Add(new DeleteUnit(target));
                }
                else
                {
                    units.Add(new StandaloneUnit(call, current));
                }

                continue;
            }

            if (current is null)
            {
                current = new SurfaceScope(surfaceId);
                units.Add(current);
            }

            if (statement.Target.StartsWith('$'))
            {
                current.DataPathAssignments.Remove(statement.Target);
                current.DataPathAssignments[statement.Target] = statement.Value;
            }
            else
            {
                current.RawSymbols[statement.Target] = statement.Value;
            }
        }

        return units;
    }

    private SurfaceScope OpenScope(CallNode node, string defaultSurfaceId)
    {
        var statement = node.Name;
        var targetSurface = StringCallArgument(node, 0, "surfaceId", statement);
        if (string.IsNullOrEmpty(targetSurface))
            targetSurface = defaultSurfaceId;
        var explicitCatalog = StringCallArgument(node, 1, "catalogId", statement);
        if (explicitCatalog is not null)
        {
            if (IsMultiCatalog)
            {
                throw Invalid($"{statement}() names catalog '{explicitCatalog}', but a surface names no catalog when several catalogs are active. " +
                    "Remove the catalog from the line and add catalogId only to components and function calls whose name several catalogs define.");
            }

            HelperFor(explicitCatalog);
        }

        bool? sendDataModel = null;
        var sendValue = node.Kwargs.Get(SendDataModelKey);
        if (sendValue is bool sendFlag)
            sendDataModel = sendFlag;
        else if (sendValue is not null)
            throw Invalid($"{SendDataModelKey} must be true or false, got: {statement}({SendDataModelKey}={Describe(sendValue)})");

        var isUpdate = false;
        var updateValue = node.Kwargs.Get(UpdateKey);
        if (updateValue is bool updateFlag)
            isUpdate = updateFlag;
        else if (updateValue is not null)
            throw Invalid($"{UpdateKey} must be true or false, got: {statement}({UpdateKey}={Describe(updateValue)})");

        object? extensions = null;
        if (node.Kwargs.ContainsKey(ExtensionsKey))
        {
            if (isUpdate)
                throw Invalid($"{statement}() with update=true does not accept extensions.");
            extensions = node.Kwargs[ExtensionsKey];
        }

        return new SurfaceScope(targetSurface) { SendDataModel = sendDataModel, IsUpdate = isUpdate, Extensions = extensions };
    }

    private void SeedComponentIds(SurfaceScope scope, Context context)
    {
        context.UsedIds.Clear();
        foreach (var (name, value) in scope.RawSymbols)
        {
            if (value is not CallNode call || !DefinesComponent(call.Name, call.Kwargs.Get("catalogId")))
                continue;
            var id = ComponentId(name, call);
            if (!context.UsedIds.Add(id))
                throw Invalid($"Duplicate component ID '{id}'.");
        }
    }

    private List<PlainMap> CompileComponents(SurfaceScope scope, Context context)
    {
        SeedComponentIds(scope, context);
        var compiled = new List<PlainMap>();
        foreach (var (name, value) in scope.RawSymbols)
        {
            var component = CompileComponent(name, value, scope.RawSymbols, context);
            if (component is not { Count: > 0 })
                continue;
            compiled.Add(component);
            compiled.AddRange(context.ExtraComponents);
            context.ExtraComponents = [];
        }

        return compiled;
    }

    private List<PlainMap> CompileScope(SurfaceScope scope, Context context)
    {
        var components = CompileComponents(scope, context);
        var surfaceId = scope.SurfaceId;
        var isCreate = !scope.IsUpdate && components.Any(component => component.Get("id") as string == "root");

        if (!isCreate)
        {
            if (scope.Extensions is not null)
                throw Invalid("surface() extensions require a surface-creating scope with a root component.");
            var dataUpdates = new List<PlainMap>();
            foreach (var (path, value) in scope.DataPathAssignments)
            {
                var pointer = ToJsonPointer(path);
                var compiledValue = CompileValue(value, scope.RawSymbols, context);
                if (pointer == "/" && compiledValue is not PlainMap)
                    throw Invalid("Cannot assign a non-map value to the root data path '$/'.");
                dataUpdates.Add(Message("updateDataModel", new PlainMap
                {
                    ["surfaceId"] = surfaceId,
                    ["path"] = pointer,
                    ["value"] = compiledValue,
                }));
            }

            if (components.Count == 0)
            {
                if (dataUpdates.Count > 0)
                    return dataUpdates;
                throw UndefinedRoot();
            }

            // Components without a root update a surface the renderer already shows, whether or not a
            // surface(...) line named it.
            var updates = new List<PlainMap>
            {
                Message("updateComponents", new PlainMap
                {
                    ["surfaceId"] = surfaceId,
                    ["components"] = components.Cast<object?>().ToList(),
                }),
            };
            updates.AddRange(dataUpdates);
            return updates;
        }

        var dataModel = new PlainMap();
        foreach (var (path, value) in scope.DataPathAssignments)
            SetNestedPath(dataModel, path, CompileValue(value, scope.RawSymbols, context));

        var create = new PlainMap { ["surfaceId"] = surfaceId };
        if (_helperList.Count == 1)
            create["catalogId"] = _helperList[0].CatalogId;
        if (scope.SendDataModel is { } send)
            create[SendDataModelKey] = send;
        if (scope.Extensions is not null)
        {
            var compiledExtensions = CompileExtensions(scope.Extensions, scope.RawSymbols, "surface()", context);
            create["metadata"] = new PlainMap { ["extensions"] = compiledExtensions };
        }

        create["components"] = components.Cast<object?>().ToList();
        if (dataModel.Count > 0)
            create["dataModel"] = dataModel;
        return [Message("createSurface", create)];
    }

    private static object? ResolveVariable(object? node, PlainMap symbols, ISet<string> seen)
    {
        var current = node;
        while (current is VariableNode variable)
        {
            if (seen.Contains(variable.Name))
                throw Invalid($"Variable '{variable.Name}' refers to itself.");
            if (!symbols.TryGetValue(variable.Name, out var next))
                throw Invalid($"Undefined variable '{variable.Name}' in 1.0 expression.");
            seen.Add(variable.Name);
            current = next;
        }

        return current;
    }

    private void CheckExtensions(object? node, PlainMap symbols, string target, ISet<string> seen)
    {
        var resolved = ResolveVariable(node, symbols, new HashSet<string>(seen, StringComparer.Ordinal));
        switch (resolved)
        {
            case PlainMap map:
                foreach (var pair in map)
                    CheckExtensions(pair.Value, symbols, target, seen);
                break;
            case List<object?> list:
                foreach (var item in list)
                    CheckExtensions(item, symbols, target, seen);
                break;
            case CallNode call:
                var catalogId = call.Kwargs.Get("catalogId");
                if (DefinesComponent(call.Name, catalogId))
                    throw Invalid($"{target} extensions cannot contain component '{call.Name}'.");
                if (call.Name is "Event" or "_template" or SurfaceStatement or DeleteSurfaceStatement && catalogId is null)
                    throw Invalid($"{target} extensions cannot contain '{call.Name}()'.");
                foreach (var argument in call.Args)
                    CheckExtensions(argument, symbols, target, seen);
                foreach (var pair in call.Kwargs)
                    CheckExtensions(pair.Value, symbols, target, seen);
                break;
        }
    }

    private PlainMap CompileExtensions(object? raw, PlainMap symbols, string target, Context context)
    {
        var top = ResolveVariable(raw, symbols, new HashSet<string>(StringComparer.Ordinal));
        if (top is not PlainMap)
            throw Invalid($"{target} extensions must be a map, got: {Describe(top)}");
        CheckExtensions(top, symbols, target, new HashSet<string>(StringComparer.Ordinal));
        var compiled = CompileValue(raw, symbols, context);
        return ValidateExtensions(compiled, target);
    }

    private static PlainMap ValidateExtensions(object? extensions, string target)
    {
        if (extensions is not PlainMap map)
            throw Invalid($"{target} extensions must be a map, got: {Describe(extensions)}");
        foreach (var key in map.Keys)
            if (!UnicodeIdentifiers.IsIdentifier(key))
                throw Invalid($"{target} extension key '{key}' is not a valid UAX #31 identifier.");
        return map;
    }

    private PlainMap CompileStandaloneCall(StandaloneUnit unit, Context context)
    {
        context.CallCounter++;
        var node = unit.Node;
        var functionCallId = $"call_{context.CallCounter.ToString(CultureInfo.InvariantCulture)}";
        if (node.Kwargs.ContainsKey(FunctionCallIdKey))
        {
            var kwargs = CopyMap(node.Kwargs);
            var explicitId = kwargs.Get(FunctionCallIdKey);
            kwargs.Remove(FunctionCallIdKey);
            if (explicitId is not string id || id.Length == 0)
                throw Invalid($"{FunctionCallIdKey} must be a non-empty string, got: {Describe(explicitId)}");
            functionCallId = id;
            node = new CallNode(node.Name, node.Args, kwargs);
        }

        if (node.Name.StartsWith('@'))
        {
            throw Invalid($"System function '{node.Name}' cannot be called as a standalone statement, " +
                "since a renderer function call names a catalog function.");
        }

        var symbols = unit.Scope?.RawSymbols ?? new PlainMap();
        if (CompileValue(node, symbols, context) is not PlainMap { } compiled || !compiled.ContainsKey("@call"))
            throw Invalid("Standalone statement did not compile to a valid function call.");

        // With several catalogs the compiled call always names its catalog.
        var catalogId = compiled.Get("catalogId") as string;
        if (string.IsNullOrEmpty(catalogId))
            catalogId = _helperList[0].CatalogId;
        return Message("callRendererFunction", new PlainMap
        {
            ["functionCallId"] = functionCallId,
            ["callFunction"] = new PlainMap
            {
                ["catalogId"] = catalogId,
                ["@call"] = compiled.Get("@call"),
                ["args"] = compiled.Get("args") ?? new PlainMap(),
            },
        });
    }

    private PlainMap? CompileComponent(string variableName, object? ast, PlainMap symbols, Context context)
    {
        if (ast is not CallNode node)
            return null;

        var name = node.Name;
        var kwargs = CopyMap(node.Kwargs);
        if (name.StartsWith('@'))
        {
            // A variable holding a system function call is inlined wherever it is named, but the call is
            // checked here so that one nothing names is rejected too.
            SystemFunctionHelper(name, kwargs);
            return null;
        }

        var catalogId = kwargs.Get("catalogId");
        kwargs.Remove("catalogId");
        // A variable holding a function call, an Event or a template is inlined wherever it is named rather
        // than emitted as a component. Events and templates belong to no catalog, so one that names a
        // catalogId is compiled as a component and rejected.
        if (!DefinesComponent(name, catalogId)
            && ((name is "Event" or "_template" && catalogId is null) || DefinesFunction(name, catalogId)))
            return null;

        var (helper, writeCatalogId) = Resolve("component", name, catalogId, $"Component '{name}'", "component");
        var properties = helper.GetComponentProperties(name);
        var id = ComponentId(variableName, node);
        kwargs.Remove(IdKey);
        object? rawExtensions = null;
        if (!properties.Contains(ExtensionsKey) && kwargs.TryGetValue(ExtensionsKey, out rawExtensions))
            kwargs.Remove(ExtensionsKey);

        var component = new PlainMap { ["id"] = id, ["component"] = name };
        if (writeCatalogId is not null)
            component["@catalogId"] = writeCatalogId;
        if (rawExtensions is not null)
        {
            var compiledExtensions = CompileExtensions(rawExtensions, symbols, $"Component '{name}'", context);
            component["@metadata"] = new PlainMap { ["extensions"] = compiledExtensions };
        }

        PlainMap? siblingValuePath = null;
        var customEnvelope = helper.GetCustomEnvelope(name);
        var nonCheckProperties = customEnvelope.Contains("checks")
            ? properties.ToList()
            : properties.Where(p => p != "checks").ToList();
        var rawChecks = new List<object?>();
        var pairs = new List<(string Name, object? Value)>();
        var positional = new List<object?>();
        foreach (var argument in node.Args)
        {
            if (!IsCheckExpression(argument))
            {
                positional.Add(argument);
                continue;
            }

            if (argument is List<object?> checkList)
                rawChecks.AddRange(checkList);
            else
                rawChecks.Add(argument);
        }

        var required = new HashSet<string>(helper.GetComponentRequired(name), StringComparer.Ordinal);
        var propertyIndex = 0;
        for (var argumentIndex = 0; argumentIndex < positional.Count; argumentIndex++)
        {
            var argument = positional[argumentIndex];
            if (propertyIndex >= nonCheckProperties.Count)
            {
                throw Invalid($"{name} takes at most {nonCheckProperties.Count} positional arguments ({string.Join(", ", nonCheckProperties)}), " +
                    $"but component '{id}' passes {positional.Count}.");
            }

            if (PlainJson.IsNumber(argument) && !helper.AcceptsNumber(helper.GetPropertySchema(name, nonCheckProperties[propertyIndex])))
            {
                var moved = false;
                if (argumentIndex == positional.Count - 1)
                {
                    var numericLater = new List<int>();
                    for (var later = propertyIndex + 1; later < nonCheckProperties.Count; later++)
                    {
                        if (!kwargs.ContainsKey(nonCheckProperties[later])
                            && helper.AcceptsNumber(helper.GetPropertySchema(name, nonCheckProperties[later])))
                            numericLater.Add(later);
                    }

                    if (numericLater.Count == 1)
                    {
                        var targetIndex = numericLater[0];
                        var skippable = true;
                        for (var skipped = propertyIndex; skipped < targetIndex; skipped++)
                        {
                            if (required.Contains(nonCheckProperties[skipped]) || helper.GetPropertyEnum(name, nonCheckProperties[skipped]) is not { Count: > 0 })
                                skippable = false;
                        }

                        if (skippable)
                        {
                            propertyIndex = targetIndex;
                            moved = true;
                        }
                    }
                }

                if (!moved && !required.Contains(nonCheckProperties[propertyIndex]))
                {
                    throw Invalid($"Positional argument {argumentIndex + 1} for component '{name}' is a number, " +
                        $"which is not valid for optional property '{nonCheckProperties[propertyIndex]}'.");
                }
            }

            pairs.Add((nonCheckProperties[propertyIndex], argument));
            propertyIndex++;
        }

        foreach (var (key, value) in kwargs)
        {
            if (!IsCheckExpression(value))
            {
                pairs.Add((key, value));
                continue;
            }

            if (value is List<object?> checkList)
                rawChecks.AddRange(checkList);
            else
                rawChecks.Add(value);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (propertyName, argument) in pairs)
        {
            if (!properties.Contains(propertyName))
            {
                var help = properties.Count > 0
                    ? $"Component '{name}' accepts properties: {string.Join(", ", properties.OrderBy(p => p, StringComparer.Ordinal))}."
                    : $"Component '{name}' has no configurable properties.";
                throw Invalid($"Property '{propertyName}' is not a valid property of component '{name}'. {help}");
            }

            if (!seen.Add(propertyName))
                throw Invalid($"Property '{propertyName}' of component '{name}' was provided multiple times.");
            if (argument is SkippedNode)
            {
                component[propertyName] = null;
                continue;
            }

            var propertySchema = helper.GetPropertySchema(name, propertyName);
            var propertyType = helper.GetPropertyType(name, propertyName);
            var slot = propertySchema is { Count: > 0 } ? new ValueSlot(propertySchema, helper) : null;
            var mapped = CompileValue(argument, symbols, context,
                isAction: CatalogHelper.IsActionSlot(propertySchema),
                parentId: id,
                parentProperty: propertyName,
                allowComponentReference: propertyType == "Child",
                allowListComponentReferences: propertyType == "ChildList" && argument is List<object?>,
                slot: slot);
            if (propertySchema is { Count: > 0 } && !SchemaAllowsDataBinding(propertySchema))
            {
                if (HasDataBinding(argument, symbols))
                {
                    throw Invalid($"Property '{propertyName}' of component '{name}' does not support dynamic data bindings (paths). " +
                        $"Property '{propertyName}' on '{name}' must be a static literal value, not a dynamic databinding ($path).");
                }

                if (mapped is List<object?> options && ExpectsOptionObjects(propertySchema))
                {
                    mapped = options.Select(option => option is string label
                        ? new PlainMap { ["label"] = label, ["value"] = label }
                        : option).ToList();
                }
            }

            var enumValues = helper.GetPropertyEnum(name, propertyName);
            if (enumValues is { Count: > 0 } && mapped is not (PlainMap or List<object?>))
            {
                if (!enumValues.Any(choice => PlainJson.DeepEquals(choice, mapped)))
                {
                    var allowed = System.Text.Encoding.UTF8.GetString(PlainJson.ToUtf8(enumValues));
                    throw Invalid($"The value is not a valid enum choice for property '{propertyName}' of component '{name}'. Allowed values are: {allowed}");
                }
            }

            component[propertyName] = mapped;

            // Only a $path value binds the checks; a map literal shaped like a binding is data.
            if (propertyName == "value" && ResolvesToPath(argument, symbols))
                siblingValuePath = mapped as PlainMap;
        }

        if (rawChecks.Count > 0 && !properties.Contains("checks") && !helper.IsCheckable(name))
            throw Invalid($"Component '{name}' does not declare a 'checks' property, so it cannot take checks.");

        // Checks bind the component's own value implicitly.
        context.ActiveValuePath = siblingValuePath;
        if (rawChecks.Count > 0)
        {
            var compiledChecks = new List<object?>();
            foreach (var rawCheck in rawChecks)
            {
                if (rawCheck is not CheckNode check)
                    continue;
                var (condition, message) = CompileCheck(check, symbols, context);
                compiledChecks.Add(new PlainMap { ["condition"] = condition, ["message"] = message });
            }

            if (compiledChecks.Count > 0)
                component["@checks"] = compiledChecks;
        }

        context.ActiveValuePath = null;
        foreach (var requiredProperty in helper.GetComponentRequired(name))
        {
            if (!component.ContainsKey(requiredProperty) && (requiredProperty is not ("checks" or "@checks") || customEnvelope.Contains(requiredProperty)))
                throw Invalid($"Component '{name}' missing required property '{requiredProperty}'.");
        }

        var result = new PlainMap();
        foreach (var pair in component)
            if (pair.Value is not null)
                result[pair.Key] = pair.Value;
        return result;
    }

    private (PlainMap Condition, string Message) CompileCheck(CheckNode check, PlainMap symbols, Context context, bool isAction = false)
    {
        var checkName = check.Name;
        var explicitArguments = new List<object?>();
        string? checkCatalogId = null;
        foreach (var item in check.Args)
        {
            if (item is PlainMap map && map.ContainsKey("catalogId"))
            {
                var otherKeys = map.Keys.Where(key => key != "catalogId").ToList();
                if (otherKeys.Count > 0)
                {
                    throw Invalid($"Unsupported key(s) in check options for '?{checkName}': {string.Join(", ", otherKeys)}. Only 'catalogId' is allowed.");
                }

                // A null catalogId would read as "no catalogId", so it is rejected rather than ignored.
                var value = map.Get("catalogId");
                if (value is null)
                    throw Invalid($"Check '{checkName}' catalogId must be a string, got: null");
                if (checkCatalogId is not null && !PlainJson.DeepEquals(checkCatalogId, value))
                    throw Invalid($"Check '?{checkName}' specifies conflicting catalogId values.");
                checkCatalogId = value as string ?? throw Invalid($"Check '{checkName}' catalogId must be a string, got: {Describe(value)}");
                continue;
            }

            explicitArguments.Add(item);
        }

        var (helper, writeCatalogId) = Resolve("function", checkName, checkCatalogId, $"Check '{checkName}'", "check function");
        var parameters = helper.GetFunctionProperties(checkName);
        var message = char.ToUpperInvariant(checkName[0]) + checkName[1..].ToLowerInvariant() + " check failed";
        var compiledArguments = new PlainMap();

        var valueInjected = false;
        if (parameters.Count > 0 && parameters[0] == "value")
        {
            var hasExplicitBinding = explicitArguments.Count > 0 && explicitArguments[0] is PathNode;
            if (!hasExplicitBinding && context.ActiveValuePath is { Count: > 0 })
            {
                compiledArguments["value"] = PlainJson.Clone(context.ActiveValuePath);
                valueInjected = true;
            }
        }

        var startIndex = valueInjected ? 1 : 0;
        for (var index = 0; index < explicitArguments.Count; index++)
        {
            var argument = explicitArguments[index];
            var targetIndex = index + startIndex;
            if (targetIndex >= parameters.Count)
            {
                if (argument is string overflowMessage)
                    message = overflowMessage;
                continue;
            }

            var parameter = parameters[targetIndex];
            var schema = helper.GetFunctionPropertySchema(checkName, parameter);
            if (argument is string text && schema is { Count: > 0 } && schema.Get("type") is "integer" or "number" or "boolean")
            {
                message = text;
                break;
            }

            if (argument is SkippedNode)
                continue;
            compiledArguments[parameter] = CompileValue(argument, symbols, context, isAction);
        }

        var condition = new PlainMap { ["@call"] = checkName };
        if (writeCatalogId is not null)
            condition["catalogId"] = writeCatalogId;
        condition["args"] = compiledArguments;
        return (condition, message);
    }

    private PlainMap CompileEvent(CallNode node, PlainMap symbols, Context context)
    {
        var arguments = node.Args;
        var kwargs = node.Kwargs;
        if (arguments.Count > 2)
            throw Invalid($"Event accepts at most 2 positional arguments (name, context), got {arguments.Count}.");
        foreach (var key in kwargs.Keys)
        {
            if (!EventKeywords.Contains(key))
            {
                throw Invalid($"Argument '{key}' is not a valid parameter of function 'Event'. Function 'Event' accepts parameters: {string.Join(", ", EventKeywords.OrderBy(k => k, StringComparer.Ordinal))}.");
            }
        }

        if (arguments.Count == 2 && kwargs.ContainsKey("context"))
            throw Invalid("Argument 'context' of function 'Event' was provided multiple times.");

        var name = arguments.Count > 0 ? CompileValue(arguments[0], symbols, context) : "";
        var eventBody = new PlainMap { ["name"] = name };
        if (kwargs.ContainsKey("userMessage"))
        {
            // userMessage is a DynamicString: a string, a data binding or a function call.
            var userMessage = CompileValue(kwargs.Get("userMessage"), symbols, context);
            var isBindingOrCall = userMessage is PlainMap map && (map.ContainsKey("@path") || map.ContainsKey("@call"));
            if (userMessage is not string && !isBindingOrCall)
            {
                throw Invalid("Event userMessage must be a string, a $path data binding or a function call, got " +
                    $"{Describe(kwargs.Get("userMessage"))}.");
            }

            eventBody["userMessage"] = userMessage;
        }

        object? contextNode;
        if (arguments.Count == 2)
            contextNode = arguments[1];
        else if (kwargs.ContainsKey("context"))
            contextNode = kwargs.Get("context");
        else
            return new PlainMap { ["event"] = eventBody };

        var rawContext = CompileValue(contextNode, symbols, context);
        // A context written in the notation is kept even when it is empty.
        var compiledContext = new PlainMap();
        if (rawContext is PlainMap contextMap)
        {
            foreach (var pair in contextMap)
                compiledContext[pair.Key] = pair.Value;
        }
        else if (rawContext is List<object?> contextList)
        {
            foreach (var item in contextList)
                if (item is PlainMap itemMap)
                    foreach (var pair in itemMap)
                        compiledContext[pair.Key] = pair.Value;
        }

        eventBody["context"] = compiledContext;
        return new PlainMap { ["event"] = eventBody };
    }

    private object? CompileValue(object? value, PlainMap symbols, Context context, bool isAction = false, string? parentId = null,
        string? parentProperty = null, int? listIndex = null, bool allowComponentReference = false,
        bool allowListComponentReferences = false, ValueSlot? slot = null)
    {
        switch (value)
        {
            case PathNode path:
                return new PlainMap { ["@path"] = path.Path };
            case VariableNode variable:
                return CompileVariable(variable, symbols, context, isAction, allowComponentReference, allowListComponentReferences, slot);
            case CheckNode check:
                return CompileCheck(check, symbols, context, isAction).Condition;
            case CallNode call:
                return CompileCall(call, symbols, context, isAction, parentId, parentProperty, listIndex);
            case SkippedNode:
                throw Invalid("'_' can only stand in for an argument.");
            case PlainMap map:
                // A map is always a map literal, whatever its keys are.
                var compiledMap = new PlainMap();
                foreach (var (key, entry) in map)
                {
                    var entrySlot = PropertySlot(slot, key);
                    var entryReferenceType = entrySlot is null ? null : CatalogHelper.ReferenceType(entrySlot.Schema);
                    var entryIsAction = isAction || (entrySlot is not null && CatalogHelper.IsActionSlot(entrySlot.Schema));
                    compiledMap[key] = CompileValue(entry, symbols, context, entryIsAction,
                        allowComponentReference: entryReferenceType == "Child",
                        allowListComponentReferences: entryReferenceType == "ChildList" && entry is List<object?>,
                        slot: entrySlot);
                }

                return compiledMap;
            case List<object?> list:
                // Resolved once for the list, since every item shares the schema.
                var itemSlot = ItemSlot(slot);
                var itemReferenceType = itemSlot is null ? null : CatalogHelper.ReferenceType(itemSlot.Schema);
                var itemIsAction = isAction || (itemSlot is not null && CatalogHelper.IsActionSlot(itemSlot.Schema));
                var compiledList = new List<object?>(list.Count);
                for (var index = 0; index < list.Count; index++)
                {
                    compiledList.Add(CompileValue(list[index], symbols, context, itemIsAction, parentId, parentProperty, index,
                        allowListComponentReferences || itemReferenceType == "Child", slot: itemSlot));
                }

                return compiledList;
            default:
                return value;
        }
    }

    private object? CompileVariable(VariableNode variable, PlainMap symbols, Context context, bool isAction,
        bool allowComponentReference, bool allowListComponentReferences, ValueSlot? slot)
    {
        var name = variable.Name;
        if (!symbols.TryGetValue(name, out var symbolValue))
        {
            if (!allowComponentReference)
                throw Invalid($"Variable '{name}' is not assigned in surface.");
            return name;
        }

        if (symbolValue is CallNode symbolCall && DefinesComponent(symbolCall.Name, symbolCall.Kwargs.Get("catalogId")))
            return ComponentId(name, symbolCall);
        if (context.Resolving.Contains(name))
            throw Invalid($"Variable '{name}' refers to itself through its own value.");
        context.Resolving.Add(name);
        try
        {
            return CompileValue(symbolValue, symbols, context, isAction, allowComponentReference: allowComponentReference,
                allowListComponentReferences: allowListComponentReferences, slot: slot);
        }
        finally
        {
            context.Resolving.Remove(name);
        }
    }

    private object? CompileCall(CallNode node, PlainMap symbols, Context context, bool isAction, string? parentId,
        string? parentProperty, int? listIndex)
    {
        var name = node.Name;
        var arguments = node.Args;
        var kwargs = CopyMap(node.Kwargs);

        if (name == "_template")
        {
            if (arguments.Count < 2)
                throw ExpressException.Parse("_template helper requires exactly 2 arguments: path and templateComponent.");
            var pathValue = CompileValue(arguments[0], symbols, context, isAction);
            var templatePath = BindingPath(pathValue, "@path")
                ?? throw ExpressException.Parse($"The first argument to _template must be a dynamic data binding path (prefixed by $), got: {Describe(arguments[0])}");
            var componentId = CompileValue(arguments[1], symbols, context, isAction, allowComponentReference: true);
            // A child list template keeps the plain path key.
            return new PlainMap { ["path"] = templatePath, ["componentId"] = componentId };
        }

        if (name == "Event")
            return CompileEvent(node, symbols, context);
        if (name.StartsWith('@'))
            return CompileSystemCall(name, arguments, kwargs, symbols, context, isAction);

        var catalogId = kwargs.Get("catalogId");
        kwargs.Remove("catalogId");
        if (DefinesComponent(name, catalogId))
        {
            string inlineId;
            if (!string.IsNullOrEmpty(parentId) && !string.IsNullOrEmpty(parentProperty))
            {
                inlineId = listIndex is { } index
                    ? $"{parentId}_{parentProperty}_{index.ToString(CultureInfo.InvariantCulture)}"
                    : $"{parentId}_{parentProperty}";
            }
            else
            {
                context.InlineCounter++;
                inlineId = $"_inline_{context.InlineCounter.ToString(CultureInfo.InvariantCulture)}";
            }

            var actualId = ComponentId(inlineId, node);
            if (!context.UsedIds.Add(actualId))
                throw Invalid($"Duplicate component ID '{actualId}'.");

            var previousExtras = context.ExtraComponents;
            context.ExtraComponents = [];
            // The inline component resolves its own catalog; its children and calls are each resolved on their own.
            var compiledInline = CompileComponent(inlineId, node, symbols, context);
            var childExtras = context.ExtraComponents;
            context.ExtraComponents = previousExtras;
            if (compiledInline is { Count: > 0 })
            {
                context.ExtraComponents.Add(compiledInline);
                context.ExtraComponents.AddRange(childExtras);
                return compiledInline.Get("id");
            }

            return inlineId;
        }

        var (helper, writeCatalogId) = Resolve("function", name, catalogId, $"Call '{name}'", "function");
        var compiledArguments = CompileCallArguments(helper, name, arguments, kwargs, symbols, context, isAction);
        var expression = new PlainMap { ["@call"] = name };
        if (writeCatalogId is not null)
            expression["catalogId"] = writeCatalogId;
        expression["args"] = compiledArguments;
        return isAction ? new PlainMap { ["functionCall"] = expression } : expression;
    }

    private object CompileSystemCall(string name, List<object?> arguments, PlainMap kwargs, PlainMap symbols, Context context, bool isAction)
    {
        var helper = SystemFunctionHelper(name, kwargs);
        var compiledArguments = CompileCallArguments(helper, name, arguments, kwargs, symbols, context, isAction);
        var expression = new PlainMap { ["@call"] = name };
        if (compiledArguments.Count > 0)
            expression["args"] = compiledArguments;
        return isAction ? new PlainMap { ["functionCall"] = expression } : expression;
    }

    /// <summary>Checks a system function call and returns the helper that reads it.</summary>
    private CatalogHelper SystemFunctionHelper(string name, PlainMap kwargs)
    {
        if (!CatalogHelper.IsSystemFunction(name))
        {
            var known = CatalogHelper.SystemFunctionNames;
            throw Invalid($"Unknown system function '{name}' in A2UI 1.0. System functions: {(known.Count == 0 ? "none" : string.Join(", ", known))}.");
        }

        if (kwargs.ContainsKey("catalogId"))
            throw Invalid($"System function '{name}' is defined by the protocol, not a catalog, so it takes no catalogId.");
        return _helperList[0];
    }

    private PlainMap CompileCallArguments(CatalogHelper helper, string name, List<object?> arguments, PlainMap kwargs, PlainMap symbols,
        Context context, bool isAction)
    {
        var parameters = helper.GetFunctionProperties(name);
        var compiled = new PlainMap();
        for (var index = 0; index < arguments.Count; index++)
        {
            if (index >= parameters.Count || arguments[index] is SkippedNode)
                continue;
            var item = CompileValue(arguments[index], symbols, context, isAction);
            if (item is not null)
                compiled[parameters[index]] = item;
        }

        foreach (var (key, value) in kwargs)
        {
            if (!parameters.Contains(key))
            {
                var help = parameters.Count > 0
                    ? $"Function '{name}' accepts parameters: {string.Join(", ", parameters.OrderBy(p => p, StringComparer.Ordinal))}."
                    : $"Function '{name}' accepts no parameters.";
                throw Invalid($"Argument '{key}' is not a valid parameter of function '{name}'. {help}");
            }

            if (compiled.ContainsKey(key))
                throw Invalid($"Argument '{key}' of function '{name}' was provided multiple times.");
            if (value is SkippedNode)
                continue;
            var item = CompileValue(value, symbols, context, isAction);
            if (item is not null)
                compiled[key] = item;
        }

        return compiled;
    }
}
