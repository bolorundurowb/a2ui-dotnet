using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Performance <report.json> <source-revision>");
    return 2;
}

const int repetitions = 7;
const int updates = 24;
const int chunkSize = 128;
var catalogs = new CatalogRegistry();
var measurements = new List<Measurement>();

foreach (var leafCount in new[] { 8, 128 })
{
    var components = new List<object>
    {
        new { id = "root", component = "Column", children = Enumerable.Range(0, leafCount).Select(i => $"row-{i}").ToArray() }
    };
    components.AddRange(Enumerable.Range(0, leafCount).Select(i =>
        new { id = $"row-{i}", component = "Text", text = $"Order {i} — ₦2,500" }));
    var create = JsonSerializer.Serialize(new
    {
        version = "1.0",
        createSurface = new
        {
            surfaceId = "orders",
            catalogId = "https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json",
            components
        }
    });
    var updateTexts = Enumerable.Range(0, updates).Select(i => JsonSerializer.Serialize(new
    {
        version = "1.0",
        updateComponents = new
        {
            surfaceId = "orders",
            components = new[] { new { id = $"row-{i % leafCount}", component = "Text", text = $"Order {i} — updated" } }
        }
    })).ToArray();
    var batch = "[" + string.Join(",", updateTexts.Prepend(create)) + "]";
    var wrapped = "<a2ui-json>" + batch + "</a2ui-json>";
    var chunks = Enumerable.Range(0, (wrapped.Length + chunkSize - 1) / chunkSize)
        .Select(i => wrapped.Substring(i * chunkSize, Math.Min(chunkSize, wrapped.Length - i * chunkSize))).ToArray();
    var wireCreate = Encoding.UTF8.GetBytes(create);
    var wireUpdates = updateTexts.Select(Encoding.UTF8.GetBytes).ToArray();

    Measure("core-progressive-updates", leafCount, 100, updates, wireUpdates.Sum(message => message.Length),
        () =>
        {
            using var session = new SurfaceSession(catalogs);
            Apply(session, wireCreate);
            return Time(() =>
            {
                foreach (var message in wireUpdates)
                    Apply(session, message);
            });
        });

    Measure("agent-direct-json-batch", leafCount, 100, updates + 1, Encoding.UTF8.GetByteCount(batch),
        () => Time(() =>
        {
            if (DirectJson.Compile(batch, catalogs, out var messages) is { } error)
                throw new InvalidOperationException(error.ToString());
            if (messages.Count != updates + 1)
                throw new InvalidOperationException("Unexpected compiled message count.");
        }));

    Measure("agent-first-message", leafCount, 20, 1, null, () =>
    {
        using var state = new RendererState(catalogs);
        var parser = new DirectJsonStream(state);
        return Time(() =>
        {
            foreach (var chunk in chunks)
            {
                if (parser.Read(chunk, out var parts) is { } error)
                    throw new InvalidOperationException(error.ToString());
                foreach (var part in parts)
                    if (part.Messages is { Count: > 0 } messages)
                    {
                        if (messages[0].Kind != "createSurface")
                            throw new InvalidOperationException("Unexpected first message.");
                        return;
                    }
            }

            throw new InvalidOperationException("The stream did not emit its first message.");
        });
    });
}

var report = new
{
    Revision = args[1],
    Runtime = RuntimeInformation.FrameworkDescription,
    OperatingSystem = RuntimeInformation.OSDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    LogicalProcessors = Environment.ProcessorCount,
    ServerGc = System.Runtime.GCSettings.IsServerGC,
    WarmupPasses = 2,
    Repetitions = repetitions,
    StreamChunkCharacters = chunkSize,
    Measurements = measurements
};
var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Wrote {measurements.Count} measurements to {output}.");
return 0;

void Measure(string operation, int leafCount, int iterations, int messages, int? bytes, Func<Sample> run)
{
    for (var i = 0; i < 2 * iterations; i++)
        run();

    var elapsed = new double[repetitions];
    var allocated = new double[repetitions];
    for (var repetition = 0; repetition < repetitions; repetition++)
    {
        long ticks = 0;
        long allocation = 0;
        for (var i = 0; i < iterations; i++)
        {
            var sample = run();
            ticks += sample.Ticks;
            allocation += sample.AllocatedBytes;
        }

        elapsed[repetition] = (double)ticks / Stopwatch.Frequency / iterations;
        allocated[repetition] = (double)allocation / iterations;
    }

    Array.Sort(elapsed);
    Array.Sort(allocated);
    var seconds = elapsed[repetitions / 2];
    measurements.Add(new(operation, leafCount, iterations, seconds * 1_000,
        allocated[repetitions / 2], bytes / seconds, bytes is null ? null : messages / seconds));
}

static Sample Time(Action run)
{
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    var start = Stopwatch.GetTimestamp();
    run();
    return new(Stopwatch.GetTimestamp() - start, GC.GetAllocatedBytesForCurrentThread() - allocated);
}

static void Apply(SurfaceSession session, byte[] message)
{
    if (!WireMessage.TryDecode(message, WireDirection.AgentToRenderer, out var decoded, out var diagnostic))
        throw new InvalidOperationException(diagnostic?.ToString());
    if (session.Apply(decoded!) is { } rejection)
        throw new InvalidOperationException(rejection.ToString());
}

internal readonly record struct Sample(long Ticks, long AllocatedBytes);
internal sealed record Measurement(string Operation, int LeafCount, int IterationsPerSample,
    double MedianMilliseconds, double MedianAllocatedBytes, double? BytesPerSecond, double? MessagesPerSecond);
