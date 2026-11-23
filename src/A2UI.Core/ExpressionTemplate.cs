using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2UI.Core;

/// <summary>Parses the interpolation syntax used by A2UI formatString.</summary>
public static class ExpressionTemplate
{
    /// <summary>Evaluates a template against a root data model and an optional collection scope.</summary>
    /// <param name="template">The template to evaluate.</param>
    /// <param name="dataModel">The root data model.</param>
    /// <param name="scope">The current collection item, or an undefined value for the root scope.</param>
    /// <param name="catalogId">The catalog selected for the enclosing formatString call.</param>
    /// <param name="invoke">An optional host resolver for function calls, receiving the catalog ID, function name, and resolved arguments. System calls receive a null catalog ID.</param>
    /// <param name="result">The interpolated string on success.</param>
    /// <param name="diagnostic">The reason evaluation failed, if any.</param>
    /// <returns>Whether the template was evaluated. Missing paths interpolate as empty strings.</returns>
    public static bool TryEvaluate(string template, JsonElement dataModel, JsonElement scope, string? catalogId,
        Func<string?, string, IReadOnlyDictionary<string, JsonElement>, JsonElement>? invoke,
        out string? result, out A2uiDiagnostic? diagnostic)
    {
        FunctionInvoker? invoker = invoke is null
            ? null
            : (string? callCatalogId, string name, IReadOnlyDictionary<string, JsonElement> args,
                out JsonElement value, out A2uiDiagnostic? failure) =>
            {
                value = invoke(callCatalogId, name, args);
                failure = null;
                return true;
            };
        return TryEvaluateWith(template, dataModel, scope, catalogId, invoker, out result, out diagnostic);
    }

    internal static bool TryEvaluateWith(string template, JsonElement dataModel, JsonElement scope, string? catalogId,
        FunctionInvoker? invoke, out string? result, out A2uiDiagnostic? diagnostic)
    {
        result = null;
        if (!TryParse(template, out var parts, out diagnostic))
            return false;

        var evaluator = new Evaluator(dataModel, scope.ValueKind == JsonValueKind.Undefined ? dataModel : scope, catalogId, invoke);
        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (!evaluator.TryResolve(part, out var value, out diagnostic))
                return false;
            var display = value.ValueKind switch
            {
                JsonValueKind.Undefined or JsonValueKind.Null => "",
                JsonValueKind.String => value.GetString()!,
                JsonValueKind.Object or JsonValueKind.Array => CompactJson(value),
                _ => value.GetRawText()
            };
            if (display.Length > 1_048_576 - text.Length)
            {
                diagnostic = new("ExpressionError", "", "Interpolated result exceeds the length limit.");
                return false;
            }
            text.Append(display);
        }

        result = text.ToString();
        return true;
    }

    private static string CompactJson(JsonElement value)
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
            value.WriteTo(writer);
        return Encoding.UTF8.GetString(bytes.WrittenSpan);
    }

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

    private sealed class Evaluator(JsonElement root, JsonElement scope, string? catalogId, FunctionInvoker? invoke)
    {
        internal bool TryResolve(JsonElement part, out JsonElement value, out A2uiDiagnostic? diagnostic)
        {
            diagnostic = null;
            value = part;
            if (part.ValueKind != JsonValueKind.Object)
                return true;
            if (part.TryGetProperty("path", out var pointer))
            {
                var path = pointer.GetString()!;
                var absolute = path.StartsWith('/');
                value = default;
                if (!DataPointer.TryParse(absolute ? path : "/" + path, out var segments))
                {
                    diagnostic = new("ExpressionError", "", "Invalid path syntax or depth.");
                    return false;
                }
                DataPointer.TryGet(absolute ? root : scope, segments, out value);
                return true;
            }

            var name = part.GetProperty("call").GetString()!;
            if (invoke is null)
            {
                diagnostic = new("ExpressionError", "", "No function resolver is available.");
                return false;
            }
            var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var argument in part.GetProperty("args").EnumerateObject())
            {
                if (!TryResolve(argument.Value, out var resolved, out diagnostic))
                    return false;
                arguments.Add(argument.Name, resolved);
            }
            return invoke(name.StartsWith('@') ? null : catalogId, name, arguments, out value, out diagnostic);
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

            var systemCall = Take("@");
            var name = ReadToken(allowPath: true);
            if (name.Length == 0)
                throw new FormatException("Unexpected characters in expression.");
            SkipWhitespace();
            if (systemCall)
            {
                if (!Take("("))
                    throw new FormatException("Expected a system function call.");
                return ReadCall(name, depth, systemCall: true);
            }
            return name switch
            {
                "null" => JsonValue.Create("")!,
                "true" => JsonValue.Create(true)!,
                "false" => JsonValue.Create(false)!,
                _ when Take("(") => ReadCall(name, depth),
                _ => new JsonObject { ["path"] = name }
            };
        }

        private JsonObject ReadCall(string name, int depth, bool systemCall = false)
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
            return new JsonObject { ["call"] = systemCall ? "@" + name : name, ["args"] = args, ["returnType"] = "any" };
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
