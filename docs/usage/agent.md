# Agent quickstart

Start with an `A2UI.Agent` console consumer using the [local SDK packages](README.md#use-the-local-packages). This example negotiates the Basic Catalog, sends a deterministic first turn, then parses a scripted Direct JSON response across arbitrary chunk boundaries for the second turn. The renderer is an in-process Core session so the example runs without model credentials.

```csharp
using A2UI.Agent;
using A2UI.Core;

var catalogues = new CatalogRegistry();
using var renderer = new SurfaceSession(catalogues);
var capabilities = catalogues.CreateRendererCapabilities();
if (RequestProcessor.Negotiate(catalogues, capabilities.Value, acceptInline: false, out var request) is { } negotiationError)
    throw new InvalidOperationException(negotiationError.ToString());

using var mirror = request!.CreateSession();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var cancellation = timeout.Token;
var surfaceId = Guid.NewGuid().ToString("N");

// Construct a deterministic turn from application-owned input.
var opening = $$$"""
    {"version":"1.0","createSurface":{
      "surfaceId":"{{{surfaceId}}}","catalogId":"{{{CatalogRegistry.BasicCatalogId}}}",
      "components":[{"id":"root","component":"Text","text":"Booking pending"}]
    }}
    """;
if (DirectJson.Compile(opening, catalogues, out var messages) is { } compileError)
    throw new InvalidOperationException(compileError.ToString());
if (mirror.Prepare(messages, out var prepared, cancellation) is { } prepareError)
    throw new InvalidOperationException(prepareError.ToString());

using (var turn = prepared!)
{
    turn.Commit(cancellation);
    while (turn.NextMessage is { } message)
    {
        await DeliverAsync(message);
        turn.Acknowledge(message);
    }
}

// Consume one scripted provider response. A real host uses request.Prompt
// when building the provider request and passes its ordered chunks to Read.
var stream = new DirectJsonStream(mirror, maxCharacters: 16_384);
var response = DirectJson.Wrap([
    new("Appointment confirmed."),
    new($$$"""{"version":"1.0","deleteSurface":{"surfaceId":"{{{surfaceId}}}"}}""", IsPayload: true)
]);
for (var offset = 0; offset < response.Length; offset += 17)
    await ConsumeAsync(response.Substring(offset, Math.Min(17, response.Length - offset)));
await ConsumeAsync("", final: true);

if (renderer.TryGetSurface(surfaceId, out _) || mirror.TryGetSurface(surfaceId, out _))
    throw new InvalidOperationException("The closing turn left a live surface.");
Console.WriteLine("Agent batch and streaming turns passed.");

async Task ConsumeAsync(string chunk, bool final = false)
{
    var diagnostic = stream.Read(chunk, out var parts, final, cancellation);
    foreach (var part in parts)
    {
        if (part.Text is { } text)
            Console.Write(text);
        if (part.Messages is { } batch)
            foreach (var message in batch)
                await DeliverAsync(message);
    }
    if (diagnostic is not null)
        throw new InvalidOperationException(diagnostic.ToString());
    if (final)
        Console.WriteLine();
}

Task DeliverAsync(WireMessage message)
{
    // Replace this with an awaited write to the renderer transport.
    if (renderer.Apply(message, cancellation) is { } error)
        throw new InvalidOperationException(error.ToString());
    return Task.CompletedTask;
}

```

- Keep one mirror per renderer conversation. Use `RequestProcessor.Fixed` when both sides agree on a fixed registry. For negotiation, use the selected catalogues. In this example the Basic Catalog is the only candidate, so the original registry also works for compiling the deterministic batch.
- A batch validates before commit. Confirm delivery before acknowledging each message. A committed batch with an interruption retains its unsent suffix through `RendererState.PendingDelivery`. Choose a reconnect contract that reflects whether the renderer kept its state and how the transport confirms delivery.
- A stream can return an accepted prefix alongside a terminal diagnostic. Deliver the prefix in order, handle the failure and settle the conversation according to your reconnect policy. Await delivery before reading the next chunk. For example, the [SSE sample](../../samples/AspNetCoreSse/README.md) discards interrupted sessions and starts with fresh IDs.
- Use `RequestProcessor.CreateMessageProcessor` to route returned actions, errors and RPC. The application authenticates the owner, validates submitted values for its business operations, and configures its provider, timeouts, transport and durable state.

See the [compatibility matrix](../compatibility.md#required-stable-coverage) for Direct JSON, Express batch, Express streaming and macros.
