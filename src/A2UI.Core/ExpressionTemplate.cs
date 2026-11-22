using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2UI.Core;

/// <summary>Parses the interpolation syntax used by A2UI formatString.</summary>
public static class ExpressionTemplate
{
    /// <summary>Parses a template into literals, path bindings, and function calls.</summary>
    /// <param name="template">The template to parse, limited to 65,536 UTF-16 code units.</param>
    /// <param name="parts">The owned JSON array of parsed parts.</param>
    /// <param name="diagnostic">The parser diagnostic when parsing fails.</param>
    /// <returns>Whether parsing succeeded.</returns>
    public static bool TryParse(string template, out JsonElement parts, out A2uiDiagnostic? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(template);
        try
        {
            if (template.Length > 65_536)
                throw new FormatException("Template length limit exceeded.");
            using var document = JsonDocument.Parse(new Parser(template).Parse().ToJsonString(),
                new JsonDocumentOptions { MaxDepth = 202 });
            parts = document.RootElement.Clone();
            diagnostic = null;
            return true;
        }
        catch (FormatException error)
        {
            parts = default;
            diagnostic = new A2uiDiagnostic("ParseError", "", error.Message);
            return false;
        }
    }

    private sealed class Parser(string source)
    {
        private int _position;
        private char Current => At(_position);

        internal JsonArray Parse()
        {
            var parts = new JsonArray();
            var literal = new StringBuilder();
            while (_position < source.Length)
            {
                if (Take("\\${"))
                    literal.Append("${");
                else if (Take("${"))
                {
                    var part = Interpolation(1);
                    if (part is JsonValue value && value.TryGetValue<string>(out var text))
                        literal.Append(text);
                    else
                    {
                        FlushLiteral();
                        parts.Add(part);
                    }
                }
                else
                    literal.Append(source[_position++]);
            }
            FlushLiteral();
            return parts;

            void FlushLiteral()
            {
                if (literal.Length == 0)
                    return;
                parts.Add((JsonNode?)JsonValue.Create(literal.ToString()));
                literal.Clear();
            }
        }

        private JsonNode Interpolation(int depth)
        {
            var value = Value(depth);
            SkipWhitespace();
            if (_position == source.Length)
                throw new FormatException("Unclosed interpolation.");
            if (!Take("}"))
                throw new FormatException("Unexpected characters after expression.");
            return value;
        }

        private JsonNode Value(int depth)
        {
            if (depth > 100)
                throw new FormatException("Max recursion depth exceeded.");
            SkipWhitespace();
            if (Take("${"))
                return Interpolation(depth + 1);
            if (Current is '\'' or '"')
                return ReadString();

            var unsigned = Current is '+' or '-' ? _position + 1 : _position;
            if (char.IsAsciiDigit(At(unsigned)) || (At(unsigned) == '.' && char.IsAsciiDigit(At(unsigned + 1))))
                return ReadNumber();

            var name = ReadToken(allowPath: true);
            if (name.Length == 0)
                throw new FormatException("Unexpected characters in expression.");
            SkipWhitespace();
            return name switch
            {
                "null" => JsonValue.Create("")!,
                "true" => JsonValue.Create(true)!,
                "false" => JsonValue.Create(false)!,
                _ when Take("(") => ReadCall(name, depth),
                _ => new JsonObject { ["path"] = name }
            };
        }

        private JsonObject ReadCall(string name, int depth)
        {
            if (name.EnumerateRunes().Any(rune => !UnicodeIdentifiers.IsContinue(rune)))
                throw new FormatException("Invalid function name.");
            var args = new JsonObject();
            SkipWhitespace();
            while (!Take(")"))
            {
                var key = ReadToken(allowPath: false);
                SkipWhitespace();
                if (key.Length == 0 || !Take(":"))
                    throw new FormatException("Expected ':' after argument name.");
                args[key] = Value(depth + 1);
                SkipWhitespace();
                if (!Take(","))
                {
                    if (!Take(")"))
                        throw new FormatException("Expected ')' after function arguments.");
                    break;
                }
                SkipWhitespace();
            }
            return new JsonObject { ["call"] = name, ["args"] = args, ["returnType"] = "any" };
        }

        private string ReadToken(bool allowPath)
        {
            var start = _position;
            while (_position < source.Length &&
                Rune.DecodeFromUtf16(source.AsSpan(_position), out var rune, out var length) == OperationStatus.Done &&
                (UnicodeIdentifiers.IsContinue(rune) || (allowPath && rune.Value is '/' or '.' or '-' or '~')))
                _position += length;
            return source[start.._position];
        }

        private JsonValue ReadString()
        {
            var quote = source[_position++];
            var value = new StringBuilder();
            while (_position < source.Length)
            {
                var character = source[_position++];
                if (character == quote)
                    return JsonValue.Create(value.ToString())!;
                if (character == '\\' && _position < source.Length)
                    character = source[_position++] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var escaped => escaped };
                value.Append(character);
            }
            throw new FormatException("Unclosed string literal.");
        }

        private JsonValue ReadNumber()
        {
            var start = _position;
            if (Current is '+' or '-')
                _position++;
            ReadDigits();
            if (Take("."))
                ReadDigits();
            if (Current is 'e' or 'E')
            {
                _position++;
                if (Current is '+' or '-')
                    _position++;
                if (!char.IsAsciiDigit(Current))
                    throw new FormatException("Invalid number literal.");
                ReadDigits();
            }
            if (Current == '.' ||
                !double.TryParse(source.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new FormatException("Invalid number literal.");
            if (!double.IsFinite(number))
                throw new FormatException("Number literal out of range.");
            return JsonValue.Create(number == 0 ? 0d : number)!;
        }

        private void ReadDigits()
        {
            while (char.IsAsciiDigit(Current))
                _position++;
        }

        private void SkipWhitespace()
        {
            while (char.IsWhiteSpace(Current))
                _position++;
        }

        private char At(int position) => position < source.Length ? source[position] : '\0';

        private bool Take(string token)
        {
            if (!source.AsSpan(_position).StartsWith(token, StringComparison.Ordinal))
                return false;
            _position += token.Length;
            return true;
        }
    }
}
