using System.Globalization;
using System.Text;
using System.Text.Json;
using A2UI.Core;

/// <summary>Runs the pinned error response and action dispatch cases through the public Core API.</summary>
internal static class OutboundCases
{
    private const string ErrorFile = "conformance/core/error_responses.yaml";
    private const string ActionFile = "conformance/core/actions.yaml";

    internal static bool Handles(string path) => path is ErrorFile or ActionFile;

    internal static string? ExclusionReason(string path, string id) => (path, id) switch
    {
        (ErrorFile, "test_error_response_update_components_invalid_uax31_id") =>
            "The fixture requires UAX #31 component IDs. The pinned common types schema defines ComponentId as a string.",
        (ActionFile, "test_action_event_with_data_binding" or "test_action_event_with_relative_scope") =>
            "The fixture binds context with the v0.9 path syntax, which the 1.0 profile excludes.",
        (ActionFile, "test_action_direct_name_shorthand") =>
            "The fixture uses the v0.9 shorthand for an event action, which the 1.0 profile excludes.",
        (ActionFile, "test_action_function_call_local_mutation_no_event_dispatched" or "test_action_function_call_execution_error") =>
            "The fixture calls a host-registered function. The evaluator implements only the Basic Catalog functions.",
        _ => null
    };

    internal static void Run(string path, JsonElement test, string assets)
    {
        if (path == ErrorFile)
            RunError(test, assets);
        else
            RunAction(test);
    }

    private static void RunError(JsonElement test, string assets)
    {
        var registry = CoreCases.BuildRegistry(test, assets);
        var strict = test.TryGetProperty("strictMode", out var strictMode) && strictMode.GetBoolean();
        using var session = new SurfaceSession(registry, strict ? GraphValidationOptions.Strict : GraphValidationOptions.Progressive);
        var steps = test.TryGetProperty("steps", out var declared)
            ? declared.EnumerateArray().Select(step => step.GetProperty("payload")).ToArray()
            : [test.GetProperty("payload")];

        RendererError? error = null;
        foreach (var payload in steps)
        {
            foreach (var item in payload.EnumerateArray())
            {
                var bytes = Encoding.UTF8.GetBytes(item.GetRawText());
                if (!WireMessage.TryDecode(bytes, WireDirection.AgentToRenderer, out var message, out var rejection))
                {
                    error = RendererError.FromRejectedEnvelope(bytes, rejection!);
                    break;
                }
                if (session.Apply(message!) is { } problem)
                {
                    error = RendererError.FromRejectedMessage(message!, problem);
                    break;
                }
            }
            if (error is not null)
                break;
        }

        if (!test.TryGetProperty("expectError", out var expected))
        {
            Require(error is null, $"Unexpected error: {error}");
            return;
        }

        Require(error is not null, "Expected a rejection.");
        Require(error!.Category == expected.GetProperty("category").GetString(), $"Category {error.Category} differs: {error}");
        if (expected.TryGetProperty("code", out var code))
            Require(error.Code == code.GetString(), $"Code {error.Code} differs.");
        if (expected.TryGetProperty("surfaceId", out var surfaceId))
            Require((error.SurfaceId ?? "") == surfaceId.GetString(), $"Surface {error.SurfaceId} differs.");
        if (expected.TryGetProperty("functionCallId", out var callId))
            Require(error.FunctionCallId == callId.GetString(), $"Call {error.FunctionCallId} differs.");
        if (expected.TryGetProperty("path", out var errorPath))
            Require(error.Path == errorPath.GetString(), $"Path {error.Path} differs: {error}");
        if (expected.TryGetProperty("details", out var details))
        {
            var first = details.EnumerateArray().First();
            Require(first.GetProperty("code").GetString() == error.Code && first.GetProperty("path").GetString() == error.Path,
                "Details differ.");
        }
        if (expected.TryGetProperty("rendererError", out var wire))
            CheckWire(error.ToWireMessage(), wire);
    }

