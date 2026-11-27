using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Agent;
using A2UI.Core;

/// <summary>Runs the pinned Express cases and the Basic Catalog golden corpus through the public Express API.</summary>
internal static class ExpressCases
{
    private const string ExpressPath = "conformance/agent/express/";
    private const string MultiCatalogPath = "conformance/agent/multi_catalog_formats.yaml";
    private const string RendererStatePath = "conformance/agent/renderer_state.yaml";
    private const string GoldenDirectory = "catalogs/basic/v1/examples";

    internal static bool IsExpress(string path, JsonElement test) =>
        path is ExpressPath + "compiler.yaml" or ExpressPath + "decompiler.yaml" or ExpressPath + "response_parser.yaml" or ExpressPath + "scaling.yaml" ||
        path == MultiCatalogPath && test.GetProperty("args").GetProperty("format").ValueEquals("express") ||
        path == RendererStatePath && test.GetProperty("turns")[0].TryGetProperty("format", out var format) && format.ValueEquals("express");

    /// <summary>Gives the reason an Express case is outside this profile, or null when it runs.</summary>
    internal static string? ExclusionReason(string path, JsonElement test)
    {
        if (!IsExpress(path, test) || !test.TryGetProperty("args", out var args))
            return null;
        var text = args.GetRawText();
        if (text.Contains("catalog_v0_", StringComparison.Ordinal) || text.Contains("_v09", StringComparison.Ordinal) || text.Contains("\"v0.9\"", StringComparison.Ordinal))
            return "Legacy protocol is outside the profile.";
        return null;
    }

    internal static void Run(JsonElement test, string assets)
    {
        switch (test.GetProperty("action").GetString())
        {
            case "unwrap" or "wrap" or "has_format_content":
                CheckFraming(test);
                break;
            case "compile":
                CheckCompile(test, assets);
                break;
            case "decompile":
                CheckDecompile(test, assets);
                break;
            case "parse_response":
                CheckParseResponse(test, assets);
                break;
            case "create_format":
                CheckDuplicateCatalogs(test, assets);
                break;
            case "renderer_state_turns":
                AgentCases.RunRendererState(test, assets);
                break;
            default:
                throw new InvalidDataException($"Unsupported Express action {test.GetProperty("action")}.");
        }
    }

    /// <summary>Decompiles and recompiles every Basic Catalog example and checks the ordered messages.</summary>
    /// <param name="assets">The pinned upstream assets.</param>
    /// <param name="run">Whether to run the examples. When false, each one is listed as excluded for this run mode.</param>
    internal static IEnumerable<(string File, string? Skip, Exception? Failure)> RunGoldenCorpus(string assets, bool run)
    {
        var catalogs = new CatalogRegistry();
        foreach (var file in GoldenFiles(assets))
        {
            var name = Path.GetFileName(file);
            string? skip = run ? null : "Run mode excludes this case.";
            Exception? failure = null;
            try
            {
                if (run)
                    RoundTripExample(catalogs, JsonNode.Parse(File.ReadAllText(file))!.AsObject());
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or JsonException)
            {
                failure = error;
            }

            yield return (GoldenDirectory + "/" + name, skip, failure);
        }
    }

    internal static void ExportGoldenCorpus(string assets, string output)
    {
        var catalogs = new CatalogRegistry();
        var examples = GoldenFiles(assets).ToDictionary(
            file => Path.GetFileName(file),
            file => RoundTripExample(catalogs, JsonNode.Parse(File.ReadAllText(file))!.AsObject()),
            StringComparer.Ordinal);
        File.WriteAllText(output, JsonSerializer.Serialize(examples));
    }

    private static IEnumerable<string> GoldenFiles(string assets) =>
        Directory.EnumerateFiles(Path.Combine(assets, GoldenDirectory), "*.json").Order(StringComparer.Ordinal);

    private static GoldenRoundTrip RoundTripExample(CatalogRegistry catalogs, JsonObject example)
    {
        var input = example["messages"]?.AsArray() ?? throw new InvalidDataException("The example has no messages.");
        var messages = new List<WireMessage>(input.Count);
        foreach (var envelope in input)
        {
            Require(WireMessage.TryDecode(Encoding.UTF8.GetBytes(envelope!.ToJsonString()), WireDirection.AgentToRenderer,
                out var wire, out var decodeError), decodeError?.ToString() ?? "Decoding failed.");
            messages.Add(wire!);
        }

        var decompileError = Express.Decompile(messages, catalogs, out var text);
        Require(decompileError is null, decompileError?.ToString() ?? "Decompilation failed.");
        var compileError = Express.Compile(text, catalogs, out var recompiled);
        Require(compileError is null, compileError?.ToString() ?? "Recompilation failed.");

        var expected = NormaliseBasicCatalogExample(input);
        var actual = JsonNode.Parse(DirectJson.Decompile(recompiled))!;
        Require(EqualJson(expected, actual), "Round trip changed the messages. Got " + actual.ToJsonString());
        return new GoldenRoundTrip(text, actual);
    }

