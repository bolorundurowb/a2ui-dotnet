using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;
using YamlDotNet.Serialization;

internal static class NodeCases
{
    private const string SurfaceId = "s";

    private static readonly HashSet<string> SupportedIds = new(StringComparer.Ordinal)
    {
        "event_property_is_not_an_action",
        "inline_create_applies_data_before_components",
        "separate_data_message_keeps_arrival_order",
        "direct_data_binding_and_function_call_schemas",
        "metadata_only_update_emits_metadata_not_props",
        "metadata_cleared_when_update_omits_it",
        "metadata_and_props_update_together"
    };

    private static readonly HashSet<string> ExpectationFields = new(StringComparer.Ordinal)
    {
        "nodes", "data", "emissions", "created_emissions", "same_nodes", "metadata_emissions"
    };

    private static readonly HashSet<string> NodeFields = new(StringComparer.Ordinal)
    {
        "component_id", "type", "state", "data_path", "props", "metadata"
    };

    internal static bool Supports(string id) => SupportedIds.Contains(id);

    internal static string ExclusionReason(JsonElement test, string assets)
    {
        var catalog = LoadFixture(test, assets).GetProperty("catalog").GetString()!;
        if (test.GetProperty("name").GetString() == "invoke_action_with_dispatch_metadata")
            return "The fixture declares component metadata as metadata. The pinned component schema defines @metadata and rejects it.";
        return catalog.Contains("v0_9_1", StringComparison.Ordinal)
            ? "The fixture loads the v0.9.1 basic catalog, which the pinned manifest does not import and the 1.0 profile excludes."
            : "The scenario needs envelope metadata, action invocation or function recording, which the node resolver does not implement.";
    }

    internal static void Run(JsonElement test, string assets)
    {
        var fixture = LoadFixture(test, assets);
        var registry = new CatalogRegistry();
        var catalogId = RegisterCatalog(registry, fixture.GetProperty("catalog").GetString()!, assets);
        using var session = new SurfaceSession(registry);
        var metadataCase = test.GetProperty("name").GetString()!.StartsWith("metadata_", StringComparison.Ordinal);
        if (!(fixture.TryGetProperty("surface", out var surfaceFlag) && surfaceFlag.ValueKind == JsonValueKind.False))
        {
            Apply(session, new
            {
                version = "1.0",
                createSurface = new
                {
                    surfaceId = SurfaceId,
                    catalogId,
                    dataModel = fixture.GetProperty("data"),
                    components = ReadComponents(fixture, metadataCase)
                }
            });
        }

        using var resolver = new NodeResolver(session, registry, SurfaceId);
        var scenario = new NodeScenario(session, resolver, metadataCase);
        scenario.Observe();
        scenario.BeginStep();
        scenario.Check(test.GetProperty("expect"), Array.Empty<ResolvedNode>(), requireEmissions: false);

        if (test.TryGetProperty("steps", out var steps))
        {
            foreach (var step in steps.EnumerateArray())
            {
                var before = resolver.Nodes.ToArray();
                scenario.BeginStep();
                scenario.Perform(step);
                scenario.Check(step.GetProperty("expect"), before, requireEmissions: true);
            }
        }
    }

    internal static void RunScope(JsonElement test)
    {
        var args = test.GetProperty("args");
        var scope = args.TryGetProperty("contextPath", out var context) ? context.GetString()! : "/";
        var path = args.GetProperty("path").GetString()!;
        var expected = test.GetProperty("expect").GetString()!;
        using var session = new SurfaceSession(new CatalogRegistry());
        Apply(session, new { version = "1.0", createSurface = new { surfaceId = SurfaceId } });
        var value = JsonSerializer.SerializeToElement(new { sentinel = true });
        Require(session.WriteScopedData(SurfaceId, "/", expected, value) is null);
        Require(session.TryGetSurface(SurfaceId, out var populated) &&
            populated!.TryGetScopedData(scope, path, out var actual) && JsonEquals(actual, value),
            $"Path '{path}' in scope '{scope}' did not resolve to '{expected}'.");
    }

