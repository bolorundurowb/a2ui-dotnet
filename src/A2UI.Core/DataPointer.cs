using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2UI.Core;

internal static class DataPointer
{
    private static readonly JsonElement EmptyRoot = ProtocolSchemas.Parse("{}");

    internal static bool TryParse(string path, out string[] segments)
    {
        segments = [];
        if (path == "/")
            return true;
        if (!path.StartsWith('/'))
            return false;

        segments = path[1..].Split('/');
        if (segments.Length > 63)
            return false;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            for (var j = 0; j < segment.Length; j++)
                if (segment[j] == '~' && (++j == segment.Length || segment[j] is not ('0' or '1')))
                    return false;
            segments[i] = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }
        return true;
    }

    internal static bool TryGet(JsonElement value, string[] segments, out JsonElement result)
    {
        result = default;
        foreach (var segment in segments)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object when value.TryGetProperty(segment, out var property):
                    value = property;
                    break;
                case JsonValueKind.Array when TryIndex(segment, out var index) && index < value.GetArrayLength():
                    value = value[index];
                    break;
                default:
                    return false;
            }
        }
        result = value;
        return true;
    }

    internal static bool TryUpdate(JsonElement root, string[] segments, JsonElement value, out JsonElement result)
    {
        result = root;
        var deleting = value.ValueKind == JsonValueKind.Null;
        if (segments.Length == 0)
        {
            result = deleting ? EmptyRoot : value;
            return true;
        }

        var candidate = JsonNode.Parse(root.GetRawText());
        if (!TrySet(candidate, segments, 0, deleting ? null : JsonNode.Parse(value.GetRawText())))
            return false;
        try
        {
            result = ProtocolSchemas.Parse(candidate!.ToJsonString());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TrySet(JsonNode? container, string[] segments, int depth, JsonNode? value)
    {
        var key = segments[depth];
        var leaf = depth == segments.Length - 1;
        switch (container)
        {
            case JsonObject obj:
                if (leaf)
                {
                    if (value is null)
                        obj.Remove(key);
                    else
                        obj[key] = value;
                    return true;
                }
                if (!obj.TryGetPropertyValue(key, out var child))
                {
                    if (value is null)
                        return true;
                    obj[key] = child = CreateContainer(segments[depth + 1]);
                }
                return TrySet(child, segments, depth + 1, value);
            case JsonArray array when TryIndex(key, out var index) && index <= array.Count:
                if (index == array.Count)
                {
                    if (value is null)
                        return true;
                    array.Add(leaf ? value : CreateContainer(segments[depth + 1]));
                    return leaf || TrySet(array[index], segments, depth + 1, value);
                }
                if (leaf)
                {
                    array[index] = value;
                    return true;
                }
                return TrySet(array[index], segments, depth + 1, value);
            default:
                return false;
        }
    }

    private static JsonNode CreateContainer(string next) =>
        TryIndex(next, out _) ? new JsonArray() : new JsonObject();

    private static bool TryIndex(string segment, out int index)
    {
        index = 0;
        return segment.Length != 0 && !segment.AsSpan().ContainsAnyExceptInRange('0', '9') &&
            (segment.Length == 1 || segment[0] != '0') &&
            int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }
}
