using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Agent;
using A2UI.Core;

internal static class StreamCases
{
    internal static void Run(JsonElement test, string assets)
    {
        var args = test.GetProperty("args");
        var registry = new CatalogRegistry(false);
        var files = args.TryGetProperty("catalog", out var single) ? new[] { single.GetString()! } :
            args.GetProperty("catalogs").EnumerateArray().Select(c => c.GetString()!);
        foreach (var file in files)
            if (!registry.TryRegister(File.ReadAllBytes(Path.Combine(assets, "conformance", file)), out var error))
                throw new InvalidDataException(error?.ToString());
        using var state = new RendererState(registry);
        var keys = args.TryGetProperty("progressive_keys", out var items) ? items.EnumerateArray().Select(i => i.GetString()!) : null;
        var wrapped = !args.TryGetProperty("wrapped", out var flag) || flag.GetBoolean();
        var stream = new DirectJsonStream(state, keys, wrapped, args.TryGetProperty("after_rejected_block", out _));
        var yielded = new List<StreamPart>();
        Steps(test, stream, yielded);
        if (test.TryGetProperty("expect_matches_single_shot", out var match) && match.GetBoolean())
        {
            using var fresh = new RendererState(registry);
            var response = string.Concat(test.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("input").GetString()));
            var problem = DirectJson.ParseResponse(response, fresh, out var batch, out var turn, wrapped);
            using var candidate = turn;
            if (problem is not null || !Normalize(yielded).SequenceEqual(Normalize(batch.Select(p => new StreamPart(p.Text, p.Messages)))))
                throw new InvalidDataException("Single-shot and streamed parts differ.");
        }

        static IEnumerable<(bool, string)> Normalize(IEnumerable<StreamPart> parts)
        {
            var text = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                if (part.Text is not null)
                {
                    text.Append(part.Text);
                    continue;
                }
                if (text.Length != 0) yield return (false, text.ToString());
                text.Clear();
                yield return (true, DirectJson.Decompile(part.Messages!));
            }
            if (text.Length != 0) yield return (false, text.ToString());
        }
    }

    internal static void Steps(JsonElement test, DirectJsonStream stream, List<StreamPart>? yielded = null)
    {
        var n = 0;
        foreach (var step in test.GetProperty("steps").EnumerateArray())
        {
            n++;
            var diagnostic = stream.Read(step.GetProperty("input").GetString()!, out var parts);
            if (step.TryGetProperty("expect_in_block", out var block) && block.GetBoolean() != stream.InBlock)
                throw new InvalidDataException($"Step {n} incorrect block state.");
            if (step.TryGetProperty("expect_error", out var error))
            {
                if (!error.GetProperty("category").ValueEquals(diagnostic?.Code))
                    throw new InvalidDataException($"Step {n} expected error, got {diagnostic}.");
                if (step.TryGetProperty("expect_partial_results", out var prefix))
                    Compare(parts, prefix, n);
                return;
            }
            if (diagnostic is not null) throw new InvalidDataException($"Step {n}: {diagnostic}");
            Compare(parts, step.GetProperty("expect"), n);
            yielded?.AddRange(parts);
        }
        var final = stream.Read("", out var tail, true);
        if (test.TryGetProperty("expect_error", out var closeError))
        {
            if (!closeError.GetProperty("category").ValueEquals(final?.Code))
                throw new InvalidDataException("Close diagnostic differs.");
            return;
        }
        yielded?.AddRange(tail);
        if (final is not null) throw new InvalidDataException(final.ToString());
        if (test.TryGetProperty("expect_close", out var close)) Compare(tail, close, n + 1);
    }

    private static void Compare(IReadOnlyList<StreamPart> parts, JsonElement expected, int n)
    {
        var actual = new JsonArray();
        foreach (var part in parts)
            actual.Add(part.Messages is not null ? new JsonObject { ["a2ui"] = JsonNode.Parse(DirectJson.Decompile(part.Messages)) } :
                part.Error is not null ? new JsonObject { ["error"] = new JsonObject { ["category"] = part.Error.Code } } :
                new JsonObject { ["text"] = part.Text });
        if (!JsonNode.DeepEquals(actual, JsonNode.Parse(expected.GetRawText())))
            throw new InvalidDataException($"Step {n}: got {actual.ToJsonString()}.");
    }
}
