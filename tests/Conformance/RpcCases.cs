using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

/// <summary>Runs the pinned RPC lifecycle and agent function routing cases through the public Core API.</summary>
internal static class RpcCases
{
    private const string RpcFile = "conformance/core/rpc_functions.yaml";
    private const string RoutingFile = "conformance/core/agent_function_routing.yaml";
    private static readonly string[] RpcCatalogIds = ["media_catalog", "system_catalog", "store_catalog", "basic"];

    internal static bool Handles(string path) => path is RpcFile or RoutingFile;

    internal static string? ExclusionReason(string path, string id) => (path, id) switch
    {
        (RpcFile, "test_rpc_call_renderer_function_requires_user_activation" or
            "test_rpc_call_renderer_function_user_activation_denied") =>
            "The fixture lets an agent call a function that requires user activation. The pinned catalog schema allows that flag only on rendererOnly functions, so the catalog cannot be registered.",
        (RpcFile, "test_rpc_call_renderer_function_protocol_version_mismatch") =>
            "The registry accepts only 1.0 catalogs, so the 0.9 catalog in the fixture cannot be registered.",
        (RoutingFile, "test_agent_routing_non_json_argument_fails_locally") =>
            "A JSON value cannot hold NaN or an infinity, so the arguments of the fixture have no representation here.",
        _ => null
    };

    internal static void Run(string path, JsonElement test)
    {
        if (path == RoutingFile)
            RunRouting(test);
        else
            RunRpc(test);
    }

    // ---- rpc_functions ----

    private static void RunRpc(JsonElement test)
    {
        var args = test.GetProperty("args");
        if (args.TryGetProperty("uiBindingScenario", out var scenario))
            RunUiBinding(test, scenario);
        else if (args.TryGetProperty("uiActionFallbackScenario", out var action))
            RunUiAction(test.GetProperty("expect"), action);
        else if (args.TryGetProperty("outboundCall", out _))
            RunAgentCall(test, args);
        else if (args.GetProperty("message").TryGetProperty("agentFunctionResponse", out var response))
            RunUnknownResponse(response);
        else
            RunRendererCall(test, args);
    }

    private static void RunUnknownResponse(JsonElement response)
    {
        using var client = new AgentFunctionClient(_ => throw new InvalidOperationException("Nothing is sent."));
        var message = Decode(new JsonObject { ["version"] = "1.0", ["agentFunctionResponse"] = JsonNode.Parse(response.GetRawText()) },
            WireDirection.AgentToRenderer);
        Require(!client.TryHandleResponse(message), "The response matched a call.");
        Require(client.PendingCount == 0, "A call is pending.");
    }

    private static void RunRendererCall(JsonElement test, JsonElement args)
    {
        var metadata = args.TryGetProperty("functionMetadata", out var declared) ? declared : Parse("{}");
        var registry = new CatalogRegistry(includeBasicCatalog: false);
        var evaluator = new FunctionEvaluator(registry);
        foreach (var id in RpcCatalogIds)
        {
            var functions = id == "basic" ? BasicFunctionDeclarations() : new JsonObject();
            foreach (var function in metadata.EnumerateObject())
            {
                functions[function.Name] = Declaration(function.Name, function.Value);
                evaluator.RegisterHostFunction(id, function.Name, Host(function.Name));
            }
            Register(registry, id, functions);
        }

        using var session = new SurfaceSession(registry);
        if (args.TryGetProperty("surfaceId", out var surfaceId))
        {
            var create = new JsonObject { ["surfaceId"] = surfaceId.GetString() };
            if (args.TryGetProperty("dataModel", out var data))
                create["dataModel"] = JsonNode.Parse(data.GetRawText());
            Apply(session, new JsonObject { ["version"] = "1.0", ["createSurface"] = create });
        }

        var envelope = Encoding.UTF8.GetBytes(args.GetProperty("message").GetRawText());
        var activation = args.TryGetProperty("userActivationPresent", out var flag) && flag.GetBoolean();
        var expectation = test.GetProperty("expect");
        if (!WireMessage.TryDecode(envelope, WireDirection.AgentToRenderer, out var call, out var rejection))
        {
            var error = RendererError.FromRejectedEnvelope(envelope, rejection!);
            Require(test.TryGetProperty("expectError", out var failure) || expectation.TryGetProperty("error", out _),
                $"The message was rejected: {rejection}");
            var expectedError = expectation.GetProperty("error");
            if (expectedError.TryGetProperty("code", out var code))
                Require(error.Code == code.GetString(), $"Code {error.Code} differs.");
            if (expectedError.TryGetProperty("functionCallId", out var callId))
                Require(error.FunctionCallId == callId.GetString(), $"Call {error.FunctionCallId} differs.");
            Require(error.Message.Contains(expectedError.GetProperty("message").GetString()!, StringComparison.Ordinal),
                $"Message '{error.Message}' differs.");
            if (test.TryGetProperty("expectError", out var details))
            {
                Require(error.Category == details.GetProperty("category").GetString(), $"Category {error.Category} differs.");
                if (details.TryGetProperty("rendererError", out var wire))
                    OutboundCases.CheckWire(error.ToWireMessage(), wire);
            }
            return;
        }

        var reply = new RendererFunctionHandler(registry, evaluator, session).Handle(call!, activation);
        var expected = expectation.GetProperty("response").GetProperty("rendererFunctionResponse");
        var actual = reply.Payload;
        Require(actual.GetProperty("functionCallId").GetString() == expected.GetProperty("functionCallId").GetString(),
            "The reply names another call.");
        if (expected.TryGetProperty("error", out var expectedFailure))
        {
            Require(actual.TryGetProperty("error", out var failure), $"Expected an error but received {actual.GetRawText()}.");
            Require(failure.GetProperty("code").GetString() == expectedFailure.GetProperty("code").GetString(),
                $"Code differs: {actual.GetRawText()}");
            Require(failure.GetProperty("message").GetString()!.Contains(expectedFailure.GetProperty("message").GetString()!,
                StringComparison.Ordinal), $"Message differs: {actual.GetRawText()}");
            return;
        }

        Require(actual.TryGetProperty("value", out var value) && NodeCases.JsonEquals(value, expected.GetProperty("value")),
            $"Expected {expected.GetRawText()} but received {actual.GetRawText()}.");
    }

