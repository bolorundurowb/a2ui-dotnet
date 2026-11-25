using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Agent;
using A2UI.Core;

internal static class AgentFunctionCases
{
    internal const string Path = "conformance/agent/agent_functions.yaml";

    internal static void Run(JsonElement test, string assets)
    {
        var args = test.GetProperty("args");
        var action = test.GetProperty("action").GetString();
        var catalogs = new CatalogRegistry(includeBasicCatalog: false);
        var functions = new AgentFunctions();
        var registrations = args.TryGetProperty("catalogs", out var items) ? items.EnumerateArray().ToArray() :
            args.TryGetProperty("catalog", out var item) ? [item] : [];
        A2uiDiagnostic? diagnostic = null;
        string[] agentNames = [];
        var handlerRuns = 0;
        var callbackRuns = 0;
        foreach (var entry in registrations)
        {
            using var source = entry.GetProperty("catalog").ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(assets, "conformance", entry.GetProperty("catalog").GetString()!)))
                : JsonDocument.Parse(entry.GetProperty("catalog").GetRawText());
            var declared = entry.TryGetProperty("agent_functions", out var declarations)
                ? declarations.EnumerateObject().Select(entry => Registration(entry, () => handlerRuns++)).ToArray()
                : [];
            agentNames = declared.Select(function => function.Name).ToArray();
            diagnostic = functions.Register(catalogs, source.RootElement, declared);
            if (diagnostic is not null)
                break;
        }

        JsonObject? actual = null;
        if (diagnostic is null)
        {
            switch (action)
            {
                case "declare_agent_functions":
                    catalogs.TryGetDefinition(catalogs.CatalogIds.Single(), out var catalog);
                    var names = catalog.GetProperty("functions").EnumerateObject().Select(property => property.Name).ToArray();
                    actual = new()
                    {
                        ["functions"] = Names(names),
                        ["document_functions"] = Names(names),
                        ["agent_functions"] = Names(agentNames)
                    };
                    break;

                case "validate_payload":
                    using (var state = new RendererState(catalogs))
                    {
                        var messages = new List<WireMessage>();
                        foreach (var value in args.GetProperty("messages").EnumerateArray())
                        {
                            if (!WireMessage.TryDecode(Encoding.UTF8.GetBytes(value.GetRawText()), WireDirection.AgentToRenderer,
                                    out var message, out diagnostic))
                                break;
                            messages.Add(message!);
                        }
                        if (diagnostic is null)
                        {
                            diagnostic = state.Prepare(messages, out var turn);
                            turn?.Dispose();
                        }
                        if (diagnostic is not null)
                            diagnostic = diagnostic with { Code = "ValidationError" };
                        else
                            Require(test.GetProperty("expect_valid").GetBoolean(), "Expected invalid payload.");
                    }
                    break;

                case "handle_agent_function":
                case "process_renderer_message":
                    var callbacks = new Dictionary<string, RendererMessageHandler>(StringComparer.Ordinal);
                    JsonObject? callback = null;
                    if (args.TryGetProperty("callbacks", out var configured))
                        foreach (var handler in configured.EnumerateArray())
                        {
                            var kind = handler.ValueKind == JsonValueKind.String ? handler.GetString()! : handler.GetProperty("kind").GetString()!;
                            var accepts = handler.ValueKind == JsonValueKind.String ||
                                !handler.TryGetProperty("declines", out var declines) || !declines.GetBoolean();
                            callbacks.Add(kind, (message, _) =>
                            {
                                callbackRuns++;
                                callback = new()
                                {
                                    ["kind"] = message.Kind,
                                    ["message"] = JsonNode.Parse(message.Envelope.GetRawText())
                                };
                                return ValueTask.FromResult(accepts);
                            });
                        }
                    var processor = new RendererMessageProcessor(catalogs, functions, callbacks.GetValueOrDefault("action"),
                        callbacks.GetValueOrDefault("error"), callbacks.GetValueOrDefault("rendererFunctionResponse"));
                    var result = processor.ProcessAsync(Encoding.UTF8.GetBytes(args.GetProperty("message").GetRawText()))
                        .AsTask().GetAwaiter().GetResult();
                    diagnostic = result.Diagnostic;
                    if (action == "handle_agent_function")
                    {
                        if (result.Reply is { } reply)
                        {
                            Require(result.Handled, "An agent function reply must be handled.");
                            actual = JsonNode.Parse(reply.Payload.GetRawText())!.AsObject();
                        }
                    }
                    else
                    {
                        actual = new() { ["handled"] = result.Handled };
                        if (callback is not null)
                            actual["callback"] = callback;
                        if (result.Reply is { } reply)
                            actual["replies"] = new JsonArray(JsonNode.Parse(reply.Envelope.GetRawText()));
                    }
                    break;

                default:
                    throw new InvalidDataException($"Unknown agent function fixture action '{action}'.");
            }
        }

        if (test.TryGetProperty("expect_error", out var expectedError))
        {
            Require(handlerRuns == 0 && callbackRuns == 0, "A rejected message ran a callback or agent function.");
            Require(diagnostic?.Code == expectedError.GetProperty("category").GetString(),
                diagnostic?.ToString() ?? "Expected a diagnostic.");
            if (expectedError.TryGetProperty("message", out var fragment))
                Require((diagnostic!.Path + diagnostic.Message).Contains(fragment.GetString()!, StringComparison.Ordinal),
                    diagnostic.ToString());
            return;
        }

        Require(diagnostic is null, diagnostic?.ToString() ?? "Unexpected diagnostic.");
        if (test.TryGetProperty("expect", out var expected))
        {
            if (action == "declare_agent_functions")
                foreach (var group in expected.EnumerateObject())
                {
                    var expectedNames = group.Value.EnumerateArray().Select(name => name.GetString()).Order(StringComparer.Ordinal);
                    var actualNames = actual![group.Name]!.AsArray().Select(name => name!.GetValue<string>()).Order(StringComparer.Ordinal);
                    Require(expectedNames.SequenceEqual(actualNames), $"Different names for '{group.Name}'.");
                }
            else
            {
                Compare(expected, actual);
                var error = action == "handle_agent_function" ? actual?["error"] :
                    (actual?["replies"] as JsonArray)?[0]?["agentFunctionResponse"]?["error"];
                if (error?["code"]?.GetValue<string>() is "INVALID_FUNCTION_CALL" or "UNKNOWN_FUNCTION")
                    Require(handlerRuns == 0, "An invalid call ran an agent function.");
            }
        }
    }

    private static AgentFunctionRegistration Registration(JsonProperty entry, Action onRun)
    {
        var item = entry.Value;
        var name = item.TryGetProperty("name", out var alias) ? alias.GetString()! : entry.Name;
        var behavior = item.GetProperty("behavior");
        AgentFunctionHandler handler = (call, cancellationToken) =>
        {
            onRun();
            if (behavior.TryGetProperty("returns", out var value))
                return ValueTask.FromResult(value);
            if (behavior.TryGetProperty("returns_args", out _))
                return ValueTask.FromResult(call.Arguments);
            if (behavior.TryGetProperty("returns_context", out _))
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new
                {
                    catalogId = call.CatalogId,
                    surfaceId = call.SurfaceId,
                    functionCallId = call.FunctionCallId
                }));
            var error = behavior.GetProperty("raises");
            var message = error.GetProperty("message").GetString()!;
            if (error.TryGetProperty("code", out var code))
                throw new AgentFunctionException(code.GetString()!, message);
            if (error.TryGetProperty("validation", out _))
                throw new AgentFunctionException("EXECUTION_ERROR", message);
            throw new InvalidOperationException(message);
        };
        if (item.TryGetProperty("from_api", out var fromApi) && fromApi.GetBoolean())
            return new(name, handler);
        var description = item.TryGetProperty("description", out var text) ? text.GetString() : null;
        var requiresActivation = item.TryGetProperty("requiresUserActivation", out var activation) && activation.GetBoolean();
        return new(name, handler, item.GetProperty("args"), item.GetProperty("returnType").GetString(), description, requiresActivation);
    }

    private static JsonArray Names(IEnumerable<string> names) =>
        new(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());

    private static void Compare(JsonElement expected, JsonNode? actual, bool rpcError = false)
    {
        if (expected.ValueKind == JsonValueKind.Object && actual is JsonObject dictionary)
        {
            foreach (var member in expected.EnumerateObject())
            {
                Require(dictionary.TryGetPropertyValue(member.Name, out var found), $"Expected '{member.Name}' in {actual}.");
                if (rpcError && member.NameEquals("message"))
                    Require(found?.GetValue<string>().Contains(member.Value.GetString()!, StringComparison.Ordinal) == true,
                        $"Expected error substring {member.Value}, received {found}.");
                else
                    Compare(member.Value, found, member.NameEquals("error"));
            }
            var extraMessage = rpcError && !expected.TryGetProperty("message", out _) && dictionary.ContainsKey("message");
            Require(dictionary.Count == expected.EnumerateObject().Count() + (extraMessage ? 1 : 0),
                $"Expected {expected}, received {actual}.");
            return;
        }
        if (expected.ValueKind == JsonValueKind.Array && actual is JsonArray array)
        {
            Require(expected.GetArrayLength() == array.Count, $"Expected {expected}, received {actual}.");
            for (var index = 0; index < array.Count; index++)
                Compare(expected[index], array[index]);
            return;
        }
        Require(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), actual), $"Expected {expected}, received {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
