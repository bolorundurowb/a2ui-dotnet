using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2UI.Core;

/// <summary>Builds and coerces the JSON values that Basic Catalog functions exchange.</summary>
internal static class FunctionValues
{
    internal static readonly JsonElement Null = ProtocolSchemas.Parse("null");
    private static readonly JsonElement True = ProtocolSchemas.Parse("true");
    private static readonly JsonElement False = ProtocolSchemas.Parse("false");

    internal static JsonElement Boolean(bool value) => value ? True : False;

    internal static JsonElement String(string value) => ProtocolSchemas.Parse(JsonValue.Create(value)!.ToJsonString());

    internal static JsonElement Number(double value) =>
        ProtocolSchemas.Parse(value.ToString("R", CultureInfo.InvariantCulture));

    internal static JsonElement Validation(bool valid, string? code = null, string? message = null)
    {
        var result = new JsonObject { ["valid"] = valid };
        if (code is not null)
            result["code"] = code;
        if (message is not null)
            result["message"] = message;
        return ProtocolSchemas.Parse(result.ToJsonString());
    }

    internal static A2uiDiagnostic ExpressionError(string message) => new("ExpressionError", "", message);

    /// <summary>Coerces a value to a boolean as DynamicBoolean does. A validation result reads as its valid member.</summary>
    internal static bool ToBoolean(JsonElement? value) => value switch
    {
        null => false,
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.String } text => string.Equals(text.GetString(), "true", StringComparison.OrdinalIgnoreCase),
        { ValueKind: JsonValueKind.Number } number => number.GetDouble() != 0,
        { ValueKind: JsonValueKind.Object } validation when validation.TryGetProperty("valid", out var valid) &&
            valid.ValueKind is JsonValueKind.True or JsonValueKind.False => valid.GetBoolean(),
        _ => false
    };

    /// <summary>Converts a value to the text that a DynamicString shows.</summary>
    internal static string ToText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => "",
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };

    internal static bool TryGetNumber(JsonElement value, out double number)
    {
        number = 0;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                number = value.GetDouble();
                return true;
            case JsonValueKind.String:
                return double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                    double.IsFinite(number);
            default:
                return false;
        }
    }
}