    // The fixtures name the functions the host runs. Each returns what its description states.
    private static HostFunction Host(string name) => call => name switch
    {
        "playMedia" => Parse("""{"playing":true,"timestamp":0}"""),
        "openExternalUrl" => Parse("""{"opened":true}"""),
        "failingFunction" => throw new InvalidOperationException("The function failed."),
        "echoArgs" => Parse(new JsonObject(call.Args.Select(argument =>
            KeyValuePair.Create<string, JsonNode?>(argument.Key, JsonNode.Parse(argument.Value.GetRawText())))).ToJsonString()),
        _ => Parse("null")
    };

    private static void RunAgentCall(JsonElement test, JsonElement args)
    {
        var outbound = args.GetProperty("outboundCall");
        var surfaceId = outbound.GetProperty("surfaceId").GetString()!;
        var callId = outbound.GetProperty("functionCallId").GetString()!;
        var function = outbound.GetProperty("callFunction");
        TimeSpan? timeout = outbound.TryGetProperty("timeoutMs", out var limit) ? TimeSpan.FromMilliseconds(limit.GetInt32()) : null;
        var listener = !args.TryGetProperty("outboundListener", out var enabled) || enabled.GetBoolean();

        var sent = new List<WireMessage>();
        var client = new AgentFunctionClient(listener ? sent.Add : null);
        try
        {
            var primary = client.CallAsync(surfaceId, function, callId, timeout);
            if (args.TryGetProperty("secondOutboundCall", out var second))
            {
                var duplicate = client.CallAsync(surfaceId, second.GetProperty("callFunction"), second.GetProperty("functionCallId").GetString());
                RequireFailure(duplicate, ExpectedCode(test));
                Require(client.PendingCount == 1, "The first call is no longer pending.");
                return;
            }

            if (args.TryGetProperty("afterCall", out var after))
            {
                if (after.GetString() == "cancel")
                {
                    client.CancelSurface(surfaceId);
                    RequireFailure(primary, ExpectedCode(test));
                    return;
                }

                client.Dispose();
                RequireFailure(primary, test.GetProperty("expect").GetProperty("inFlightError").GetProperty("code").GetString()!);
                RequireFailure(client.CallAsync(surfaceId, function, callId + "-after"), ExpectedCode(test));
                CheckLateResponse(client, args, callId);
                return;
            }

            if (timeout is not null || !listener)
            {
                RequireFailure(primary, ExpectedCode(test));
                CheckLateResponse(client, args, callId);
                return;
            }

            var inbound = args.GetProperty("inboundResponse");
            Require(client.TryHandleResponse(Decode(inbound, WireDirection.AgentToRenderer)), "The response matched no call.");
            Require(primary.Wait(TimeSpan.FromSeconds(5)), "The call did not finish.");
            var expectation = test.GetProperty("expect");
            Require(NodeCases.JsonEquals(primary.Result, expectation.GetProperty("result")), "The result differs.");
            if (expectation.TryGetProperty("correlatedCallId", out var correlated))
                Require(sent.Count == 1 && sent[0].Payload.GetProperty("functionCallId").GetString() == correlated.GetString(),
                    "The sent call carries another identifier.");
        }
        finally
        {
            client.Dispose();
        }
    }

    private static string ExpectedCode(JsonElement test) => test.GetProperty("expectError").GetProperty("code").GetString()!;

