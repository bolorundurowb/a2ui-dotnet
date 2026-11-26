using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>Decides what the expander does with a macro call that has bad arguments.</summary>
public enum MacroErrorMode
{
    /// <summary>Keep the unexpanded component in the output and continue. Other macro calls are still expanded.</summary>
    Passthrough,
    /// <summary>Stop and report a diagnostic.</summary>
    Raise
}

/// <summary>Limits and error handling for <see cref="MacroExpander"/>.</summary>
public sealed record MacroExpanderOptions
{
    /// <summary>The deepest level of nested macro expansion. A deeper expansion fails with <c>RecursionError</c>. The default is 32.</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>The most components that one call to <see cref="MacroExpander.Expand"/> may add. The default is 10,000.</summary>
    public int MaxComponents { get; init; } = 10_000;

    /// <summary>
    /// How to treat a macro call whose arguments are not acceptable. Errors that make the expansion unsafe always stop it. They include
    /// runaway recursion, an unknown or ambiguous catalog and a macro that emits the reserved <c>Surface</c> component.
    /// The default is <see cref="MacroErrorMode.Passthrough"/>.
    /// </summary>
    public MacroErrorMode ErrorMode { get; init; } = MacroErrorMode.Passthrough;
}

/// <summary>
/// Expands macro components in A2UI 1.0 agent messages into the catalog components they stand for. Expansion uses the catalog of
/// each component, namespaces the generated IDs and keeps message order. Validate the result with <see cref="RendererState.Prepare(IEnumerable{WireMessage}, out RendererTurn?, CancellationToken)"/>.
/// </summary>
public sealed partial class MacroExpander
{
    /// <summary>The ID of the default macro catalog, which owns every macro that names no catalog.</summary>
    public const string DefaultCatalogId = "https://a2ui.org/catalogs/macros";

    private static readonly Lazy<CatalogRegistry> BuiltInBasic = new(() => new CatalogRegistry());

    private readonly CatalogRegistry _catalogs;
    private readonly MacroDefinition[] _macros;
    private readonly Dictionary<string, List<MacroDefinition>> _byName;
    private readonly MacroExpanderOptions _options;

    private MacroExpander(CatalogRegistry catalogs, MacroDefinition[] macros, MacroExpanderOptions options)
    {
        _catalogs = catalogs;
        _macros = macros;
        _options = options;
        _byName = new(StringComparer.Ordinal);
        foreach (var macro in macros)
        {
            if (!_byName.TryGetValue(macro.Name, out var group))
                _byName[macro.Name] = group = [];
            group.Add(macro);
        }
    }

    /// <summary>Creates an expander after checking the macro definitions.</summary>
    /// <param name="macros">The macros. A name may repeat only across different catalogs. Names must be UAX #31 identifiers and must not normalise to <c>Surface</c>.</param>
    /// <param name="catalogs">
    /// The base catalogs of the application. They decide which names belong to a catalog, and which catalog owns a component that names none.
    /// The registry must not change while the expander is in use.
    /// </param>
    /// <param name="expander">The expander on success.</param>
    /// <param name="options">The limits and error handling, or <see langword="null"/> for the defaults.</param>
    /// <returns>A <c>ValidationError</c> diagnostic for an invalid macro, otherwise <see langword="null"/>.</returns>
    public static A2uiDiagnostic? Create(IEnumerable<MacroDefinition> macros, CatalogRegistry catalogs,
        out MacroExpander? expander, MacroExpanderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(macros);
        ArgumentNullException.ThrowIfNull(catalogs);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxComponents, 1);
        expander = null;
        var list = macros.ToArray();
        var seen = new HashSet<(string?, string)>();
        foreach (var macro in list)
        {
            if (Check(macro) is { } problem)
                return problem;
            if (!seen.Add((macro.CatalogId, macro.Name)))
                return Invalid(macro, "Macro is defined more than once in the same catalog.");
        }

