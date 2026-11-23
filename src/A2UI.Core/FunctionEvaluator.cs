using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2UI.Core;

/// <summary>Evaluates function calls on a surface by routing each call to the catalog that declares it.</summary>
/// <remarks>
/// A call runs in the catalog its own <c>catalogId</c> names, else in the surface default catalog, else it fails with a
/// <c>CatalogError</c>. A call nested in the arguments of another call routes on its own and never inherits the outer
/// call's catalog. A call inside a <c>formatString</c> template always runs in the catalog that <c>formatString</c>
/// resolved to, because template syntax cannot name a catalog.
/// Failures use the diagnostic code as the error category: <c>CatalogError</c>, <c>ExpressionError</c> or
/// <c>ValidationError</c>. The evaluator implements the functions of the Basic Catalog and the functions registered with
/// <see cref="RegisterHostFunction"/>. A call to any other function fails with an <c>ExpressionError</c>. Use
/// <see cref="FunctionCallRouter"/> to send such calls to the agent.
/// </remarks>
public sealed class FunctionEvaluator
{
    internal const string IndexOutsideCollection =
        "@index function can only be evaluated inside a collection template iteration scope.";

    private const int MaxNesting = 64;
    private readonly CatalogRegistry _catalogs;
    private readonly Action<Uri>? _openUrl;
    private readonly Dictionary<(string CatalogId, string Name), HostFunction> _hostFunctions = new();

    /// <summary>Creates an evaluator.</summary>
    /// <param name="catalogs">The registry that holds the catalogs calls route to.</param>
    /// <param name="openUrl">The host handler that opens a URL for <c>openUrl</c>. Without one, <c>openUrl</c> validates and does nothing.</param>
    public FunctionEvaluator(CatalogRegistry catalogs, Action<Uri>? openUrl = null)
    {
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _openUrl = openUrl;
    }

    /// <summary>Registers the implementation of a function that a catalog declares but the SDK does not implement.</summary>
    /// <param name="catalogId">The catalog that declares the function.</param>
    /// <param name="name">The function name.</param>
    /// <param name="implementation">The host code that runs the call. An exception it throws becomes an <c>EXECUTION_ERROR</c> with a fixed message, so internal details never reach the agent.</param>
    /// <remarks>A host function takes precedence over a Basic Catalog function of the same name. Register functions before the first call. The evaluator does not synchronize registration.</remarks>
    public void RegisterHostFunction(string catalogId, string name, HostFunction implementation)
    {
        ArgumentException.ThrowIfNullOrEmpty(catalogId);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(implementation);
        _hostFunctions[(catalogId, name)] = implementation;
    }

    /// <summary>Evaluates a function call against a surface.</summary>
    /// <param name="surface">The committed surface the call runs on.</param>
    /// <param name="dataPath">The absolute pointer to the data scope of the call. Use "/" at the root, or an item pointer inside a collection.</param>
    /// <param name="call">The wire-level call, with <c>@call</c>, an optional <c>catalogId</c> and optional <c>args</c>.</param>
    /// <param name="userInitiated">Whether a user action started the call. Functions that require user activation fail without it.</param>
    /// <param name="value">The result. A function without a return value produces a JSON null.</param>
    /// <param name="diagnostic">The failure, with the error category in <see cref="A2uiDiagnostic.Code"/>.</param>
    /// <returns>Whether the call produced a value.</returns>
    /// <remarks>Calls declared <c>agentOnly</c> always fail here, because this evaluator serves renderer calls.</remarks>
    public bool TryEvaluate(SurfaceSnapshot surface, string dataPath, JsonElement call, bool userInitiated,
        out JsonElement value, out A2uiDiagnostic? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(dataPath);
        return TryCall(surface, dataPath, call, userInitiated, 0, out value, out diagnostic);
    }

    /// <summary>Resolves the bindings and function calls inside a JSON value, such as the context of an action.</summary>
    /// <param name="surface">The committed surface the value is read on.</param>
    /// <param name="dataPath">The absolute pointer to the data scope of the value.</param>
    /// <param name="value">A literal, a <c>@path</c> binding, a <c>@call</c> or an object or array that contains them.</param>
    /// <param name="userInitiated">Whether a user action started the resolution.</param>
    /// <param name="resolved">The value with every binding and call replaced by its result. A path with no data resolves to null.</param>
    /// <param name="diagnostic">The failure, with the error category in <see cref="A2uiDiagnostic.Code"/>.</param>
    /// <returns>Whether the value resolved.</returns>
    /// <remarks>A member name that starts with <c>@@</c> is a literal member whose name loses one <c>@</c>. Any other member name that starts with <c>@</c> is rejected.</remarks>
    public bool TryResolveValue(SurfaceSnapshot surface, string dataPath, JsonElement value, bool userInitiated,
        out JsonElement resolved, out A2uiDiagnostic? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(dataPath);
        return TryResolve(surface, dataPath, value, userInitiated, 0, out resolved, out diagnostic);
    }