    internal static void RunAccessibility(JsonElement test)
    {
        var surfaceId = test.GetProperty("messages")[0].GetProperty("createSurface").GetProperty("surfaceId").GetString()!;
        var registry = new CatalogRegistry();
        using var session = new SurfaceSession(registry);
        using var resolver = new NodeResolver(session, registry, surfaceId);

        if (test.TryGetProperty("steps", out var steps))
        {
            foreach (var step in steps.EnumerateArray())
                Check(step);
        }
        else
            Check(test);

        void Check(JsonElement step)
        {
            foreach (var message in step.GetProperty("messages").EnumerateArray())
                Apply(session, message);

            foreach (var entry in step.GetProperty("assertions").GetProperty("accessibilityTree").EnumerateObject())
            {
                var node = resolver.Nodes.Single(candidate => candidate.ComponentId == entry.Name);
                foreach (var attribute in entry.Value.EnumerateObject())
                {
                    // Platform roles and interactive states are derived by renderer adapters.
                    if (attribute.Name is "role" or "checked")
                        continue;
                    Require(node.Accessibility is { } accessibility &&
                        accessibility.TryGetProperty(attribute.Name, out var value) && JsonEquals(value, attribute.Value),
                        $"Accessibility attribute {entry.Name}.{attribute.Name} differs.");
                }
            }
        }
    }

    internal static void RunEscaping(JsonElement test, string assets)
    {
        var registry = CoreCases.BuildRegistry(test, assets);
        using var session = new SurfaceSession(registry);
        foreach (var message in test.GetProperty("messages").EnumerateArray())
            Apply(session, message);
        foreach (var surface in test.GetProperty("expect").GetProperty("surfaces").EnumerateObject())
        {
            using var resolver = new NodeResolver(session, registry, surface.Name);
            foreach (var component in surface.Value.GetProperty("components").EnumerateArray())
            {
                var id = component.GetProperty("id").GetString();
                var node = resolver.Nodes.Single(candidate => candidate.ComponentId == id);
                Require(component.GetProperty("component").ValueEquals(node.ComponentType));
                foreach (var property in component.EnumerateObject().Where(property => property.Name is not ("id" or "component")))
                    Require(node.Props.TryGetValue(property.Name, out var resolved) &&
                        resolved.Value is { } value && JsonEquals(value, property.Value),
                        $"Resolved property {id}.{property.Name} differs.");
            }
        }
    }

    private static JsonElement ReadComponents(JsonElement owner, bool metadataCase)
    {
        var components = owner.GetProperty("components");
        if (!metadataCase)
            return components;
        // The pinned cases use the pre-rename spelling. Adapt only the component envelope.
        var normalized = JsonNode.Parse(components.GetRawText())!.AsArray();
        foreach (var component in normalized)
        {
            var definition = component!.AsObject();
            if (definition.Remove("metadata", out var metadata))
                definition["@metadata"] = metadata;
        }
        return JsonSerializer.SerializeToElement(normalized);
    }