        expander = new(catalogs, list, options);
        return null;
    }

    private static A2uiDiagnostic? Check(MacroDefinition macro)
    {
        if (macro.ProtocolVersion != WireMessage.Version)
            return Invalid(macro, $"Macro targets protocol version {macro.ProtocolVersion}. Only {WireMessage.Version} is supported.");
        if (macro.CatalogId is { Length: 0 })
            return Invalid(macro, "The catalog ID must not be empty.");
        if (!UnicodeIdentifiers.IsIdentifier(macro.Name))
            return Invalid(macro, "The name must be a UAX #31 identifier.");
        // The reserved name applies after compatibility normalisation, so a full-width spelling is also rejected.
        if (macro.Name.Normalize(NormalizationForm.FormKC) == "Surface")
            return Invalid(macro, "Surface is reserved.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in macro.Parameters)
            if (string.IsNullOrEmpty(parameter.Name) || !names.Add(parameter.Name) ||
                parameter.Name is "component" or "id" || parameter.Name.StartsWith('@'))
                return Invalid(macro, $"Parameter name {parameter.Name} is invalid or repeated.");
        return null;
    }

    private static A2uiDiagnostic Invalid(MacroDefinition macro, string message) =>
        new("ValidationError", "/macros/" + macro.Name, message);

    /// <summary>Expands the macro components in ordered agent messages.</summary>
    /// <param name="messages">
    /// Messages from the agent. Components that name no macro are kept as they are. A macro component is replaced in place by its expansion,
    /// whose root component receives the ID of the call and follows the components it refers to.
    /// </param>
    /// <param name="expanded">The messages in the same order. Messages that need no change are returned as they are.</param>
    /// <param name="surfaceCatalogs">
    /// The default catalog of surfaces that were created earlier, by surface ID. A <see langword="null"/> value is a surface created without a
    /// default catalog. Surfaces that are created in <paramref name="messages"/> replace these.
    /// </param>
    /// <returns>The first diagnostic, or <see langword="null"/> on success.</returns>
    public A2uiDiagnostic? Expand(IEnumerable<WireMessage> messages, out IReadOnlyList<WireMessage> expanded,
        IReadOnlyDictionary<string, string?>? surfaceCatalogs = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        expanded = Array.Empty<WireMessage>();
        var run = new Expansion(this, surfaceCatalogs);
        var output = new List<WireMessage>();
        try
        {
            foreach (var message in messages)
                output.Add(run.Process(message));
        }
        catch (MacroFailure failure)
        {
            return failure.Diagnostic;
        }

        expanded = output.AsReadOnly();
        return null;
    }

    private bool Defines(string catalogId, string component) =>
        _catalogs.TryGetDefinition(catalogId, out var definition) &&
        definition.TryGetProperty("components", out var components) && components.TryGetProperty(component, out _);

    private bool AnyDefines(string component) => _catalogs.CatalogIds.Any(id => Defines(id, component));

    private bool IsRegistered(string catalogId) => _catalogs.TryGetDefinition(catalogId, out _);

    // The catalog that a component without an explicit catalog resolves to, in the order that decides a macro call.
    private MacroDefinition? Resolve(string name, string? explicitCatalog, MacroDefinition? enclosing, SurfaceInfo surface)
    {
        if (!_byName.TryGetValue(name, out var candidates))
            return null;
        var current = explicitCatalog;
        // A node that a scoped macro emits belongs to that macro's catalog, unless the surface already provides the name.
        if (current is null && enclosing?.CatalogId is { } scope && !surface.IsForeign(this) && !surface.Provides(this, name))
            current = scope;
        current ??= surface.Declared;
        foreach (var macro in candidates)
        {
            if (macro.CatalogId is not null)
            {
                if (macro.CatalogId == current)
                    return macro;
                continue;
            }

            if (current == DefaultCatalogId)
                return macro;
            // A macro never replaces a primitive that the catalog in force already defines.
            var collides = current is null ? AnyDefines(name) : Defines(current, name);
            if (!collides)
                return macro;
        }

        return null;
    }

    // Finds the schema that declares a component, falling back to the built-in Basic Catalog for unregistered owners.
    private bool TryGetComponentSchema(string? owner, string name, out JsonElement schema, out JsonElement document)
    {
        schema = default;
        document = default;
        if (owner is not null && _catalogs.TryGetDefinition(owner, out document))
            return document.TryGetProperty("components", out var registered) && registered.TryGetProperty(name, out schema);
        return BuiltInBasic.Value.TryGetDefinition(CatalogRegistry.BasicCatalogId, out document) &&
            document.TryGetProperty("components", out var basic) && basic.TryGetProperty(name, out schema);
    }

    private bool Declares(string? owner, string name, string property)
    {
        var found = false;
        if (TryGetComponentSchema(owner, name, out var schema, out var document))
            VisitProperties(schema, document, 0, (key, _) => found |= key == property);
        return found;
    }

    private void ApplyDefaults(JsonObject component, string? owner, string name)
    {
        if (!TryGetComponentSchema(owner, name, out var schema, out var document))
            return;
        var defaults = new List<(string, JsonElement)>();
        VisitProperties(schema, document, 0, (key, property) =>
        {
            if (property.ValueKind == JsonValueKind.Object && property.TryGetProperty("default", out var value))
                defaults.Add((key, value));
        });
        foreach (var (key, value) in defaults)
            if (!component.ContainsKey(key))
                component[key] = JsonNode.Parse(value.GetRawText());
    }

    private static void VisitProperties(JsonElement schema, JsonElement document, int depth, Action<string, JsonElement> visit)
    {
        if (depth > 8 || schema.ValueKind != JsonValueKind.Object)
            return;
        if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String &&
            reference.GetString()!.StartsWith('#') &&
            SchemaValidator.TryResolvePointer(document, reference.GetString()![1..], out var target))
            VisitProperties(target, document, depth + 1, visit);
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            foreach (var property in properties.EnumerateObject())
                visit(property.Name, property.Value);
        if (schema.TryGetProperty("allOf", out var branches) && branches.ValueKind == JsonValueKind.Array)
            foreach (var branch in branches.EnumerateArray())
                VisitProperties(branch, document, depth + 1, visit);
    }

    // The default catalog of one surface, as far as the messages seen so far reveal it.
    private sealed class SurfaceInfo
    {
        private SurfaceInfo(SurfaceKind kind, string? declared, string? effective) =>
            (Kind, Declared, Effective) = (kind, declared, effective);

        internal static SurfaceInfo Untracked { get; } = new(SurfaceKind.Untracked, null, null);

        internal SurfaceKind Kind { get; }

        // The catalog the agent named. It selects macros. It may be the default macro catalog.
        internal string? Declared { get; }

        // The catalog that the renderer will use. It owns components that name no catalog.
        internal string? Effective { get; }

        internal static SurfaceInfo Tracked(string? declared, string? effective) =>
            effective is null ? new(SurfaceKind.Defaultless, declared, null) : new(SurfaceKind.Tracked, declared, effective);

        // A default catalog that the application did not register cannot be checked, so its components stay as they are.
        internal bool IsForeign(MacroExpander owner) => Kind == SurfaceKind.Tracked && !owner.IsRegistered(Effective!);

        internal bool Provides(MacroExpander owner, string name) =>
            Kind == SurfaceKind.Tracked ? owner.Defines(Effective!, name) : owner.AnyDefines(name);
    }

    private enum SurfaceKind
    {
        Untracked,
        Defaultless,
        Tracked
    }
}
