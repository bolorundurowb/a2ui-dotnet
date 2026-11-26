using System.Text.RegularExpressions;

namespace A2UI.Agent;

/// <summary>
/// Finds the Express blocks in a model response. The scan skips strings and comments, so that a closing tag
/// inside either does not end a block, and it recovers a payload whose opening tag the model left out.
/// </summary>
internal static class ExpressBlockLexer
{
    private static readonly Regex OpenTag = new("<a2ui(?:\\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CloseTagAnywhere = new("</a2ui\\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CloseTagHere = new("\\G</a2ui\\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingFence = new("^```[a-zA-Z0-9_-]*\\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TrailingFence = new("\\s*```[a-zA-Z0-9_-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OpenFence = new("^[ \\t]*```[a-zA-Z0-9_-]*[ \\t]*\\r?\\n?", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex ClosingFenceEnd = new("\\r?\\n?[ \\t]*```[ \\t]*\\r?\\n?$", RegexOptions.CultureInvariant);

    // The start of an unfenced payload, for recovering from a missing opening tag.
    private static readonly Regex PayloadStart = new(
        "^[ \\t]*(?:surface\\s*\\(|deleteSurface\\s*\\(|\\$[a-zA-Z0-9_/]*\\s*=(?!=)|[a-zA-Z_][a-zA-Z0-9_]*\\s*=(?!=))",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly string[] SingleLineComments = ["#", "//", "--"];

    internal static List<ExpressPart> Tokenize(string content)
    {
        var parts = new List<ExpressPart>();
        var index = 0;
        var recovered = false;
        while (index < content.Length)
        {
            var open = OpenTag.Match(content, index);
            var searchEnd = open.Success ? open.Index : content.Length;
            if (!recovered && searchEnd > index && TryRecoverUnmatchedClose(parts, content, index, searchEnd) is { } next)
            {
                recovered = true;
                index = next;
                continue;
            }

            if (!open.Success)
                break;
            AddText(parts, content[index..open.Index]);
            var bodyStart = open.Index + open.Length;
            if (ScanBlock(content, bodyStart) is not { } close)
            {
                parts.Add(new ExpressPart(CleanRaw(content[bodyStart..]), true, false));
                return parts;
            }

            parts.Add(new ExpressPart(CleanRaw(content[bodyStart..close.Start]), true));
            index = close.End;
        }

        AddText(parts, content[index..]);
        return parts;
    }

    private static string CleanMarkdown(string text)
    {
        if (text.Length == 0)
            return string.Empty;
        text = text.Trim();
        text = LeadingFence.Replace(text, string.Empty, 1);
        text = TrailingFence.Replace(text, string.Empty, 1);
        text = text.Trim();
        // A run of nothing but backticks is left over from tags written as inline code.
        return text.Trim('`').Length == 0 ? string.Empty : text;
    }

    // No payload starts or ends with a backtick, so the ones at the edges are left over from tags written as inline code.
    private static string CleanRaw(string text) => CleanMarkdown(text).Trim('`').Trim();

    private static void AddText(List<ExpressPart> parts, string text)
    {
        var cleaned = CleanMarkdown(text);
        if (cleaned.Length > 0)
            parts.Add(new ExpressPart(cleaned));
    }

    private static (int Start, int End)? ScanBlock(string content, int start)
    {
        var index = start;
        var end = content.Length;
        var state = ScanState.Block;
        var delimiter = string.Empty;
        var triple = false;
        while (index < end)
        {
            switch (state)
            {
                case ScanState.Block:
                    var close = CloseTagHere.Match(content, index);
                    if (close.Success)
                        return (index, index + close.Length);
                    var current = content[index];
                    if (current == '"')
                    {
                        if (index + 2 < end && content[index + 1] == '"' && content[index + 2] == '"')
                        {
                            delimiter = "\"\"\"";
                            triple = true;
                            index += 3;
                        }
                        else
                        {
                            delimiter = "\"";
                            triple = false;
                            index++;
                        }

                        state = ScanState.String;
                        continue;
                    }

                    if (string.CompareOrdinal(content, index, "/*", 0, 2) == 0)
                    {
                        index += 2;
                        state = ScanState.BlockComment;
                        continue;
                    }

                    var comment = SingleLineComments.FirstOrDefault(marker => string.CompareOrdinal(content, index, marker, 0, marker.Length) == 0);
                    if (comment is not null)
                    {
                        index += comment.Length;
                        state = ScanState.LineComment;
                        continue;
                    }

                    index++;
                    break;
                case ScanState.String:
                    if (content[index] == '\\')
                    {
                        index += 2;
                        continue;
                    }

                    if (triple ? string.CompareOrdinal(content, index, delimiter, 0, 3) == 0 : content[index] == '"')
                    {
                        index += delimiter.Length;
                        state = ScanState.Block;
                        continue;
                    }

                    index++;
                    break;
                case ScanState.LineComment:
                    var commentClose = CloseTagHere.Match(content, index);
                    if (commentClose.Success)
                        return (index, index + commentClose.Length);
                    if (content[index] is '\n' or '\r')
                        state = ScanState.Block;
                    index++;
                    break;
                default:
                    // A close tag inside a block comment does not end the block.
                    if (string.CompareOrdinal(content, index, "*/", 0, 2) == 0)
                    {
                        index += 2;
                        state = ScanState.Block;
                        continue;
                    }

                    index++;
                    break;
            }
        }

        return null;
    }

    private enum ScanState
    {
        Block,
        String,
        LineComment,
        BlockComment,
    }

    /// <summary>Recovers at most one block that a close tag ends when the model left out the opening tag.</summary>
    private static int? TryRecoverUnmatchedClose(List<ExpressPart> parts, string content, int start, int searchEnd)
    {
        var segment = content[start..searchEnd];
        if (!CloseTagAnywhere.IsMatch(segment))
            return null;

        var fences = OpenFence.Matches(segment).ToList();
        for (var i = 0; i < fences.Count; i += 2)
        {
            var fence = fences[i];
            if (ScanBlock(segment, fence.Index + fence.Length) is not { } span)
                continue;
            var body = segment[(fence.Index + fence.Length)..span.Start];
            var stripped = ClosingFenceEnd.Replace(body, string.Empty);
            if (OpenFence.IsMatch(stripped))
                continue;
            var raw = CleanRaw(stripped);
            if (raw.Length == 0)
                continue;
            AddText(parts, segment[..fence.Index]);
            parts.Add(new ExpressPart(raw, true));
            return start + span.End;
        }

        var searchFrom = 0;
        var pair = 0;
        while (pair + 1 < fences.Count)
        {
            if (ScanBlock(segment, fences[pair + 1].Index + fences[pair + 1].Length) is null)
                break;
            searchFrom = fences[pair + 1].Index + fences[pair + 1].Length;
            pair += 2;
        }

        var statement = PayloadStart.Match(segment, searchFrom);
        if (!statement.Success)
            return null;
        if (ScanBlock(segment, statement.Index) is not { } closeSpan)
            return null;
        var payload = CleanRaw(segment[statement.Index..closeSpan.Start]);
        if (payload.Length == 0)
            return null;
        AddText(parts, segment[..statement.Index]);
        parts.Add(new ExpressPart(payload, true));
        return start + closeSpan.End;
    }
}