    private static JsonArray NormaliseBasicCatalogExample(JsonArray input)
    {
        var expected = (JsonArray)input.DeepClone();
        var create = expected[0]?["createSurface"]?.AsObject()
            ?? throw new InvalidDataException("The Basic Catalog example must start by creating its surface.");

        // The pinned Python round-trip test folds the initial same-surface updates into the create message.
        // Preserve every later message and compare arrays in order, including component definitions.
        var index = 1;
        var canMerge = true;
        while (index < expected.Count)
        {
            var message = expected[index]!;
            if (canMerge && message["updateComponents"] is JsonObject components &&
                JsonNode.DeepEquals(components["surfaceId"], create["surfaceId"]) && !create.ContainsKey("components"))
            {
                create["components"] = components["components"]!.DeepClone();
                expected.RemoveAt(index);
                continue;
            }

            if (message["updateDataModel"] is JsonObject update && (string?)update["path"] is null or "" or "/")
            {
                if (canMerge && JsonNode.DeepEquals(update["surfaceId"], create["surfaceId"]) && update["value"] is JsonObject data)
                {
                    create["dataModel"] = data.DeepClone();
                    expected.RemoveAt(index);
                    continue;
                }

                update["path"] = "/";
            }

            canMerge = false;
            index++;
        }

        return expected;
    }

    private static bool EqualJson(JsonNode? expected, JsonNode? actual) => (expected, actual) switch
    {
        (JsonObject left, JsonObject right) => left.Count == right.Count &&
            left.All(pair => right.TryGetPropertyValue(pair.Key, out var value) && EqualJson(pair.Value, value)),
        (JsonArray left, JsonArray right) => left.Count == right.Count &&
            left.Zip(right).All(pair => EqualJson(pair.First, pair.Second)),
        // JSON has one number type. The compiled Express form may spell 1188.0 as 1188.
        (JsonValue left, JsonValue right) when left.TryGetValue<decimal>(out var expectedNumber) &&
            right.TryGetValue<decimal>(out var actualNumber) => expectedNumber == actualNumber,
        _ => JsonNode.DeepEquals(expected, actual),
    };

    private sealed record GoldenRoundTrip(string Express, JsonNode Messages);

    private static void CheckFraming(JsonElement test)
    {
        var action = test.GetProperty("action");
        if (action.ValueEquals("wrap"))
        {
            var input = Parts(test.GetProperty("parts"));
            var result = Express.Wrap(input);
            if (test.TryGetProperty("expect_output", out var expected))
                Require(expected.ValueEquals(result), "Wrapped response differs.");
            if (test.TryGetProperty("expect_contains", out var fragments))
                foreach (var fragment in fragments.EnumerateArray())
                    Require(result.Contains(fragment.GetString()!, StringComparison.Ordinal), "Missing wrapped content.");
            if (test.TryGetProperty("expect_round_trip", out var roundTrip) && roundTrip.GetBoolean())
                Require(input.SequenceEqual(Express.Unwrap(result)), "Round trip changed response parts.");
            return;
        }

        var parts = Express.Unwrap(test.GetProperty("input").GetString()!);
        if (action.ValueEquals("unwrap"))
            Require(parts.SequenceEqual(Parts(test.GetProperty("expect"))), "Unwrapped parts differ.");
        else
        {
            var complete = test.GetProperty("args").TryGetProperty("complete", out var flag) && flag.GetBoolean();
            Require(Express.HasFormatContent(test.GetProperty("input").GetString()!, complete) == test.GetProperty("expect").GetBoolean(),
                "Format content detection differs.");
        }

        static ExpressPart[] Parts(JsonElement array) => array.EnumerateArray().Select(item =>
            item.TryGetProperty("text", out var text) ? new ExpressPart(text.GetString()!) :
                new ExpressPart(item.GetProperty("a2ui_raw").GetString()!, true,
                    !item.TryGetProperty("is_final", out var final) || final.GetBoolean())).ToArray();
    }

    private static void CheckCompile(JsonElement test, string assets)
    {
        var registry = AgentCases.RegisterCatalogs(test.GetProperty("args"), assets);
        var diagnostic = Express.Compile(test.GetProperty("input").GetString()!, registry, out var messages);
        if (CheckExpectedError(test, diagnostic))
            return;
        Require(diagnostic is null, diagnostic?.ToString() ?? "Compilation failed.");
        var actual = JsonNode.Parse(DirectJson.Decompile(messages))!;
        if (test.TryGetProperty("expect_present", out var pointers))
            foreach (var pointer in pointers.EnumerateArray())
            {
                // The wording of a generated value is not fixed, so it is checked for presence and then set aside.
                Require(Remove(actual, pointer.GetString()!), $"Missing {pointer}.");
            }

        if (test.TryGetProperty("expect", out var expected))
            Require(JsonNode.DeepEquals(actual, JsonNode.Parse(expected.GetRawText())), "Compiled messages differ. Got " + actual.ToJsonString());
    }

