using System.Text;
using System.Text.Json;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>Selects the catalogs available to an A2UI 1.0 request.</summary>
public static class CatalogNegotiation
{
    /// <summary>Reads A2UI 1.0 renderer capabilities from request metadata.</summary>
    /// <param name="metadata">The host's metadata object, which may be null or undefined.</param>
    /// <param name="capabilities">The owned version-keyed capabilities, or an undefined element when absent.</param>
    /// <param name="diagnostic">The reason the metadata was rejected, if any.</param>
    /// <returns>Whether the metadata was read successfully.</returns>
    public static bool TryReadCapabilities(JsonElement metadata, out JsonElement capabilities, out A2uiDiagnostic? diagnostic)
    {
        const string key = "a2uiRendererCapabilities";
        capabilities = default;
        diagnostic = null;
        if (metadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            diagnostic = new("CatalogError", "", "Expected a request metadata object.");
            return false;
        }
        if (!metadata.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
            return true;
        if (!WireMetadata.TryDecode(Encode(value), WireMetadataKind.RendererCapabilities, out var decoded, out var error))
        {
            diagnostic = new("CatalogError", "/" + key + error?.Path, error?.Message ?? "Invalid renderer capabilities.");
            return false;
        }
        var entry = decoded!.Value.GetProperty(WireMessage.Version);
        if (entry.EnumerateObject().Any(property => property.Name is not ("supportedCatalogIds" or "inlineCatalogs")))
        {
            diagnostic = new("CatalogError", "/" + key + "/" + WireMessage.Version, "Unrecognized renderer capability.");
            return false;
        }
        if (entry.TryGetProperty("inlineCatalogs", out var inline) && inline.EnumerateArray().Any(catalog =>
                !catalog.TryGetProperty("protocolVersion", out var protocol) ||
                protocol.ValueKind != JsonValueKind.String || !protocol.ValueEquals(WireMessage.Version)))
        {
            diagnostic = new("CatalogError", "/" + key + "/" + WireMessage.Version + "/inlineCatalogs",
                "Inline catalog protocol version must match renderer capabilities.");
            return false;
        }
        capabilities = decoded.Value;
        return true;
    }

    /// <summary>Resolves a request-local catalog registry from renderer capabilities.</summary>
    /// <param name="registered">The host's configured catalog registry.</param>
    /// <param name="capabilities">The renderer capabilities JSON, or an undefined element when absent.</param>
    /// <param name="acceptInlineCatalogs">Whether validated inline catalogs may be selected.</param>
    /// <param name="active">The request-local registry on success.</param>
    /// <param name="diagnostic">The reason negotiation failed, if any.</param>
    /// <returns>Whether the catalogs were resolved.</returns>
    public static bool TryResolve(CatalogRegistry registered, JsonElement capabilities, bool acceptInlineCatalogs,
        out CatalogRegistry? active, out A2uiDiagnostic? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(registered);
        active = null;
        diagnostic = null;
        var selected = new CatalogRegistry(includeBasicCatalog: false);
        if (capabilities.ValueKind == JsonValueKind.Undefined)
        {
            foreach (var id in registered.CatalogIds.Order(StringComparer.Ordinal))
            {
                registered.TryGetDefinition(id, out var definition);
                if (!selected.TryRegister(Encode(definition), out diagnostic))
                    return false;
            }
            active = selected;
            return true;
        }

        if (capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty(WireMessage.Version, out var version) || version.ValueKind != JsonValueKind.Object ||
            !version.TryGetProperty("supportedCatalogIds", out var ids) || ids.ValueKind != JsonValueKind.Array ||
            ids.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String))
        {
            diagnostic = new("ValidationError", "/1.0", "Expected renderer capabilities with a supported catalog ID array.");
            return false;
        }

        foreach (var id in ids.EnumerateArray().Select(value => value.GetString()!).Distinct(StringComparer.Ordinal))
            if (registered.TryGetDefinition(id, out var definition) && !selected.TryRegister(Encode(definition), out diagnostic))
                return false;

        if (version.TryGetProperty("inlineCatalogs", out var inline) &&
            !TryIncludeInline(inline, registered, selected, acceptInlineCatalogs, out diagnostic))
            return false;

        if (selected.CatalogIds.Count == 0)
        {
            diagnostic = new("CatalogError", "/1.0", "The renderer and agent have no catalog in common.");
            return false;
        }

        active = selected;
        return true;
    }

    private static bool TryIncludeInline(JsonElement inline, CatalogRegistry registered, CatalogRegistry selected,
        bool acceptInlineCatalogs, out A2uiDiagnostic? diagnostic)
    {
        const string path = "/1.0/inlineCatalogs";
        diagnostic = null;
        if (inline.ValueKind != JsonValueKind.Array)
        {
            diagnostic = new("CatalogError", path, "Inline catalogs must be an array.");
            return false;
        }
        var validated = new CatalogRegistry(includeBasicCatalog: false);
        foreach (var entry in inline.EnumerateArray())
        {
            if (!validated.TryRegisterInline(Encode(entry), allowInlineCatalogs: true, out var error))
            {
                diagnostic = new("CatalogError", path + error?.Path, error?.Message ?? "Invalid inline catalog.");
                return false;
            }
        }
        if (!acceptInlineCatalogs)
            return true;

        foreach (var id in validated.CatalogIds)
        {
            if (registered.TryGetDefinition(id, out _))
            {
                diagnostic = new("CatalogError", path, "An inline catalog reuses a registered catalog ID.");
                return false;
            }
            validated.TryGetDefinition(id, out var definition);
            if (!selected.TryRegister(Encode(definition), out diagnostic))
                return false;
        }
        return true;
    }

    private static byte[] Encode(JsonElement value) => Encoding.UTF8.GetBytes(value.GetRawText());
}
