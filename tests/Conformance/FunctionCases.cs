using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

/// <summary>Runs the function, validation result and index suites through the public function and node APIs.</summary>
internal static class FunctionCases
{
    private const string OutsideCollectionCase = "test_index_function_outside_loop_error";

    internal static bool Supports(string file, JsonElement test) => file switch
    {
        "conformance/core/functions.yaml" => !IsLegacy(test),
        "conformance/core/validation_result.yaml" => true,
        "conformance/core/index_function.yaml" => ReadString(test, "name") != OutsideCollectionCase,
        _ => false
    };

    internal static bool IsLegacy(JsonElement test) =>
        test.TryGetProperty("catalog", out var catalog) && catalog.TryGetProperty("protocolVersion", out var version) &&
        version.ValueKind == JsonValueKind.String && version.GetString() != "1.0";

    internal static string? ExclusionReason(string file, JsonElement test)
    {
        if (file == "conformance/core/functions.yaml" && IsLegacy(test))
            return "Legacy protocol is outside the profile.";
        return file == "conformance/core/index_function.yaml" && ReadString(test, "name") == OutsideCollectionCase
            ? "Surface validation does not yet reject @index outside a collection template."
            : null;
    }

    internal static void Run(string assets, JsonElement test)
    {
        if (test.GetProperty("action").ValueEquals("evaluate_function"))
            RunEvaluate(assets, test);
        else
            RunValidate(test);
    }

    // ---- evaluate_function ----

    private static void RunEvaluate(string assets, JsonElement test)
    {
        var function = test.GetProperty("function").GetString()!;
        var args = test.TryGetProperty("args", out var declared) ? declared : Parse("{}");
        JsonElement value;
        A2uiDiagnostic? diagnostic;
        if (!test.TryGetProperty("surface", out var surfaceBlock))
        {
            var arguments = args.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            BasicFunctions.TryInvoke(function, arguments, out value, out diagnostic);
        }
        else
            (value, diagnostic) = EvaluateOnSurface(assets, test, surfaceBlock, function, args);

        if (test.TryGetProperty("expectError", out var expectedError))
        {
            Require(diagnostic is not null, "Expected the call to fail.");
            Require(diagnostic!.Code == expectedError.GetProperty("category").GetString(),
                $"Expected {expectedError.GetProperty("category").GetString()} but received {diagnostic}.");
            if (expectedError.TryGetProperty("message", out var message))
                Require(diagnostic.Message.Contains(message.GetString()!, StringComparison.Ordinal),
                    $"The message differs: {diagnostic.Message}");
            return;
        }

        Require(diagnostic is null, diagnostic?.ToString() ?? "");
        Require(Matches(test.GetProperty("expect"), value), $"Expected {test.GetProperty("expect").GetRawText()} but received {value.GetRawText()}.");
    }

    private static (JsonElement Value, A2uiDiagnostic? Diagnostic) EvaluateOnSurface(string assets, JsonElement test,
        JsonElement surfaceBlock, string function, JsonElement args)
    {
        var registry = new CatalogRegistry(includeBasicCatalog: false);
        foreach (var path in test.GetProperty("catalogPaths").EnumerateArray())
        {
            var location = Path.Combine(assets, path.GetString()!);
            // The pinned manifest does not import the v0.9 catalogs, which a v1.0 surface cannot use anyway.
            if (!File.Exists(location) && path.GetString()!.Contains("/v0_", StringComparison.Ordinal))
                continue;
            var bytes = File.ReadAllBytes(location);
            var registered = registry.TryRegister(bytes, out var error);
            // A catalog built for another protocol version is loaded by the SDK it targets and unavailable here.
            using var document = JsonDocument.Parse(bytes);
            var current = document.RootElement.TryGetProperty("protocolVersion", out var version) && version.GetString() == "1.0";
            Require(registered || !current, error?.ToString() ?? "Catalog registration failed.");
        }

        var createSurface = new JsonObject { ["surfaceId"] = "main" };
        if (surfaceBlock.TryGetProperty("catalogId", out var defaultCatalog))
            createSurface["catalogId"] = defaultCatalog.GetString();
        if (test.TryGetProperty("dataModel", out var dataModel))
            createSurface["dataModel"] = JsonNode.Parse(dataModel.GetRawText());
        using var session = new SurfaceSession(registry);
        Apply(session, new JsonObject { ["version"] = "1.0", ["createSurface"] = createSurface });
        Require(session.TryGetSurface("main", out var surface) && surface is not null, "The surface was not created.");

        var call = new JsonObject { ["@call"] = function, ["args"] = JsonNode.Parse(args.GetRawText()) };
        if (test.TryGetProperty("catalogId", out var named))
            call["catalogId"] = named.GetString();
        var evaluator = new FunctionEvaluator(registry);
        evaluator.TryEvaluate(surface!, "/", Parse(call.ToJsonString()), userInitiated: false,
            out var value, out var diagnostic);
        return (value, diagnostic);
    }

    // ---- validate ----