    private static void CheckLateResponse(AgentFunctionClient client, JsonElement args, string callId)
    {
        if (!args.TryGetProperty("lateInboundResponse", out var late))
            return;
        Require(!client.TryHandleResponse(Decode(late, WireDirection.AgentToRenderer)), $"The late response for {callId} matched a call.");
    }

    private static void RequireFailure(Task task, string code)
    {
        AgentFunctionException? failure = null;
        try
        {
            Require(task.Wait(TimeSpan.FromSeconds(5)), "The call did not finish.");
        }
        catch (AggregateException error)
        {
            failure = error.InnerException as AgentFunctionException;
        }
        Require(failure is not null, "Expected the call to fail.");
        Require(failure!.Code == code, $"Code {failure.Code} differs from {code}.");
    }

    // ---- reactive bindings routed to the agent ----

    // Each scenario runs twice: once through a router binding, and once through the mounted nodes of a NodeResolver.
    private static void RunUiBinding(JsonElement test, JsonElement scenario)
    {
        RunUiBinding(test, scenario, mounted: false);
        RunUiBinding(test, scenario, mounted: true);
    }

    private static void RunUiBinding(JsonElement test, JsonElement scenario, bool mounted)
    {
        var surfaceId = scenario.GetProperty("surfaceId").GetString()!;
        var catalogId = scenario.GetProperty("catalogId").GetString()!;
        var components = scenario.TryGetProperty("components", out var many) ? many.EnumerateArray().ToArray() : [scenario.GetProperty("component")];
        var registry = new CatalogRegistry(includeBasicCatalog: false);
        Register(registry, catalogId, mounted ? RemoteDeclarations(components, []) : new JsonObject(), mounted);
        using var session = new SurfaceSession(registry);
        Apply(session, new JsonObject
        {
            ["version"] = "1.0",
            ["createSurface"] = new JsonObject { ["surfaceId"] = surfaceId, ["catalogId"] = catalogId }
        });

        var sent = new List<WireMessage>();
        var errors = new List<RendererError>();
        using var client = new AgentFunctionClient(sent.Add);
        using var router = new FunctionCallRouter(session, registry, new FunctionEvaluator(registry), client);
        router.ErrorReported += errors.Add;

        var observed = scenario.TryGetProperty("observe", out var watch) ? watch.GetProperty("componentId").GetString() : components[0].GetProperty("id").GetString();
        var observedComponent = components.First(component => component.GetProperty("id").GetString() == observed);
        var isCheck = observedComponent.TryGetProperty("checks", out var checks);
        var propertyName = scenario.TryGetProperty("observe", out var property) ? property.GetProperty("property").GetString()! : "text";
        var expression = isCheck ? checks.EnumerateArray().First().GetProperty("condition") : observedComponent.GetProperty(propertyName);

        using var resolver = mounted ? new NodeResolver(session, registry, surfaceId, router) : null;
        using var binding = mounted ? null : router.Bind(surfaceId, "/", expression);
        var changed = new List<ResolvedNode>();
        ResolvedNode? node = null;
        if (resolver is not null)
        {
            resolver.PropsChanged += changed.Add;
            // The fixture spells check rules as bare checks. The pinned 1.0 schema declares them as @checks.
            var mountedComponents = new JsonArray();
            foreach (var component in components)
            {
                var copy = JsonNode.Parse(component.GetRawText())!.AsObject();
                if (copy.Remove("checks", out var rules))
                    copy["@checks"] = rules;
                mountedComponents.Add(copy);
            }
            Apply(session, new JsonObject
            {
                ["version"] = "1.0",
                ["updateComponents"] = new JsonObject { ["surfaceId"] = surfaceId, ["components"] = mountedComponents }
            });
            node = Mounted(resolver, observed!);
            if (isCheck)
                Require(node.Checks[0].State == CheckState.Pending && node.IsValid, "The pending check is not pending.");
        }
        JsonElement Value() => node is null ? binding!.Value : node.Props[propertyName].Value ?? Parse("null");

        var expectation = test.GetProperty("expect");
        if (expectation.TryGetProperty("initialValue", out var initial))
            Require(NodeCases.JsonEquals(Value(), initial), "The initial value differs.");
        Require(sent.Count == 1, $"Expected one outbound call but {sent.Count} were sent.");
        var outbound = sent[0].Payload;
        var expectedCall = expectation.GetProperty("outboundCall").GetProperty("callAgentFunction");
        Require(outbound.GetProperty("surfaceId").GetString() == expectedCall.GetProperty("surfaceId").GetString() &&
            NodeCases.JsonEquals(outbound.GetProperty("callFunction"), expectedCall.GetProperty("callFunction")),
            $"The outbound call differs: {outbound.GetRawText()}");

        var response = new JsonObject { ["functionCallId"] = outbound.GetProperty("functionCallId").GetString() };
        if (scenario.TryGetProperty("inboundResponseError", out var failure))
            response["error"] = JsonNode.Parse(failure.GetRawText());
        else
            response["value"] = JsonNode.Parse(scenario.GetProperty("inboundResponseValue").GetRawText());
        Require(router.ReceiveResponse(Decode(new JsonObject { ["version"] = "1.0", ["agentFunctionResponse"] = response },
            WireDirection.AgentToRenderer)), "The response matched no call.");

        if (expectation.TryGetProperty("resolvedValue", out var resolved))
        {
            Require(NodeCases.JsonEquals(Value(), resolved), $"The resolved value differs: {Value().GetRawText()}");
            if (node is not null && resolved.ValueKind != JsonValueKind.Null)
                Require(changed.Contains(node), "The node did not report the settled value.");
        }
        if (expectation.TryGetProperty("validationResult", out var validation))
        {
            var valid = validation.GetProperty("valid").GetBoolean();
            var message = validation.GetProperty("message").GetString();
            if (node is null)
            {
                Require(binding!.Passes == valid, "The check result differs.");
                Require(message == checks.EnumerateArray().First().GetProperty("message").GetString(), "The check message differs.");
            }
            else
            {
                var check = node.Checks[0];
                Require(check.State == (valid ? CheckState.Valid : CheckState.Invalid) && node.IsValid == valid &&
                    changed.Contains(node), $"The check state {check.State} differs.");
                Require(valid || check.Result!.Message == message && check.Result.Severity == validation.GetProperty("severity").GetString(),
                    "The check result differs.");
            }
        }
        if (expectation.TryGetProperty("siblings", out var siblings))
            foreach (var sibling in siblings.EnumerateObject())
            {
                var text = resolver is null
                    ? components.First(item => item.GetProperty("id").GetString() == sibling.Name).GetProperty("text")
                    : Mounted(resolver, sibling.Name).Props["text"].Value ?? Parse("null");
                Require(NodeCases.JsonEquals(text, sibling.Value.GetProperty("text")), "A sibling differs.");
            }
        if (resolver is not null)
            Require(ReferenceEquals(node, Mounted(resolver, observed!)), "The observed node was replaced.");
        if (expectation.TryGetProperty("surfaceError", out var surfaceError))
        {
            Require(errors.Count == 1, $"Expected one error but {errors.Count} were reported.");
            Require(errors[0].Code == surfaceError.GetProperty("code").GetString(), $"Code {errors[0].Code} differs.");
            Require(errors[0].FunctionCallId == outbound.GetProperty("functionCallId").GetString(), "The error names another call.");
        }
    }

