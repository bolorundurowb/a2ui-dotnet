using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

namespace A2UI.Agent;

public sealed partial class MacroExpander
{
    /// <summary>Writes the macros as a standalone catalog that a model can use to call them.</summary>
    /// <param name="catalogId">
    /// The ID of the catalog, or <see langword="null"/> for <see cref="DefaultCatalogId"/>. Macros that name no catalog belong to it, and
    /// macros of other catalogs are left out.
    /// </param>
    /// <param name="definition">The catalog definition as JSON on success. It is accepted by <see cref="CatalogRegistry.TryRegister(ReadOnlyMemory{byte}, out A2uiDiagnostic?)"/>.</param>
    /// <returns>A diagnostic when Core rejects the generated catalog, otherwise <see langword="null"/>.</returns>
    public A2uiDiagnostic? CreateCatalog(string? catalogId, out string definition)
    {
        catalogId ??= DefaultCatalogId;
        var components = new JsonObject();
        foreach (var macro in _macros.Where(macro => macro.CatalogId is null || macro.CatalogId == catalogId))
            components[macro.Name] = ComponentSchema(macro);
        var catalog = NewCatalog(catalogId, components);
        catalog["$defs"]!["anyComponent"] = AnyComponent(components.Select(entry => entry.Key));
        return Finish(catalog, out definition);
    }

    /// <summary>
    /// Adds the macros to a registered catalog. A model that is shown the result can call the macros as well as the base components.
    /// </summary>
    /// <param name="baseCatalogId">The ID of a catalog in the registry that the expander was created with.</param>
    /// <param name="catalogId">The ID of the new catalog.</param>
    /// <param name="passthroughComponents">
    /// The base components to keep, or <see langword="null"/> to keep them all. Macros are always kept.
    /// </param>
    /// <param name="definition">The catalog definition as JSON on success.</param>
    /// <returns>
    /// A <c>CatalogError</c> diagnostic when the base catalog is unknown or a macro has the name of a base component, a <c>ValidationError</c>
    /// diagnostic when the catalog targets another protocol version, or the diagnostic from Core.
    /// </returns>
    public A2uiDiagnostic? AugmentCatalog(string baseCatalogId, string catalogId, IEnumerable<string>? passthroughComponents,
        out string definition)
    {
        ArgumentNullException.ThrowIfNull(baseCatalogId);
        ArgumentException.ThrowIfNullOrEmpty(catalogId);
        definition = string.Empty;
        if (!_catalogs.TryGetDefinition(baseCatalogId, out var source))
            return new("CatalogError", "/baseCatalogId", "The base catalog is not registered.");
        var catalog = (JsonObject)JsonNode.Parse(source.GetRawText())!;
        if (catalog["protocolVersion"]?.GetValue<string>() != WireMessage.Version)
            return new("ValidationError", "/protocolVersion", $"Macros target protocol version {WireMessage.Version}.");

        var components = catalog["components"] as JsonObject ?? [];
        if (passthroughComponents is not null)
        {
            var keep = passthroughComponents.ToHashSet(StringComparer.Ordinal);
            foreach (var name in components.Select(entry => entry.Key).Where(name => !keep.Contains(name)).ToArray())
                components.Remove(name);
        }

        foreach (var macro in _macros)
        {
            if (components.ContainsKey(macro.Name))
                return new("CatalogError", "/macros/" + macro.Name, "The macro has the name of a component in the base catalog.");
            components[macro.Name] = ComponentSchema(macro);
        }

        catalog["catalogId"] = catalogId;
        catalog["components"] = components;
        foreach (var key in new[] { "$id", "title", "description" })
            catalog.Remove(key);
        var defs = catalog["$defs"] as JsonObject ?? [];
        defs["anyComponent"] = AnyComponent(components.Select(entry => entry.Key));
        catalog["$defs"] = defs;
        return Finish(catalog, out definition);
    }

    private static JsonObject NewCatalog(string catalogId, JsonObject components) => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["protocolVersion"] = WireMessage.Version,
        ["catalogId"] = catalogId,
        ["components"] = components,
        ["$defs"] = new JsonObject { ["anyFunction"] = new JsonObject { ["not"] = new JsonObject() } }
    };

    private static JsonObject AnyComponent(IEnumerable<string> names)
    {
        var references = new JsonArray();
        foreach (var name in names)
            references.Add((JsonNode)new JsonObject { ["$ref"] = "#/components/" + name });
        return references.Count == 0
            ? new JsonObject { ["not"] = new JsonObject() }
            : new JsonObject
            {
                ["oneOf"] = references,
                ["discriminator"] = new JsonObject { ["propertyName"] = "component" }
            };
    }

    // The catalog is checked by registering it in a scratch registry, so the output is a catalog that Core accepts.
    private static A2uiDiagnostic? Finish(JsonObject catalog, out string definition)
    {
        definition = catalog.ToJsonString();
        return new CatalogRegistry(includeBasicCatalog: false).TryRegister(Encoding.UTF8.GetBytes(definition), out var problem)
            ? null
            : problem;
    }

    private static JsonObject ComponentSchema(MacroDefinition macro)
    {
        var properties = new JsonObject
        {
            ["component"] = new JsonObject { ["const"] = macro.Name },
            ["id"] = CommonType("ComponentId")
        };
        var required = new JsonArray("component", "id");
        foreach (var parameter in macro.Parameters)
        {
            properties[parameter.Name] = ParameterSchema(parameter);
            if (parameter.Required && parameter.DefaultValue is null)
                required.Add((JsonNode?)JsonValue.Create(parameter.Name));
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
        if (macro.Description is not null)
            schema["description"] = macro.Description;
        return schema;
    }

    private static JsonObject ParameterSchema(MacroParameter parameter)
    {
        switch (parameter.Type)
        {
            case MacroParameterType.String:
                var text = new JsonObject { ["type"] = "string" };
                if (parameter.AllowedValues is { } allowed)
                    text["enum"] = new JsonArray(allowed.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
                return text;
            case MacroParameterType.Integer:
                return new JsonObject { ["type"] = "integer" };
            case MacroParameterType.Number:
                return new JsonObject { ["type"] = "number" };
            case MacroParameterType.Boolean:
                return new JsonObject { ["type"] = "boolean" };
            case MacroParameterType.CheckRuleList:
                return new JsonObject { ["type"] = "array", ["items"] = CommonType("CheckRule") };
            default:
                return CommonType(parameter.Type switch
                {
                    MacroParameterType.Component => "Child",
                    MacroParameterType.ComponentList or MacroParameterType.ChildList => "ChildList",
                    MacroParameterType.Action => "Action",
                    MacroParameterType.DynamicString => "DynamicString",
                    MacroParameterType.DynamicNumber => "DynamicNumber",
                    MacroParameterType.DynamicBoolean => "DynamicBoolean",
                    MacroParameterType.DynamicStringList => "DynamicStringList",
                    MacroParameterType.DynamicValue => "DynamicValue",
                    _ => "AccessibilityAttributes"
                });
        }
    }

    private static JsonObject CommonType(string name) => new() { ["$ref"] = "common_types.json#/$defs/" + name };
}
