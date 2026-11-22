using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace A2UI.Core;

// Evaluates the subset used by the pinned protocol and Basic Catalog schemas.
internal sealed class SchemaValidator(Func<string, JsonElement?> resolve, bool deferCatalogItems = false,
    Func<JsonElement, string?, (JsonElement Document, JsonElement Schema)?>? resolveFunction = null)
{
    internal A2uiDiagnostic? Validate(JsonElement instance, JsonElement schema, JsonElement document, string? surfaceCatalog = null) =>
        Evaluate(instance, schema, document, "", 0, surfaceCatalog).Error;

    internal A2uiDiagnostic? ValidateComponent(JsonElement instance, JsonElement schema,
        JsonElement document, JsonElement common, string? surfaceCatalog)
    {
        var envelope = Evaluate(instance, common.GetProperty("$defs").GetProperty("ComponentCommon"),
            common, "", 0, surfaceCatalog);
        if (envelope.Error is not null)
            return envelope.Error;
        var item = Evaluate(instance, schema, document, "", 0, surfaceCatalog);
        if (item.Error is not null)
            return item.Error;
        foreach (var member in instance.EnumerateObject())
            if (!envelope.Contains(member.Name) && !item.Contains(member.Name))
                return new("unexpectedProperty", WireJson.Path("", member.Name), "Component field is not declared.");
        return null;
    }

    private Result Evaluate(JsonElement value, JsonElement schema, JsonElement document, string path, int depth, string? surfaceCatalog)
    {
        Result Fail(string code, string? location = null) =>
            new(new A2uiDiagnostic(code, location ?? path, "Value does not satisfy the schema constraint."));

        if (depth > 128)
            return Fail("schemaDepth");
        if (schema.ValueKind == JsonValueKind.True)
            return default;
        if (schema.ValueKind == JsonValueKind.False)
            return Fail("unexpectedValue");
        if (!WireJson.IsObject(schema))
            return Fail("invalidSchema");

        if (schema.TryGetProperty("type", out var type) && !(type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Any(entry => MatchesType(value, entry.GetString()))
                : MatchesType(value, type.GetString())))
            return Fail("invalidType");
        if (schema.TryGetProperty("const", out var constant) && !Equal(value, constant))
            return Fail("invalidValue");
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(item => Equal(value, item)))
            return Fail("invalidValue");

        var result = new Result();
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var parts = reference.GetString()!.Split('#', 2);
            if (parts[0] == "catalog.json" && deferCatalogItems)
            {
                // Catalog selection and field closure happen when the surface context is available.
                if (parts.Length == 2 && parts[1] == "/$defs/anyFunction")
                {
                    if (value.TryGetProperty("@call", out var name) && name.GetString() == "@index")
                        return Fail("catalogFunction");
                    if (value.TryGetProperty("args", out var arguments))
                    {
                        if (!WireJson.IsObject(arguments))
                            return Fail("invalidType", WireJson.Path(path, "args"));
                        result.Mark("args");
                    }
                }
                else if (WireJson.IsObject(value))
                {
                    foreach (var property in value.EnumerateObject())
                        result.Mark(property.Name);
                }
            }
            else if (parts[0] == "catalog.json" && parts.Length == 2 &&
                parts[1] == "/$defs/anyFunction" && resolveFunction is not null)
            {
                if (resolveFunction(value, surfaceCatalog) is not { } resolved)
                    return Fail("unknownCatalogFunction", WireJson.Path(path, "@call"));
                var outcome = Evaluate(value, resolved.Schema, resolved.Document, path, depth + 1, surfaceCatalog);
                if (outcome.Error is not null)
                    return outcome;
                result.Merge(outcome);
            }
            else
            {
                var targetDocument = parts[0].Length == 0 ||
                    document.TryGetProperty("$id", out var id) && id.GetString() == parts[0]
                    ? document : resolve(parts[0]);
                if (targetDocument is null || !TryResolvePointer(targetDocument.Value, parts.Length == 2 ? parts[1] : "", out var target))
                    return Fail("unresolvedReference");
                var referenced = Evaluate(value, target, targetDocument.Value, path, depth + 1, surfaceCatalog);
                if (referenced.Error is not null)
                    return referenced;
                result.Merge(referenced);
            }
        }

        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(keyword, out var branches))
                continue;
            var matches = 0;
            Result failure = default;
            foreach (var branch in branches.EnumerateArray())
            {
                var outcome = Evaluate(value, branch, document, path, depth + 1, surfaceCatalog);
                if (outcome.Error is not null)
                {
                    if (keyword == "allOf" || outcome.Error.Code is "unresolvedReference" or "schemaDepth")
                        return outcome;
                    if (failure.Error is null || outcome.Error.Path.Length > failure.Error.Path.Length)
                        failure = outcome;
                    continue;
                }

                matches++;
                result.Merge(outcome);
            }

            if (keyword != "allOf" && matches == 0)
                return failure.Error is null ? Fail("noMatchingSchema") : failure;
            if (keyword == "oneOf" && matches > 1)
                return Fail("ambiguousSchema");
        }

        if (schema.TryGetProperty("not", out var excluded))
        {
            var outcome = Evaluate(value, excluded, document, path, depth + 1, surfaceCatalog);
            if (outcome.Error is null)
                return Fail("disallowedValue");
            if (outcome.Error.Code is "unresolvedReference" or "schemaDepth")
                return outcome;
        }

        if (schema.TryGetProperty("if", out var condition))
        {
            var outcome = Evaluate(value, condition, document, path, depth + 1, surfaceCatalog);
            if (outcome.Error?.Code is "unresolvedReference" or "schemaDepth")
                return outcome;
            if (outcome.Error is null)
                result.Merge(outcome);
            if (schema.TryGetProperty(outcome.Error is null ? "then" : "else", out var branch))
            {
                outcome = Evaluate(value, branch, document, path, depth + 1, surfaceCatalog);
                if (outcome.Error is not null)
                    return outcome;
                result.Merge(outcome);
            }
        }

        if (WireJson.IsObject(value))
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var member in required.EnumerateArray())
                    if (!value.TryGetProperty(member.GetString()!, out _))
                        return Fail("missingProperty", WireJson.Path(path, member.GetString()!));

            schema.TryGetProperty("properties", out var properties);
            schema.TryGetProperty("patternProperties", out var patterns);
            schema.TryGetProperty("additionalProperties", out var additional);
            schema.TryGetProperty("unevaluatedProperties", out var unevaluated);
            schema.TryGetProperty("propertyNames", out var names);
            var count = 0;
            foreach (var member in value.EnumerateObject())
            {
                count++;
                var memberPath = WireJson.Path(path, member.Name);
                if (names.ValueKind != JsonValueKind.Undefined)
                {
                    using var name = JsonDocument.Parse(JsonEncodedTextForString(member.Name));
                    var outcome = Evaluate(name.RootElement, names, document, memberPath, depth + 1, surfaceCatalog);
                    if (outcome.Error is not null)
                        return outcome;
                }

                var covered = false;
                if (WireJson.IsObject(properties) && properties.TryGetProperty(member.Name, out var propertySchema))
                {
                    var outcome = Evaluate(member.Value, propertySchema, document, memberPath, depth + 1, surfaceCatalog);
                    if (outcome.Error is not null)
                        return outcome;
                    covered = true;
                }

                if (WireJson.IsObject(patterns))
                    foreach (var pattern in patterns.EnumerateObject())
                        if (MatchesPattern(member.Name, pattern.Name))
                        {
                            var outcome = Evaluate(member.Value, pattern.Value, document, memberPath, depth + 1, surfaceCatalog);
                            if (outcome.Error is not null)
                                return outcome;
                            covered = true;
                        }

                if (!covered && additional.ValueKind != JsonValueKind.Undefined)
                {
                    var outcome = Evaluate(member.Value, additional, document, memberPath, depth + 1, surfaceCatalog);
                    if (outcome.Error is not null)
                        return outcome;
                    covered = true;
                }

                if (!covered && !result.Contains(member.Name) && unevaluated.ValueKind != JsonValueKind.Undefined)
                {
                    var outcome = Evaluate(member.Value, unevaluated, document, memberPath, depth + 1, surfaceCatalog);
                    if (outcome.Error is not null)
                        return outcome;
                    covered = true;
                }

                if (covered)
                    result.Mark(member.Name);
            }

            if (schema.TryGetProperty("minProperties", out var min) && count < min.GetInt32() ||
                schema.TryGetProperty("maxProperties", out var max) && count > max.GetInt32())
                return Fail("propertyCount");
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var min) && value.GetArrayLength() < min.GetInt32())
                return Fail("itemCount");
            if (schema.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var outcome = Evaluate(item, items, document, WireJson.Path(path, index++.ToString(CultureInfo.InvariantCulture)), depth + 1, surfaceCatalog);
                    if (outcome.Error is not null)
                        return outcome;
                }
            }

            if (schema.TryGetProperty("contains", out var contains))
            {
                var found = false;
                foreach (var item in value.EnumerateArray())
                {
                    var outcome = Evaluate(item, contains, document, path, depth + 1, surfaceCatalog);
                    if (outcome.Error?.Code is "unresolvedReference" or "schemaDepth")
                        return outcome;
                    found |= outcome.Error is null;
                }
                if (!found)
                    return Fail("missingItem");
            }

            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
                for (var index = 0; index < value.GetArrayLength(); index++)
                    for (var previous = 0; previous < index; previous++)
                        if (Equal(value[index], value[previous]))
                            return Fail("duplicateItem", WireJson.Path(path, index.ToString(CultureInfo.InvariantCulture)));
        }

        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out var minimum) &&
            value.GetDouble() < minimum.GetDouble())
            return Fail("invalidValue");
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("pattern", out var pattern) && !MatchesPattern(text, pattern.GetString()!))
                return Fail("invalidPattern");
            if (schema.TryGetProperty("format", out var format) && format.GetString() == "date-time" &&
                !value.TryGetDateTimeOffset(out _))
                return Fail("invalidFormat");
        }

        return result;
    }

    private static string JsonEncodedTextForString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

    private static bool MatchesPattern(string value, string pattern) =>
        pattern == @"^[\p{XID_Start}_][\p{XID_Continue}]*$" ? IsIdentifier(value) :
            Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static bool IsIdentifier(string value) => UnicodeIdentifiers.IsIdentifier(value);

    private static bool Equal(JsonElement left, JsonElement right) =>
        JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));

    private static bool MatchesType(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) &&
            decimal.Truncate(number) == number,
        _ => false
    };

    internal static bool TryResolvePointer(JsonElement document, string fragment, out JsonElement target)
    {
        target = document;
        if (fragment.Length == 0)
            return true;
        var pointer = Uri.UnescapeDataString(fragment);
        if (!pointer.StartsWith('/'))
            return false;

        foreach (var segment in pointer[1..].Split('/'))
        {
            var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (WireJson.IsObject(target) && target.TryGetProperty(key, out var property))
                target = property;
            else if (target.ValueKind == JsonValueKind.Array &&
                int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                index < target.GetArrayLength() && key == index.ToString(CultureInfo.InvariantCulture))
                target = target[index];
            else
                return false;
        }

        return true;
    }

    private struct Result(A2uiDiagnostic? error)
    {
        private HashSet<string>? _evaluated;
        internal A2uiDiagnostic? Error { get; } = error;
        internal bool Contains(string property) => _evaluated?.Contains(property) == true;
        internal void Mark(string property) => (_evaluated ??= new(StringComparer.Ordinal)).Add(property);
        internal void Merge(Result other)
        {
            if (other._evaluated is not null)
                (_evaluated ??= new(StringComparer.Ordinal)).UnionWith(other._evaluated);
        }
    }
}
