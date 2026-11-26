using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

namespace A2UI.Agent;

// A failure inside one macro expansion. A recoverable failure is an error in the call, which the host may pass through.
// Any other failure stops the whole expansion.
internal sealed class MacroFailure : Exception
{
    internal MacroFailure(string code, string path, string message, bool recoverable)
        : base(message) => (Diagnostic, Recoverable) = (new A2uiDiagnostic(code, path, message), recoverable);

    internal A2uiDiagnostic Diagnostic { get; }

    internal bool Recoverable { get; }

    internal static MacroFailure Argument(string path, string message) => new("ValidationError", path, message, true);

    internal static MacroFailure Fatal(string code, string path, string message) => new(code, path, message, false);
}

// Coerces the properties of a macro call to the wire forms that the parameter types describe.
internal static class MacroCoercion
{
    private static readonly string[] EnvelopeKeys = ["@catalogId", "@accessibility", "@metadata", "@checks"];

    internal static bool IsSlot(MacroParameterType type) =>
        type is MacroParameterType.Component or MacroParameterType.ComponentList or MacroParameterType.ChildList;

    internal static JsonObject Coerce(MacroDefinition macro, JsonObject call, string path)
    {
        var declared = macro.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var member in call)
            if (member.Key is not ("component" or "id") && !declared.Contains(member.Key) &&
                Array.IndexOf(EnvelopeKeys, member.Key) < 0)
                throw MacroFailure.Argument(path + "/" + member.Key, $"Macro {macro.Name} does not declare {member.Key}.");

        var arguments = new JsonObject();
        foreach (var parameter in macro.Parameters)
        {
            var location = path + "/" + parameter.Name;
            var supplied = call[parameter.Name];
            if (supplied is null)
            {
                if (parameter.DefaultValue is not null)
                    arguments[parameter.Name] = parameter.DefaultValue.DeepClone();
                else if (parameter.Required)
                    throw MacroFailure.Argument(location, $"Macro {macro.Name} requires {parameter.Name}.");
                continue;
            }

            arguments[parameter.Name] = CoerceValue(parameter, supplied, location);
        }

        return arguments;
    }

    private static JsonNode CoerceValue(MacroParameter parameter, JsonNode value, string path)
    {
        switch (parameter.Type)
        {
            case MacroParameterType.String:
                var text = AsString(value, path);
                if (parameter.AllowedValues is { } allowed && !allowed.Contains(text, StringComparer.Ordinal))
                    throw MacroFailure.Argument(path, $"{parameter.Name} must be one of: {string.Join(", ", allowed)}.");
                return JsonValue.Create(text);
            case MacroParameterType.Integer:
                return Number(value, path, integral: true);
            case MacroParameterType.Number:
                return Number(value, path, integral: false);
            case MacroParameterType.Boolean:
                return Kind(value, JsonValueKind.True, JsonValueKind.False) ? value.DeepClone() : throw Invalid(path, "a boolean");
            case MacroParameterType.Component:
                return JsonValue.Create(AsString(value, path));
            case MacroParameterType.ComponentList:
                return IdList(value, path);
            case MacroParameterType.ChildList:
                return value is JsonObject template && template["path"] is JsonValue && template["componentId"] is JsonValue
                    ? template.DeepClone() : IdList(value, path);
            case MacroParameterType.Action:
                return Action(value, path);
            case MacroParameterType.DynamicString:
                return IsBinding(value) || Kind(value, JsonValueKind.String) ? value.DeepClone() : throw Invalid(path, "a string, binding or function call");
            case MacroParameterType.DynamicNumber:
                return IsBinding(value) || Kind(value, JsonValueKind.Number) ? value.DeepClone() : throw Invalid(path, "a number, binding or function call");
            case MacroParameterType.DynamicBoolean:
                return IsBinding(value) || Kind(value, JsonValueKind.True, JsonValueKind.False) ? value.DeepClone() : throw Invalid(path, "a boolean, binding or function call");
            case MacroParameterType.DynamicStringList:
                return IsBinding(value) || value is JsonArray strings && strings.All(item => Kind(item, JsonValueKind.String))
                    ? value.DeepClone() : throw Invalid(path, "a list of strings, binding or function call");
            case MacroParameterType.DynamicValue:
                return value.DeepClone();
            case MacroParameterType.CheckRuleList:
                return value is JsonArray rules && rules.All(rule => rule is JsonObject { } entry &&
                    entry["condition"] is JsonObject && Kind(entry["message"], JsonValueKind.String))
                    ? value.DeepClone() : throw Invalid(path, "a list of check rules");
            case MacroParameterType.Accessibility:
                return value is JsonObject ? value.DeepClone() : throw Invalid(path, "accessibility attributes");
            default:
                throw new InvalidOperationException("Unknown macro parameter type.");
        }
    }

    private static bool IsBinding(JsonNode? value) =>
        value is JsonObject members && (Kind(members["@call"], JsonValueKind.String) ||
            members.Count == 1 && Kind(members["@path"], JsonValueKind.String));

    private static bool Kind(JsonNode? value, params JsonValueKind[] kinds) =>
        value is JsonValue primitive && Array.IndexOf(kinds, primitive.GetValueKind()) >= 0;

    private static MacroFailure Invalid(string path, string expected) =>
        MacroFailure.Argument(path, $"Expected {expected}.");

    private static string AsString(JsonNode value, string path) =>
        Kind(value, JsonValueKind.String) ? value.GetValue<string>() : throw Invalid(path, "a string");

    private static JsonNode Number(JsonNode value, string path, bool integral)
    {
        if (!Kind(value, JsonValueKind.Number))
            throw Invalid(path, integral ? "an integer" : "a number");
        if (integral && (!value.AsValue().TryGetValue<double>(out var number) || number != Math.Floor(number)))
            throw Invalid(path, "an integer");
        return value.DeepClone();
    }

    private static JsonArray IdList(JsonNode value, string path)
    {
        if (value is not JsonArray items || !items.All(item => Kind(item, JsonValueKind.String)))
            throw Invalid(path, "a list of component IDs");
        return (JsonArray)value.DeepClone();
    }

    private static JsonNode Action(JsonNode value, string path)
    {
        if (Kind(value, JsonValueKind.String))
            return new JsonObject { ["event"] = new JsonObject { ["name"] = value.GetValue<string>() } };
        if (value is not JsonObject action)
            throw Invalid(path, "an action");
        if (Kind(action["@call"], JsonValueKind.String))
            return new JsonObject { ["functionCall"] = action.DeepClone() };
        if (action["event"] is JsonObject || action["functionCall"] is JsonObject)
            return action.DeepClone();

        // The event body may sit beside its name, or under an event shorthand that holds only the name.
        var name = Kind(action["event"], JsonValueKind.String) ? action["event"] : action["name"];
        if (!Kind(name, JsonValueKind.String))
            throw Invalid(path, "an event name, an event body or a function call");
        var body = new JsonObject { ["name"] = name!.DeepClone() };
        foreach (var key in new[] { "userMessage", "context" })
            if (action[key] is { } extra)
                body[key] = extra.DeepClone();
        return new JsonObject { ["event"] = body };
    }
}