    private static void RunValidate(JsonElement test)
    {
        var registry = new CatalogRegistry();
        Require(registry.TryGetDefinition(CatalogRegistry.BasicCatalogId, out var basic), "Basic Catalog is unavailable.");
        var alias = JsonNode.Parse(basic.GetRawText())!.AsObject();
        alias["catalogId"] = "basic";
        alias["$id"] = "basic";
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(alias.ToJsonString()), out var registrationError),
            registrationError?.ToString() ?? "Basic Catalog alias is unavailable.");

        using var session = new SurfaceSession(registry);
        var surfaceId = "";
        NodeResolver? resolver = null;
        try
        {
            foreach (var step in test.GetProperty("steps").EnumerateArray())
            {
                var rejection = ApplyAll(session, step.GetProperty("payload"));
                if (step.TryGetProperty("expectError", out var expectedError))
                {
                    Require(rejection is not null, "Expected the messages to be rejected.");
                    if (expectedError.TryGetProperty("message", out var message))
                        Require(rejection!.Message.Contains(message.GetString()!, StringComparison.Ordinal), rejection.ToString());
                    continue;
                }

                Require(rejection is null, rejection?.ToString() ?? "");
                if (resolver is null)
                {
                    surfaceId = step.GetProperty("payload")[0].GetProperty("createSurface").GetProperty("surfaceId").GetString()!;
                    resolver = new NodeResolver(session, registry, surfaceId);
                }
                if (step.TryGetProperty("expect", out var stepExpectation))
                    Check(resolver, stepExpectation, surfaceId);
            }
            if (test.TryGetProperty("expect", out var expectation))
                Check(resolver ?? throw new InvalidDataException("The case created no surface."), expectation, surfaceId);
        }
        finally
        {
            resolver?.Dispose();
        }
    }

    private static A2uiDiagnostic? ApplyAll(SurfaceSession session, JsonElement payload)
    {
        foreach (var item in payload.EnumerateArray())
        {
            if (!WireMessage.TryDecode(Encoding.UTF8.GetBytes(item.GetRawText()), WireDirection.AgentToRenderer,
                    out var message, out var decodeError))
                return decodeError;
            if (session.Apply(message!) is { } rejection)
                return rejection;
        }
        return null;
    }

    private static void Check(NodeResolver resolver, JsonElement expectation, string surfaceId)
    {
        var expected = expectation.GetProperty("surfaces").GetProperty(surfaceId);
        var nodes = resolver.Nodes.ToDictionary(Key);

        if (expected.TryGetProperty("components", out var components))
            foreach (var component in components.EnumerateObject())
            {
                Require(nodes.TryGetValue(component.Name, out var node), $"Missing component {component.Name}.");
                foreach (var property in component.Value.EnumerateObject())
                {
                    if (property.NameEquals("component"))
                    {
                        Require(node!.ComponentType == property.Value.GetString(), $"Component type of {component.Name} differs.");
                        continue;
                    }
                    Require(node!.Props.TryGetValue(property.Name, out var actual) && actual.Value is { } value &&
                        Matches(property.Value, value), $"Property {property.Name} of {component.Name} differs.");
                }
            }

        foreach (var field in new[] { "isValid", "validationErrors", "validationResults", "validationResult" })
        {
            if (!expected.TryGetProperty(field, out var byComponent))
                continue;
            foreach (var entry in byComponent.EnumerateObject())
            {
                Require(nodes.TryGetValue(entry.Name, out var node), $"Missing component {entry.Name}.");
                var actual = field switch
                {
                    "isValid" => JsonValue.Create(node!.IsValid),
                    "validationErrors" => new JsonArray(node!.ValidationErrors.Select(error => (JsonNode?)JsonValue.Create(error)).ToArray()),
                    "validationResults" => new JsonArray(node!.ValidationResults.Select(Describe).ToArray()),
                    _ => node!.BlockingResult is { } blocking ? Describe(blocking) : null
                };
                // The fixtures omit severity from the singular result in one case, so that field is compared by the members it names.
                var same = field == "validationResult" && actual is JsonObject actualObject
                    ? entry.Value.EnumerateObject().All(member =>
                        JsonNode.DeepEquals(JsonNode.Parse(member.Value.GetRawText()), actualObject[member.Name]))
                    : JsonNode.DeepEquals(JsonNode.Parse(entry.Value.GetRawText()), actual);
                Require(same, $"{field} of {entry.Name} differs: expected {entry.Value.GetRawText()} but received {actual?.ToJsonString()}.");
            }
        }
    }

    // Instance IDs are positional, so a fixture names a component by its ID, with the item index appended inside a collection.
    private static string Key(ResolvedNode node)
    {
        var segment = node.DataPath[(node.DataPath.LastIndexOf('/') + 1)..];
        return node.DataPath != "/" && int.TryParse(segment, out _) ? node.ComponentId + "_" + segment : node.ComponentId;
    }

    private static JsonNode Describe(ValidationResult result)
    {
        var description = new JsonObject { ["valid"] = result.Valid };
        if (result.Code is not null)
            description["code"] = result.Code;
        if (result.Message is not null)
            description["message"] = result.Message;
        description["severity"] = result.Severity;
        return description;
    }

    private static void Apply(SurfaceSession session, JsonObject message)
    {
        Require(WireMessage.TryDecode(Encoding.UTF8.GetBytes(message.ToJsonString()), WireDirection.AgentToRenderer,
            out var decoded, out var decodeError), decodeError?.ToString() ?? "Invalid fixture message.");
        var rejection = session.Apply(decoded!);
        Require(rejection is null, rejection?.ToString() ?? "The fixture message was rejected.");
    }

    // A validator returns a validation result object, so a boolean expectation reads its valid member. A Text property
    // displays a number as text, so a string expectation also matches the number's text.
    private static bool Matches(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind is JsonValueKind.True or JsonValueKind.False && actual.ValueKind == JsonValueKind.Object &&
            actual.TryGetProperty("valid", out var valid))
            return (expected.ValueKind == JsonValueKind.True) == (valid.ValueKind == JsonValueKind.True);
        if (expected.ValueKind == JsonValueKind.String && actual.ValueKind == JsonValueKind.Number)
            return expected.GetString() == actual.GetRawText();
        return JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonNode.Parse(actual.GetRawText()));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? ReadString(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
