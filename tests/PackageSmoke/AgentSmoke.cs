using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

internal static class AgentSmoke
{
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

        static WireMessage Decode(string json) =>
            WireMessage.TryDecode(System.Text.Encoding.UTF8.GetBytes(json), WireDirection.AgentToRenderer,
                out var message, out var diagnostic) ? message! : throw new InvalidOperationException(diagnostic?.ToString());
    }

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
    }

    internal static void Run()
    {
        CheckDelivery();
        CheckStreaming();
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
