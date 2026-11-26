using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

internal static class AgentSmoke
{
    private static void CheckMixedCatalogDirectJson()
    {
        var catalogs = new CatalogRegistry();
        const string custom = """
            {"protocolVersion":"1.0","catalogId":"smoke/custom","components":{
                "Text":{"type":"object","properties":{"component":{"const":"Text"}},"required":["component"]},
                "Chart":{"type":"object","properties":{"component":{"const":"Chart"}},"required":["component"]}
            }}
            """;
        if (!catalogs.TryRegister(System.Text.Encoding.UTF8.GetBytes(custom), out var registrationError))
            throw new InvalidOperationException(registrationError?.ToString());

        const string mixed = """
            [
                {"version":"1.0","createSurface":{"surfaceId":"report","components":[
                    {"id":"root","component":"Column","children":["chart","caption"]},
                    {"id":"chart","component":"Chart"},
                    {"id":"caption","component":"Text","@catalogId":"smoke/custom"}
                ]}}
            ]
            """;
        var compilationError = DirectJson.Compile(mixed, catalogs, out var messages);
        if (compilationError is not null)
            throw new InvalidOperationException(compilationError.ToString());
        var payload = messages.Single().Payload;
        var components = payload.GetProperty("components");
        if (payload.TryGetProperty("catalogId", out _) ||
            !components[0].TryGetProperty("@catalogId", out _) ||
            !components[1].GetProperty("@catalogId").ValueEquals("smoke/custom") ||
            !components[2].GetProperty("@catalogId").ValueEquals("smoke/custom"))
            throw new InvalidOperationException("Mixed-catalog routing was not written to the wire.");

        var ambiguous = mixed.Replace(",\"@catalogId\":\"smoke/custom\"", "", StringComparison.Ordinal);
        if (DirectJson.Compile(ambiguous, catalogs, out var rejected)?.Message.Contains("ambiguous", StringComparison.Ordinal) != true ||
            rejected.Count != 0)
            throw new InvalidOperationException("An ambiguous component was accepted.");
    }

    private static void CheckDelivery()
    {
        using var state = new RendererState(new CatalogRegistry());
        var create = Decode("""{"version":"1.0","createSurface":{"surfaceId":"s"}}""");
        var update = Decode("""{"version":"1.0","updateDataModel":{"surfaceId":"s","value":{"count":1}}}""");
        var bad = Decode("""{"version":"1.0","updateDataModel":{"surfaceId":"unknown","value":2}}""");
        if (state.Prepare([create, bad], out var failed) is null || failed is not null ||
            state.TryGetSurface("s", out _) || state.PendingDelivery is not null)
            throw new InvalidOperationException("Rejected batch committed state or messages.");

        if (state.Prepare([create, update], out var candidate) is { } error)
            throw new InvalidOperationException(error.ToString());
        using var turn = candidate!;
        try
        {
            turn.Commit(new CancellationToken(true));
            throw new InvalidOperationException("Cancelled commit succeeded.");
        }
        catch (OperationCanceledException)
        {
            if (state.TryGetSurface("s", out _) || turn.NextMessage is not null)
                throw new InvalidOperationException("Cancelled commit changed the mirror.");
        }
        turn.Commit();
        turn.Acknowledge(create);
        if (state.Prepare([update], out var next) is not null)
            throw new InvalidOperationException("Could not prepare next turn.");
        using var followup = next!;
        try
        {
            followup.Commit();
        }
        catch (InvalidOperationException) when (ReferenceEquals(state.PendingDelivery, turn))
        {
            if (turn.EmittedCount != 1 || !ReferenceEquals(state.PendingDelivery.NextMessage, update))
                throw new InvalidOperationException("Partial delivery cursor changed.");
            turn.Acknowledge(update);
            followup.Commit();
            followup.Acknowledge(update);
            if (state.PendingDelivery is null)
                return;
        }
        throw new InvalidOperationException("Delivery did not resume in order.");
    }

    private static WireMessage Decode(string json) =>
        WireMessage.TryDecode(System.Text.Encoding.UTF8.GetBytes(json), WireDirection.AgentToRenderer,
            out var message, out var diagnostic) ? message! : throw new InvalidOperationException(diagnostic?.ToString());

