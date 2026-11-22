using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using A2UI.Core;
using Json.Schema;

if (args.Length != 1)
    throw new ArgumentException("Pass the path to the verified upstream assets.");

var assets = args[0];
var registry = new CatalogRegistry();
var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
var commonText = File.ReadAllText(Path.Combine(assets, "specification/v1_0/json/common_types.json"));
JsonSchema common;
try
{
    common = JsonSchema.FromText(commonText);
}
catch (RegexParseException)
{
    Console.WriteLine("JsonSchema.Net cannot load the pinned XID pattern. The timing workload excludes extensions.");
    var reduced = JsonNode.Parse(commonText)!;
    reduced["$defs"]!["Extensions"]!.AsObject().Remove("patternProperties");
    common = JsonSchema.FromText(reduced.ToJsonString());
}
var catalog = JsonSchema.FromText(File.ReadAllText(Path.Combine(assets, "catalogs/basic/v1/catalog.json")));
options.SchemaRegistry.Register(common);
options.SchemaRegistry.Register(catalog);
options.SchemaRegistry.Register(new Uri("https://a2ui.org/specification/v1_0/catalog.json"), catalog);
options.SchemaRegistry.Register(new Uri("https://a2ui.org/specification/v1_0/catalogs/basic/common_types.json"), common);
var wrapper = JsonSchema.FromText("""
    {
      "$schema":"https://json-schema.org/draft/2020-12/schema",
      "allOf":[
        {"$ref":"https://a2ui.org/specification/v1_0/common_types.json#/$defs/ComponentCommon"},
        {"$ref":"https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json#/components/Text"}
      ],
      "unevaluatedProperties":false
    }
    """);

using var valid = JsonDocument.Parse("""{"id":"root","component":"Text","text":{"@path":"/label"}}""");
using var invalid = JsonDocument.Parse("""{"id":"root","component":"Text","text":7}""");
if (registry.ValidateComponent(valid.RootElement, CatalogRegistry.BasicCatalogId) is not null ||
    registry.ValidateComponent(invalid.RootElement, CatalogRegistry.BasicCatalogId)?.Path != "/text" ||
    !wrapper.Evaluate(valid.RootElement, options).IsValid || wrapper.Evaluate(invalid.RootElement, options).IsValid)
    throw new InvalidOperationException("The validators disagreed on the workload.");

Console.WriteLine($".NET {Environment.Version} / {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
Measure("Core", () => registry.ValidateComponent(valid.RootElement, CatalogRegistry.BasicCatalogId) is null);
Measure("JsonSchema.Net 7.3.4", () => wrapper.Evaluate(valid.RootElement, options).IsValid);

static void Measure(string name, Func<bool> validate)
{
    const int iterations = 20_000;
    for (var i = 0; i < 2_000; i++)
        validate();

    var clock = new Stopwatch();
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    clock.Start();
    for (var i = 0; i < iterations; i++)
        if (!validate())
            throw new InvalidOperationException("The valid workload was rejected.");
    clock.Stop();
    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
    Console.WriteLine(FormattableString.Invariant(
        $"{name}: {clock.Elapsed.TotalMicroseconds / iterations:F2} us/op, {(double)allocated / iterations:F0} B/op"));
}
