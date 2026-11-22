using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2UI.Core;

/// <summary>An offline catalog registry. Configure the registry before passing it to a session.</summary>
public sealed class CatalogRegistry
{
    /// <summary>The canonical ID of the pinned A2UI 1.0 Basic Catalog.</summary>
    public const string BasicCatalogId = "https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json";

    private readonly Dictionary<string, Catalog> _catalogs = new(StringComparer.Ordinal);

    /// <summary>Creates an offline catalog registry.</summary>
    /// <param name="includeBasicCatalog">Whether to register the pinned Basic Catalog initially.</param>
    public CatalogRegistry(bool includeBasicCatalog = true)
    {
        if (includeBasicCatalog)
            Add(ProtocolSchemas.Read("catalog.json"));
    }

    /// <summary>Gets a live view of the currently registered catalog identifiers.</summary>
    public IReadOnlyCollection<string> CatalogIds => _catalogs.Keys;

    /// <summary>Registers an application-supplied catalog. Callers serialize registry changes and validation.</summary>
    /// <param name="utf8">The complete UTF-8 catalog definition.</param>
    /// <param name="diagnostic">The reason registration failed, if any.</param>
    /// <returns>Whether the catalog was registered. Existing entries are preserved on failure.</returns>
    public bool TryRegister(ReadOnlyMemory<byte> utf8, out A2uiDiagnostic? diagnostic)
    {
        if (!WireJson.TryRead(utf8, out var definition, out diagnostic))
            return false;
        diagnostic = CheckDefinition(definition);
        if (diagnostic is not null)
            return false;
        Add(definition);
        return true;
    }

    /// <summary>Registers an inline catalog after checking caller opt-in and the 256 KiB limit.</summary>
    /// <param name="utf8">The complete UTF-8 catalog definition.</param>
    /// <param name="allowInlineCatalogs">Whether the host permits inline catalogs.</param>
    /// <param name="diagnostic">The reason registration failed, if any.</param>
    /// <returns>Whether the inline catalog was registered.</returns>
    public bool TryRegisterInline(ReadOnlyMemory<byte> utf8, bool allowInlineCatalogs, out A2uiDiagnostic? diagnostic)
    {
        if (allowInlineCatalogs && utf8.Length <= 256 * 1024)
            return TryRegister(utf8, out diagnostic);
        diagnostic = new("inlineCatalogPolicy", "", "Inline catalog is disabled or exceeds the size limit.");
        return false;
    }

    /// <summary>Registers a new catalog containing selected entries from a registered catalog.</summary>
    /// <param name="sourceId">The identifier of the registered source.</param>
    /// <param name="catalogId">The identifier for the new catalog.</param>
    /// <param name="components">Component names to retain, or null to retain all components.</param>
    /// <param name="functions">Function names to retain, or null to retain all functions.</param>
    /// <param name="instructions">Optional instructions for the derived catalog.</param>
    /// <param name="diagnostic">The reason the derivation failed, if any.</param>
    /// <returns>Whether the derived catalog was registered.</returns>
    public bool TryDerive(string sourceId, string catalogId, IEnumerable<string>? components,
        IEnumerable<string>? functions, string? instructions, out A2uiDiagnostic? diagnostic)
    {
        if (!TryGetDefinition(sourceId, out var source))
        {
            diagnostic = new("unknownCatalog", "/sourceId", "Source catalog is unavailable.");
            return false;
        }

        var derived = JsonNode.Parse(source.GetRawText())!.AsObject();
        derived["catalogId"] = catalogId;
        foreach (var key in new[] { "$id", "title", "description", "instructions" })
            derived.Remove(key);
        if (instructions is not null)
            derived["instructions"] = instructions;

        if (!SelectEntries(derived, "components", components, out diagnostic) ||
            !SelectEntries(derived, "functions", functions, out diagnostic))
            return false;
        return TryRegister(System.Text.Encoding.UTF8.GetBytes(derived.ToJsonString()), out diagnostic);
    }

