using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

internal static class AgentSmoke
{
    internal static void Run()
    {
        var host = new CatalogRegistry();
        using var capabilities = JsonDocument.Parse("""
            {"1.0":{"supportedCatalogIds":["https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json","unknown"]}}
            """);
        if (!CatalogNegotiation.TryResolve(host, capabilities.RootElement, acceptInlineCatalogs: false,
                out var active, out var error) || active is null ||
            !active.CatalogIds.SequenceEqual(new[] { CatalogRegistry.BasicCatalogId }) ||
            !active.Remove(CatalogRegistry.BasicCatalogId) ||
            !host.TryGetDefinition(CatalogRegistry.BasicCatalogId, out _))
            throw new InvalidOperationException(error?.ToString() ?? "Agent catalog negotiation smoke assertion failed.");
    }
}
