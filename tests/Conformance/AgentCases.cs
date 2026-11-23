using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

internal static class AgentCases
{
    internal static bool Supports(string path, JsonElement test) =>
        path == "conformance/agent/renderer_state.yaml" && test.GetProperty("turns")[0].TryGetProperty("payload", out _) ||
        path == "conformance/agent/catalog_resolution.yaml" ||
        path == "conformance/agent/catalog_provider.yaml" ||
        path == "conformance/agent/renderer_capabilities.yaml" &&
        test.GetProperty("args").GetProperty("version").ValueEquals("1.0") ||
        path == "conformance/agent/catalog_transformer.yaml" &&
        !test.GetProperty("name").ValueEquals("test_function_pruning_on_catalog_without_functions");

    internal static void Run(JsonElement test, string assets)
    {
        if (test.GetProperty("action").ValueEquals("renderer_state_turns"))
        {
            RunRendererState(test, assets);
            return;
        }
        var args = test.GetProperty("args");
        if (test.GetProperty("action").ValueEquals("read_renderer_capabilities"))
        {
            args.TryGetProperty("metadata", out var metadata);
            var read = CatalogNegotiation.TryReadCapabilities(metadata, out var capabilitiesResult, out var metadataError);
            if (test.TryGetProperty("expect_error", out var capabilityError))
                Require(!read && metadataError?.Code == capabilityError.GetProperty("category").GetString(),
                    metadataError?.ToString() ?? "Expected invalid metadata.");
            else
            {
                var expectedCapabilities = test.GetProperty("expect");
                Require(read && (expectedCapabilities.ValueKind == JsonValueKind.Null
                    ? capabilitiesResult.ValueKind == JsonValueKind.Undefined
                    : System.Text.Json.Nodes.JsonNode.DeepEquals(
                        System.Text.Json.Nodes.JsonNode.Parse(capabilitiesResult.GetRawText()),
                        System.Text.Json.Nodes.JsonNode.Parse(expectedCapabilities.GetRawText()))),
                    metadataError?.ToString() ?? "Capabilities differ.");
            }
            return;
        }
        var registry = new CatalogRegistry(includeBasicCatalog: false);
        if (test.GetProperty("action").ValueEquals("provide_catalog"))
        {
            var path = Path.Combine(assets, "conformance", args.GetProperty("path").GetString()!);
            var suppliedId = args.TryGetProperty("catalog_id", out var id) ? id.GetString() : null;
            var suppliedVersion = args.TryGetProperty("protocol_version", out var version) ? version.GetString() : null;
            var loaded = FileCatalogProvider.TryRegister(registry, path, suppliedId, suppliedVersion, out var providerError);
            if (test.TryGetProperty("expect_error", out var loadError))
                Require(!loaded && providerError?.Code == loadError.GetProperty("category").GetString(),
                    providerError?.ToString() ?? "Expected provider rejection.");
            else
            {
                Require(loaded, providerError?.ToString() ?? "Catalog provider failed.");
                Require(registry.TryGetDefinition(registry.CatalogIds.Single(), out var definition), "Provided catalog is unavailable.");
                CheckDefinition(definition, test.GetProperty("expect"));
            }
            return;
        }
        if (test.GetProperty("action").ValueEquals("transform_catalog"))
        {
            Register(registry, args, assets);
            Require(registry.TryGetDefinition(registry.CatalogIds.Single(), out var transformed), "Transformed catalog is unavailable.");
            CheckDefinition(transformed, test.GetProperty("expect"));
            return;
        }

        foreach (var item in args.GetProperty("catalogs").EnumerateArray())
            Register(registry, item, assets);

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
            CheckDefinition(definition, catalog);
        }
    }

    private static void Register(CatalogRegistry registry, JsonElement item, string assets)
    {
        var path = Path.Combine(assets, "conformance", item.GetProperty("catalog").GetString()!);
        HashSet<string>? components = null;
        HashSet<string>? functions = null;
        if (item.TryGetProperty("transformers", out var transformers))
            foreach (var transformer in transformers.EnumerateArray())
            {
                if (transformer.TryGetProperty("component_pruning", out var componentList))
                    components = Intersect(components, componentList);
                if (transformer.TryGetProperty("function_pruning", out var functionList))
                    functions = Intersect(functions, functionList);
            }
        Require(registry.TryRegister(File.ReadAllBytes(path), components, functions, out var error),
            error?.ToString() ?? "Registration failed.");
    }

    private static HashSet<string> Intersect(HashSet<string>? previous, JsonElement allowlist)
    {
        var names = allowlist.ValueKind == JsonValueKind.Null
            ? Enumerable.Empty<string>() : allowlist.EnumerateArray().Select(item => item.GetString()!);
        if (previous is null)
            return names.ToHashSet(StringComparer.Ordinal);
        previous.IntersectWith(names);
        return previous;
    }

    private static void CheckDefinition(JsonElement definition, JsonElement expected)
    {
        if (expected.TryGetProperty("catalog_id", out var id))
            Require(definition.GetProperty("catalogId").GetString() == id.GetString(), "Catalog ID differs.");
        if (expected.TryGetProperty("protocol_version", out var version))
            Require(definition.GetProperty("protocolVersion").GetString() == version.GetString(), "Protocol version differs.");
        foreach (var group in new[] { "components", "functions" })
            if (expected.TryGetProperty(group, out var names))
                CompareNames(definition.TryGetProperty(group, out var entries)
                    ? entries.EnumerateObject().Select(entry => entry.Name)
                    : [], names);
    }

    private static void CompareNames(IEnumerable<string> actual, JsonElement expected) =>
        Require(actual.Order(StringComparer.Ordinal).SequenceEqual(
            expected.EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal)), "Catalog names differ.");

    private static void RunRendererState(JsonElement test, string assets)
    {
        var registry = new CatalogRegistry(false);
        foreach (var catalog in test.GetProperty("catalogs").EnumerateArray())
            Require(registry.TryRegister(File.ReadAllBytes(Path.Combine(assets, "conformance", catalog.GetString()!)),
                out var error), error?.ToString() ?? "Catalog registration failed.");

        using var state = new RendererState(registry);
        foreach (var step in test.GetProperty("turns").EnumerateArray())
        {
            var messages = new List<WireMessage>();
            foreach (var envelope in step.GetProperty("payload").EnumerateArray())
            {
                Require(WireMessage.TryDecode(System.Text.Encoding.UTF8.GetBytes(envelope.GetRawText()),
                    WireDirection.AgentToRenderer, out var message, out var decode), decode?.ToString() ?? "Decode failed.");
                messages.Add(message!);
            }

            var diagnostic = state.Prepare(messages, out var candidate);
            if (step.TryGetProperty("expect_error", out var expectedError))
            {
                Require(candidate is null && diagnostic is not null &&
                    diagnostic.Message.Contains(expectedError.GetProperty("message").GetString()!, StringComparison.Ordinal),
                    diagnostic?.ToString() ?? "Expected failed validation.");
                continue;
            }
            Require(candidate is not null && diagnostic is null, diagnostic?.ToString() ?? "Expected a valid turn.");
            using var turn = candidate!;
            Require(turn.NextMessage is null, "Uncommitted output was available.");
            turn.Commit();
            foreach (var message in messages)
            {
                Require(ReferenceEquals(turn.NextMessage, message), "Emission order changed.");
                turn.Acknowledge(message);
            }
            Require(turn.EmittedCount == messages.Count && state.PendingDelivery is null, "Delivery was incomplete.");
            foreach (var item in step.GetProperty("expect_surfaces").EnumerateObject())
            {
                Require(state.TryGetSurface(item.Name, out var snapshot), "Expected live surface.");
                var components = item.Value.GetProperty("components");
                Require(snapshot!.Components.Count == components.EnumerateObject().Count(), "Component count differs.");
                foreach (var component in components.EnumerateObject())
                    foreach (var prop in component.Value.EnumerateObject())
                        Equal(snapshot.Components[component.Name].GetProperty(prop.Name), prop.Value);
                if (item.Value.TryGetProperty("data", out var data))
                    Equal(snapshot.DataModel, data);
            }
        }

        static void Equal(JsonElement actual, JsonElement expected) =>
            Require(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(actual.GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(expected.GetRawText())), "Renderer snapshot differs.");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidDataException(description);
    }
}
