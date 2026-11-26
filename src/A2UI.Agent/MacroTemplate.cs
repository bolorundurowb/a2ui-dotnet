using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2UI.Agent;

// Interprets the declarative macro template syntax used by the upstream macro conformance suite.
internal static class MacroTemplate
{
    internal static MacroResult Expand(JsonObject template, IReadOnlyList<MacroParameter> parameters, MacroInvocation invocation)
    {
        var names = parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        try
        {
            return TryEvaluate(template, invocation.Arguments, names, out var root) && root is JsonObject tree
                ? MacroResult.Success(tree)
                : MacroResult.Failure("The macro template has no root component.");
        }
        catch (InvalidDataException problem)
        {
            return MacroResult.Failure(problem.Message);
        }
    }

    // Returns false when the node is an omitted optional argument.
    private static bool TryEvaluate(JsonNode? node, JsonObject arguments, HashSet<string> names, out JsonNode? result)
    {
        switch (node)
        {
            case JsonObject members when members.Count == 1 && members.TryGetPropertyValue("$param", out var reference):
                var name = reference?.GetValue<string>() ?? throw new InvalidDataException("A $param reference needs a name.");
                result = arguments[name]?.DeepClone();
                return result is not null;
            case JsonObject members:
                var copy = new JsonObject();
                foreach (var member in members)
                    if (TryEvaluate(member.Value, arguments, names, out var value))
                        copy[member.Key] = value;
                result = copy;
                return true;
            case JsonArray items:
                var list = new JsonArray();
                foreach (var item in items)
                {
                    if (item is JsonObject spread && spread.Count == 1 && spread.TryGetPropertyValue("$spreadParam", out var source))
                    {
                        var spreadName = source?.GetValue<string>() ?? throw new InvalidDataException("A $spreadParam reference needs a name.");
                        if (arguments[spreadName] is JsonArray values)
                            foreach (var entry in values)
                                list.Add(entry?.DeepClone());
                    }
                    else if (TryEvaluate(item, arguments, names, out var value))
                        list.Add(value);
                }

                result = list;
                return true;
            case JsonValue text when text.TryGetValue<string>(out var literal):
                result = JsonValue.Create(Interpolate(literal, arguments, names));
                return true;
            default:
                result = node?.DeepClone();
                return true;
        }
    }

    private static string Interpolate(string text, JsonObject arguments, HashSet<string> names)
    {
        if (!text.Contains("${", StringComparison.Ordinal))
            return text;
        var output = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var start = text.IndexOf("${", position, StringComparison.Ordinal);
            var end = start < 0 ? -1 : text.IndexOf('}', start + 2);
            if (end < 0)
            {
                output.Append(text, position, text.Length - position);
                break;
            }

            var path = text.Substring(start + 2, end - start - 2);
            output.Append(text, position, start - position);
            var segments = path.Split('.');
            // Placeholders that do not name a parameter belong to the renderer's own interpolation.
            output.Append(names.Contains(segments[0]) ? Format(Resolve(segments, arguments), path) : text.Substring(start, end - start + 1));
            position = end + 1;
        }

        return output.ToString();
    }

    private static JsonNode? Resolve(string[] segments, JsonObject arguments)
    {
        JsonNode? current = arguments;
        foreach (var segment in segments)
        {
            if (current is not JsonObject members || !members.TryGetPropertyValue(segment, out current))
                return null;
        }

        return current;
    }

    private static string Format(JsonNode? value, string path)
    {
        if (value is not JsonValue primitive)
            throw new InvalidDataException($"Interpolated argument {path} must be a primitive value.");
        return primitive.GetValueKind() switch
        {
            JsonValueKind.String => primitive.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => primitive.ToJsonString(),
            _ => throw new InvalidDataException($"Interpolated argument {path} must be a primitive value.")
        };
    }
}
