using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

internal static class AgentCases
{
    internal static bool Supports(string path, JsonElement test) =>
        path == "conformance/agent/catalog_resolution.yaml" &&
        test.GetProperty("args").GetProperty("catalogs").EnumerateArray().All(item => !item.TryGetProperty("transformers", out _));

    internal static void Run(JsonElement test, string assets)
    {
        var args = test.GetProperty("args");
        var registry = new CatalogRegistry(includeBasicCatalog: false);
        foreach (var item in args.GetProperty("catalogs").EnumerateArray())
        {
            var path = Path.Combine(assets, "conformance", item.GetProperty("catalog").GetString()!);
            Require(registry.TryRegister(File.ReadAllBytes(path), out var error), error?.ToString() ?? "Registration failed.");
        }

        args.TryGetProperty("renderer_capabilities", out var capabilities);
        var acceptsInline = args.TryGetProperty("accepts_inline_catalogs", out var inline) && inline.GetBoolean();
        var resolved = CatalogNegotiation.TryResolve(registry, capabilities, acceptsInline, out var active, out var diagnostic);
        if (test.TryGetProperty("expect_error", out var expectedError))
        {
            Require(!resolved && diagnostic?.Code == expectedError.GetProperty("category").GetString(),
                diagnostic?.ToString() ?? "Expected negotiation to fail.");
            return;
        }

        Require(resolved && active is not null, diagnostic?.ToString() ?? "Negotiation failed.");
        var expected = test.GetProperty("expect");
        CompareNames(active!.CatalogIds, expected.GetProperty("active_catalog_ids"));
        if (!expected.TryGetProperty("catalogs", out var catalogs))
            return;
        foreach (var catalog in catalogs.EnumerateArray())
        {
            Require(active.TryGetDefinition(catalog.GetProperty("catalog_id").GetString()!, out var definition),
                "Expected catalog is unavailable.");
            foreach (var group in new[] { "components", "functions" })
                if (catalog.TryGetProperty(group, out var names))
                    CompareNames(definition.TryGetProperty(group, out var entries)
                        ? entries.EnumerateObject().Select(entry => entry.Name)
                        : [], names);
        }
    }

    private static void CompareNames(IEnumerable<string> actual, JsonElement expected) =>
        Require(actual.Order(StringComparer.Ordinal).SequenceEqual(
            expected.EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal)), "Catalog names differ.");

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidDataException(description);
    }
}
