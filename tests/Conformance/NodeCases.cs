using System.Text.Json;
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
        "direct_data_binding_and_function_call_schemas"
    };

    private static readonly HashSet<string> ExpectationFields = new(StringComparer.Ordinal)
    {
        "nodes", "data", "emissions", "created_emissions", "same_nodes"
    };

    private static readonly HashSet<string> NodeFields = new(StringComparer.Ordinal)
    {
        "component_id", "type", "state", "data_path", "props"
    };

    internal static bool Supports(string id) => SupportedIds.Contains(id);

    internal static string ExclusionReason(JsonElement test, string assets)
    {
        var catalog = LoadFixture(test, assets).GetProperty("catalog").GetString()!;
        return catalog.Contains("v0_9_1", StringComparison.Ordinal)
            ? "The fixture uses the v0.9.1 basic catalog, which the pinned revision does not include."
            : "The scenario needs envelope metadata, action invocation or function recording, which the node resolver does not implement.";
    }

    internal static void Run(JsonElement test, string assets)
    {
        var fixture = LoadFixture(test, assets);
        var registry = new CatalogRegistry();
        var catalogId = RegisterCatalog(registry, fixture.GetProperty("catalog").GetString()!, assets);
        using var session = new SurfaceSession(registry);
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
                    components = fixture.GetProperty("components")
                }
            });
        }

        using var resolver = new NodeResolver(session, registry, SurfaceId);
        var scenario = new NodeScenario(session, resolver);
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

    private static bool JsonEquals(JsonElement left, JsonElement right)
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
            return actual.Kind == ResolvedPropertyKind.Binding && actual.Writable == writable.GetBoolean() &&
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

    private sealed class NodeScenario(SurfaceSession session, NodeResolver resolver)
    {
        private readonly HashSet<ResolvedNode> _createdInStep = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ResolvedNode, int> _emissions = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ResolvedNode, int> _createdEmissions = new(ReferenceEqualityComparer.Instance);

        public void Observe()
        {
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
                        updateComponents = new { surfaceId = SurfaceId, components = step.GetProperty("components") }
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
                CheckEmissions(emissions, before);
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

        private void CheckEmissions(JsonElement expected, IReadOnlyList<ResolvedNode> before)
        {
            var beforeSet = new HashSet<ResolvedNode>(before, ReferenceEqualityComparer.Instance);
            var retained = resolver.Nodes.Where(node => beforeSet.Contains(node)).ToArray();
            foreach (var node in retained)
            {
                var expectedCount = expected.TryGetProperty(node.Position, out var count) ? count.GetInt32() : 0;
                Require(_emissions.GetValueOrDefault(node) == expectedCount,
                    $"{node.Position} emitted {_emissions.GetValueOrDefault(node)} times, expected {expectedCount}.");
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
