namespace A2UI.Agent;

/// <summary>A text or raw payload part from an A2UI Direct JSON response.</summary>
/// <param name="Content">The text or unwrapped payload.</param>
/// <param name="IsPayload">Whether the content is a Direct JSON block.</param>
/// <param name="IsFinal">Whether the closing sentinel was present.</param>
public sealed record DirectJsonPart(string Content, bool IsPayload = false, bool IsFinal = true);

/// <summary>Reads and writes the A2UI Direct JSON response format.</summary>
public static partial class DirectJson
{
    private const string Opening = "<a2ui-json";
    private const string Closing = "</a2ui-json>";

    /// <summary>Splits a complete model response into ordered text and raw JSON blocks.</summary>
    /// <param name="response">The complete model response.</param>
    /// <returns>Ordered parts including any unterminated final block.</returns>
    public static IReadOnlyList<DirectJsonPart> Unwrap(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var parts = new List<DirectJsonPart>();
        var start = 0;
        var recovered = false;
        while (start < response.Length)
        {
            var open = FindOpening(response, start, out var body);
            if (!recovered && response.AsSpan(start, (open < 0 ? response.Length : open) - start).Contains(Closing, StringComparison.Ordinal)
                && TryRecover(response, start, open < 0 ? response.Length : open, out var payload, out var close))
            {
                Text(start, payload);
                parts.Add(new(Clean(response[payload..close]), true));
                start = close + Closing.Length;
                recovered = true;
                continue;
            }
            if (open < 0)
            {
                Text(start, response.Length);
                break;
            }
            Text(start, open);
            var end = FindClosing(response, body, response.Length);
            parts.Add(new(Clean(response[body..(end < 0 ? response.Length : end)]), true, end >= 0));
            start = end < 0 ? response.Length : end + Closing.Length;
        }
        return parts.AsReadOnly();

        void Text(int from, int to)
        {
            var text = response[from..to].Trim();
            if (text.Length > 0 && text.Any(c => c != '`'))
                parts.Add(new(text));
        }
    }

    /// <summary>Writes text and raw payloads in order using Direct JSON sentinel tags.</summary>
    /// <param name="parts">The parts to wrap.</param>
    /// <returns>A model response in the Direct JSON format.</returns>
    public static string Wrap(IEnumerable<DirectJsonPart> parts) =>
        string.Join("\n", parts.Select(p => p.IsPayload
            ? Opening + ">\n" + p.Content + (p.IsFinal ? "\n" + Closing : "") : p.Content));

    private static int FindOpening(string source, int start, out int body)
    {
        body = -1;
        for (var i = start; (i = source.IndexOf(Opening, i, StringComparison.Ordinal)) >= 0; i += Opening.Length)
        {
            var end = i + Opening.Length;
            if (end >= source.Length || (source[end] != '>' && !char.IsWhiteSpace(source[end])))
                continue;
            var bracket = source.IndexOf('>', end);
            if (bracket < 0)
                break;
            body = bracket + 1;
            return i;
        }
        return -1;
    }

    private static int FindClosing(string source, int start, int limit)
    {
        var quoted = false;
        for (var i = start; i < limit; i++)
        {
            var c = source[i];
            if (quoted && c == '\\')
                i++;
            else if (c == '"')
                quoted = !quoted;
            else if (!quoted && c == '<' && source.AsSpan(i, limit - i).StartsWith(Closing, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private static bool TryRecover(string source, int start, int limit, out int payload, out int close)
    {
        payload = close = -1;
        var fenced = false;
        for (var line = start; line < limit;)
        {
            var newline = source.IndexOf('\n', line, limit - line);
            var end = newline < 0 ? limit : newline;
            var content = source.AsSpan(line, end - line).Trim();
            if (content.StartsWith("```", StringComparison.Ordinal))
                fenced = !fenced;
            else if (!content.IsEmpty && content[0] is '[' or '{')
            {
                payload = line;
                if (fenced)
                {
                    var previous = line > start + 1 ? source.LastIndexOf('\n', line - 2, line - start - 1) : -1;
                    var openingLine = Math.Max(start, previous + 1);
                    if (source.AsSpan(openingLine, line - openingLine).Trim().StartsWith("```", StringComparison.Ordinal))
                        payload = openingLine;
                    else
                    {
                        line = end + 1;
                        continue;
                    }
                }
                close = FindClosing(source, line, limit);
                return close >= 0;
            }
            line = end + 1;
        }
        return false;
    }

    internal static string Clean(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            text = text[7..];
        return text.Trim('`').Trim().Trim('`').Trim();
    }
}