    private static void CheckStreaming()
    {
        using var state = new RendererState(new CatalogRegistry());
        var stream = new DirectJsonStream(state);
        try
        {
            stream.Read("<a2ui-json>", out _, cancellationToken: new CancellationToken(true));
            throw new InvalidOperationException("A cancelled read succeeded.");
        }
        catch (OperationCanceledException)
        {
        }

        // The cancelled read consumed nothing, so the same stream still accepts the first message,
        // and a bad second message is rejected without retracting the message already sent.
        var first = """{"version":"1.0","createSurface":{"surfaceId":"s"}}""";
        var bad = """{"version":"1.0","updateDataModel":{"surfaceId":"gone","value":1}}""";
        var issue = stream.Read("<a2ui-json>[" + first + "," + bad + ",", out var sent);
        if (issue?.Code != "ValidationError" || sent.Count != 1 || sent[0].Messages?.Count != 1 ||
            !state.TryGetSurface("s", out _))
            throw new InvalidOperationException(issue?.ToString() ?? "A failure after a partial send retracted output.");
        try
        {
            stream.Read("]</a2ui-json>", out _);
            throw new InvalidOperationException("A failed stream accepted more input.");
        }
        catch (InvalidOperationException) when (state.TryGetSurface("s", out _))
        {
        }

        using var limited = new RendererState(new CatalogRegistry());
        var small = new DirectJsonStream(limited, maxCharacters: 16);
        if (small.Read("<a2ui-json>[" + first, out _)?.Code != "ResourceLimit")
            throw new InvalidOperationException("The stream size limit was not enforced.");

        CheckRejectedPrefix(first, bad);
        CheckRejectedBlockRecovery(first);
    }

    private static async Task CheckStreamingBackpressure()
    {
        using var state = new RendererState(new CatalogRegistry());
        var stream = new DirectJsonStream(state);
        var deliveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunksRead = 0;
        var delivered = new List<string>();
        var pump = Pump();

        try
        {
            await deliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (chunksRead != 1 || state.TryGetSurface("second", out _))
                throw new InvalidOperationException("The host read ahead while delivery was blocked.");
        }
        finally
        {
            releaseDelivery.TrySetResult();
            await pump;
        }

        if (chunksRead != 2 || !delivered.SequenceEqual(new[] { "first", "second" }) ||
            !state.TryGetSurface("second", out _))
            throw new InvalidOperationException("The host did not resume ordered delivery.");

        IEnumerable<string> ReadChunks()
        {
            foreach (var id in new[] { "first", "second" })
            {
                chunksRead++;
                yield return $$$"""<a2ui-json>{"version":"1.0","createSurface":{"surfaceId":"{{{id}}}"}}</a2ui-json>""";
            }
        }

        async Task Pump()
        {
            foreach (var chunk in ReadChunks())
            {
                if (stream.Read(chunk, out var parts) is { } error)
                    throw new InvalidOperationException(error.ToString());

                foreach (var message in parts.SelectMany(part => part.Messages ?? []))
                {
                    if (delivered.Count == 0)
                    {
                        deliveryStarted.TrySetResult();
                        await releaseDelivery.Task;
                    }

                    delivered.Add(message.Payload.GetProperty("surfaceId").GetString()!);
                }
            }

            if (stream.Read("", out var tail, final: true) is not null || tail.Count != 0)
                throw new InvalidOperationException("The response did not finalise cleanly.");
        }
    }

    private static void CheckRejectedPrefix(string first, string bad)
    {
        const string open = "<a2ui-json>";
        const string close = "</a2ui-json>";
        const string second = """{"version":"1.0","createSurface":{"surfaceId":"t"}}""";
        using var malformedState = new RendererState(new CatalogRegistry());
        var malformed = new DirectJsonStream(malformedState);
        if (malformed.Read(open + "[" + first + second, out var prefix) is not null ||
            prefix.Sum(p => p.Messages?.Count ?? 0) != 1 ||
            malformed.Read("]" + close, out var rejected)?.Code != "ParseError" ||
            rejected.Count != 0 || !malformedState.TryGetSurface("s", out _) ||
            malformedState.TryGetSurface("t", out _))
            throw new InvalidOperationException("An object without an array separator was emitted.");

        using var completeState = new RendererState(new CatalogRegistry());
        var complete = new DirectJsonStream(completeState);
        if (complete.Read(open + "[" + first + "," + bad + "]" + close, out var accepted)?.Code != "ValidationError" ||
            accepted.Sum(p => p.Messages?.Count ?? 0) != 1 || !completeState.TryGetSurface("s", out _))
            throw new InvalidOperationException("A complete block lost the valid prefix before a terminal validation error.");
    }

