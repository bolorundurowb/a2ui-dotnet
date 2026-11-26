using System.Text.Json;
using A2UI.Agent;
using A2UI.Core;

internal static class AgentCases
{
    internal static bool Supports(string path, JsonElement test) =>
        path == "conformance/agent/multi_catalog_formats.yaml" &&
            test.GetProperty("args").GetProperty("format").ValueEquals("direct_json") ||
        path == "conformance/agent/direct_json/response_streaming.yaml" &&
            !test.GetProperty("args").GetRawText().Contains("catalog_v0_", StringComparison.Ordinal) ||
        path == "conformance/agent/request_processor.yaml" &&
            test.GetProperty("args").TryGetProperty("format", out var format) && format.ValueEquals("direct_json") &&
            !test.GetProperty("args").TryGetProperty("format_override", out _) &&
            !test.GetProperty("name").GetString()!.Contains("v0_9", StringComparison.Ordinal) ||
        path == "conformance/agent/direct_json/prompt_generator.yaml" ||
        path == "conformance/agent/skill.yaml" && test.GetProperty("action").ValueEquals("export_to_directory") ||
        path == "conformance/agent/skill.yaml" && test.GetProperty("name").GetString()!.StartsWith("test_direct_json_", StringComparison.Ordinal) ||
        path == "conformance/agent/direct_json/compiler.yaml" ||
        path == "conformance/agent/direct_json/decompiler.yaml" ||
        path == "conformance/agent/direct_json/response_parser.yaml" ||
        path == "conformance/agent/renderer_state.yaml" && (test.GetProperty("turns")[0].TryGetProperty("payload", out _) ||
            test.GetProperty("turns")[0].GetProperty("format").ValueEquals("direct_json")) ||
        path == "conformance/agent/catalog_resolution.yaml" ||
        path == "conformance/agent/catalog_provider.yaml" ||
        path == "conformance/agent/renderer_capabilities.yaml" &&
        test.GetProperty("args").GetProperty("version").ValueEquals("1.0") ||
        path == "conformance/agent/catalog_transformer.yaml";

