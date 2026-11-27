using A2UI.Agent;
using A2UI.Core;
using Microsoft.AspNetCore.Http;

using var state = new RendererState(new CatalogRegistry());
using var body = new PausedBody();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var response = new DefaultHttpContext().Response;
response.Body = body;
var events = new SseEvents(response);
var chunksRead = 0;
var writing = events.StreamAsync(state, Chunks(), timeout.Token);
await body.Flushing.Task.WaitAsync(timeout.Token);
if (chunksRead != 1 || writing.IsCompleted)
    throw new InvalidOperationException("The SSE writer read another chunk while response delivery was blocked.");
timeout.Cancel();
try
{
    await writing;
    throw new InvalidOperationException("The blocked delivery did not observe cancellation.");
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested)
{
    if (chunksRead != 1)
        throw new InvalidOperationException("The SSE writer read another chunk after cancellation.");
}
Console.WriteLine("SSE delivery applies backpressure and observes cancellation.");

IEnumerable<string> Chunks()
{
    chunksRead++;
    yield return "Open<a2ui-json>";
    chunksRead++;
    yield return "[]</a2ui-json>";
}

internal sealed class PausedBody : MemoryStream
{
    internal TaskCompletionSource Flushing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Flushing.TrySetResult();
        return Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