    private static void RunUiAction(JsonElement expectation, JsonElement scenario)
    {
        RunUiAction(expectation, scenario, mounted: false);
        RunUiAction(expectation, scenario, mounted: true);
    }

    private static void RunUiAction(JsonElement expectation, JsonElement scenario, bool mounted)
    {
        var surfaceId = scenario.GetProperty("surfaceId").GetString()!;
        var catalogId = scenario.GetProperty("catalogId").GetString()!;
        var registry = new CatalogRegistry(includeBasicCatalog: false);
        var functions = new JsonObject();
        if (scenario.TryGetProperty("localFunctions", out var local))
            foreach (var definition in local.EnumerateObject())
                functions[definition.Name] = Declaration(definition.Name, definition.Value);
        // The mounted run places the action on a root Button with a text label.
        var components = new[]
        {
            Parse(new JsonObject
            {
                ["id"] = "root",
                ["component"] = "Button",
                ["child"] = "label",
                ["action"] = JsonNode.Parse(scenario.GetProperty("actionPayload").GetRawText())
            }.ToJsonString()),
            Parse("""{"id":"label","component":"Text","text":"Run"}""")
        };
        if (mounted)
            foreach (var (name, declaration) in RemoteDeclarations(components, functions.Select(entry => entry.Key).ToHashSet()).ToArray())
                functions[name] = declaration!.DeepClone();
        Register(registry, catalogId, functions, mounted);
        using var session = new SurfaceSession(registry);
        Apply(session, new JsonObject
        {
            ["version"] = "1.0",
            ["createSurface"] = new JsonObject { ["surfaceId"] = surfaceId, ["catalogId"] = catalogId }
        });

        var evaluator = new FunctionEvaluator(registry);
        var effects = new List<string>();
        if (local.ValueKind == JsonValueKind.Object)
            foreach (var function in local.EnumerateObject())
            {
                var write = function.Value.GetProperty("effect").GetProperty("setData");
                evaluator.RegisterHostFunction(catalogId, function.Name, call =>
                {
                    var error = session.WriteData(call.SurfaceId, write.GetProperty("path").GetString()!, write.GetProperty("value"));
                    Require(error is null, error?.ToString() ?? "");
                    effects.Add(call.Name);
                    return Parse("true");
                });
            }

        var sent = new List<WireMessage>();
        var errors = new List<RendererError>();
        var results = new List<JsonElement>();
        var events = new List<WireMessage>();
        using var client = new AgentFunctionClient(sent.Add);
        using var router = new FunctionCallRouter(session, registry, evaluator, client);
        router.ErrorReported += errors.Add;
        router.ActionDispatched += events.Add;
        using var resolver = mounted ? new NodeResolver(session, registry, surfaceId, router) : null;
        if (resolver is not null)
        {
            Apply(session, new JsonObject
            {
                ["version"] = "1.0",
                ["updateComponents"] = new JsonObject
                {
                    ["surfaceId"] = surfaceId,
                    ["components"] = new JsonArray(components.Select(component => JsonNode.Parse(component.GetRawText())).ToArray())
                }
            });
            Require(resolver.RouteFunctionAction(Mounted(resolver, "root"), "action", results.Add) is null, "The action was refused.");
        }
        else
        {
            router.InvokeFunctionAction(surfaceId, "source", "/", scenario.GetProperty("actionPayload"), results.Add);
        }
        Require(sent.Count == 1 && results.Count == 0 && errors.Count == 0, "Expected one pending call.");
        var outbound = sent[0].Payload;
        var callId = outbound.GetProperty("functionCallId").GetString();
        var expectedCall = expectation.GetProperty("outboundCall").GetProperty("callAgentFunction");
        Require(outbound.GetProperty("surfaceId").GetString() == expectedCall.GetProperty("surfaceId").GetString() &&
            NodeCases.JsonEquals(outbound.GetProperty("callFunction"), expectedCall.GetProperty("callFunction")),
            $"The outbound call differs: {outbound.GetRawText()}");

        var response = new JsonObject { ["functionCallId"] = callId };
        if (scenario.TryGetProperty("inboundResponseError", out var failure))
            response["error"] = JsonNode.Parse(failure.GetRawText());
        else
            response["value"] = JsonNode.Parse(scenario.GetProperty("inboundResponseValue").GetRawText());
        Require(router.ReceiveResponse(Decode(new JsonObject { ["version"] = "1.0", ["agentFunctionResponse"] = response },
            WireDirection.AgentToRenderer)), "The reply matched no call.");

        if (expectation.TryGetProperty("result", out var result))
            Require(results.Count == 1 && NodeCases.JsonEquals(results[0], result) && errors.Count == 0, "The action result differs.");
        if (expectation.TryGetProperty("dataModel", out var data))
        {
            Require(session.TryGetSurface(surfaceId, out var snapshot) && NodeCases.JsonEquals(snapshot!.DataModel, data),
                "The action did not preserve only the earlier effects.");
            Require(effects.SequenceEqual(["markStarted"]), "The earlier effect must run exactly once.");
        }
        if (expectation.TryGetProperty("surfaceError", out var errorExpectation))
        {
            Require(errors.Count == 1 && results.Count == 0, "Expected one action failure.");
            Require(errors[0].Code == errorExpectation.GetProperty("code").GetString() &&
                errors[0].FunctionCallId == callId, "The error does not identify the failed RPC.");
        }
        Require(sent.Count == 1 && events.Count == 0, "The function action sent an unexpected message.");
    }

