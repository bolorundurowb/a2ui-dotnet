using System.Text.Json;

namespace A2UI.Core;

internal static class ProtocolSchemas
{
    private const string BaseUri = "https://a2ui.org/specification/v1_0/";
    private static readonly Dictionary<string, JsonElement> Schemas = new(StringComparer.Ordinal)
    {
        ["agent_to_renderer.json"] = Read("agent_to_renderer.json"),
        ["renderer_to_agent.json"] = Read("renderer_to_agent.json"),
        ["common_types.json"] = Read("common_types.json"),
        ["agent_capabilities.json"] = Read("agent_capabilities.json"),
        ["renderer_capabilities.json"] = Read("renderer_capabilities.json"),
        ["renderer_data_model.json"] = Read("renderer_data_model.json"),
        ["catalog_definition.json"] = Read("catalog_definition.json")
    };

    internal static JsonElement? Resolve(string reference)
    {
        if (reference == "https://json-schema.org/draft/2020-12/schema")
            return SchemaObjectOrBoolean;
        var name = reference.StartsWith(BaseUri, StringComparison.Ordinal) ? reference[BaseUri.Length..] : reference;
        return Schemas.TryGetValue(name, out var schema) ? schema : null;
    }

    internal static JsonElement Read(string name)
    {
        using var stream = typeof(ProtocolSchemas).Assembly.GetManifestResourceStream("A2UI.Core.Upstream." + name)
            ?? throw new InvalidOperationException("Missing pinned A2UI asset " + name);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    internal static A2uiDiagnostic? ValidateMessage(JsonElement envelope, string kind, WireDirection direction)
    {
        if (direction == WireDirection.AgentToRenderer)
        {
            var document = Schemas["agent_to_renderer.json"];
            var name = char.ToUpperInvariant(kind[0]) + kind[1..] + "Message";
            return WireValidator.Validate(envelope, document.GetProperty("$defs").GetProperty(name), document);
        }

        var renderer = Schemas["renderer_to_agent.json"];
        return WireValidator.Validate(envelope, renderer, renderer);
    }

    internal static A2uiDiagnostic? ValidateMetadata(JsonElement metadata, WireMetadataKind kind)
    {
        var document = Schemas[kind switch
        {
            WireMetadataKind.AgentCapabilities => "agent_capabilities.json",
            WireMetadataKind.RendererCapabilities => "renderer_capabilities.json",
            WireMetadataKind.RendererDataModel => "renderer_data_model.json",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        }];
        return WireValidator.Validate(metadata, document, document);
    }

    internal static JsonElement Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static readonly JsonElement SchemaObjectOrBoolean = Parse("""{"type":["object","boolean"]}""");
    private static readonly SchemaValidator WireValidator = new(Resolve, deferCatalogItems: true);
}
