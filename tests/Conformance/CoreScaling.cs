using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

internal static class CoreScaling
{
    internal static string Run(JsonElement test, string assets)
    {
        var catalogs = new CatalogRegistry(includeBasicCatalog: false);
        foreach (var path in test.GetProperty("catalogPaths").EnumerateArray())
        {
            var relativePath = path.GetString()!;
            var bytes = File.ReadAllBytes(Path.Combine(assets, relativePath));
            if (relativePath == "conformance/test_data/catalogs/composition_catalog_v1_0.json")
            {
                // The fixture has no functions and omits the required union for that group.
                var catalog = JsonNode.Parse(bytes)!;
                catalog["$defs"]!["anyFunction"] = false;
                bytes = Encoding.UTF8.GetBytes(catalog.ToJsonString());
            }
            if (!catalogs.TryRegister(bytes, out var error))
                throw new InvalidDataException(error?.ToString());
        }

        var sizes = test.GetProperty("sizes");
        var small = Expand(test.GetProperty("payload"), sizes[0].GetInt32());
        var large = Expand(test.GetProperty("payload"), sizes[1].GetInt32());
        Measure(small, catalogs);
        Measure(large, catalogs);
        var smallSamples = new double[5];
        var largeSamples = new double[5];
        for (var i = 0; i < smallSamples.Length; i++)
        {
            smallSamples[i] = Measure(small, catalogs);
            largeSamples[i] = Measure(large, catalogs);
        }
        Array.Sort(smallSamples);
        Array.Sort(largeSamples);
        var smallMedian = smallSamples[2];
        var largeMedian = largeSamples[2];
        var ratio = largeMedian / smallMedian;
        var limit = test.GetProperty("max_ratio").GetDouble();
        var summary = FormattableString.Invariant(
            $"Median {smallMedian:F1} ms at {sizes[0]} and {largeMedian:F1} ms at {sizes[1]}. Ratio {ratio:F2}, limit {limit:F2}.");
        if (ratio > limit)
            throw new InvalidDataException(summary);
        return summary;
    }

    private static double Measure(JsonElement payload, CatalogRegistry catalogs)
    {
        using var session = new SurfaceSession(catalogs, GraphValidationOptions.Progressive);
        var start = Stopwatch.GetTimestamp();
        foreach (var entry in payload.EnumerateArray())
        {
            if (!WireMessage.TryDecode(Encoding.UTF8.GetBytes(entry.GetRawText()), WireDirection.AgentToRenderer,
                    out var message, out var diagnostic))
                throw new InvalidDataException(diagnostic?.ToString());
            if (session.Apply(message!) is { } rejection)
                throw new InvalidDataException(rejection.ToString());
        }
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static JsonElement Expand(JsonElement payload, int size)
    {
        var builder = new StringBuilder();
        foreach (var part in payload.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                builder.Append(part.GetString());
                continue;
            }
            var template = part.GetProperty("repeat").GetString()!;
            var separator = part.GetProperty("separator").GetString();
            for (var i = 0; i < size; i++)
            {
                if (i > 0)
                    builder.Append(separator);
                builder.Append(template.Replace("{i}", i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            }
        }
        using var doc = JsonDocument.Parse(builder.ToString());
        return doc.RootElement.Clone();
    }
}
