using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>A failure while compiling or decompiling Express. <see cref="Code"/> is the diagnostic code to report.</summary>
internal sealed class ExpressException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;

    internal static ExpressException Parse(string message) => new("ParseError", message);

    internal static ExpressException Validation(string message) => new("ValidationError", message);
}

/// <summary>A data path such as <c>$/user/name</c>, holding the text after the <c>$</c>.</summary>
internal sealed record PathNode(string Path);

/// <summary>A reference to a variable assigned in the same block.</summary>
internal sealed record VariableNode(string Name);

/// <summary>The <c>_</c> placeholder that skips a positional argument.</summary>
internal sealed class SkippedNode
{
    internal static readonly SkippedNode Instance = new();
}

/// <summary>A component or function call such as <c>Text("Hi", variant="h1")</c>.</summary>
internal sealed class CallNode(string name, List<object?> args, PlainMap kwargs)
{
    internal string Name { get; } = name;

    internal List<object?> Args { get; } = args;

    internal PlainMap Kwargs { get; } = kwargs;
}

/// <summary>A check such as <c>?required</c> or <c>?length(min=1)</c>.</summary>
internal sealed record CheckNode(string Name, List<object?> Args);

/// <summary>A parsed statement. <see cref="Target"/> is null for a standalone expression.</summary>
internal sealed record ExpressStatement(string? Target, object? Value);

/// <summary>
/// Reads Express text into statements. The lexer and the parser are written by hand against the
/// Express.g4 grammar of the pinned upstream revision rather than generated from it, which keeps the
/// package free of a parser runtime. Anything the grammar rejects is reported as a parse error.
/// </summary>
internal static partial class ExpressSyntax
{
    private const int MaxDepth = 256;

    private const string IdentifierPattern = "[a-zA-Z_][a-zA-Z0-9_]*";
    private const string IdentifierCharacters = "a-zA-Z0-9_";

    private static readonly Regex CommentOrToken = new(
        "(" + "[rR]\"\"\"[\\s\\S]*?\"\"\"|[rR]\"[^\\r\\n\"]*\"|\"\"\"(?:\\\\[\\s\\S]|[^\\\\])*?\"\"\"|\"(?:\\\\[\\s\\S]|[^\\\\])*?\"|(?:#|//)[^\\r\\n]*|/\\*[\\s\\S]*?\\*/" + ")|--([^\\r\\n]*)",
        RegexOptions.CultureInvariant);

    private static readonly Regex DeclaredLeftSide = new("(?m)^[ \\t]*(" + IdentifierPattern + ")[ \\t]*=(?!=)", RegexOptions.CultureInvariant);

    private static readonly Regex SplitLeftSide = new(
        "(?m)^([ \\t]*" + IdentifierPattern + ")[ \\t]*(\\r?\\n)[ \\t]*([" + IdentifierCharacters + "]+)(?=[ \\t]*=(?!=))",
        RegexOptions.CultureInvariant);

