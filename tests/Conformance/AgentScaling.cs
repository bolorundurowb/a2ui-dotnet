using System.Diagnostics;
using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

internal static class AgentScaling
{
    internal static string Run(JsonElement test, string assets)
    {
        var registry = new CatalogRegistry(false);
        var args = test.GetProperty("args");
        if (!registry.TryRegister(File.ReadAllBytes(Path.Combine(assets, "conformance", args.GetProperty("catalog").GetString()!)), out var diagnostic))
            throw new InvalidDataException(diagnostic?.ToString());
        var keys = args.TryGetProperty("progressive_keys", out var keyList)
            ? keyList.EnumerateArray().Select(k => k.GetString()!).ToArray() : [];
        var express = args.TryGetProperty("format", out var format) && format.ValueEquals("express");
        var overhead = test.TryGetProperty("size", out var size);
        var small = Payload(overhead ? size.GetInt32() : test.GetProperty("sizes")[0].GetInt32());
        var large = overhead ? small : Payload(test.GetProperty("sizes")[1].GetInt32());
        var streaming = test.GetProperty("operation").ValueEquals("parse_chunk");
        var chunk = test.TryGetProperty("chunk_size", out var count) ? count.GetInt32() : 16;
        Measure(small, !overhead && streaming);
        Measure(large, streaming);
        var first = new double[5];
        var second = new double[5];
        for (var i = 0; i < 5; i++)
        {
            first[i] = Measure(small, !overhead && streaming);
            second[i] = Measure(large, streaming);
        }
        Array.Sort(first);
        Array.Sort(second);
        var ratio = second[2] / first[2];
        var limit = test.GetProperty("max_ratio").GetDouble();
        var summary = $"Medians {first[2]:F2} and {second[2]:F2} ms, ratio {ratio:F2}, limit {limit}.";
        if (ratio > limit) throw new InvalidDataException(summary);
        return summary;

        string Payload(int length) => express
            ? CoreScaling.ExpandText(test.GetProperty("payload"), length)
            : CoreScaling.Expand(test.GetProperty("payload"), length).GetRawText();

        double Measure(string payload, bool stream)
        {
            var wrapped = "<a2ui-json>" + payload + "</a2ui-json>";
            var start = Stopwatch.GetTimestamp();
            if (stream)
            {
                using var state = new RendererState(registry);
                var parser = new DirectJsonStream(state, keys);
                for (var i = 0; i < wrapped.Length; i += chunk)
                    if (parser.Read(wrapped.Substring(i, Math.Min(chunk, wrapped.Length - i)), out _) is { } error)
                        throw new InvalidDataException(error.ToString());
                if (parser.Read("", out _, true) is { } end) throw new InvalidDataException(end.ToString());
            }
            else if ((express ? Express.Compile(payload, registry, out _) : DirectJson.Compile(payload, registry, out _)) is { } error)
                throw new InvalidDataException(error.ToString());
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }
}
