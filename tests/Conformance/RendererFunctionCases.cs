using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Agent;
using A2UI.Core;

internal static class RendererFunctionCases
{
    internal const string Path = "conformance/agent/renderer_function_calls.yaml";

    internal static void Run(JsonElement test)
    {
        test.TryGetProperty("args", out var args);
        CatalogRegistry? catalogs = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("catalogs", out var definitions))
        {
            catalogs = new(includeBasicCatalog: false);
            foreach (var definition in definitions.EnumerateArray())
            {
                if (catalogs.TryRegister(Encoding.UTF8.GetBytes(BuildCatalog(definition).ToJsonString()), out var error))
                    continue;
                if (test.TryGetProperty("expect_error", out var expected) &&
                    expected.GetProperty("category").ValueEquals("CatalogError") &&
                    error!.Code == "duplicateCatalog")
                    return;
                throw new InvalidOperationException(error!.ToString());
            }
        }

        if (test.TryGetProperty("expect_error", out _))
            throw new InvalidOperationException("Expected catalog construction to fail.");

        var clock = new ManualClock();
        using var client = new RendererFunctionClient(catalogs,
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty("max_pending", out var capacity) ? capacity.GetInt32() : 256,
            ReadTimeout(args, "default_timeout_ms"), clock);
        var processor = new RendererMessageProcessor(catalogs ?? new(includeBasicCatalog: false),
            onRendererFunctionResponse: (message, _) => ValueTask.FromResult(client.TryHandleResponse(message)));
        var calls = new Dictionary<string, RendererFunctionInvocation>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in test.GetProperty("steps").EnumerateArray())
        {
            if (step.TryGetProperty("call", out var call))
            {
                step.TryGetProperty("expect_error", out var expectedError);
                try
                {
                    call.TryGetProperty("args", out var arguments);
                    var invocation = client.Call(call.GetProperty("catalogId").GetString()!,
                        call.GetProperty("name").GetString()!, arguments, ReadString(call, "surfaceId"),
                        ReadString(call, "functionCallId"), ReadTimeout(call, "timeout_ms"));
                    if (expectedError.ValueKind != JsonValueKind.Undefined)
                        throw new InvalidOperationException("Expected call registration to fail.");
                    calls.Add(call.GetProperty("label").GetString()!, invocation);
                    var id = invocation.Message.Payload.GetProperty("functionCallId").GetString();
                    if (string.IsNullOrEmpty(id) || !ids.Add(id))
                        throw new InvalidOperationException("Expected a unique nonempty correlation ID.");
                    if (step.TryGetProperty("expect_message", out var message))
                    {
                        var expected = JsonNode.Parse(message.GetRawText())!;
                        expected["callRendererFunction"]!["functionCallId"] = id;
                        Compare(expected, JsonNode.Parse(invocation.Message.Envelope.GetRawText()), "Call message");
                    }
                }
                catch (AgentFunctionException error) when (expectedError.ValueKind != JsonValueKind.Undefined)
                {
                    CheckError(expectedError, error);
                }
            }
            if (step.TryGetProperty("respond", out var response))
            {
                var invocation = ReadString(response, "call") is { } label ? calls[label] : null;
                var id = invocation is null ? ReadString(response, "functionCallId") :
                    invocation.Message.Payload.GetProperty("functionCallId").GetString();
                var expectedHandled = invocation is not null && !invocation.Result.IsCompleted;
                var payload = JsonNode.Parse(response.GetRawText())!.AsObject();
                payload.Remove("call");
                payload["functionCallId"] = id;
                var envelope = new JsonObject { ["version"] = "1.0", ["rendererFunctionResponse"] = payload };
                var result = processor.ProcessAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString())).GetAwaiter().GetResult();
                if (result.Diagnostic is not null || result.Reply is not null || result.Handled != expectedHandled)
                    throw new InvalidOperationException(result.Diagnostic?.ToString() ?? "Unexpected response handling.");
            }
            if (step.TryGetProperty("advance_ms", out var delta))
                clock.Advance(delta.GetInt32());
            if (step.TryGetProperty("dispose", out var dispose))
                client.Dispose(dispose.GetProperty("reason").GetString()!);
            if (step.TryGetProperty("expect_pending_count", out var count) && count.GetInt32() != client.PendingCount)
                throw new InvalidOperationException($"Expected {count.GetInt32()} pending calls, found {client.PendingCount}.");
            if (step.TryGetProperty("expect_calls", out var expectations))
                foreach (var expected in expectations.EnumerateObject())
                    CheckOutcome(expected.Value, calls[expected.Name].Result);
        }
    }

    private static void CheckOutcome(JsonElement expected, Task<JsonElement> result)
    {
        if (expected.ValueKind == JsonValueKind.String && expected.ValueEquals("pending"))
        {
            if (result.IsCompleted)
                throw new InvalidOperationException("Expected a pending call.");
            return;
        }

        if (!result.IsCompleted)
            throw new InvalidOperationException("Expected a completed call.");
        if (expected.TryGetProperty("error", out var expectedError))
        {
            try
            {
                result.GetAwaiter().GetResult();
                throw new InvalidOperationException("Expected a failed call.");
            }
            catch (AgentFunctionException error)
            {
                CheckError(expectedError, error);
            }
            return;
        }
        Compare(JsonNode.Parse(expected.GetProperty("value").GetRawText()),
            JsonNode.Parse(result.GetAwaiter().GetResult().GetRawText()), "Call result");
    }

    private static void CheckError(JsonElement expected, AgentFunctionException error)
    {
        var message = ReadString(expected, "message");
        if (!expected.GetProperty("code").ValueEquals(error.Code) ||
            message is not null && !error.Message.Contains(message, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected call error {error.Code}: {error.Message}");
    }

    private static void Compare(JsonNode? expected, JsonNode? actual, string subject)
    {
        if (!JsonNode.DeepEquals(expected, actual))
            throw new InvalidOperationException($"{subject}: expected {expected}, got {actual}.");
    }

    private static JsonObject BuildCatalog(JsonElement definition)
    {
        var functions = new JsonObject();
        foreach (var function in definition.GetProperty("functions").EnumerateObject())
        {
            var declaration = new JsonObject
            {
                ["type"] = "object",
                ["returnType"] = "string",
                ["properties"] = new JsonObject
                {
                    ["@call"] = new JsonObject { ["const"] = function.Name },
                    ["args"] = new JsonObject { ["type"] = "object" }
                },
                ["required"] = new JsonArray("@call")
            };
            foreach (var member in function.Value.EnumerateObject())
                declaration[member.Name] = JsonNode.Parse(member.Value.GetRawText());
            functions.Add(function.Name, declaration);
        }
        return new JsonObject { ["catalogId"] = ReadString(definition, "catalogId"), ["protocolVersion"] = WireMessage.Version, ["functions"] = functions };
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) ? member.GetString() : null;

    private static TimeSpan? ReadTimeout(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member)
            ? TimeSpan.FromMilliseconds(member.GetInt32()) : null;

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        internal void Advance(int milliseconds)
        {
            _elapsed += TimeSpan.FromMilliseconds(milliseconds);
            foreach (var timer in _timers.ToArray())
                timer.Run();
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan? _dueAt;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan)
                    throw new NotSupportedException("This clock supports one-shot timers.");
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._elapsed + dueTime;
                return true;
            }

            internal void Run()
            {
                if (_dueAt is not { } dueAt || clock._elapsed < dueAt)
                    return;
                Dispose();
                callback(state);
            }

            public void Dispose()
            {
                _dueAt = null;
                clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