    private static readonly Regex SplitDataPath = new(
        "(\\$[" + IdentifierCharacters + "/]*/)[ \\t]*(\\r?\\n)[ \\t]*([" + IdentifierCharacters + "/]+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex IdentifierOnly = new("^" + IdentifierPattern + "$", RegexOptions.CultureInvariant);

    private static readonly Regex AssignEquals = new("^[ \\t]*=(?!=)", RegexOptions.CultureInvariant);

    private static readonly Regex SplitArgumentIdentifier = new(
        "([(\\[,:=][ \\t]*\\r?\\n?[ \\t]*|[(\\[,:=][ \\t]*)(" + IdentifierPattern + ")[ \\t]*(\\r?\\n)[ \\t]*([" + IdentifierCharacters +
        "]+)(?=[ \\t]*[,)\\]}]|\\s*\\r?\\n)",
        RegexOptions.CultureInvariant);

    private static readonly Regex OpenSentinel = new("<a2ui(?:\\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CloseSentinel = new("</a2ui\\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SentinelToken = new(
        "[rR]\"\"\"[\\s\\S]*?(?:\"\"\"|\\z)|[rR]\"[^\\r\\n\"]*(?:\"|\\z)|\"\"\"(?:\\\\[\\s\\S]|[^\\\\])*?(?:\"\"\"|\\z)|\"(?:\\\\[\\s\\S]|[^\\\\\"\\r\\n])*(?:\"|\\z)|(?:#|//|--)" +
        "[^\\r\\n]*|/\\*[\\s\\S]*?(?:\\*/|\\z)",
        RegexOptions.CultureInvariant);

    /// <summary>Parses a block of Express text, including any sentinel tags around it.</summary>
    internal static List<ExpressStatement> Parse(string text)
    {
        var body = Normalise(StripSentinelTags(text));
        return new Parser(Lex(body)).ParseProgram();
    }

    /// <summary>Strips outer sentinel tags that sit outside string literals and comments.</summary>
    internal static string StripSentinelTags(string source)
    {
        var segments = new List<(bool IsToken, string Text)>();
        var position = 0;
        foreach (Match match in SentinelToken.Matches(source))
        {
            if (match.Index > position)
                segments.Add((false, source[position..match.Index]));
            var token = match.Value;
            if (!token.StartsWith('"') && !token.StartsWith('r') && !token.StartsWith('R') && !token.StartsWith("/*", StringComparison.Ordinal))
            {
                var close = CloseSentinel.Match(token);
                if (close.Success)
                {
                    if (close.Index > 0)
                        segments.Add((true, token[..close.Index]));
                    segments.Add((false, token[close.Index..]));
                    position = match.Index + match.Length;
                    continue;
                }
            }

            segments.Add((true, token));
            position = match.Index + match.Length;
        }

        if (position < source.Length)
            segments.Add((false, source[position..]));

        var hasOpen = segments.Any(segment => !segment.IsToken && OpenSentinel.IsMatch(segment.Text));
        var output = new StringBuilder();
        var inside = !hasOpen;
        foreach (var (isToken, segment) in segments)
        {
            if (isToken)
            {
                if (inside)
                    output.Append(segment);
                continue;
            }

            var cursor = 0;
            while (cursor < segment.Length)
            {
                if (!inside)
                {
                    var opening = OpenSentinel.Match(segment, cursor);
                    if (!opening.Success)
                        break;
                    inside = true;
                    cursor = opening.Index + opening.Length;
                    continue;
                }

                var closing = CloseSentinel.Match(segment, cursor);
                var nextOpening = OpenSentinel.Match(segment, cursor);
                if (nextOpening.Success && (!closing.Success || nextOpening.Index < closing.Index))
                {
                    output.Append(segment, cursor, nextOpening.Index - cursor);
                    cursor = nextOpening.Index + nextOpening.Length;
                    continue;
                }

                if (closing.Success)
                {
                    output.Append(segment, cursor, closing.Index - cursor);
                    inside = false;
                    cursor = closing.Index + closing.Length;
                    if (!hasOpen)
                        return output.ToString();
                    continue;
                }

                output.Append(segment, cursor, segment.Length - cursor);
                break;
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Rewrites <c>--</c> comments as <c>//</c> comments and joins tokens that a model split across a line
    /// break, outside strings and comments. Line counts are preserved so that errors name the original line.
    /// </summary>
    internal static string Normalise(string source)
    {
        var segments = new List<(bool IsToken, string Text)>();
        var position = 0;
        foreach (Match match in CommentOrToken.Matches(source))
        {
            if (match.Index > position)
                segments.Add((false, source[position..match.Index]));
            segments.Add(match.Groups[1].Success ? (true, match.Groups[1].Value) : (true, "//" + match.Groups[2].Value));
            position = match.Index + match.Length;
        }

        if (position < source.Length)
            segments.Add((false, source[position..]));

        var depth = 0;
        var segmentDepths = new List<int[]>();
        foreach (var (isToken, segment) in segments)
        {
            if (isToken)
            {
                segmentDepths.Add([]);
                continue;
            }

            var depths = new int[segment.Length];
            for (var i = 0; i < segment.Length; i++)
            {
                depths[i] = depth;
                if (segment[i] is '(' or '[' or '{')
                    depth++;
                else if (segment[i] is ')' or ']' or '}' && depth > 0)
                    depth--;
            }

            segmentDepths.Add(depths);
        }

        var splitSecondHalves = new HashSet<(int, int)>();
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].IsToken)
                continue;
            foreach (Match match in SplitLeftSide.Matches(segments[index].Text))
                if (segmentDepths[index][match.Groups[1].Index] == 0)
                    splitSecondHalves.Add((index, match.Groups[3].Index));
        }

        var directlyDeclared = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].IsToken)
                continue;
            foreach (Match match in DeclaredLeftSide.Matches(segments[index].Text))
                if (segmentDepths[index][match.Groups[1].Index] == 0 && !splitSecondHalves.Contains((index, match.Groups[1].Index)))
                    directlyDeclared.Add(match.Groups[1].Value);
        }