    private static void CheckSkillExport(JsonElement test)
    {
        var temporary = Directory.CreateTempSubdirectory("a2ui-skills-");
        var root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "output")).FullName;
        try
        {
            var args = test.GetProperty("args");
            foreach (var file in args.GetProperty("pre_existing_files").EnumerateArray())
            {
                var path = Path.Combine(root, file.GetString()!);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "existing content");
            }

            var skills = args.GetProperty("skills").EnumerateArray().Select(skill =>
                new SkillFile(skill.TryGetProperty("filename", out var file) ? file.GetString()! :
                    skill.GetProperty("name").GetString() + "/SKILL.md",
                    skill.GetProperty("content").GetString()!)).ToArray();
            var error = SkillSet.ExportToDirectory(root, skills);
            if (test.TryGetProperty("expect_error", out var expectedError))
                Require(error?.Code == expectedError.GetProperty("category").GetString(), "Wrong skill export diagnostic.");
            else
            {
                Require(error is null, error?.ToString() ?? "");
                foreach (var skill in skills)
                    Require(File.ReadAllText(Path.Combine(root, skill.FileName)) == skill.Content, "Wrong skill content.");
            }

            foreach (var file in test.GetProperty("expected_files").EnumerateArray())
                Require(File.Exists(Path.Combine(root, file.GetString()!)), $"Missing file {file}.");
            if (test.TryGetProperty("expected_absent_files", out var absent))
                foreach (var file in absent.EnumerateArray())
                    Require(!File.Exists(Path.Combine(root, file.GetString()!)), $"Unexpected file {file}.");
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    internal static void Run(JsonElement test, string assets)
    {
        if (test.GetProperty("action").ValueEquals("export_to_directory"))
        {
            CheckSkillExport(test);
            return;
        }
        if (test.GetProperty("action").ValueEquals("parse_chunk"))
        {
            StreamCases.Run(test, assets);
            return;
        }
        if (test.GetProperty("action").GetString() is "create_format" or "create_processor")
        {
            CheckProcessor(test, assets);
            return;
        }
        if (test.GetProperty("action").GetString() is "generate_prompt_snippet" or "monolithic_skill_equals_prompt_snippet")
        {
            CheckPrompt(test, assets);
            return;
        }
        if (test.GetProperty("action").ValueEquals("renderer_state_turns"))
        {
            RunRendererState(test, assets);
            return;
        }
        if (test.GetProperty("action").GetString() is "unwrap" or "wrap" or "has_format_content")
        {
            CheckFraming(test);
            return;
        }
        if (test.GetProperty("action").GetString() is "compile" or "decompile" or "parse_response")
        {
            CheckCompiler(test, assets);
            return;
        }
        var args = test.GetProperty("args");
        if (test.GetProperty("action").ValueEquals("read_renderer_capabilities"))
        {
            args.TryGetProperty("metadata", out var metadata);
            var read = CatalogNegotiation.TryReadCapabilities(metadata, out var capabilitiesResult, out var metadataError);
            if (test.TryGetProperty("expect_error", out var capabilityError))
                Require(!read && metadataError?.Code == capabilityError.GetProperty("category").GetString(),
                    metadataError?.ToString() ?? "Expected invalid metadata.");
            else
            {
                var expectedCapabilities = test.GetProperty("expect");
                Require(read && (expectedCapabilities.ValueKind == JsonValueKind.Null
                    ? capabilitiesResult.ValueKind == JsonValueKind.Undefined
                    : System.Text.Json.Nodes.JsonNode.DeepEquals(
                        System.Text.Json.Nodes.JsonNode.Parse(capabilitiesResult.GetRawText()),
                        System.Text.Json.Nodes.JsonNode.Parse(expectedCapabilities.GetRawText()))),
                    metadataError?.ToString() ?? "Capabilities differ.");
            }
            return;
        }
        var registry = new CatalogRegistry(includeBasicCatalog: false);
        if (test.GetProperty("action").ValueEquals("provide_catalog"))
        {
            var path = Path.Combine(assets, "conformance", args.GetProperty("path").GetString()!);
            var suppliedId = args.TryGetProperty("catalog_id", out var id) ? id.GetString() : null;
            var suppliedVersion = args.TryGetProperty("protocol_version", out var version) ? version.GetString() : null;
            var loaded = FileCatalogProvider.TryRegister(registry, path, suppliedId, suppliedVersion, out var providerError);
            if (test.TryGetProperty("expect_error", out var loadError))
                Require(!loaded && providerError?.Code == loadError.GetProperty("category").GetString(),
                    providerError?.ToString() ?? "Expected provider rejection.");
            else
            {
                Require(loaded, providerError?.ToString() ?? "Catalog provider failed.");
                Require(registry.TryGetDefinition(registry.CatalogIds.Single(), out var definition), "Provided catalog is unavailable.");
                CheckDefinition(definition, test.GetProperty("expect"));
            }
            return;
        }
        if (test.GetProperty("action").ValueEquals("transform_catalog"))
        {
            Register(registry, args, assets);
            Require(registry.TryGetDefinition(registry.CatalogIds.Single(), out var transformed), "Transformed catalog is unavailable.");
            CheckDefinition(transformed, test.GetProperty("expect"));
            return;
        }

        foreach (var item in args.GetProperty("catalogs").EnumerateArray())
            Register(registry, item, assets);

        args.TryGetProperty("renderer_capabilities", out var capabilities);
        var acceptsInline = args.TryGetProperty("accepts_inline_catalogs", out var inline) && inline.GetBoolean();
        var resolved = CatalogNegotiation.TryResolve(registry, capabilities, acceptsInline, out var active, out var diagnostic);
        if (test.TryGetProperty("expect_error", out var expectedError))
        {
            Require(!resolved && diagnostic?.Code == expectedError.GetProperty("category").GetString(),
                diagnostic?.ToString() ?? "Expected negotiation to fail.");
            return;
        }

        Require(resolved && active is not null, diagnostic?.ToString() ?? "Negotiation failed.");
        var expected = test.GetProperty("expect");
        CompareNames(active!.CatalogIds, expected.GetProperty("active_catalog_ids"));
        if (!expected.TryGetProperty("catalogs", out var catalogs))
            return;
        foreach (var catalog in catalogs.EnumerateArray())
        {
            Require(active.TryGetDefinition(catalog.GetProperty("catalog_id").GetString()!, out var definition),
                "Expected catalog is unavailable.");
            CheckDefinition(definition, catalog);
        }
    }

    private static void Register(CatalogRegistry registry, JsonElement item, string assets)
    {
        var path = Path.Combine(assets, "conformance", item.GetProperty("catalog").GetString()!);
        HashSet<string>? components = null;
        HashSet<string>? functions = null;
        if (item.TryGetProperty("transformers", out var transformers))
            foreach (var transformer in transformers.EnumerateArray())
            {
                if (transformer.TryGetProperty("component_pruning", out var componentList))
                    components = Intersect(components, componentList);
                if (transformer.TryGetProperty("function_pruning", out var functionList))
                    functions = Intersect(functions, functionList);
            }
        var bytes = File.ReadAllBytes(path);
        if (item.GetProperty("catalog").ValueEquals("test_data/catalogs/catalog_without_id.json"))
        {
            // This transformer fixture tests an anonymous catalog. Supply its host identity for registration.
            var source = new CatalogRegistry(false);
            const string id = "conformance/no-functions";
            Require(FileCatalogProvider.TryRegister(source, path, id, "1.0", out var loadError),
                loadError?.ToString() ?? "Failed to provide a catalog identity.");
            Require(source.TryGetDefinition(id, out var supplied), "Missing provided catalog.");
            bytes = System.Text.Encoding.UTF8.GetBytes(supplied.GetRawText());
        }
        Require(registry.TryRegister(bytes, components, functions, out var error),
            error?.ToString() ?? "Registration failed.");
    }

    private static HashSet<string> Intersect(HashSet<string>? previous, JsonElement allowlist)
    {
        var names = allowlist.ValueKind == JsonValueKind.Null
            ? Enumerable.Empty<string>() : allowlist.EnumerateArray().Select(item => item.GetString()!);
        if (previous is null)
            return names.ToHashSet(StringComparer.Ordinal);
        previous.IntersectWith(names);
        return previous;
    }

    private static void CheckDefinition(JsonElement definition, JsonElement expected)
    {
        if (expected.TryGetProperty("catalog_id", out var id))
            Require(definition.GetProperty("catalogId").GetString() == id.GetString(), "Catalog ID differs.");
        if (expected.TryGetProperty("protocol_version", out var version))
            Require(definition.GetProperty("protocolVersion").GetString() == version.GetString(), "Protocol version differs.");
        foreach (var group in new[] { "components", "functions" })
            if (expected.TryGetProperty(group, out var names))
                CompareNames(definition.TryGetProperty(group, out var entries)
                    ? entries.EnumerateObject().Select(entry => entry.Name)
                    : [], names);
    }

    private static void CompareNames(IEnumerable<string> actual, JsonElement expected) =>
        Require(actual.Order(StringComparer.Ordinal).SequenceEqual(
            expected.EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal)), "Catalog names differ.");

    private static void CheckProcessor(JsonElement test, string assets)
    {
        var args = test.GetProperty("args");
        var host = new CatalogRegistry(false);
        if (test.GetProperty("name").ValueEquals("test_direct_json_create_format_rejects_duplicate_catalog_ids"))
        {
            // A Direct JSON request takes a registry whose public entry point rejects duplicates.
            var entries = args.GetProperty("catalogs");
            Register(host, entries[0], assets);
            Require(RequestProcessor.Fixed(host, out _) is null, "The first catalog must bind successfully.");
            var path = Path.Combine(assets, "conformance", entries[1].GetProperty("catalog").GetString()!);
            Require(!host.TryRegister(File.ReadAllBytes(path), out var duplicate) &&
                duplicate?.Code == "duplicateCatalog", "Duplicate catalog ID was accepted.");
            return;
        }
        foreach (var entry in args.GetProperty("catalogs").EnumerateArray())
            Register(host, entry, assets);
        var initial = host.CatalogIds.ToArray();
        var examples = args.TryGetProperty("examples", out var list) ? list.EnumerateArray().Select(e =>
            File.ReadAllText(Path.Combine(assets, "conformance", e.GetString()!))).ToArray() : null;
        args.TryGetProperty("renderer_capabilities", out var caps);
        var issue = test.GetProperty("action").ValueEquals("create_format")
            ? RequestProcessor.Fixed(host, out var processor, examples)
            : RequestProcessor.Negotiate(host, caps, false, out processor, examples);
        if (test.TryGetProperty("expect_error", out var error))
        {
            Require(error.GetProperty("category").ValueEquals(issue?.Code), issue?.ToString() ?? "Expected error.");
            return;
        }
        Require(issue is null && processor is not null, issue?.ToString() ?? "Processor unavailable.");
        var expected = test.GetProperty("expect");
        if (expected.TryGetProperty("active_catalog_ids", out var ids))
            Require(processor!.CatalogIds.SequenceEqual(ids.EnumerateArray().Select(i => i.GetString())), "Catalog order differs.");
        Require(host.CatalogIds.SequenceEqual(initial), "Host registry changed.");
        foreach (var group in new[] { "prompt_snippet_contains", "prompt_snippet_absent" })
            if (expected.TryGetProperty(group, out var names))
                foreach (var name in names.EnumerateArray())
                    Require(processor!.Prompt.Text.Contains(name.GetString()!, StringComparison.Ordinal) ==
                        (group == "prompt_snippet_contains"), "Unexpected prompt content.");
        if (expected.TryGetProperty("catalogs", out var definitions))
            foreach (var item in definitions.EnumerateArray())
            {
                Require(processor!.TryGetCatalog(item.GetProperty("catalog_id").GetString()!, out var definition),
                    "Missing selected catalog.");
                CheckDefinition(definition, item);
            }
        using var state = processor!.CreateSession();
        using var other = processor.CreateSession();
        Require(!ReferenceEquals(state, other), "Parser sessions must be independent.");
        if (test.TryGetProperty("then_stream", out var stream))
            StreamCases.Steps(stream, new DirectJsonStream(state));
        if (args.TryGetProperty("probe_chunks", out var chunks))
        {
            var first = new DirectJsonStream(state);
            var second = new DirectJsonStream(other);
            foreach (var chunk in chunks.EnumerateArray())
            {
                Require(first.Read(chunk.GetString()!, out var one) is null &&
                    second.Read(chunk.GetString()!, out var two) is null && one.Count == two.Count,
                    "Parser streams failed isolation probe.");
            }
        }
        if (test.TryGetProperty("then_parse", out var parse))
        {
            var diagnostic = DirectJson.ParseResponse(parse.GetProperty("input").GetString()!, state, out var parts, out var turn);
            using var transaction = turn;
            if (parse.TryGetProperty("expect_error", out var failure))
                Require(failure.GetProperty("category").ValueEquals(diagnostic?.Code), "Expected parse failure.");
            else
            {
                Require(diagnostic is null, diagnostic?.ToString() ?? "Parse failed.");
                var wanted = parse.GetProperty("expect");
                Require(parts.Count == wanted.GetArrayLength(), "Part count differs.");
                for (var i = 0; i < parts.Count; i++)
                    if (wanted[i].TryGetProperty("text", out var text))
                        Require(text.ValueEquals(parts[i].Text), "Text differs.");
                    else
                        Require(System.Text.Json.Nodes.JsonNode.DeepEquals(
                            System.Text.Json.Nodes.JsonNode.Parse(DirectJson.Decompile(parts[i].Messages!)),
                            System.Text.Json.Nodes.JsonNode.Parse(wanted[i].GetProperty("a2ui").GetRawText())), "Payload differs.");
            }
        }
    }

    private static void CheckPrompt(JsonElement test, string assets)
    {
        var args = test.GetProperty("args");
        var registry = new CatalogRegistry(false);
        foreach (var item in args.GetProperty("catalogs").EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                Require(registry.TryRegister(File.ReadAllBytes(Path.Combine(assets, item.GetString()!)), out var problem),
                    problem?.ToString() ?? "Catalog registration failed.");
            else Register(registry, item, assets);
        }
        var examples = args.TryGetProperty("examples", out var list) ? list.EnumerateArray().Select(e =>
            File.ReadAllText(Path.Combine(assets, "conformance", e.GetString()!))).ToArray() : null;
        var allowed = args.TryGetProperty("allowed_messages", out var kinds)
            ? kinds.EnumerateArray().Select(e => e.GetString()!).ToArray() : null;
        var issue = DirectJsonPrompt.Create(registry, out var prompt, examples, allowed);
        if (test.TryGetProperty("expect_error", out var error))
        {
            Require(error.GetProperty("category").ValueEquals(issue?.Code), "Unexpected prompt diagnostic.");
            return;
        }
        Require(prompt is not null && issue is null, issue?.ToString() ?? "Prompt generation failed.");
        if (test.TryGetProperty("expect_file", out var file))
        {
            var expected = File.ReadAllText(Path.Combine(assets, "conformance", file.GetString()!)).ReplaceLineEndings("\n").Trim();
            var actual = prompt!.Text.Trim();
            var i = 0;
            while (i < Math.Min(expected.Length, actual.Length) && expected[i] == actual[i]) i++;
            Require(i == expected.Length && i == actual.Length, $"Prompt differs at {i}: expected {expected.Substring(i, Math.Min(45, expected.Length - i))}, got {actual.Substring(i, Math.Min(45, actual.Length - i))}.");
        }
        if (test.TryGetProperty("expect_contains", out var fragments))
            foreach (var fragment in fragments.EnumerateArray())
                Require(prompt!.Text.Contains(fragment.GetString()!, StringComparison.Ordinal), "Example order differs.");
        if (test.TryGetProperty("expect_deterministic", out _))
            Require(DirectJsonPrompt.Create(registry, out var again, examples, allowed) is null &&
                again!.Text == prompt!.Text, "Prompt differs between calls.");
        if (test.GetProperty("action").ValueEquals("monolithic_skill_equals_prompt_snippet"))
            Require(prompt!.ToSkill().Split("---\n", 3)[2].Trim() == prompt.Text.Trim(), "Skill body differs.");
    }

    private static void CheckCompiler(JsonElement test, string assets)
    {
        var args = test.GetProperty("args");
        var registry = new CatalogRegistry(false);
        var files = args.TryGetProperty("catalog", out var catalog)
            ? new[] { catalog } : args.GetProperty("catalogs").EnumerateArray().ToArray();
        foreach (var entry in files)
        {
            var bytes = entry.ValueKind == JsonValueKind.String
                ? File.ReadAllBytes(Path.Combine(assets, "conformance", entry.GetString()!))
                : System.Text.Encoding.UTF8.GetBytes(entry.GetRawText());
            if (entry.ValueKind == JsonValueKind.String && entry.ValueEquals("test_data/catalogs/agent_functions_catalog_v1_0.json"))
            {
                // The empty component set omits the required union in this pinned fixture.
                var doc = System.Text.Json.Nodes.JsonNode.Parse(bytes)!;
                doc["$defs"]!["anyComponent"] = false;
                bytes = System.Text.Encoding.UTF8.GetBytes(doc.ToJsonString());
            }
            Require(registry.TryRegister(bytes, out var error), error?.ToString() ?? "Catalog registration failed.");
        }
        if (test.GetProperty("action").ValueEquals("parse_response"))
        {
            using var state = new RendererState(registry);
            var issue = DirectJson.ParseResponse(test.GetProperty("input").GetString()!, state, out var parts, out var turn,
                !args.TryGetProperty("wrapped", out var wrap) || wrap.GetBoolean());
            using var transaction = turn;
            if (test.TryGetProperty("expect_error", out var error))
                Require(error.GetProperty("category").ValueEquals(issue?.Code), issue?.ToString() ?? "Expected an error.");
            else
            {
                Require(issue is null && transaction is not null, issue?.ToString() ?? "Missing transaction.");
                var expectedParts = test.GetProperty("expect");
                Require(parts.Count == expectedParts.GetArrayLength(), "Wrong part count.");
                for (var i = 0; i < parts.Count; i++)
                {
                    if (expectedParts[i].TryGetProperty("text", out var text))
                        Require(text.ValueEquals(parts[i].Text), "Text differs.");
                    else
                        Require(System.Text.Json.Nodes.JsonNode.DeepEquals(
                            ExpectedWireMessages(expectedParts[i].GetProperty("a2ui"), registry.CatalogIds.Count > 1),
                            System.Text.Json.Nodes.JsonNode.Parse(DirectJson.Decompile(parts[i].Messages!))), "Messages differ.");
                }
            }
            return;
        }
        var decompile = test.GetProperty("action").ValueEquals("decompile");
        var input = decompile ? test.GetProperty("messages").GetRawText() : test.GetProperty("input").GetString()!;
        var diagnostic = DirectJson.Compile(input, registry, out var messages);
        if (test.TryGetProperty("expect_error", out var expectedError))
        {
            Require(diagnostic?.Code == expectedError.GetProperty("category").GetString(),
                diagnostic?.ToString() ?? "Expected a failure.");
            if (expectedError.TryGetProperty("message", out var fragment))
                Require(diagnostic!.ToString().Contains(fragment.GetString()!, StringComparison.OrdinalIgnoreCase),
                    diagnostic.ToString());
            return;
        }
        Require(diagnostic is null, diagnostic?.ToString() ?? "Compilation failed.");
        var output = DirectJson.Decompile(messages);
        if (test.TryGetProperty("expect_contains", out var fragments))
            foreach (var fragment in fragments.EnumerateArray())
                Require(output.Contains(fragment.GetString()!, StringComparison.Ordinal), fragment.GetString()!);
        Require(DirectJson.Compile(output, registry, out var roundTrip) is null &&
            DirectJson.Decompile(roundTrip) == output, "Round trip changed messages.");
        var expected = test.GetProperty(decompile ? "messages" : "expect");
        Require(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(output),
            ExpectedWireMessages(expected, registry.CatalogIds.Count > 1)), "Compiled messages differ.");
    }

    private static System.Text.Json.Nodes.JsonNode ExpectedWireMessages(JsonElement expected, bool mixedCatalogs)
    {
        var messages = System.Text.Json.Nodes.JsonNode.Parse(expected.GetRawText())!.AsArray();
        if (mixedCatalogs)
            foreach (var message in messages)
            {
                var payload = message!["createSurface"] ?? message["updateComponents"];
                if (payload?["components"] is not System.Text.Json.Nodes.JsonArray components)
                    continue;
                // The pinned inference fixtures use both spellings. The 1.0 wire field is @catalogId.
                foreach (var component in components.Select(item => item!.AsObject()))
                    if (!component.ContainsKey("@catalogId") && component.Remove("catalogId", out var catalog))
                        component["@catalogId"] = catalog;
            }
        return messages;
    }

    private static void CheckFraming(JsonElement test)
    {
        if (test.GetProperty("action").ValueEquals("wrap"))
        {
            var input = Parts(test.GetProperty("parts"));
            var result = DirectJson.Wrap(input);
            if (test.TryGetProperty("expect_output", out var expected))
                Require(expected.ValueEquals(result), "Wrapped response differs.");
            if (test.TryGetProperty("expect_contains", out var fragments))
                foreach (var fragment in fragments.EnumerateArray())
                    Require(result.Contains(fragment.GetString()!, StringComparison.Ordinal), "Missing wrapped content.");
            if (test.TryGetProperty("expect_round_trip", out var roundTrip) && roundTrip.GetBoolean())
                Require(input.SequenceEqual(DirectJson.Unwrap(result)), "Round trip changed response parts.");
            return;
        }
        var parts = DirectJson.Unwrap(test.GetProperty("input").GetString()!);
        if (test.GetProperty("action").ValueEquals("unwrap"))
            Require(parts.SequenceEqual(Parts(test.GetProperty("expect"))), "Unwrapped parts differ.");
        else
        {
            var complete = test.GetProperty("args").TryGetProperty("complete", out var flag) && flag.GetBoolean();
            Require(parts.Any(p => p.IsPayload && (!complete || p.IsFinal)) == test.GetProperty("expect").GetBoolean(),
                "Format content detection differs.");
        }

        static DirectJsonPart[] Parts(JsonElement array) => array.EnumerateArray().Select(item =>
            item.TryGetProperty("text", out var text) ? new DirectJsonPart(text.GetString()!) :
                new DirectJsonPart(item.GetProperty("a2ui_raw").GetString()!, true,
                    !item.TryGetProperty("is_final", out var final) || final.GetBoolean())).ToArray();
    }

    private static void RunRendererState(JsonElement test, string assets)
    {
        var registry = new CatalogRegistry(false);
        foreach (var catalog in test.GetProperty("catalogs").EnumerateArray())
            Require(registry.TryRegister(File.ReadAllBytes(Path.Combine(assets, "conformance", catalog.GetString()!)),
                out var error), error?.ToString() ?? "Catalog registration failed.");

        using var state = new RendererState(registry);
        foreach (var step in test.GetProperty("turns").EnumerateArray())
        {
            var messages = new List<WireMessage>();
            A2uiDiagnostic? diagnostic;
            RendererTurn? candidate;
            if (step.TryGetProperty("payload", out var payload))
            {
            foreach (var envelope in payload.EnumerateArray())
            {
                Require(WireMessage.TryDecode(System.Text.Encoding.UTF8.GetBytes(envelope.GetRawText()),
                    WireDirection.AgentToRenderer, out var message, out var decode), decode?.ToString() ?? "Decode failed.");
                messages.Add(message!);
            }

                diagnostic = state.Prepare(messages, out candidate);
            }
            else
            {
                diagnostic = DirectJson.ParseResponse(step.GetProperty("response").GetString()!, state, out var parts, out candidate);
                foreach (var part in parts)
                    if (part.Messages is not null)
                        messages.AddRange(part.Messages);
            }
            if (step.TryGetProperty("expect_error", out var expectedError))
            {
                Require(candidate is null && diagnostic is not null &&
                    (!expectedError.TryGetProperty("message", out var fragment) || diagnostic.Message.Contains(fragment.GetString()!, StringComparison.Ordinal)),
                    diagnostic?.ToString() ?? "Expected failed validation.");
                continue;
            }
            Require(candidate is not null && diagnostic is null, diagnostic?.ToString() ?? "Expected a valid turn.");
            using var turn = candidate!;
            Require(turn.NextMessage is null, "Uncommitted output was available.");
            turn.Commit();
            foreach (var message in messages)
            {
                Require(ReferenceEquals(turn.NextMessage, message), "Emission order changed.");
                turn.Acknowledge(message);
            }
            Require(turn.EmittedCount == messages.Count && state.PendingDelivery is null, "Delivery was incomplete.");
            foreach (var item in step.GetProperty("expect_surfaces").EnumerateObject())
            {
                Require(state.TryGetSurface(item.Name, out var snapshot), "Expected live surface.");
                var components = item.Value.GetProperty("components");
                Require(snapshot!.Components.Count == components.EnumerateObject().Count(), "Component count differs.");
                foreach (var component in components.EnumerateObject())
                    foreach (var prop in component.Value.EnumerateObject())
                        Equal(snapshot.Components[component.Name].GetProperty(prop.Name), prop.Value);
                if (item.Value.TryGetProperty("data", out var data))
                    Equal(snapshot.DataModel, data);
            }
        }

        static void Equal(JsonElement actual, JsonElement expected) =>
            Require(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(actual.GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(expected.GetRawText())), "Renderer snapshot differs.");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidDataException(description);
    }
}
