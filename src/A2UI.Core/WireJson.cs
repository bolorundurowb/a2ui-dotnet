using System.Text.Json;

namespace A2UI.Core;

internal static class WireJson
{
    internal const int RecursionLimit = 50;

    internal static bool ExceedsContainerDepth(JsonElement value, int depth = 0)
    {
        if (IsObject(value))
            return depth > RecursionLimit ||
                value.EnumerateObject().Any(member => ExceedsContainerDepth(member.Value, depth + 1));
        if (value.ValueKind == JsonValueKind.Array)
            return depth > RecursionLimit ||
                value.EnumerateArray().Any(item => ExceedsContainerDepth(item, depth + 1));
        return false;
    }

    internal static bool IsObject(JsonElement value) => value.ValueKind == JsonValueKind.Object;

    internal static string Path(string parent, string member) =>
        parent + "/" + member.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static bool TryReadMessage(ReadOnlyMemory<byte> utf8, int maxEncodedBytes,
        out JsonElement value, out A2uiDiagnostic? error)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEncodedBytes);
        if (utf8.Length <= maxEncodedBytes)
            return TryRead(utf8, out value, out error);

        value = default;
        error = new("messageTooLarge", "", "The encoded A2UI message exceeds the byte limit.");
        return false;
    }

    internal static bool TryRead(ReadOnlyMemory<byte> utf8, out JsonElement value, out A2uiDiagnostic? error)
    {
        value = default;
        try
        {
            _ = StrictUtf8.GetCharCount(utf8.Span);
            using var document = JsonDocument.Parse(utf8);
            error = FindDuplicate(document.RootElement, "");
            if (error is not null)
                return false;
            value = document.RootElement.Clone();
            return true;
        }
        catch (Exception errorCause) when (errorCause is JsonException or System.Text.DecoderFallbackException or InvalidOperationException)
        {
            error = new("invalidJson", "", "Expected one valid UTF-8 JSON value.");
            return false;
        }
    }

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    private static A2uiDiagnostic? FindDuplicate(JsonElement value, string path)
    {
        if (IsObject(value))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                var memberPath = Path(path, member.Name);
                if (!seen.Add(member.Name))
                    return new("duplicateProperty", memberPath, "Duplicate JSON property.");
                if (FindDuplicate(member.Value, memberPath) is { } error)
                    return error;
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            // Force validation of escaped surrogate pairs before returning an owned value.
            _ = value.GetString();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                if (FindDuplicate(item, Path(path, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture))) is { } error)
                    return error;
        }
        return null;
    }
}
