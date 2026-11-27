using System.Text;
using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

internal static class RpcReplay
{
    internal static async Task<object> Run(SurfaceSession session, string surfaceId, string catalogPath, JsonElement? peer)
    {
        using var definition = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var catalogId = definition.RootElement.GetProperty("catalogId").GetString()!;
        var catalogs = new CatalogRegistry(includeBasicCatalog: false);
        var agents = new AgentFunctions();
        var registration = agents.Register(catalogs, definition.RootElement, [
            new("echoAgent", (call, _) => call.Arguments.GetProperty("fail").GetBoolean()
                ? throw new AgentFunctionException("EXECUTION_ERROR", "Scripted appointment lookup failed.")
                : ValueTask.FromResult(JsonSerializer.SerializeToElement("Agent answered")))
        ]);
        if (registration is not null)
            throw new InvalidOperationException(registration.ToString());
        var functions = new FunctionEvaluator(catalogs);
        functions.RegisterHostFunction(catalogId, "echoRenderer", call => call.Args["fail"].GetBoolean()
            ? throw new InvalidOperationException("Scripted appointment lookup failed.")
            : JsonSerializer.SerializeToElement("Renderer answered"));
        var renderer = new RendererFunctionHandler(catalogs, functions, session);
        using var rendererCalls = new RendererFunctionClient(catalogs);
        var inbound = new RendererMessageProcessor(catalogs, agents,
            onRendererFunctionResponse: (reply, _) => ValueTask.FromResult(rendererCalls.TryHandleResponse(reply)));
        var sentAgentCalls = new List<WireMessage>();
        using var agentCalls = new AgentFunctionClient(sentAgentCalls.Add);
        var sentRendererCalls = new List<JsonElement>();
        var rendererReplies = new List<JsonElement>();
        var agentReplies = new List<JsonElement>();

        for (var index = 0; index < 2; index++)
        {
            var arguments = JsonSerializer.SerializeToElement(new { fail = index == 1 });
            var rendererCall = rendererCalls.Call(catalogId, "echoRenderer", arguments, surfaceId, $"renderer-{index}");
            sentRendererCalls.Add(rendererCall.Message.Envelope);
            var rendererReply = renderer.Handle(PeerMessage("rendererCalls", index, rendererCall.Message, WireDirection.AgentToRenderer));
            rendererReplies.Add(rendererReply.Envelope);
            var routedReply = PeerMessage("rendererReplies", index, rendererReply, WireDirection.RendererToAgent);
            var result = await inbound.ProcessAsync(Bytes(routedReply.Envelope));
            if (!result.Handled || result.Diagnostic is not null)
                throw new InvalidOperationException($"Renderer reply was not handled: {result.Diagnostic}");
            await CheckOutcome(rendererCall.Result, "Renderer answered", index == 1);

            var call = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["@call"] = "echoAgent",
                ["catalogId"] = catalogId,
                ["args"] = arguments
            });
            var pendingAgent = agentCalls.CallAsync(surfaceId, call, $"agent-{index}", TimeSpan.FromSeconds(5));
            var peerCall = PeerMessage("agentCalls", index, sentAgentCalls[index], WireDirection.RendererToAgent);
            var reply = await inbound.ProcessAsync(Bytes(peerCall.Envelope));
            if (!reply.Handled || reply.Reply is not { } agentReply)
                throw new InvalidOperationException($"Agent call was not handled: {reply.Diagnostic}");
            agentReplies.Add(agentReply.Envelope);
            if (!agentCalls.TryHandleResponse(PeerMessage("agentReplies", index, agentReply, WireDirection.AgentToRenderer)))
                throw new InvalidOperationException("Agent reply was not correlated to its call.");
            await CheckOutcome(pendingAgent, "Agent answered", index == 1);
        }

        return new
        {
            rendererCalls = sentRendererCalls,
            rendererReplies,
            agentCalls = sentAgentCalls.Select(call => call.Envelope),
            agentReplies
        };

        WireMessage PeerMessage(string name, int index, WireMessage local, WireDirection direction)
        {
            if (peer is null)
                return local;
            return WireMessage.TryDecode(Bytes(peer.Value.GetProperty(name)[index]), direction, out var message, out var diagnostic)
                ? message! : throw new InvalidOperationException(diagnostic?.ToString());
        }
    }

    private static byte[] Bytes(JsonElement value) => Encoding.UTF8.GetBytes(value.GetRawText());

    private static async Task CheckOutcome(Task<JsonElement> call, string value, bool fails)
    {
        try
        {
            var actual = await call;
            if (fails || !actual.ValueEquals(value))
                throw new InvalidOperationException("Unexpected RPC result.");
        }
        catch (AgentFunctionException error) when (fails && error.Code == "EXECUTION_ERROR")
        {
        }
    }
}