    // ---- agent_function_routing ----

    // Cases that also run through the mounted nodes of a NodeResolver. Their steps evaluate only bindings and checks.
    private static readonly HashSet<string> MountedRoutingCases = new(StringComparer.Ordinal)
    {
        "test_agent_routing_pending_check_rule_is_not_failing"
    };

    private static void RunRouting(JsonElement test)
    {
        RunRouting(test, mounted: false);
        if (MountedRoutingCases.Contains(test.GetProperty("name").GetString()!))
            RunRouting(test, mounted: true);
    }

    private static void RunRouting(JsonElement test, bool mounted)
    {
        var registry = new CatalogRegistry(includeBasicCatalog: false);
        var basic = BasicFunctionDeclarations();
        var steps = test.GetProperty("steps").EnumerateArray().ToArray();
        foreach (var catalog in test.GetProperty("catalogs").EnumerateArray())
        {
            var functions = new JsonObject();
            if (catalog.TryGetProperty("localFunctions", out var local))
                foreach (var name in local.EnumerateArray())
                    functions[name.GetString()!] = basic[name.GetString()!]!.DeepClone();
            if (catalog.TryGetProperty("agentFunctions", out var declared))
                foreach (var function in declared.EnumerateObject())
                    functions[function.Name] = Declaration(function.Name, function.Value, function.Value.TryGetProperty("args", out var schema) ? schema : null);
            if (mounted)
            {
                var evaluated = steps.Where(step => step.TryGetProperty("evaluate", out _)).Select(step => step.GetProperty("evaluate")).ToArray();
                foreach (var (name, declaration) in RemoteDeclarations(evaluated, functions.Select(entry => entry.Key).ToHashSet()).ToArray())
                    functions[name] = declaration!.DeepClone();
            }
            Register(registry, catalog.GetProperty("catalogId").GetString()!, functions, mounted);
        }

        var surface = test.GetProperty("surface");
        var create = new JsonObject { ["surfaceId"] = "main" };
        if (surface.TryGetProperty("catalogId", out var defaultCatalog))
            create["catalogId"] = defaultCatalog.GetString();
        if (test.TryGetProperty("dataModel", out var model))
            create["dataModel"] = JsonNode.Parse(model.GetRawText());
        if (mounted)
            create["components"] = new JsonArray(new JsonObject { ["id"] = "root", ["component"] = "Column", ["children"] = new JsonArray() });
        using var session = new SurfaceSession(registry);
        Apply(session, new JsonObject { ["version"] = "1.0", ["createSurface"] = create });

        var sent = new List<WireMessage>();
        var errors = new List<RendererError>();
        var actions = new List<WireMessage>();
        var listener = !surface.TryGetProperty("outboundListener", out var enabled) || enabled.GetBoolean();
        using var client = new AgentFunctionClient(listener ? sent.Add : null);
        using var router = new FunctionCallRouter(session, registry, new FunctionEvaluator(registry), client);
        router.ErrorReported += errors.Add;
        router.ActionDispatched += actions.Add;
        using var resolver = mounted ? new NodeResolver(session, registry, "main", router) : null;

        var observations = new List<Observation>();
        var mountedIds = new JsonArray();
        foreach (var step in steps)
        {
            var sentBefore = sent.Count;
            var errorsBefore = errors.Count;
            var actionsBefore = actions.Count;

            if (step.TryGetProperty("evaluate", out var evaluate))
            {
                var mode = step.TryGetProperty("as", out var kind) ? kind.GetString() : "binding";
                switch (mode)
                {
                    case "action" when resolver is null:
                        router.Invoke("main", "source", "/", Parse(new JsonObject { ["functionCall"] = JsonNode.Parse(evaluate.GetRawText()) }.ToJsonString()));
                        break;
                    case "event" when resolver is null:
                        router.Invoke("main", "source", "/", Parse(new JsonObject { ["event"] = JsonNode.Parse(evaluate.GetRawText()) }.ToJsonString()));
                        break;
                    case "binding" or "check" when resolver is not null:
                        observations.Add(Mount(session, resolver, mountedIds, evaluate, mode == "check"));
                        break;
                    case "binding" or "check":
                        var binding = router.Bind("main", "/", evaluate);
                        observations.Add(new(mode == "check", () => binding.Passes, () => binding.Value));
                        break;
                    default:
                        throw new InvalidDataException($"The mounted adapter does not support '{mode}' steps.");
                }
            }
            else if (step.TryGetProperty("respond", out var respond))
            {
                var original = sent[respond.GetProperty("call").GetInt32()].Payload;
                var response = new JsonObject { ["functionCallId"] = original.GetProperty("functionCallId").GetString() };
                if (respond.TryGetProperty("error", out var failure))
                    response["error"] = JsonNode.Parse(failure.GetRawText());
                else
                    response["value"] = JsonNode.Parse(respond.GetProperty("value").GetRawText());
                router.ReceiveResponse(Decode(new JsonObject { ["version"] = "1.0", ["agentFunctionResponse"] = response },
                    WireDirection.AgentToRenderer));
            }
            else if (step.TryGetProperty("updateData", out var update))
            {
                var rejection = session.WriteData("main", update.GetProperty("path").GetString()!, update.GetProperty("value"));
                Require(rejection is null, rejection?.ToString() ?? "");
            }
            else if (step.TryGetProperty("cancelSurfaceCalls", out _))
            {
                router.CancelPendingCalls("main");
            }
            else if (step.TryGetProperty("deleteSurface", out _))
            {
                Apply(session, new JsonObject { ["version"] = "1.0", ["deleteSurface"] = new JsonObject { ["surfaceId"] = "main" } });
            }
            else
            {
                throw new InvalidDataException("Unknown step.");
            }

            CheckStep(step, sent.Skip(sentBefore).ToList(), errors.Skip(errorsBefore).ToList(), actions.Skip(actionsBefore).ToList(), observations);
        }
    }

