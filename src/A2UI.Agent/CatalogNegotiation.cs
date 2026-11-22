using System.Text;
using System.Text.Json;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>Selects the catalogs available to an A2UI 1.0 request.</summary>
public static class CatalogNegotiation
{
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