    private static bool SelectEntries(JsonObject definition, string group, IEnumerable<string>? names,
        out A2uiDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (names is null)
            return true;
        var selected = new JsonObject();
        foreach (var name in names)
        {
            if (definition[group]?[name] is not { } item)
            {
                diagnostic = new("unknownCatalogItem", WireJson.Path(WireJson.Path("", group), name),
                    "The source catalog does not contain the selected entry.");
                return false;
            }
            selected[name] = item.DeepClone();
        }
        definition[group] = selected;
        if (definition["$defs"] is JsonObject definitions)
        {
            var references = new JsonArray();
            foreach (var entry in selected)
                references.Add((JsonNode)new JsonObject { ["$ref"] = "#" + WireJson.Path(WireJson.Path("", group), entry.Key) });
            definitions[group == "components" ? "anyComponent" : "anyFunction"] = references.Count == 0
                ? JsonValue.Create(false)
                : new JsonObject { ["oneOf"] = references };
        }
        return true;
    }

    /// <summary>Removes a catalog from subsequent lookup and validation.</summary>
    /// <param name="catalogId">The catalog identifier.</param>
    /// <returns>Whether a registration was removed.</returns>
    public bool Remove(string catalogId) => _catalogs.Remove(catalogId);

    /// <summary>Gets the registered catalog as owned JSON.</summary>
    /// <param name="catalogId">The catalog identifier.</param>
    /// <param name="definition">The registered definition on success.</param>
    /// <returns>Whether the catalog is registered.</returns>
    public bool TryGetDefinition(string catalogId, out JsonElement definition)
    {
        definition = default;
        if (!_catalogs.TryGetValue(catalogId, out var catalog))
            return false;
        definition = catalog.Definition;
        return true;
    }

    /// <summary>Validates a component against its explicit catalog or the surface default.</summary>
    /// <param name="component">The wire-level component object.</param>
    /// <param name="defaultCatalogId">The surface catalog, or null when the surface has none.</param>
    /// <returns>The first validation failure, or null on success.</returns>
    public A2uiDiagnostic? ValidateComponent(JsonElement component, string? defaultCatalogId) =>
        ValidateItem(component, defaultCatalogId, "components", "component", "@catalogId");

    /// <summary>Validates a function call against its explicit catalog or the surface default.</summary>
    /// <param name="function">The wire-level function call object.</param>
    /// <param name="defaultCatalogId">The surface catalog, or null when the surface has none.</param>
    /// <returns>The first validation failure, or null on success.</returns>
    public A2uiDiagnostic? ValidateFunction(JsonElement function, string? defaultCatalogId) =>
        ValidateItem(function, defaultCatalogId, "functions", "@call", "catalogId");

    internal bool TryGetComponentSchema(JsonElement component, string? defaultCatalogId,
        out JsonElement document, out JsonElement schema)
    {
        schema = default;
        var id = component.TryGetProperty("@catalogId", out var explicitId) ? explicitId.GetString() : defaultCatalogId;
        document = default;
        return id is not null && TryGetDefinition(id, out document) &&
            document.TryGetProperty("components", out var components) &&
            components.TryGetProperty(component.GetProperty("component").GetString()!, out schema);
    }

    internal bool MatchesComponentBranch(JsonElement instance, JsonElement schema, JsonElement document,
        string? surfaceCatalogId) =>
        _catalogs[document.GetProperty("catalogId").GetString()!].Validator
            .Validate(instance, schema, document, surfaceCatalogId) is null;

    private void Add(JsonElement definition)
    {
        var id = definition.GetProperty("catalogId").GetString()!;
        var validator = new SchemaValidator(ProtocolSchemas.Resolve, resolveFunction: ResolveFunction);
        _catalogs.Add(id, new(definition, validator));
    }

    private A2uiDiagnostic? ValidateItem(JsonElement value, string? defaultCatalogId, string group, string nameKey, string catalogKey)
    {
        if (!WireJson.IsObject(value) || !value.TryGetProperty(nameKey, out var name) || name.ValueKind != JsonValueKind.String)
            return new("invalidItemName", WireJson.Path("", nameKey), "Expected a catalog item name.");
        var id = defaultCatalogId;
        if (value.TryGetProperty(catalogKey, out var explicitId))
        {
            if (explicitId.ValueKind != JsonValueKind.String)
                return new("invalidType", WireJson.Path("", catalogKey), "Expected a catalog identifier.");
            id = explicitId.GetString();
        }

        if (id is null || !_catalogs.TryGetValue(id, out var catalog))
            return new("unknownCatalog", WireJson.Path("", catalogKey), "Catalog is unavailable.");
        if (!catalog.Definition.TryGetProperty(group, out var items) || !items.TryGetProperty(name.GetString()!, out var schema))
            return new("unknownCatalogItem", WireJson.Path("", nameKey), "Catalog item is unavailable.");

        var common = ProtocolSchemas.Resolve("common_types.json")!.Value;
        return group == "components"
            ? catalog.Validator.ValidateComponent(value, schema, catalog.Definition, common, defaultCatalogId)
            : catalog.Validator.Validate(value, common.GetProperty("$defs").GetProperty("FunctionCall"), common, defaultCatalogId);
    }

