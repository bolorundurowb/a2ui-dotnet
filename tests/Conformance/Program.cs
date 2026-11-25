using System.Text.Json;
using YamlDotNet.Serialization;

if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--scaling"))
{
    Console.Error.WriteLine("Usage: Conformance <pinned-assets> <results.json> [--scaling]");
    return 2;
}

var assets = Path.GetFullPath(args[0]);
var scaling = args.Length == 3;
using var baseline = JsonDocument.Parse(File.ReadAllText("upstream/a2ui-baseline.json"));
var revision = baseline.RootElement.GetProperty("revision").GetString();
var yaml = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build();
var results = new List<CaseResult>();

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
            var version = ReadString(test, "protocolVersion") ?? ReadString(test, "version")
                ?? ReadString(document, "protocolVersion") ?? ReadString(document, "version");
            var runScaling = scaling && path == "conformance/core/message_processor_scaling.yaml" &&
                id is "test_scaling_process_single_component_messages_relaxed_v10" or "test_scaling_process_single_component_messages_composition_v10";
            var agentScaling = scaling && path == "conformance/agent/direct_json/scaling.yaml" &&
                !id.EndsWith("_v09", StringComparison.Ordinal);
            string? reason = (isScaling != scaling, version, path) switch
            {
                (true, _, _) => "Run mode excludes this case.",
                (_, "v0.8" or "v0.9" or "v0.9.1" or "0.8" or "0.9" or "0.9.1", _) => "Legacy protocol is outside the profile.",
                (_, _, _) when path.StartsWith("conformance/agent/legacy/", StringComparison.Ordinal) => "Legacy protocol is outside the profile.",
                (_, _, "conformance/core/adapter_conformance.yaml") => "Renderer adapter is outside the headless profile.",
                (_, _, "conformance/agent/direct_json/decompiler.yaml") when id == "test_decompile_json_catalog_qualified_components" =>
                    "Fixture uses bare catalogId on components without a default. Pinned 1.0 wire protocol requires @catalogId.",
                _ when runScaling || agentScaling => null,
                (_, _, "conformance/core/message_processor_scaling.yaml") when id == "test_scaling_process_single_component_messages_v10" =>
                    "SurfaceSession requires topology validation and has no unchecked processing mode.",
                _ when !scaling && CoreCases.Supports(path, id) => null,
                _ when !scaling && FunctionCases.Supports(path, test) => null,
                _ when OutboundCases.Handles(path) && OutboundCases.ExclusionReason(path, id) is { } outboundReason => outboundReason,
                _ when !scaling && OutboundCases.Handles(path) => null,
                _ when RpcCases.Handles(path) && RpcCases.ExclusionReason(path, id) is { } rpcReason => rpcReason,
                _ when !scaling && RpcCases.Handles(path) => null,
                (_, _, "conformance/core/functions.yaml" or "conformance/core/index_function.yaml")
                    when FunctionCases.ExclusionReason(path, test) is { } why => why,
                (_, _, "conformance/core/node_resolution.yaml") when NodeCases.Supports(id) => null,
                (_, _, "conformance/core/node_resolution.yaml") => NodeCases.ExclusionReason(test, assets),
                _ when !scaling && AgentCases.Supports(path, test) => null,
                (_, _, "conformance/agent/direct_json/response_streaming.yaml") when test.GetProperty("args").GetRawText()
                    .Contains("catalog_v0_", StringComparison.Ordinal) => "Legacy protocol is outside the profile.",
                (_, _, "conformance/agent/catalog_transformer.yaml") when id == "test_function_pruning_on_catalog_without_functions" =>
                    "The source catalog fixture omits its ID. Registration requires a catalog identity before pruning.",
                (_, _, "conformance/core/catalog.yaml") when id == "test_v10_catalog_from_json_v1_0_attributes" =>
                    "The fixture uses a function entry without type and properties. The pinned catalog schema requires both.",
                (_, _, "conformance/core/validator_v1_0.yaml") when id == "test_v10_uax31_invalid_identifier_error" =>
                    "The fixture requires UAX #31 component IDs. The pinned common types schema defines ComponentId as a string.",
                (_, _, "conformance/core/message_processor_v1_0.yaml")
                    when id == "test_v10_get_renderer_data_model_filters_by_surface_protocol_version" =>
                    "The fixture creates a v0.9 surface alongside a 1.0 surface. Core supports only 1.0.",
                (_, _, "conformance/core/message_processor_v1_0.yaml") when id is
                    "test_v10_component_catalog_override" or "test_v10_components_naming_catalogs_on_surface_without_default" or
                    "test_v10_component_from_named_catalog_with_closed_schema_accepted" or
                    "test_v10_component_update_without_catalog_id_on_surface_without_default_errors" or
                    "test_v10_component_update_without_catalog_id_uses_surface_default" or
                    "test_v10_component_naming_catalog_is_checked_against_that_catalog" or
                    "test_v10_update_components_mismatched_catalog_protocol_version_error" =>
                    "The fixture routes a component using bare catalogId. The pinned protocol uses @catalogId for component routing.",
                (_, _, "conformance/core/message_processor_v1_0.yaml") when id is
                    "test_v10_update_components_stores_component_metadata" or "test_v10_update_components_replaces_component_metadata" or
                    "test_v10_update_components_omitted_metadata_clears_component_metadata" or
                    "test_v10_inline_catalog_id_and_metadata_standard_only_beside_inline_id" =>
                    "The fixture sends component metadata as bare metadata. The pinned protocol uses @metadata.",
                (_, _, "conformance/core/reserved_keys.yaml") when id is
                    "test_escaped_doubled_at_unescaping" or "test_escaped_doubled_at_metadata_unescaping" =>
                    "The fixture expects @@ keys to be unescaped when the value is evaluated. Core keeps wire values unchanged and resolved nodes do not unescape yet (#12).",
                (_, _, "conformance/core/error_responses.yaml") =>
                    "The case expects a renderer error message with a protocol error code. Mapping diagnostics to those messages belongs to structured errors (#14).",
                _ => "Public API or fixture adapter is pending."
            };
            if (reason is not null)
                results.Add(new(suite, path, id, version, "excluded", reason));
            else
            {
                try
                {
                    if (agentScaling)
                        Console.WriteLine($"{id} {AgentScaling.Run(test, assets)}");
                    else if (runScaling)
                        Console.WriteLine($"{id} {CoreScaling.Run(test, assets)}");
                    else if (suite == "agent")
                        AgentCases.Run(test, assets);
                    else if (OutboundCases.Handles(path))
                        OutboundCases.Run(path, test, assets);
                    else if (RpcCases.Handles(path))
                        RpcCases.Run(path, test);
                    else if (path == "conformance/core/node_resolution.yaml")
                        NodeCases.Run(test, assets);
                    else if (path is "conformance/core/functions.yaml" or "conformance/core/validation_result.yaml"
                        or "conformance/core/index_function.yaml")
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

File.WriteAllText(args[1], JsonSerializer.Serialize(new { revision, scaling, results },
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