    internal static void CheckWire(WireMessage message, JsonElement expected)
    {
        Require(message.Kind == "error" && message.Direction == WireDirection.RendererToAgent, "Expected an error message.");
        var actual = message.Payload;
        foreach (var field in expected.GetProperty("error").EnumerateObject())
        {
            if (field.Name == "message")
                Require(actual.GetProperty("message").GetString()!.Contains(field.Value.GetString()!, StringComparison.Ordinal),
                    $"Message '{actual.GetProperty("message").GetString()}' lacks '{field.Value.GetString()}'.");
            else
                Require(actual.TryGetProperty(field.Name, out var value) && value.GetString() == field.Value.GetString(),
                    $"Wire field {field.Name} differs.");
        }
    }

    private static void RunAction(JsonElement test)
    {
        var registry = new CatalogRegistry();
        var surfaceId = Text(test, "surfaceId") ?? "main_surface";
        var sourceId = Text(test, "sourceComponentId") ?? "source";
        var data = test.TryGetProperty("dataModel", out var model) ? model.GetRawText() : "{}";
        using var session = new SurfaceSession(registry);
        var created = $$$"""{"version":"1.0","createSurface":{"surfaceId":"{{{surfaceId}}}","catalogId":"{{{CatalogRegistry.BasicCatalogId}}}","dataModel":{{{data}}}}}""";
        Require(WireMessage.TryDecode(Encoding.UTF8.GetBytes(created), WireDirection.AgentToRenderer, out var message, out var decodeError) &&
            session.Apply(message!) is null, decodeError?.ToString() ?? "The surface was rejected.");
        Require(session.TryGetSurface(surfaceId, out var surface) && surface is not null, "The surface is missing.");

        var dispatcher = new ActionDispatcher(new FunctionEvaluator(registry));
        var activation = !test.TryGetProperty("userActivation", out var flag) || flag.GetBoolean();
        JsonElement? metadata = test.TryGetProperty("metadata", out var extra) ? extra : null;
        var result = dispatcher.Dispatch(surface!, sourceId, Text(test, "scope") ?? "/", test.GetProperty("actionPayload"),
            activation, metadata);

        if (test.TryGetProperty("expectError", out var expected))
        {
            Require(result.Message is null && result.Error is not null, "Expected a failure.");
            var error = result.Error!;
            if (expected.TryGetProperty("category", out var category))
                Require(error.Category == category.GetString(), $"Category {error.Category} differs.");
            if (expected.TryGetProperty("code", out var code))
                Require(error.Code == code.GetString(), $"Code {error.Code} differs.");
            if (expected.TryGetProperty("surfaceId", out var expectedSurface))
                Require(error.SurfaceId == expectedSurface.GetString(), $"Surface {error.SurfaceId} differs.");
            if (expected.TryGetProperty("message", out var text))
                Require(error.Message.Contains(text.GetString()!, StringComparison.Ordinal), $"Message '{error.Message}' differs.");
            return;
        }

        Require(result.Error is null, $"Unexpected error: {result.Error}");
        var dispatched = test.GetProperty("expectDispatched");
        if (dispatched.ValueKind == JsonValueKind.Null)
        {
            Require(result.Message is null, "Expected no message.");
            return;
        }

        Require(result.Message is not null && result.Message.Kind == "action", "Expected an action message.");
        var actual = result.Message!.Payload;
        foreach (var field in dispatched.EnumerateObject())
        {
            switch (field.Name)
            {
                case "timestampIso8601":
                    Require(DateTimeOffset.TryParse(actual.GetProperty("timestamp").GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _), "The timestamp is not ISO 8601.");
                    break;
                case "metadata" when field.Value.ValueKind == JsonValueKind.Null:
                    Require(!actual.TryGetProperty("metadata", out _), "Unexpected metadata.");
                    break;
                default:
                    Require(actual.TryGetProperty(field.Name, out var value) && NodeCases.JsonEquals(value, field.Value),
                        $"Action field {field.Name} differs.");
                    break;
            }
        }
    }

    private static string? Text(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