    private (JsonElement, JsonElement)? ResolveFunction(JsonElement function, string? defaultCatalogId)
    {
        var id = function.TryGetProperty("catalogId", out var catalogId) ? catalogId.GetString() : defaultCatalogId;
        var name = function.GetProperty("@call").GetString()!;
        if (id is null || !_catalogs.TryGetValue(id, out var catalog) ||
            !catalog.Definition.TryGetProperty("functions", out var functions) || !functions.TryGetProperty(name, out var schema))
            return null;
        return (catalog.Definition, schema);
    }

    private A2uiDiagnostic? CheckDefinition(JsonElement definition)
    {
        if (!WireJson.IsObject(definition) || !definition.TryGetProperty("protocolVersion", out var version) ||
            version.ValueKind != JsonValueKind.String || version.GetString() != WireMessage.Version)
            return new("unsupportedVersion", "/protocolVersion", "Expected A2UI version 1.0.");
        if (!definition.TryGetProperty("catalogId", out var catalogId) || catalogId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(catalogId.GetString()))
            return new("invalidCatalogId", "/catalogId", "Expected a nonempty catalog identifier.");
        var id = catalogId.GetString()!;
        if (definition.TryGetProperty("$id", out var schemaIdentity) &&
            (schemaIdentity.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(schemaIdentity.GetString())))
            return new("invalidCatalogId", "/$id", "Expected a nonempty schema identifier.");
        if (_catalogs.ContainsKey(id))
            return new("duplicateCatalog", "/catalogId", "Catalog is already registered.");

        foreach (var group in new[] { "components", "functions" })
        {
            if (!definition.TryGetProperty(group, out var items))
                continue;
            if (!WireJson.IsObject(items))
                return new("invalidType", WireJson.Path("", group), "Expected a map of catalog schemas.");
            foreach (var item in items.EnumerateObject())
                if (!SchemaValidator.IsIdentifier(item.Name) || !WireJson.IsObject(item.Value))
                    return new("invalidCatalogItem", WireJson.Path(WireJson.Path("", group), item.Name),
                        "Expected an identifier and an object schema.");
        }

        var meta = ProtocolSchemas.Resolve("catalog_definition.json")!.Value;
        var diagnostic = new SchemaValidator(ProtocolSchemas.Resolve).Validate(definition, meta, meta);
        if (diagnostic is not null)
            return diagnostic;
        var schemaId = schemaIdentity.ValueKind == JsonValueKind.String ? schemaIdentity.GetString() : null;
        return CheckReferences(definition, definition, schemaId, "");
    }

    private static A2uiDiagnostic? CheckReferences(JsonElement value, JsonElement document, string? schemaId, string path)
    {
        if (WireJson.IsObject(value))
        {
            foreach (var property in value.EnumerateObject())
            {
                var childPath = WireJson.Path(path, property.Name);
                if (property.NameEquals("$ref"))
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return new("invalidReference", childPath, "Expected a schema reference string.");
                    var parts = property.Value.GetString()!.Split('#', 2);
                    var target = parts[0] is "" || parts[0] == schemaId ? document :
                        ProtocolSchemas.IsCommonTypes(parts[0]) ? ProtocolSchemas.Resolve(parts[0]) : null;
                    if (target is null || !SchemaValidator.TryResolvePointer(target.Value, parts.Length == 2 ? parts[1] : "", out var resolved)
                        || resolved.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
                        return new("unresolvedReference", childPath, "Reference must point to a registered schema.");
                }
                else if (property.Name is not ("default" or "examples" or "const" or "enum" or "metadata") &&
                    CheckReferences(property.Value, document, schemaId, childPath) is { } failure)
                    return failure;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in value.EnumerateArray())
                if (CheckReferences(child, document, schemaId, WireJson.Path(path, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture))) is { } failure)
                    return failure;
        }
        return null;
    }

    private sealed record Catalog(JsonElement Definition, SchemaValidator Validator);
}