    // A value the steps observe: a router binding, or a property or check rule of a mounted node.
    private sealed record Observation(bool IsCheck, Func<bool> Passes, Func<JsonElement> Value);

    // Adds a component that holds the expression to the root Column. A check rule passes while it is pending or valid.
    private static Observation Mount(SurfaceSession session, NodeResolver resolver, JsonArray mountedIds, JsonElement expression, bool isCheck)
    {
        var id = "evaluated" + mountedIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        mountedIds.Add(id);
        var component = isCheck
            ? new JsonObject
            {
                ["id"] = id,
                ["component"] = "TextField",
                ["label"] = "Checked",
                ["value"] = "",
                ["@checks"] = new JsonArray(new JsonObject { ["condition"] = JsonNode.Parse(expression.GetRawText()), ["message"] = "Check failed" })
            }
            : new JsonObject { ["id"] = id, ["component"] = "Text", ["text"] = JsonNode.Parse(expression.GetRawText()) };
        Apply(session, new JsonObject
        {
            ["version"] = "1.0",
            ["updateComponents"] = new JsonObject
            {
                ["surfaceId"] = "main",
                ["components"] = new JsonArray(
                    new JsonObject { ["id"] = "root", ["component"] = "Column", ["children"] = mountedIds.DeepClone() },
                    component)
            }
        });
        return isCheck
            ? new(true, () => Mounted(resolver, id).Checks[0].State is CheckState.Pending or CheckState.Valid, () => Parse("null"))
            : new(false, () => true, () => Mounted(resolver, id).Props["text"].Value ?? Parse("null"));
    }