    private static void CheckRejectedBlockRecovery(string first)
    {
        using var state = new RendererState(new CatalogRegistry());
        var stream = new DirectJsonStream(state, continueAfterRejectedBlock: true);
        if (stream.Read("""<a2ui-json>{"broken":"unterminated</a2ui-""", out var waiting) is not null ||
            waiting.Count != 0 ||
            stream.Read("json> after <a2ui-json>" + first + "</a2ui-json>", out var recovered) is not null ||
            recovered.Count != 3 || recovered[0].Error?.Code != "ParseError" ||
            recovered[1].Text != "after" || recovered[2].Messages?.Count != 1 ||
            !state.TryGetSurface("s", out _) || stream.InBlock)
            throw new InvalidOperationException("Recovery lost the text and block after an unterminated string.");

        var literal = new DirectJsonStream(state, continueAfterRejectedBlock: true);
        const string text = "</a2ui-json><a2ui-json>[]";
        if (literal.Read("""<a2ui-json>{"version":"1.0","updateDataModel":{"surfaceId":"s","value":{"text":"</a2ui-""",
                out var partial) is not null || partial.Count != 0 ||
            literal.Read("""json><a2ui-json>[]" }}}</a2ui-json>""", out var delivered) is not null ||
            delivered.Count != 1 || delivered[0].Messages?.Count != 1 ||
            delivered[0].Messages![0].Payload.GetProperty("value").GetProperty("text").GetString() != text)
            throw new InvalidOperationException("Recovery treated literal JSON string content as framing.");

        var final = new DirectJsonStream(state, continueAfterRejectedBlock: true);
        if (final.Read("""<a2ui-json>{"broken":"unterminated</a2ui-json> after""", out var tail, final: true) is not null ||
            tail.Count != 2 || tail[0].Error?.Code != "ParseError" || tail[1].Text != "after")
            throw new InvalidOperationException("Recovery lost the text after the last rejected block.");
    }

    private static void CheckInboundTurn()
    {
        var catalogs = new CatalogRegistry(includeBasicCatalog: false);
        using var source = JsonDocument.Parse("""
            {"catalogId":"smoke/form","protocolVersion":"1.0","components":{}}
            """);
        using var arguments = JsonDocument.Parse("""
            {"type":"object","properties":{"salutation":{"type":"string","default":"Hello"}}}
            """);
        var functions = new AgentFunctions();
        var problem = functions.Register(catalogs, source.RootElement,
            [new("greet", (call, _) => ValueTask.FromResult(call.Arguments.GetProperty("salutation")),
                arguments.RootElement, "string")]);
        if (problem is not null)
            throw new InvalidOperationException(problem.ToString());
        if (RequestProcessor.Fixed(catalogs, out var request) is { } configurationError)
            throw new InvalidOperationException(configurationError.ToString());

        using var state = request!.CreateSession();
        var create = Decode("""{"version":"1.0","createSurface":{"surfaceId":"form","catalogId":"smoke/form"}}""");
        if (state.Prepare([create], out var initial) is { } initialError)
            throw new InvalidOperationException(initialError.ToString());
        using var initialTurn = initial!;
        initialTurn.Commit();
        initialTurn.Acknowledge(create);

        WireMessage? received = null;
        var inbound = request.CreateMessageProcessor(functions, onAction: (message, _) =>
        {
            received = message;
            return ValueTask.FromResult(true);
        });
        var routed = inbound.ProcessAsync("""
            {"version":"1.0","action":{"name":"submit","sourceComponentId":"button",
              "surfaceId":"form","timestamp":"2026-01-01T00:00:00Z","context":{"name":"Ada"}}}
            """u8.ToArray()).AsTask().GetAwaiter().GetResult();
        if (!routed.Handled || routed.Reply is not null || routed.Diagnostic is not null ||
            received?.Payload.GetProperty("context").GetProperty("name").GetString() != "Ada")
            throw new InvalidOperationException("The action did not reach the second turn.");

        var rpc = inbound.ProcessAsync("""
            {"version":"1.0","callAgentFunction":{"functionCallId":"call-1","surfaceId":"form",
              "callFunction":{"@call":"greet","catalogId":"smoke/form","args":{}}}}
            """u8.ToArray()).AsTask().GetAwaiter().GetResult();
        if (!rpc.Handled || rpc.Reply?.Payload.GetProperty("value").GetString() != "Hello" ||
            rpc.Reply.Payload.GetProperty("functionCallId").GetString() != "call-1")
            throw new InvalidOperationException("The agent function did not return its correlated result.");

        var remove = Decode("""{"version":"1.0","deleteSurface":{"surfaceId":"form"}}""");
        if (state.Prepare([remove], out var final) is { } finalError)
            throw new InvalidOperationException(finalError.ToString());
        using var finalTurn = final!;
        finalTurn.Commit();
        finalTurn.Acknowledge(remove);
        if (state.TryGetSurface("form", out _))
            throw new InvalidOperationException("The second turn did not close the form.");
    }