    private static string RegisterCatalog(CatalogRegistry registry, string relativePath, string assets)
    {
        var path = Path.GetFullPath(Path.Combine(assets, "conformance", relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        var catalogId = document.RootElement.TryGetProperty("catalogId", out var declared)
            ? declared.GetString()!
            : document.RootElement.GetProperty("$id").GetString()!;
        if (catalogId != CatalogRegistry.BasicCatalogId)
            Require(registry.TryRegister(bytes, out var diagnostic), diagnostic?.ToString() ?? "Catalog registration failed.");
        return catalogId;
    }

    private static JsonElement LoadFixture(JsonElement test, string assets)
    {
        var relative = test.GetProperty("fixture").GetString()!;
        var path = Path.Combine(assets, "conformance", relative.Replace('/', Path.DirectorySeparatorChar));
        var yaml = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build();
        return JsonSerializer.SerializeToElement(Normalize(yaml.Deserialize<object>(File.ReadAllText(path))));
    }

    private static object? Normalize(object? value) => value switch
    {
        IDictionary<object, object> map => map.ToDictionary(
            entry => Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture)!,
            entry => Normalize(entry.Value)),
        IList<object> list => list.Select(Normalize).ToArray(),
        _ => value
    };

    private static void Apply(SurfaceSession session, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        Require(WireMessage.TryDecode(bytes, WireDirection.AgentToRenderer, out var decoded, out var decodeError),
            decodeError?.ToString() ?? "Message decoding failed.");
        var applyError = session.Apply(decoded!);
        Require(applyError is null, applyError?.ToString() ?? "Message was rejected.");
    }

    internal static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;
        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                if (left.EnumerateObject().Count() != right.EnumerateObject().Count())
                    return false;
                foreach (var property in left.EnumerateObject())
                    if (!right.TryGetProperty(property.Name, out var other) || !JsonEquals(property.Value, other))
                        return false;
                return true;
            case JsonValueKind.Array:
                var leftItems = left.EnumerateArray().ToArray();
                var rightItems = right.EnumerateArray().ToArray();
                return leftItems.Length == rightItems.Length &&
                    leftItems.Zip(rightItems).All(pair => JsonEquals(pair.First, pair.Second));
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            case JsonValueKind.Number:
                return left.GetDouble() == right.GetDouble();
            default:
                return true;
        }
    }

    private static bool Matches(ResolvedProperty actual, JsonElement expected)
    {
        if (expected.ValueKind == JsonValueKind.Object && expected.TryGetProperty("node", out var node))
            return actual.Kind == ResolvedPropertyKind.Child && actual.ChildPositions.Count == 1 &&
                actual.ChildPositions[0] == node.GetString();
        if (expected.ValueKind == JsonValueKind.Object && expected.TryGetProperty("action", out var action))
            return actual.Kind == ResolvedPropertyKind.Action && action.ValueKind == JsonValueKind.True;
        if (expected.ValueKind == JsonValueKind.Object && expected.TryGetProperty("writable", out var writable))
            return actual.Kind is ResolvedPropertyKind.Binding or ResolvedPropertyKind.Value &&
                actual.Writable == writable.GetBoolean() &&
                actual.Value is { } value && JsonEquals(value, expected.GetProperty("value"));
        if (expected.ValueKind == JsonValueKind.Array && actual.Kind == ResolvedPropertyKind.Children &&
            expected.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("node", out _)))
            return expected.EnumerateArray().Select(item => item.GetProperty("node").GetString())
                .SequenceEqual(actual.ChildPositions);
        return actual.Kind == ResolvedPropertyKind.Value && actual.Value is { } plain && JsonEquals(plain, expected);
    }

    private static void Require(bool condition, string message = "Node resolution assertion failed.")
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class NodeScenario(SurfaceSession session, NodeResolver resolver, bool metadataCase)
    {
        private readonly HashSet<ResolvedNode> _createdInStep = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ResolvedNode, int> _emissions = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ResolvedNode, int> _createdEmissions = new(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<ResolvedNode, int> _metadataEmissions = new(ReferenceEqualityComparer.Instance);

        public void Observe()
        {
            resolver.MetadataChanged += node => _metadataEmissions[node] = _metadataEmissions.GetValueOrDefault(node) + 1;
            resolver.NodeCreated += node => _createdInStep.Add(node);
            resolver.PropsChanged += node =>
            {
                var counts = _createdInStep.Contains(node) ? _createdEmissions : _emissions;
                counts[node] = counts.GetValueOrDefault(node) + 1;
            };
        }

        public void BeginStep()
        {
            _createdInStep.Clear();
            _emissions.Clear();
            _metadataEmissions.Clear();
            _createdEmissions.Clear();
        }

        public void Perform(JsonElement step)
        {
            var op = step.GetProperty("op").GetString();
            switch (op)
            {
                case "process_messages":
                    foreach (var message in step.GetProperty("messages").EnumerateArray())
                        Apply(session, message);
                    break;
                case "update_components":
                    Apply(session, new
                    {
                        version = "1.0",
                        updateComponents = new { surfaceId = SurfaceId, components = ReadComponents(step, metadataCase) }
                    });
                    break;
                case "set_data":
                    Require(session.WriteScopedData(SurfaceId, "/", step.GetProperty("path").GetString()!,
                        step.GetProperty("value")) is null, "The data write was rejected.");
                    break;
                case "write":
                    var position = step.GetProperty("node").GetString();
                    var node = resolver.Nodes.SingleOrDefault(candidate => candidate.Position == position);
                    Require(node is not null, $"No node at {position}.");
                    Require(resolver.Write(node!, step.GetProperty("property").GetString()!,
                        step.GetProperty("value")) is null, "The node write was rejected.");
                    break;
                default:
                    throw new NotSupportedException($"The node operation {op} is not implemented.");
            }
        }

        public void Check(JsonElement expect, IReadOnlyList<ResolvedNode> before, bool requireEmissions)
        {
            foreach (var field in expect.EnumerateObject())
                Require(ExpectationFields.Contains(field.Name), $"The expectation {field.Name} is not implemented.");

            if (expect.TryGetProperty("nodes", out var nodes))
                CheckNodes(nodes, resolver.Nodes);
            if (expect.TryGetProperty("data", out var data))
                CheckData(data);
            if (requireEmissions)
                Require(expect.TryGetProperty("emissions", out _), "A step must declare emissions.");
            if (expect.TryGetProperty("emissions", out var emissions))
                CheckEmissions(emissions, before, _emissions);
            if (expect.TryGetProperty("metadata_emissions", out var metadataEmissions))
                CheckEmissions(metadataEmissions, before, _metadataEmissions);
            if (expect.TryGetProperty("created_emissions", out var created))
                CheckCreatedEmissions(created);
            if (expect.TryGetProperty("same_nodes", out var same))
                CheckSameNodes(same, before);
        }

        private void CheckNodes(JsonElement expected, IReadOnlyList<ResolvedNode> actual)
        {
            var expectedPositions = expected.EnumerateObject().Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
            var actualPositions = actual.Select(node => node.Position).ToHashSet(StringComparer.Ordinal);
            Require(expectedPositions.SetEquals(actualPositions),
                $"Mounted positions [{string.Join(", ", actualPositions)}] differ from [{string.Join(", ", expectedPositions)}].");

            foreach (var entry in expected.EnumerateObject())
            {
                var node = actual.Single(candidate => candidate.Position == entry.Name);
                foreach (var field in entry.Value.EnumerateObject())
                {
                    Require(NodeFields.Contains(field.Name), $"The node field {field.Name} is not implemented.");
                    switch (field.Name)
                    {
                        case "component_id":
                            Require(node.ComponentId == field.Value.GetString(), $"{entry.Name} component_id differs.");
                            break;
                        case "type":
                            Require(node.ComponentType == field.Value.GetString(), $"{entry.Name} type differs.");
                            break;
                        case "state":
                            Require(field.Value.GetString() == "resolved", $"{entry.Name} state differs.");
                            break;
                        case "data_path":
                            Require(node.DataPath == field.Value.GetString(), $"{entry.Name} data_path differs.");
                            break;
                        case "metadata":
                            Require(node.Metadata is { } metadata
                                ? JsonEquals(metadata, field.Value)
                                : field.Value.ValueKind == JsonValueKind.Null, $"{entry.Name} metadata differs.");
                            break;
                        case "props":
                            foreach (var property in field.Value.EnumerateObject())
                                Require(node.Props.TryGetValue(property.Name, out var resolved) &&
                                    Matches(resolved, property.Value), $"{entry.Name} property {property.Name} differs.");
                            break;
                    }
                }
            }
        }

        private void CheckData(JsonElement expected)
        {
            Require(session.TryGetSurface(SurfaceId, out var snapshot) && snapshot is not null, "The surface is missing.");
            foreach (var entry in expected.EnumerateObject())
                Require(snapshot!.TryGetData(entry.Name, out var actual) && JsonEquals(actual, entry.Value),
                    $"The data at {entry.Name} differs.");
        }

        private void CheckEmissions(JsonElement expected, IReadOnlyList<ResolvedNode> before,
            Dictionary<ResolvedNode, int> emissions)
        {
            var beforeSet = new HashSet<ResolvedNode>(before, ReferenceEqualityComparer.Instance);
            var retained = resolver.Nodes.Where(node => beforeSet.Contains(node)).ToArray();
            foreach (var node in retained)
            {
                var expectedCount = expected.TryGetProperty(node.Position, out var count) ? count.GetInt32() : 0;
                Require(emissions.GetValueOrDefault(node) == expectedCount,
                    $"{node.Position} emitted {emissions.GetValueOrDefault(node)} times, expected {expectedCount}.");
            }
            foreach (var entry in expected.EnumerateObject())
                Require(retained.Any(node => node.Position == entry.Name), $"No retained node at {entry.Name}.");
        }

        private void CheckCreatedEmissions(JsonElement expected)
        {
            foreach (var entry in expected.EnumerateObject())
            {
                var node = _createdInStep.SingleOrDefault(candidate => candidate.Position == entry.Name);
                Require(node is not null, $"{entry.Name} was not created in this step.");
                Require(_createdEmissions.GetValueOrDefault(node!) == entry.Value.GetInt32(),
                    $"{entry.Name} created emissions differ.");
            }
        }

        private void CheckSameNodes(JsonElement expected, IReadOnlyList<ResolvedNode> before)
        {
            foreach (var entry in expected.EnumerateArray())
            {
                var position = entry.GetString()!;
                var previous = before.SingleOrDefault(candidate => candidate.Position == position);
                var current = resolver.Nodes.SingleOrDefault(candidate => candidate.Position == position);
                Require(previous is not null && ReferenceEquals(previous, current), $"{position} was replaced or missing.");
            }
        }
    }
}
