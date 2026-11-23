using System.Buffers;
using System.Security;
using System.Text.Json;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>Loads catalog files from paths configured by the application.</summary>
public static class FileCatalogProvider
{
    /// <summary>Loads, checks, and registers a catalog from a host-controlled file path.</summary>
    /// <param name="registry">The catalog registry to populate.</param>
    /// <param name="path">A file path supplied by the host.</param>
    /// <param name="catalogId">The expected catalog ID, or null to read it from the file.</param>
    /// <param name="protocolVersion">The expected wire version, or null to read it from the file.</param>
    /// <param name="diagnostic">The reason the catalog could not be loaded, if any.</param>
    /// <returns>Whether registration succeeded.</returns>
    /// <remarks>A file with no functions may omit the empty function union from its schema definitions.</remarks>
    public static bool TryRegister(CatalogRegistry registry, string path, string? catalogId, string? protocolVersion,
        out A2uiDiagnostic? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var file = File.OpenRead(path);
            using var catalog = JsonDocument.Parse(file);
            var root = catalog.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostic = new("CatalogError", "", "Expected a catalog object.");
                return false;
            }
            if (!IdentityMatches(root, "catalogId", catalogId, out var addId))
            {
                diagnostic = new("CatalogError", "/catalogId", "Catalog ID is missing or conflicts with the provider.");
                return false;
            }
            if (!IdentityMatches(root, "protocolVersion", protocolVersion, out var addVersion))
            {
                diagnostic = new("CatalogError", "/protocolVersion", "Catalog protocol version is missing or conflicts with the provider.");
                return false;
            }

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                    if (property.NameEquals("$defs") && property.Value.ValueKind == JsonValueKind.Object &&
                        !property.Value.TryGetProperty("anyFunction", out _) && !root.TryGetProperty("functions", out _))
                    {
                        writer.WriteStartObject("$defs");
                        foreach (var definition in property.Value.EnumerateObject())
                            definition.WriteTo(writer);
                        writer.WriteBoolean("anyFunction", false);
                        writer.WriteEndObject();
                    }
                    else
                        property.WriteTo(writer);
                if (addId)
                    writer.WriteString("catalogId", catalogId);
                if (addVersion)
                    writer.WriteString("protocolVersion", protocolVersion);
                writer.WriteEndObject();
            }
            if (registry.TryRegister(buffer.WrittenMemory, out var error))
            {
                diagnostic = null;
                return true;
            }
            diagnostic = new("CatalogError", error?.Path ?? "", error?.Message ?? "Catalog registration failed.");
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            diagnostic = new("CatalogError", "", "Catalog file could not be read as JSON.");
            return false;
        }
    }

    private static bool IdentityMatches(JsonElement catalog, string field, string? expected, out bool add)
    {
        add = !catalog.TryGetProperty(field, out var actual);
        return add ? !string.IsNullOrWhiteSpace(expected) :
            actual.ValueKind == JsonValueKind.String && (expected is null || actual.ValueEquals(expected));
    }
}