    private bool TryCall(SurfaceSnapshot surface, string dataPath, JsonElement call, bool userInitiated, int depth,
        out JsonElement value, out A2uiDiagnostic? diagnostic)
    {
        value = default;
        if (depth > MaxNesting)
            return Fail("Function calls are nested too deeply.", out diagnostic);
        if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("@call", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
            return Fail("Expected a function call.", out diagnostic);

        var name = nameElement.GetString()!;
        if (name.StartsWith('@'))
            return TryCallSystem(surface, dataPath, call, userInitiated, depth, name, out value, out diagnostic);

        string? catalogId = surface.CatalogId;
        if (call.TryGetProperty("catalogId", out var named))
        {
            if (named.ValueKind != JsonValueKind.String)
            {
                diagnostic = new("CatalogError", "", "The catalogId of a call must be a string.");
                return false;
            }
            catalogId = named.GetString();
        }

        if (!TryRoute(surface.Id, catalogId, name, userInitiated, out diagnostic))
            return false;
        if (!TryArguments(surface, dataPath, call, userInitiated, depth, out var args, out diagnostic))
            return false;
        return TryInvoke(surface, dataPath, catalogId!, name, args, userInitiated, out value, out diagnostic);
    }

    private bool TryCallSystem(SurfaceSnapshot surface, string dataPath, JsonElement call, bool userInitiated, int depth,
        string name, out JsonElement value, out A2uiDiagnostic? diagnostic)
    {
        value = default;
        if (name != "@index")
            return Fail($"System function '{name}' is not defined.", out diagnostic);
        if (!TryArguments(surface, dataPath, call, userInitiated, depth, out var args, out diagnostic))
            return false;
        return TryIndex(dataPath, args, out value, out diagnostic);
    }

    // The index of the item in the innermost collection, plus the optional offset.
    private static bool TryIndex(string dataPath, IReadOnlyDictionary<string, JsonElement> args,
        out JsonElement value, out A2uiDiagnostic? diagnostic)
    {
        value = default;
        var segment = dataPath[(dataPath.LastIndexOf('/') + 1)..];
        if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            return Fail(IndexOutsideCollection, out diagnostic);

        long offset = 0;
        if (args.TryGetValue("offset", out var offsetArgument) &&
            offsetArgument.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            if (!FunctionValues.TryGetNumber(offsetArgument, out var number) || number != Math.Floor(number) ||
                Math.Abs(number) > int.MaxValue)
                return Fail("The offset of @index must be a whole number.", out diagnostic);
            offset = (long)number;
        }

        value = FunctionValues.Number(index + offset);
        diagnostic = null;
        return true;
    }

    private bool TryRoute(string surfaceId, string? catalogId, string name, bool userInitiated, out A2uiDiagnostic? diagnostic)
    {
        switch (Classify(surfaceId, defaultCatalogId: null, catalogId, name, userInitiated, out _, out var declared, out diagnostic))
        {
            case CallRoute.Local:
                return true;
            case CallRoute.Agent:
                return Fail(declared
                    ? $"Function '{name}' has no implementation."
                    : $"Function '{name}' is not defined in catalog '{catalogId}'.", out diagnostic);
            default:
                return false;
        }
    }

    // Decides where a call runs. The router sends an Agent call to the agent, and the evaluator refuses it.
    internal CallRoute Classify(string surfaceId, string? defaultCatalogId, string? explicitCatalogId, string name,
        bool userInitiated, out string? catalogId, out bool declared, out A2uiDiagnostic? diagnostic)
    {
        catalogId = explicitCatalogId ?? defaultCatalogId;
        declared = false;
        if (catalogId is null)
        {
            diagnostic = new("CatalogError", "",
                $"Surface '{surfaceId}' has no default catalogId, and the call to '{name}' names none.");
            return CallRoute.Refused;
        }
        if (!_catalogs.TryGetDefinition(catalogId, out var definition))
        {
            diagnostic = new("CatalogError", "", $"Catalog not found: {catalogId}");
            return CallRoute.Refused;
        }

        diagnostic = null;
        if (!definition.TryGetProperty("functions", out var functions) || !functions.TryGetProperty(name, out var declaration))
            return CallRoute.Agent;
        declared = true;
        if (declaration.TryGetProperty("allowedCallers", out var callers) && callers.ValueEquals("agentOnly"))
        {
            Fail($"Function '{name}' cannot be called by renderer.", out diagnostic);
            return CallRoute.Refused;
        }
        if (!userInitiated && declaration.TryGetProperty("requiresUserActivation", out var activation) &&
            activation.ValueKind == JsonValueKind.True)
        {
            Fail($"Function '{name}' requires user activation.", out diagnostic);
            return CallRoute.Refused;
        }
        return IsImplemented(catalogId, name) ? CallRoute.Local : CallRoute.Agent;
    }

    internal bool IsImplemented(string catalogId, string name) =>
        _hostFunctions.ContainsKey((catalogId, name)) || BasicFunctions.Names.Contains(name);

    // Runs a call whose arguments are already resolved. The caller has classified it as local.
    internal bool TryInvoke(SurfaceSnapshot surface, string dataPath, string catalogId, string name,
        IReadOnlyDictionary<string, JsonElement> args, bool userInitiated, out JsonElement value, out A2uiDiagnostic? diagnostic)
    {
        if (!_hostFunctions.TryGetValue((catalogId, name), out var implementation))
            return BasicFunctions.TryInvoke(name, args, CreateContext(surface, dataPath, catalogId, userInitiated),
                out value, out diagnostic);

        try
        {
            value = implementation(new(catalogId, name, args, surface.Id, userInitiated));
            diagnostic = null;
            return true;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            value = default;
            diagnostic = new("ExecutionError", "", "An error occurred during function execution.");
            return false;
        }
    }
    private FunctionContext CreateContext(SurfaceSnapshot surface, string dataPath, string catalogId, bool userInitiated)
    {
        surface.TryGetScopedData(dataPath, "", out var scope);
        return new(surface.DataModel, scope, catalogId, TemplateInvoker(surface, dataPath, userInitiated), _openUrl);
    }

    private FunctionInvoker TemplateInvoker(SurfaceSnapshot surface, string dataPath, bool userInitiated) =>
        (string? catalogId, string name, IReadOnlyDictionary<string, JsonElement> args, out JsonElement value,
            out A2uiDiagnostic? diagnostic) =>
        {
            value = default;
            if (name.StartsWith('@'))
                return name == "@index"
                    ? TryIndex(dataPath, args, out value, out diagnostic)
                    : Fail($"System function '{name}' is not defined.", out diagnostic);
            if (!TryRoute(surface.Id, catalogId, name, userInitiated, out diagnostic))
                return false;
            return TryInvoke(surface, dataPath, catalogId!, name, args, userInitiated, out value, out diagnostic);
        };

    private bool TryArguments(SurfaceSnapshot surface, string dataPath, JsonElement call, bool userInitiated, int depth,
        out Dictionary<string, JsonElement> args, out A2uiDiagnostic? diagnostic)
    {
        args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        diagnostic = null;
        if (!call.TryGetProperty("args", out var declared) || declared.ValueKind != JsonValueKind.Object)
            return true;
        foreach (var argument in declared.EnumerateObject())
        {
            if (!TryResolve(surface, dataPath, argument.Value, userInitiated, depth, out var resolved, out diagnostic))
                return false;
            args[argument.Name] = resolved;
        }
        return true;
    }

    // Replaces bindings and nested calls with their values. A path with no data resolves to null.
    private bool TryResolve(SurfaceSnapshot surface, string dataPath, JsonElement value, bool userInitiated, int depth,
        out JsonElement resolved, out A2uiDiagnostic? diagnostic)
    {
        resolved = value;
        diagnostic = null;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("@path", out var pointer) &&
            pointer.ValueKind == JsonValueKind.String)
        {
            resolved = surface.TryGetScopedData(dataPath, pointer.GetString()!, out var data) ? data : FunctionValues.Null;
            return true;
        }
        if (IsCall(value))
            return TryCall(surface, dataPath, value, userInitiated, depth + 1, out resolved, out diagnostic);

        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return true;
        if (!TryResolveNode(surface, dataPath, value, userInitiated, depth, out var node, out diagnostic))
            return false;
        resolved = ProtocolSchemas.Parse(node?.ToJsonString() ?? "null");
        return true;
    }