    private static void CheckDecompile(JsonElement test, string assets)
    {
        var registry = AgentCases.RegisterCatalogs(test.GetProperty("args"), assets);
        var messages = new List<WireMessage>();
        foreach (var envelope in test.GetProperty("messages").EnumerateArray())
        {
            Require(WireMessage.TryDecode(Encoding.UTF8.GetBytes(envelope.GetRawText()), WireDirection.AgentToRenderer,
                out var message, out var decodeError), decodeError?.ToString() ?? "Decode failed.");
            messages.Add(message!);
        }

        var diagnostic = Express.Decompile(messages, registry, out var text);
        if (CheckExpectedError(test, diagnostic))
            return;
        Require(diagnostic is null, diagnostic?.ToString() ?? "Decompilation failed.");
        if (test.TryGetProperty("expect_contains", out var fragments))
            foreach (var fragment in fragments.EnumerateArray())
                Require(text.Contains(fragment.GetString()!, StringComparison.Ordinal), $"Missing {fragment} in:\n{text}");
        if (test.TryGetProperty("expect_absent", out var absent))
            foreach (var fragment in absent.EnumerateArray())
                Require(!text.Contains(fragment.GetString()!, StringComparison.Ordinal), $"Unexpected {fragment} in:\n{text}");

        var recompile = Express.Compile(text, registry, out var recompiled);
        Require(recompile is null, recompile?.ToString() + "\n" + text);
        var roundTrip = test.TryGetProperty("expect_round_trip", out var flag) && flag.GetBoolean();
        if (!roundTrip && !test.TryGetProperty("expect_recompiled", out _))
            return;
        var expected = roundTrip ? test.GetProperty("messages") : test.GetProperty("expect_recompiled");
        var actual = JsonNode.Parse(DirectJson.Decompile(recompiled))!;
        Require(JsonNode.DeepEquals(actual, JsonNode.Parse(expected.GetRawText())), "Recompiled messages differ. Got " + actual.ToJsonString() + "\n" + text);
    }

    private static void CheckParseResponse(JsonElement test, string assets)
    {
        var args = test.GetProperty("args");
        var registry = AgentCases.RegisterCatalogs(args, assets);
        using var state = new RendererState(registry);
        var diagnostic = Express.ParseResponse(test.GetProperty("input").GetString()!, state, out var parts, out var turn,
            !args.TryGetProperty("wrapped", out var wrapped) || wrapped.GetBoolean());
        using var transaction = turn;
        if (CheckExpectedError(test, diagnostic))
            return;
        Require(diagnostic is null && transaction is not null, diagnostic?.ToString() ?? "Missing transaction.");
        var expectedParts = test.GetProperty("expect");
        Require(parts.Count == expectedParts.GetArrayLength(), "Wrong part count.");
        for (var i = 0; i < parts.Count; i++)
        {
            if (expectedParts[i].TryGetProperty("text", out var text))
                Require(text.ValueEquals(parts[i].Text), "Text differs.");
            else
                Require(JsonNode.DeepEquals(JsonNode.Parse(expectedParts[i].GetProperty("a2ui").GetRawText()),
                    JsonNode.Parse(DirectJson.Decompile(parts[i].Messages!))), "Messages differ.");
        }
    }

    private static void CheckDuplicateCatalogs(JsonElement test, string assets)
    {
        // Express has no request binding yet (#71), so the check is made where every format registers catalogs.
        var entries = test.GetProperty("args").GetProperty("catalogs").EnumerateArray().ToArray();
        var host = new CatalogRegistry(false);
        Require(host.TryRegister(File.ReadAllBytes(CatalogPath(entries[0], assets)), out var first), first?.ToString() ?? "The first catalog must register.");
        Require(!host.TryRegister(File.ReadAllBytes(CatalogPath(entries[1], assets)), out var duplicate) &&
            duplicate?.Code == "duplicateCatalog", "Duplicate catalog ID was accepted.");

        static string CatalogPath(JsonElement entry, string assets) =>
            Path.Combine(assets, "conformance", entry.GetProperty("catalog").GetString()!);
    }

    /// <summary>Checks a case that expects an error, and returns whether it did.</summary>
    private static bool CheckExpectedError(JsonElement test, A2uiDiagnostic? diagnostic)
    {
        if (!test.TryGetProperty("expect_error", out var expected))
            return false;
        Require(diagnostic?.Code == expected.GetProperty("category").GetString(), diagnostic?.ToString() ?? "Expected a failure.");
        if (expected.TryGetProperty("message", out var fragment))
            Require(diagnostic!.ToString().Contains(fragment.GetString()!, StringComparison.OrdinalIgnoreCase), diagnostic.ToString());
        return true;
    }

    private static bool Remove(JsonNode root, string pointer)
    {
        var tokens = pointer.Split('/').Skip(1).ToArray();
        var node = root;
        foreach (var token in tokens[..^1])
        {
            node = node switch
            {
                JsonArray list when int.TryParse(token, out var index) && index < list.Count => list[index],
                JsonObject map => map[token],
                _ => null,
            };
            if (node is null)
                return false;
        }

        return node is JsonObject owner && owner.Remove(tokens[^1]);
    }

    private static void Require(bool condition, string description) => AgentCases.Require(condition, description);
}