    private static ResolvedNode Mounted(NodeResolver resolver, string componentId) =>
        resolver.Nodes.SingleOrDefault(node => node.ComponentId == componentId) ??
        throw new InvalidDataException($"Component '{componentId}' is not mounted.");

    private static void CheckStep(JsonElement step, List<WireMessage> sent, List<RendererError> errors,
        List<WireMessage> actions, List<Observation> bindings)
    {
        var expectedCalls = step.TryGetProperty("expectOutbound", out var outbound) ? outbound.EnumerateArray().ToList() : [];
        Require(sent.Count == expectedCalls.Count, $"Expected {expectedCalls.Count} outbound calls but {sent.Count} were sent.");
        var anyOrder = step.TryGetProperty("outboundInAnyOrder", out var unordered) && unordered.GetBoolean();
        var remaining = expectedCalls.ToList();
        foreach (var message in sent)
        {
            Require(message.Payload.GetProperty("surfaceId").GetString() == "main", "A call names another surface.");
            var call = message.Payload.GetProperty("callFunction");
            var index = anyOrder
                ? remaining.FindIndex(expected => NodeCases.JsonEquals(call, expected))
                : NodeCases.JsonEquals(call, remaining[0]) ? 0 : -1;
            Require(index >= 0, $"Unexpected outbound call {call.GetRawText()}.");
            remaining.RemoveAt(index);
        }

        var expectedErrors = step.TryGetProperty("expectErrors", out var reported) ? reported.EnumerateArray().ToList() : [];
        Require(errors.Count == expectedErrors.Count,
            $"Expected {expectedErrors.Count} errors but received: {string.Join("; ", errors.Select(error => error.Code + " " + error.Message))}");
        for (var i = 0; i < errors.Count; i++)
        {
            Require(errors[i].Code == expectedErrors[i].GetProperty("code").GetString(), $"Error code {errors[i].Code} differs.");
            if (expectedErrors[i].TryGetProperty("message", out var text))
                Require(errors[i].Message.Contains(text.GetString()!, StringComparison.Ordinal), $"Error message '{errors[i].Message}' differs.");
        }

        var expectedActions = step.TryGetProperty("expectActions", out var dispatched) ? dispatched.EnumerateArray().ToList() : [];
        Require(actions.Count == expectedActions.Count, $"Expected {expectedActions.Count} actions but {actions.Count} were dispatched.");
        for (var i = 0; i < actions.Count; i++)
        {
            var actual = actions[i].Payload;
            var expected = expectedActions[i];
            Require(actual.GetProperty("name").GetString() == expected.GetProperty("name").GetString(), "The action name differs.");
            Require(NodeCases.JsonEquals(actual.GetProperty("context"), expected.GetProperty("context")), "The action context differs.");
            if (expected.TryGetProperty("userMessage", out var userMessage))
                Require(actual.TryGetProperty("userMessage", out var actualMessage) && actualMessage.GetString() == userMessage.GetString(),
                    "The user message differs.");
            else
                Require(!actual.TryGetProperty("userMessage", out _), "The action carries an unexpected user message.");
        }

        if (!step.TryGetProperty("expectValues", out var values))
            return;
        var expectedValues = values.EnumerateArray().ToList();
        Require(bindings.Count == expectedValues.Count, $"Expected {expectedValues.Count} values but {bindings.Count} bindings exist.");
        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            var matches = binding.IsCheck
                ? expectedValues[i].ValueKind == (binding.Passes() ? JsonValueKind.True : JsonValueKind.False)
                : NodeCases.JsonEquals(binding.Value(), expectedValues[i]);
            Require(matches, $"Value {i} is {(binding.IsCheck ? binding.Passes().ToString() : binding.Value().GetRawText())} but expected {expectedValues[i].GetRawText()}.");
        }
    }

    // ---- catalogs ----

    private static JsonObject BasicFunctionDeclarations() => BasicDefinition()["functions"]!.DeepClone().AsObject();

    private static JsonObject BasicDefinition()
    {
        var basic = new CatalogRegistry();
        Require(basic.TryGetDefinition(CatalogRegistry.BasicCatalogId, out var definition), "The Basic Catalog is unavailable.");
        return JsonNode.Parse(definition.GetRawText())!.AsObject();
    }

    // Core validates component calls against the catalog, so a mounted run declares each function that the fixture
    // leaves to the agent. A declaration without an implementation routes to the agent as an undeclared function does,
    // and the any-object argument schema sends the arguments unchanged.
    private static JsonObject RemoteDeclarations(IEnumerable<JsonElement> values, HashSet<string> local)
    {
        var declarations = new JsonObject();
        void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    Visit(item);
                return;
            }
            if (value.ValueKind != JsonValueKind.Object)
                return;
            if (value.TryGetProperty("@call", out var call) && call.GetString() is { } name && !name.StartsWith('@') &&
                !local.Contains(name) && !declarations.ContainsKey(name))
                declarations[name] = Declaration(name, Parse("{}"));
            foreach (var member in value.EnumerateObject())
                Visit(member.Value);
        }
        foreach (var value in values)
            Visit(value);
        return declarations;
    }

    // A catalog function that the fixture describes with its metadata. The args schema defaults to any object.
    private static JsonNode Declaration(string name, JsonElement metadata, JsonElement? argsSchema = null)
    {
        var declaration = new JsonObject
        {
            ["type"] = "object",
            ["returnType"] = "any",
            ["properties"] = new JsonObject
            {
                ["@call"] = new JsonObject { ["const"] = name },
                ["args"] = argsSchema is { } schema
                    ? JsonNode.Parse(schema.GetRawText())
                    : metadata.TryGetProperty("parameters", out var parameters)
                        ? JsonNode.Parse(parameters.GetRawText())
                        : new JsonObject { ["type"] = "object" }
            },
            ["required"] = new JsonArray("@call", "args")
        };
        foreach (var key in new[] { "allowedCallers", "requiresUserActivation", "returnType" })
            if (metadata.TryGetProperty(key, out var value))
                declaration[key] = JsonNode.Parse(value.GetRawText());
        return declaration;
    }

    // A mounted run copies the Basic Catalog components it places, with the shared definitions they reference, and
    // lists those components and the functions in the catalog's unions.
    private static void Register(CatalogRegistry registry, string catalogId, JsonObject functions, bool mounted = false)
    {
        var components = new JsonObject();
        var definitions = new JsonObject();
        if (mounted)
        {
            var basic = BasicDefinition();
            foreach (var name in new[] { "Button", "Column", "Text", "TextField" })
                components[name] = basic["components"]![name]!.DeepClone();
            foreach (var (name, schema) in basic["$defs"]!.AsObject())
                if (name is not ("anyComponent" or "anyFunction"))
                    definitions[name] = schema!.DeepClone();
            definitions["anyComponent"] = new JsonObject
            {
                ["oneOf"] = Union("components", components),
                ["discriminator"] = new JsonObject { ["propertyName"] = "component" }
            };
            definitions["anyFunction"] = functions.Count == 0 ? false : new JsonObject { ["oneOf"] = Union("functions", functions) };
        }
        var definition = new JsonObject
        {
            ["protocolVersion"] = WireMessage.Version,
            ["catalogId"] = catalogId,
            ["components"] = components,
            ["functions"] = functions
        };
        if (definitions.Count > 0)
            definition["$defs"] = definitions;
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(definition.ToJsonString()), out var error),
            error?.ToString() ?? "Catalog registration failed.");

        static JsonArray Union(string group, JsonObject members) =>
            new(members.Select(member => (JsonNode)new JsonObject { ["$ref"] = $"#/{group}/{member.Key}" }).ToArray());
    }

    private static WireMessage Decode(JsonElement message, WireDirection direction) =>
        Decode(JsonNode.Parse(message.GetRawText())!, direction);

    private static WireMessage Decode(JsonNode message, WireDirection direction)
    {
        Require(WireMessage.TryDecode(Encoding.UTF8.GetBytes(message.ToJsonString()), direction, out var decoded, out var problem),
            problem?.ToString() ?? "Invalid fixture message.");
        return decoded!;
    }

    private static void Apply(SurfaceSession session, JsonObject message)
    {
        var rejection = session.Apply(Decode(message, WireDirection.AgentToRenderer));
        Require(rejection is null, rejection?.ToString() ?? "The fixture message was rejected.");
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