    private bool TryResolveNode(SurfaceSnapshot surface, string dataPath, JsonElement value, bool userInitiated, int depth,
        out JsonNode? node, out A2uiDiagnostic? diagnostic)
    {
        node = null;
        diagnostic = null;
        if (IsBinding(value))
        {
            if (!TryResolve(surface, dataPath, value, userInitiated, depth, out var bound, out diagnostic))
                return false;
            node = JsonNode.Parse(bound.GetRawText());
            return true;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var members = new JsonObject();
                foreach (var member in value.EnumerateObject())
                {
                    var name = member.Name;
                    if (name.StartsWith("@@", StringComparison.Ordinal))
                        name = name[1..];
                    else if (name.StartsWith('@'))
                        return Fail($"Unknown key '{name}'. Write '@{name}' for a literal key.", out diagnostic);
                    if (!TryResolveNode(surface, dataPath, member.Value, userInitiated, depth, out var child, out diagnostic))
                        return false;
                    members[name] = child;
                }
                node = members;
                return true;
            case JsonValueKind.Array:
                var items = new JsonArray();
                foreach (var item in value.EnumerateArray())
                {
                    if (!TryResolveNode(surface, dataPath, item, userInitiated, depth, out var child, out diagnostic))
                        return false;
                    items.Add(child);
                }
                node = items;
                return true;
            default:
                node = JsonNode.Parse(value.GetRawText());
                return true;
        }
    }

    private static bool IsCall(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("@call", out _);

    private static bool IsBinding(JsonElement value) =>
        IsCall(value) || value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("@path", out var pointer) && pointer.ValueKind == JsonValueKind.String;

    private static bool Fail(string message, out A2uiDiagnostic? diagnostic)
    {
        diagnostic = FunctionValues.ExpressionError(message);
        return false;
    }
}
