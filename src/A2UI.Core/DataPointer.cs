using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2UI.Core;

internal enum DataWriteFailureKind
{
    None,
    ThroughPrimitive,
    NonNumericSegment,
    IndexTooLarge,
    Unrepresentable
}

/// <summary>Why a write was refused and the path segment that caused it.</summary>
internal readonly record struct DataWriteFailure(DataWriteFailureKind Kind, string Segment = "");

internal static class DataPointer
{
    /// <summary>The highest list index a write may address. A write past the end pads the list with null up to the index.</summary>
    internal const int MaxListIndex = 10_000;

    private static readonly JsonElement EmptyRoot = ProtocolSchemas.Parse("{}");

    internal static bool TryParse(string path, out string[] segments) => TryParse(path, dropEmptySegments: false, out segments);

    /// <summary>Parses an absolute pointer. With <paramref name="dropEmptySegments"/> an empty path is the root and repeated or trailing slashes collapse.</summary>
    internal static bool TryParse(string path, bool dropEmptySegments, out string[] segments)
    {
        segments = [];
        if (path == "/" || (dropEmptySegments && path.Length == 0))
            return true;
        if (!path.StartsWith('/'))
            return false;

        segments = dropEmptySegments
            ? path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            : path[1..].Split('/');
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

    internal static bool TryUpdate(JsonElement root, string[] segments, JsonElement value, out JsonElement result) =>
        TryUpdate(root, segments, value, out result, out _);

    /// <summary>
    /// Writes <paramref name="value"/> at the path. A null value removes the member, or empties a list slot, and does nothing
    /// when the path holds no value. A refused write leaves <paramref name="result"/> as the unchanged root.
    /// </summary>
    internal static bool TryUpdate(JsonElement root, string[] segments, JsonElement value, out JsonElement result,
        out DataWriteFailure failure)
    {
        result = root;
        failure = default;
        var deleting = value.ValueKind == JsonValueKind.Null;
        if (segments.Length == 0)
        {
            // Clone so that the model never depends on a document the caller may dispose.
            result = deleting ? EmptyRoot : value.Clone();
            return true;
        }

        if (deleting && !TryGet(root, segments, out _))
            return true;

        var candidate = JsonNode.Parse(root.GetRawText());
        failure = Set(candidate, segments, 0, deleting ? null : JsonNode.Parse(value.GetRawText()));
        if (failure.Kind != DataWriteFailureKind.None)
            return false;
        try
        {
            result = ProtocolSchemas.Parse(candidate!.ToJsonString());
            return true;
        }
        catch (JsonException)
        {
            failure = new(DataWriteFailureKind.Unrepresentable);
            return false;
        }
    }

    private static DataWriteFailure Set(JsonNode? container, string[] segments, int depth, JsonNode? value)
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
                    return default;
                }

                // An explicit null is a placeholder that a write replaces with a container.
                if (!obj.TryGetPropertyValue(key, out var child) || child is null)
                    obj[key] = child = CreateContainer(segments[depth + 1]);
                return Set(child, segments, depth + 1, value);
            case JsonArray array:
                var kind = ClassifyIndex(key, out var index);
                if (kind == IndexKind.NotIndex)
                    return new(DataWriteFailureKind.NonNumericSegment, key);
                if (kind == IndexKind.TooLarge || index > MaxListIndex)
                    return new(DataWriteFailureKind.IndexTooLarge, key);
                while (array.Count <= index)
                    array.Add(null);
                if (leaf)
                {
                    array[index] = value;
                    return default;
                }

                var element = array[index];
                if (element is null)
                    array[index] = element = CreateContainer(segments[depth + 1]);
                return Set(element, segments, depth + 1, value);
            default:
                return new(DataWriteFailureKind.ThroughPrimitive, key);
        }
    }

    // A segment that is only digits, without a leading zero, creates a list. Any other segment creates an object.
    private static JsonNode CreateContainer(string next) =>
        ClassifyIndex(next, out _) == IndexKind.NotIndex ? new JsonObject() : new JsonArray();

    private static bool TryIndex(string segment, out int index) => ClassifyIndex(segment, out index) == IndexKind.Index;

    private static IndexKind ClassifyIndex(string segment, out int index)
    {
        index = 0;
        if (segment.Length == 0 || segment.AsSpan().ContainsAnyExceptInRange('0', '9') || (segment.Length > 1 && segment[0] == '0'))
            return IndexKind.NotIndex;
        return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index) ? IndexKind.Index : IndexKind.TooLarge;
    }

    private enum IndexKind
    {
        NotIndex,
        Index,
        TooLarge
    }
}