        var declared = new HashSet<string>(directlyDeclared, StringComparer.Ordinal);
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].IsToken)
                continue;
            foreach (Match match in SplitLeftSide.Matches(segments[index].Text))
            {
                if (segmentDepths[index][match.Groups[1].Index] != 0)
                    continue;
                var first = match.Groups[1].Value.Trim();
                var second = match.Groups[3].Value;
                if (!directlyDeclared.Contains(first) && !directlyDeclared.Contains(second))
                    declared.Add(first + second);
            }
        }

        var output = new StringBuilder();
        foreach (var (isToken, original) in segments)
        {
            if (isToken)
            {
                output.Append(original);
                continue;
            }

            var segment = SplitDataPath.Replace(original, match =>
            {
                var rest = original[(match.Index + match.Length)..];
                if (IdentifierOnly.IsMatch(match.Groups[3].Value) && AssignEquals.IsMatch(rest))
                    return match.Value;
                return match.Groups[1].Value + match.Groups[3].Value + match.Groups[2].Value;
            });

            segment = SplitLeftSide.Replace(segment, match =>
            {
                var first = match.Groups[1].Value.Trim();
                var second = match.Groups[3].Value;
                if (!directlyDeclared.Contains(first) && !directlyDeclared.Contains(second))
                    return match.Groups[1].Value + second + match.Groups[2].Value;
                return match.Value;
            });

            segment = SplitArgumentIdentifier.Replace(segment, match =>
            {
                var first = match.Groups[2].Value;
                var second = match.Groups[4].Value;
                var joined = first + second;
                if (declared.Contains(joined) && !declared.Contains(first) && !declared.Contains(second))
                    return match.Groups[1].Value + joined + match.Groups[3].Value;
                return match.Value;
            });
            output.Append(segment);
        }

        return output.ToString();
    }

    private enum TokenKind
    {
        String,
        Number,
        Boolean,
        Null,
        Skip,
        Identifier,
        SystemIdentifier,
        Path,
        Check,
        Equals,
        OpenParen,
        CloseParen,
        OpenBracket,
        CloseBracket,
        OpenBrace,
        CloseBrace,
        Comma,
        Colon,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);

    private static List<Token> Lex(string text)
    {
        var tokens = new List<Token>();
        var position = 0;
        var line = 1;
        var lineStart = 0;
        while (position < text.Length)
        {
            var current = text[position];
            if (current == '\n')
            {
                line++;
                position++;
                lineStart = position;
                continue;
            }

            if (current is ' ' or '\t' or '\r' or ';')
            {
                position++;
                continue;
            }

            if (current == '#' || (current == '/' && position + 1 < text.Length && text[position + 1] == '/'))
            {
                while (position < text.Length && text[position] is not ('\r' or '\n'))
                    position++;
                continue;
            }

            if (current == '/' && position + 1 < text.Length && text[position + 1] == '*')
            {
                var close = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (close >= 0)
                {
                    for (var i = position; i < close + 2; i++)
                    {
                        if (text[i] == '\n')
                        {
                            line++;
                            lineStart = i + 1;
                        }
                    }

                    position = close + 2;
                    continue;
                }
            }

            var column = position - lineStart;
            var start = position;
            var (kind, end) = ReadToken(text, position);
            if (end <= position)
            {
                var character = char.ConvertFromUtf32(char.ConvertToUtf32(text, position));
                throw ExpressException.Parse($"Syntax error at line {line}:{column}: token recognition error at: '{character}'");
            }
            var value = text[start..end];
            if (kind == TokenKind.Identifier)
            {
                kind = value switch
                {
                    "true" or "false" => TokenKind.Boolean,
                    "null" => TokenKind.Null,
                    "_" => TokenKind.Skip,
                    _ => TokenKind.Identifier,
                };
            }

            tokens.Add(new Token(kind, value, line, column));
            for (var i = start; i < end; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            position = end;
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, line, position - lineStart));
        return tokens;
    }

    private static (TokenKind Kind, int End) ReadToken(string text, int position)
    {
        var current = text[position];
        switch (current)
        {
            case '$':
                var pathEnd = position + 1;
                while (pathEnd < text.Length && IsPathCharacter(text[pathEnd]))
                    pathEnd++;
                return (TokenKind.Path, pathEnd);
            case '?':
                return (TokenKind.Check, IdentifierEnd(text, position + 1));
            case '@':
                return (TokenKind.SystemIdentifier, IdentifierEnd(text, position + 1));
            case '=':
                return (TokenKind.Equals, position + 1);
            case '(':
                return (TokenKind.OpenParen, position + 1);
            case ')':
                return (TokenKind.CloseParen, position + 1);
            case '[':
                return (TokenKind.OpenBracket, position + 1);
            case ']':
                return (TokenKind.CloseBracket, position + 1);
            case '{':
                return (TokenKind.OpenBrace, position + 1);
            case '}':
                return (TokenKind.CloseBrace, position + 1);
            case ',':
                return (TokenKind.Comma, position + 1);
            case ':':
                return (TokenKind.Colon, position + 1);
            case '"':
                return (TokenKind.String, StringEnd(text, position));
            case '+' or '-' or '.' or (>= '0' and <= '9'):
                return (TokenKind.Number, NumberEnd(text, position));
        }

        // A raw string such as r"..." wins over the identifier r when it is longer, which it always is.
        var identifierEnd = IdentifierEnd(text, position);
        if (current is 'r' or 'R')
        {
            var stringEnd = StringEnd(text, position);
            if (stringEnd > identifierEnd)
                return (TokenKind.String, stringEnd);
        }

        return (TokenKind.Identifier, identifierEnd);
    }
    private static bool IsPathCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is '_' or '/';

    private static int NumberEnd(string text, int position)
    {
        var index = position;
        if (text[index] is '+' or '-')
            index++;
        var digitsStart = index;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
            index++;
        if (index > digitsStart)
        {
            if (index < text.Length && text[index] == '.')
            {
                index++;
                while (index < text.Length && char.IsAsciiDigit(text[index]))
                    index++;
            }
        }
        else if (index + 1 < text.Length && text[index] == '.' && char.IsAsciiDigit(text[index + 1]))
        {
            index++;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
                index++;
        }
        else
        {
            return -1;
        }

        if (index < text.Length && text[index] is 'e' or 'E')
        {
            var exponent = index + 1;
            if (exponent < text.Length && text[exponent] is '+' or '-')
                exponent++;
            var exponentDigits = exponent;
            while (exponent < text.Length && char.IsAsciiDigit(text[exponent]))
                exponent++;
            if (exponent > exponentDigits)
                index = exponent;
        }

        return index;
    }

    /// <summary>Returns the end of the identifier that starts at the position, or -1 when none starts there.</summary>
    private static int IdentifierEnd(string text, int position)
    {
        var index = position;
        var first = true;
        while (index < text.Length)
        {
            var rune = Rune.GetRuneAt(text, index);
            var valid = first
                ? rune.Value == '_' || (rune.IsAscii ? char.IsAsciiLetter((char)rune.Value) : UnicodeIdentifiers.IsIdentifier(rune.ToString()))
                : UnicodeIdentifiers.IsContinue(rune);
            if (!valid)
                break;
            first = false;
            index += rune.Utf16SequenceLength;
        }

        return first ? -1 : index;
    }

    /// <summary>Returns the end of the longest string token that starts at the position, or -1.</summary>
    private static int StringEnd(string text, int position)
    {
        var best = -1;
        var raw = text[position] is 'r' or 'R';
        var quote = raw ? position + 1 : position;
        if (quote >= text.Length || text[quote] != '"')
            return -1;
        var tripleOpen = string.CompareOrdinal(text, quote, "\"\"\"", 0, 3) == 0;
        if (raw)
        {
            if (tripleOpen)
            {
                var close = text.IndexOf("\"\"\"", quote + 3, StringComparison.Ordinal);
                if (close >= 0)
                    best = close + 3;
            }

            var index = quote + 1;
            while (index < text.Length && text[index] is not ('\r' or '\n' or '"'))
                index++;
            if (index < text.Length && text[index] == '"')
                best = Math.Max(best, index + 1);
            return best;
        }

        if (tripleOpen)
        {
            var index = quote + 3;
            while (index < text.Length)
            {
                if (text[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (string.CompareOrdinal(text, index, "\"\"\"", 0, 3) == 0)
                {
                    best = index + 3;
                    break;
                }

                index++;
            }
        }

        var scan = quote + 1;
        while (scan < text.Length)
        {
            if (text[scan] == '\\')
            {
                scan += 2;
                continue;
            }

            if (text[scan] == '"')
                return Math.Max(best, scan + 1);
            scan++;
        }

        return best;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            var next = value[++i];
            switch (next)
            {
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case '\\':
                    builder.Append('\\');
                    break;
                case '"':
                    builder.Append('"');
                    break;
                default:
                    builder.Append('\\').Append(next);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string StringValue(string token)
    {
        var raw = token[0] is 'r' or 'R';
        var body = raw ? token[1..] : token;
        if (body.StartsWith("\"\"\"", StringComparison.Ordinal) && body.Length >= 6)
            return raw ? body[3..^3] : Unescape(body[3..^3]);
        return raw ? body[1..^1] : Unescape(body[1..^1].Trim('\r', '\n'));
    }

    /// <summary>Reads the text of a number token. Integer text stays exact; anything else is a double.</summary>
    internal static object ParseNumber(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw ExpressException.Parse($"Number literal {text} is out of range.");
        if (text.AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
            return number;
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            return integer;
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big) ? big : number;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _index;
        private int _depth;

        private Token Current => tokens[_index];

        private Token Peek(int offset = 1) => tokens[Math.Min(_index + offset, tokens.Count - 1)];

        internal List<ExpressStatement> ParseProgram()
        {
            var statements = new List<ExpressStatement>();
            while (Current.Kind != TokenKind.End)
                statements.Add(ParseStatement());
            return statements;
        }

        private ExpressStatement ParseStatement()
        {
            if (Current.Kind is TokenKind.Identifier or TokenKind.Path && Peek().Kind == TokenKind.Equals)
            {
                var target = Current.Text;
                _index += 2;
                return new ExpressStatement(target, ParseExpression());
            }

            return new ExpressStatement(null, ParseExpression());
        }

        private object? ParseExpression()
        {
            if (++_depth > MaxDepth)
                throw Error("expression is nested too deeply");
            try
            {
                var token = Current;
                switch (token.Kind)
                {
                    case TokenKind.OpenBracket:
                        return ParseArray();
                    case TokenKind.OpenBrace:
                        return ParseMap();
                    case TokenKind.Path:
                        _index++;
                        return new PathNode(token.Text[1..]);
                    case TokenKind.Check:
                        return ParseCheck();
                    case TokenKind.SystemIdentifier:
                        return ParseCall();
                    case TokenKind.Identifier:
                        if (Peek().Kind == TokenKind.OpenParen)
                            return ParseCall();
                        _index++;
                        return new VariableNode(token.Text);
                    case TokenKind.Skip:
                        _index++;
                        return SkippedNode.Instance;
                    case TokenKind.String:
                        _index++;
                        return StringValue(token.Text);
                    case TokenKind.Number:
                        _index++;
                        return ParseNumber(token.Text);
                    case TokenKind.Boolean:
                        _index++;
                        return token.Text == "true";
                    case TokenKind.Null:
                        _index++;
                        return null;
                    default:
                        throw Error($"unexpected {Describe(token)}");
                }
            }
            finally
            {
                _depth--;
            }
        }

        private List<object?> ParseArray()
        {
            _index++;
            var items = new List<object?>();
            while (Current.Kind != TokenKind.CloseBracket)
            {
                items.Add(ParseExpression());
                if (Current.Kind == TokenKind.Comma)
                    _index++;
                else if (Current.Kind != TokenKind.CloseBracket)
                    throw Error($"expected ',' or ']' but found {Describe(Current)}");
            }

            _index++;
            return items;
        }

        private PlainMap ParseMap()
        {
            _index++;
            var map = new PlainMap();
            while (Current.Kind != TokenKind.CloseBrace)
            {
                var keyToken = Current;
                string key;
                if (keyToken.Kind == TokenKind.Identifier)
                    key = keyToken.Text;
                else if (keyToken.Kind == TokenKind.String)
                    key = StringValue(keyToken.Text);
                else
                    throw Error($"expected a map key but found {Describe(keyToken)}");
                _index++;
                if (Current.Kind != TokenKind.Colon)
                    throw Error($"expected ':' but found {Describe(Current)}");
                _index++;
                map[key] = ParseExpression();
                if (Current.Kind == TokenKind.Comma)
                    _index++;
                else if (Current.Kind != TokenKind.CloseBrace)
                    throw Error($"expected ',' or '}}' but found {Describe(Current)}");
            }

            _index++;
            return map;
        }

        private CheckNode ParseCheck()
        {
            var name = Current.Text[1..];
            _index++;
            var args = new List<object?>();
            if (Current.Kind != TokenKind.OpenParen)
                return new CheckNode(name, args);
            _index++;
            while (Current.Kind != TokenKind.CloseParen)
            {
                args.Add(ParseExpression());
                if (Current.Kind == TokenKind.Comma)
                    _index++;
                else if (Current.Kind != TokenKind.CloseParen)
                    throw Error($"expected ',' or ')' but found {Describe(Current)}");
            }

            _index++;
            return new CheckNode(name, args);
        }

        private CallNode ParseCall()
        {
            var name = Current.Text;
            _index++;
            if (Current.Kind != TokenKind.OpenParen)
                throw Error($"expected '(' after {name} but found {Describe(Current)}");
            _index++;
            var args = new List<object?>();
            var kwargs = new PlainMap();
            while (Current.Kind != TokenKind.CloseParen)
            {
                if (Current.Kind == TokenKind.Identifier && Peek().Kind == TokenKind.Equals)
                {
                    var key = Current.Text;
                    _index += 2;
                    kwargs[key] = ParseExpression();
                }
                else
                {
                    args.Add(ParseExpression());
                }

                if (Current.Kind == TokenKind.Comma)
                    _index++;
                else if (Current.Kind != TokenKind.CloseParen)
                    throw Error($"expected ',' or ')' but found {Describe(Current)}");
            }

            _index++;
            return new CallNode(name, args, kwargs);
        }

        private static string Describe(Token token) => token.Kind == TokenKind.End ? "the end of the input" : $"'{token.Text}'";

        private ExpressException Error(string message) =>
            ExpressException.Parse($"Syntax error at line {Current.Line}:{Current.Column}: {message}");
    }
}
