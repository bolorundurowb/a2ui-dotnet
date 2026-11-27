using System.Text.Json;
using YamlDotNet.Serialization;

if (args.Length == 3 && args[2] == "--express-peer")
{
    ExpressCases.ExportGoldenCorpus(Path.GetFullPath(args[0]), args[1]);
    return 0;
}

if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--scaling"))
{
    Console.Error.WriteLine("Usage: Conformance <pinned-assets> <results.json> [--scaling | --express-peer]");
    return 2;
}

var assets = Path.GetFullPath(args[0]);
var scaling = args.Length == 3;
using var baseline = JsonDocument.Parse(File.ReadAllText("upstream/a2ui-baseline.json"));
var revision = baseline.RootElement.GetProperty("revision").GetString();
var yaml = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization()
    .WithNodeDeserializer(new DoubleScalarDeserializer(), syntax => syntax.OnTop()).Build();
var results = new List<CaseResult>();

if (!scaling)
    ExpressionChecks.Run();

foreach (var suite in new[] { "core", "agent" })
{
    var directory = Path.Combine(assets, "conformance", suite);
    foreach (var file in Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var path = Path.GetRelativePath(assets, file).Replace('\\', '/');
        var isScaling = Path.GetFileNameWithoutExtension(file).Contains("scaling", StringComparison.Ordinal);
        var source = File.ReadAllText(file);
        // The legacy suite contains an isolated surrogate in an expected partial result.
        // Keep it as a literal escape for inventory and exclude that legacy scenario.
        if (path == "conformance/agent/legacy/streaming_parser.yaml")
            source = source.Replace("text: \"Rocket \\ud83d\"", "text: \"Rocket \\\\ud83d\"", StringComparison.Ordinal);
        var document = ToJson(yaml.Deserialize<object>(source));
        var tests = document.ValueKind == JsonValueKind.Array ? document : document.TryGetProperty("tests", out var cases) ? cases : throw new InvalidDataException($"Unknown suite format in {path}.");
        foreach (var test in tests.EnumerateArray())
        {
            var id = ReadString(test, "name") ?? ReadString(test, "id")
                ?? throw new InvalidDataException($"Unnamed scenario in {path}.");
            var version = path == MacroCases.Path ? MacroCases.Version(test)
                : ReadString(test, "protocolVersion") ?? ReadString(test, "version")
                ?? ReadString(document, "protocolVersion") ?? ReadString(document, "version");
            var runScaling = scaling && path == "conformance/core/message_processor_scaling.yaml" &&
                id is "test_scaling_process_single_component_messages_relaxed_v10" or "test_scaling_process_single_component_messages_composition_v10";
            var agentScaling = scaling && (path == "conformance/agent/direct_json/scaling.yaml" &&
                !id.EndsWith("_v09", StringComparison.Ordinal) || path == "conformance/agent/express/scaling.yaml");
            if (isScaling != scaling)
            {
                results.Add(new(suite, path, id, version, "excluded", "Run mode excludes this case."));
                continue;
            }

            if (ProfileCases.Classify(path, id) is { } classification)
            {
                results.Add(new(suite, path, id, version, classification.Status, classification.Reason));
                continue;
            }

            const string Unclassified = "No conformance adapter or exclusion is registered for this case.";
            string? reason = (version, path) switch
            {
                (_, MacroCases.Path) when MacroCases.IsLegacy(test) =>
                    "The case uses a v0.9 envelope. The suite defaults to v0.9 unless envelope_version names 1.0, and v0.9 is outside the profile.",
                ("v0.8" or "v0.9" or "v0.9.1" or "0.8" or "0.9" or "0.9.1", _) => "Legacy protocol is outside the profile.",
                (_, _) when path.StartsWith("conformance/agent/legacy/", StringComparison.Ordinal) => "Legacy protocol is outside the profile.",
                (_, "conformance/core/adapter_conformance.yaml") => "Renderer adapter is outside the headless profile.",
                _ when runScaling || agentScaling => null,
                (_, "conformance/core/message_processor_scaling.yaml") when id == "test_scaling_process_single_component_messages_v10" =>
                    "SurfaceSession requires topology validation and has no unchecked processing mode.",
                _ when CoreCases.CatalogExclusion(path, test, id) is { } catalogReason => catalogReason,
                _ when !scaling && CoreCases.Supports(path, id, test) => null,
                _ when !scaling && FunctionCases.Supports(path, test) => null,
                _ when OutboundCases.Handles(path) && OutboundCases.ExclusionReason(path, id) is { } outboundReason => outboundReason,
                _ when !scaling && OutboundCases.Handles(path) => null,
                _ when RpcCases.Handles(path) && RpcCases.ExclusionReason(path, id) is { } rpcReason => rpcReason,
                _ when !scaling && RpcCases.Handles(path) => null,
                (_, "conformance/core/functions.yaml" or "conformance/core/index_function.yaml")
                    when FunctionCases.ExclusionReason(path, test) is { } why => why,
                (_, "conformance/core/data_context.yaml") => null,
                (_, "conformance/core/accessibility.yaml") when id == "test_accessibility_v1_0_legacy_bare_accessibility_dual_read" =>
                    "The pinned 1.0 component schema requires @accessibility. Core does not accept the transitional bare spelling.",
                (_, "conformance/core/accessibility.yaml") when id == "test_accessibility_implicit_label_inference" =>
                    "Inferring accessible names from a platform's rendered controls belongs to renderer adapters.",
                (_, "conformance/core/accessibility.yaml") => null,
                (_, "conformance/core/node_resolution.yaml") when NodeCases.Supports(id) => null,
                (_, "conformance/core/node_resolution.yaml") => NodeCases.ExclusionReason(test, assets),
                (_, RendererFunctionCases.Path) when id == "test_call_renderer_function_checked_rejects_a_function_that_needs_user_activation" =>
                    "The fixture pairs requiresUserActivation with rendererOrAgent. The pinned catalog schema only allows this requirement on rendererOnly functions.",
                _ when !scaling && path == RendererFunctionCases.Path => null,
                _ when !scaling && path == AgentFunctionCases.Path => null,
                _ when !scaling && path == DataModelCases.Path => null,
                (_, MacroCases.Path) when id == "36_surface_lifecycle_delete_recreate_and_null_catalog_id" =>
                    "The case sends \"@catalogId\": null. The pinned message schema requires a string and Core rejects the null when it decodes the message, so the expander never sees it.",
                _ when !scaling && path == MacroCases.Path => null,
                _ when suite == "agent" && ExpressCases.ExclusionReason(path, test) is { } expressReason => expressReason,
                _ when !scaling && suite == "agent" && ExpressCases.IsExpress(path, test) => null,
                _ when !scaling && AgentCases.Supports(path, test) => null,
                (_, "conformance/agent/direct_json/response_streaming.yaml") when test.GetProperty("args").GetRawText()
                    .Contains("catalog_v0_", StringComparison.Ordinal) => "Legacy protocol is outside the profile.",
                (_, "conformance/agent/catalog_transformer.yaml") when id == "test_function_pruning_on_catalog_without_functions" =>
                    "The source catalog fixture omits its ID. Registration requires a catalog identity before pruning.",
                (_, "conformance/core/validator_v1_0.yaml") when id == "test_v10_uax31_invalid_identifier_error" =>
                    "The fixture requires UAX #31 component IDs. The pinned common types schema defines ComponentId as a string.",
                (_, "conformance/core/message_processor_v1_0.yaml")
                    when id == "test_v10_get_renderer_data_model_filters_by_surface_protocol_version" =>
                    "The fixture creates a v0.9 surface alongside a 1.0 surface. Core supports only 1.0.",
                (_, "conformance/core/message_processor_v1_0.yaml") when id is
                    "test_v10_component_catalog_override" or "test_v10_components_naming_catalogs_on_surface_without_default" or
                    "test_v10_component_from_named_catalog_with_closed_schema_accepted" or
                    "test_v10_component_update_without_catalog_id_on_surface_without_default_errors" or
                    "test_v10_component_update_without_catalog_id_uses_surface_default" or
                    "test_v10_component_naming_catalog_is_checked_against_that_catalog" or
                    "test_v10_update_components_mismatched_catalog_protocol_version_error" =>
                    "The fixture routes a component using bare catalogId. The pinned protocol uses @catalogId for component routing.",
                (_, "conformance/core/message_processor_v1_0.yaml") when id is
                    "test_v10_update_components_stores_component_metadata" or "test_v10_update_components_replaces_component_metadata" or
                    "test_v10_update_components_omitted_metadata_clears_component_metadata" or
                    "test_v10_inline_catalog_id_and_metadata_standard_only_beside_inline_id" =>
                    "The fixture sends component metadata as bare metadata. The pinned protocol uses @metadata.",
                (_, "conformance/core/reserved_keys.yaml") when id is
                    "test_escaped_doubled_at_unescaping" or "test_escaped_doubled_at_metadata_unescaping" =>
                    null,
                _ => Unclassified
            };
            if (reason is not null)
                results.Add(new(suite, path, id, version, reason == Unclassified ? "fail" : "excluded", reason));
            else
            {
                try
                {
                    if (agentScaling)
                        Console.WriteLine($"{id} {AgentScaling.Run(test, assets)}");
                    else if (runScaling)
                        Console.WriteLine($"{id} {CoreScaling.Run(test, assets)}");
                    else if (path == RendererFunctionCases.Path)
                        RendererFunctionCases.Run(test);
                    else if (path == AgentFunctionCases.Path)
                        AgentFunctionCases.Run(test, assets);
                    else if (path == DataModelCases.Path)
                        DataModelCases.Run(test);
                    else if (path == MacroCases.Path)
                        MacroCases.Run(test, document, assets);
                    else if (suite == "agent" && ExpressCases.IsExpress(path, test))
                        ExpressCases.Run(test, assets);
                    else if (suite == "agent")
                        AgentCases.Run(test, assets);
                    else if (OutboundCases.Handles(path))
                        OutboundCases.Run(path, test, assets);
                    else if (RpcCases.Handles(path))
                        RpcCases.Run(path, test);
                    else if (path == "conformance/core/data_context.yaml")
                        NodeCases.RunScope(test);
                    else if (path == "conformance/core/accessibility.yaml")
                        NodeCases.RunAccessibility(test);
                    else if (path == "conformance/core/reserved_keys.yaml" && id is
                        "test_escaped_doubled_at_unescaping" or "test_escaped_doubled_at_metadata_unescaping")
                        NodeCases.RunEscaping(test, assets);
                    else if (path == "conformance/core/node_resolution.yaml")
                        NodeCases.Run(test, assets);
                    else if (FunctionCases.Supports(path, test))
                        FunctionCases.Run(assets, test);
                    else
                        CoreCases.Run(path, test, assets);
                    results.Add(new(suite, path, id, version, "pass", null));
                }
                catch (Exception failure)
                {
                    results.Add(new(suite, path, id, version, "fail", failure.Message));
                }
            }
        }
    }
}

foreach (var (file, skip, failure) in ExpressCases.RunGoldenCorpus(assets, run: !scaling))
    {
        var status = skip is not null ? "excluded" : failure is null ? "pass" : "fail";
        results.Add(new("agent", file, "test_express_specification_round_trip_" + Path.GetFileNameWithoutExtension(file), null, status, skip ?? failure?.Message));
    }

File.WriteAllText(args[1], JsonSerializer.Serialize(new { inventoryVersion = 1, revision, scaling, results },
    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
foreach (var group in results.GroupBy(result => (result.Suite, result.Status)))
    Console.WriteLine($"{group.Key.Suite} {group.Key.Status}: {group.Count()}");

return results.Any(result => result.Status == "fail") ? 1 : 0;

static string? ReadString(JsonElement value, string key) =>
    value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property)
    && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(Normalize(value), new JsonSerializerOptions { MaxDepth = 256, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });

static object? Normalize(object? value) => value switch
{
    IDictionary<object, object> map => map.ToDictionary(
        entry => Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture)!, entry => Normalize(entry.Value)),
    IList<object> list => list.Select(Normalize).ToArray(),
    _ => value
};

sealed record CaseResult(string Suite, string File, string Id, string? Version, string Status, string? Reason);
