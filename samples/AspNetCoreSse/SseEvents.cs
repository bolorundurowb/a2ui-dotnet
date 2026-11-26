using System.Globalization;
using System.Text;
using System.Text.Json;
using A2UI.Agent;

internal sealed class SseEvents(HttpResponse response)
{
    private int _sequence;

    internal async Task WriteAsync(string kind, string json, CancellationToken cancellation)
    {
        // Both generated JSON and decoded envelopes may contain newlines. Each line needs its own SSE prefix.
        using var data = new StringReader(json);
        var frame = new StringBuilder();
        frame.Append("id: ").Append((++_sequence).ToString(CultureInfo.InvariantCulture)).Append('\n');
        frame.Append("event: ").Append(kind).Append('\n');
        while (data.ReadLine() is { } line)
            frame.Append("data: ").Append(line).Append('\n');
        frame.Append('\n');
        await response.WriteAsync(frame.ToString(), cancellation);
        await response.Body.FlushAsync(cancellation);
    }

    internal async Task StreamAsync(RendererState state, IEnumerable<string> chunks, CancellationToken cancellation)
    {
        var parser = new DirectJsonStream(state);
        foreach (var chunk in chunks)
            await ReadAsync(chunk, final: false);
        await ReadAsync("", final: true);

        async Task ReadAsync(string chunk, bool final)
        {
            var diagnostic = parser.Read(chunk, out var parts, final, cancellation);
            foreach (var part in parts)
            {
                if (part.Text is { } text)
                    await WriteAsync("text", JsonSerializer.Serialize(text), cancellation);
                if (part.Messages is { } messages)
                    foreach (var message in messages)
                        await WriteAsync("a2ui", message.Envelope.GetRawText(), cancellation);
            }
            if (diagnostic is not null)
                throw new InvalidDataException();
        }
    }
}
