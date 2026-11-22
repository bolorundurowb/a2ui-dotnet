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
            string? reason = (isScaling != scaling, version, path) switch
            {
                (true, _, _) => "Run mode excludes this case.",
                (_, "v0.8" or "v0.9" or "v0.9.1" or "0.8" or "0.9" or "0.9.1", _) => "Legacy protocol is outside the profile.",
                (_, _, _) when path.StartsWith("conformance/agent/legacy/", StringComparison.Ordinal) => "Legacy protocol is outside the profile.",
                (_, _, "conformance/core/adapter_conformance.yaml") => "Renderer adapter is outside the headless profile.",
                _ when runScaling => null,
                (_, _, "conformance/core/message_processor_scaling.yaml") when id == "test_scaling_process_single_component_messages_v10" =>
                    "SurfaceSession requires topology validation and has no unchecked processing mode.",
                _ when !scaling && CoreCases.Supports(path, id) => null,
                _ when !scaling && AgentCases.Supports(path, test) => null,
                (_, _, "conformance/core/catalog.yaml") when id == "test_v10_catalog_from_json_v1_0_attributes" =>
                    "The fixture uses a function entry without type and properties. The pinned catalog schema requires both.",
                _ when ReadString(test, "action") is "agent_to_renderer_schema" or "common_types_schema" => "Generated schema parity is outside the current profile.",
                _ => "Public API or fixture adapter is pending."
            };
            if (reason is not null)
                results.Add(new(suite, path, id, version, "excluded", reason));
            else
            {
                try
                {
                    if (runScaling)
                        Console.WriteLine($"{id} {CoreScaling.Run(test, assets)}");
                    else if (suite == "agent")
                        AgentCases.Run(test, assets);
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