    private static void CheckSkillExport()
    {
        var catalog = new CatalogRegistry();
        if (DirectJsonPrompt.Create(catalog, out var prompt) is { } error)
            throw new InvalidOperationException(error.ToString());

        var directory = Directory.CreateTempSubdirectory("a2ui-skill-smoke-");
        try
        {
            var skill = prompt!.ToSkillFile();
            var path = Path.Combine(directory.FullName, skill.FileName);
            var diagnostic = SkillSet.ExportToDirectory(directory.FullName, [skill]);
            if (diagnostic is not null || File.ReadAllText(path) != prompt.ToSkill())
                throw new InvalidOperationException(diagnostic?.ToString() ?? "The generated skill changed during export.");

            var stale = Path.Combine(Path.GetDirectoryName(path)!, "stale.txt");
            File.WriteAllText(stale, "old");
            diagnostic = SkillSet.ExportToDirectory(directory.FullName, [skill]);
            if (diagnostic is not null || File.Exists(stale) || File.ReadAllText(path) != skill.Content)
                throw new InvalidOperationException(diagnostic?.ToString() ?? "Repeated export did not replace stale content.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }

    }

    private static void CheckRendererFunctionCall()
    {
        var catalogs = new CatalogRegistry(includeBasicCatalog: false);
        const string catalog = """
            {"catalogId":"test:device","protocolVersion":"1.0","functions":{
              "getLocale":{"type":"object","returnType":"string","allowedCallers":"rendererOrAgent",
                "properties":{"@call":{"const":"getLocale"},"args":{"type":"object"}},"required":["@call"]}}}
            """;
        if (!catalogs.TryRegister(System.Text.Encoding.UTF8.GetBytes(catalog), out var error))
            throw new InvalidOperationException(error!.ToString());
        if (RequestProcessor.Fixed(catalogs, out var request) is { } requestError)
            throw new InvalidOperationException(requestError.ToString());

        using var client = request!.CreateRendererFunctionClient();
        var incoming = request.CreateMessageProcessor(
            onRendererFunctionResponse: (message, _) => ValueTask.FromResult(client.TryHandleResponse(message)));
        var call = client.Call("test:device", "getLocale", functionCallId: "locale");
        const string response = """{"version":"1.0","rendererFunctionResponse":{"functionCallId":"locale","value":"en-GB"}}""";
        var payload = System.Text.Encoding.UTF8.GetBytes(response);
        if (call.Message.Kind != "callRendererFunction" ||
            !incoming.ProcessAsync(payload).GetAwaiter().GetResult().Handled ||
            !call.Result.IsCompletedSuccessfully || !call.Result.Result.ValueEquals("en-GB") ||
            incoming.ProcessAsync(payload).GetAwaiter().GetResult().Handled)
            throw new InvalidOperationException("The renderer function response was not correlated exactly once.");
    }

    internal static void Run()
    {
        CheckSkillExport();
        CheckDelivery();
        CheckMixedCatalogDirectJson();
        CheckInboundTurn();
        CheckRendererFunctionCall();
        CheckStreaming();
        CheckStreamingBackpressure().GetAwaiter().GetResult();
        using var mirror = new RendererState(new CatalogRegistry());
        const string response = """Hello <a2ui-json>{"version":"1.0","createSurface":{"surfaceId":"form"}}</a2ui-json> Done""";
        var issue = DirectJson.ParseResponse(response, mirror, out var parts, out var candidate);
        using var turn = candidate;
        if (issue is not null || turn is null || parts.Count != 3 || parts[0].Text != "Hello" ||
            parts[2].Text != "Done" || parts[1].Messages?.Count != 1)
            throw new InvalidOperationException(issue?.ToString() ?? "Mixed response did not compile.");
        turn.Commit();
        turn.Acknowledge(parts[1].Messages![0]);
        var broken = DirectJson.ParseResponse("Before <a2ui-json>[", mirror, out var partial, out var rejected);
        if (broken?.Code != "ParseError" || partial.Count != 1 || partial[0].Text != "Before" || rejected is not null)
            throw new InvalidOperationException("Truncated response lost its diagnostic prefix.");
        var host = new CatalogRegistry();
        using var capabilities = JsonDocument.Parse("""
            {"1.0":{"supportedCatalogIds":["https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json","unknown"]}}
            """);
        if (!CatalogNegotiation.TryResolve(host, capabilities.RootElement, acceptInlineCatalogs: false,
                out var active, out var error) || active is null ||
            !active.CatalogIds.SequenceEqual(new[] { CatalogRegistry.BasicCatalogId }) ||
            !active.Remove(CatalogRegistry.BasicCatalogId) ||
            !host.TryGetDefinition(CatalogRegistry.BasicCatalogId, out _))
            throw new InvalidOperationException(error?.ToString() ?? "Agent catalog negotiation smoke assertion failed.");
    }
}
