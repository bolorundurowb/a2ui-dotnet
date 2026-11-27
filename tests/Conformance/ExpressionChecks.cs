using System.Text.Json;
using A2UI.Core;

internal static class ExpressionChecks
{
    internal static void Run()
    {
        RequireParses(new string('a', 65_536), "input length at the limit");
        RequireParseError(new string('a', 65_537), "input length beyond the limit");
        RequireParses(NestedInterpolation(100), "interpolation depth at the limit");
        RequireParseError(NestedInterpolation(101), "interpolation depth beyond the limit");
        RequireParses(NestedCall(100), "function argument depth at the limit");
        RequireParseError(NestedCall(101), "function argument depth beyond the limit");
        RequireParseError("${/unclosed", "malformed interpolation");

        RequireEvaluationError("${/~2}", default, "ExpressionError", "invalid path escape");
        RequireEvaluationError("${unavailable()}", default, "ExpressionError", "missing resolver");
        RequireEvaluationError("${/unclosed", default, "ParseError", "parse failure during evaluation");

        using var model = JsonDocument.Parse(JsonSerializer.Serialize(new { text = new string('a', 1_048_576) }));
        if (!ExpressionTemplate.TryEvaluate("${/text}", model.RootElement, default, null, null,
                out var result, out var diagnostic) || result?.Length != 1_048_576 || diagnostic is not null)
            throw new InvalidDataException("Could not evaluate an expression at the result length limit.");
        RequireEvaluationError("${/text}a", model.RootElement, "ExpressionError", "result length beyond the limit");
    }

    private static string NestedInterpolation(int depth) =>
        string.Concat(Enumerable.Repeat("${", depth)) + "true" + new string('}', depth);

    private static string NestedCall(int depth) =>
        "${" + string.Concat(Enumerable.Repeat("fn(arg:", depth - 1)) + "true" + new string(')', depth - 1) + "}";

    private static void RequireParses(string template, string scenario)
    {
        if (!ExpressionTemplate.TryParse(template, out var parts, out var diagnostic) ||
            parts.ValueKind != JsonValueKind.Array || diagnostic is not null)
            throw new InvalidDataException($"Expected a parsed expression for {scenario}. Got {diagnostic}.");
    }

    private static void RequireParseError(string template, string scenario)
    {
        if (ExpressionTemplate.TryParse(template, out var parts, out var diagnostic) ||
            parts.ValueKind != JsonValueKind.Undefined || diagnostic?.Code != "ParseError")
            throw new InvalidDataException($"Expected ParseError for {scenario}. Got {diagnostic}.");
    }

    private static void RequireEvaluationError(string template, JsonElement model, string code, string scenario)
    {
        if (ExpressionTemplate.TryEvaluate(template, model, default, null, null, out var result, out var diagnostic) ||
            result is not null || diagnostic?.Code != code)
            throw new InvalidDataException($"Expected {code} for {scenario}. Got {diagnostic}.");
    }
}
