using System.Globalization;
using System.Text;
using System.Text.Json;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>
/// Writes A2UI 1.0 messages back as Express text. The output is meant to compile back to the messages it was
/// written from, so that a prompt example authored as messages can be shown in the notation a model is asked to write.
/// </summary>
internal sealed class ExpressDecompiler
{
    private const string DefaultSurfaceId = "default_surface";
    private static readonly Dictionary<string, string> NoVariables = new(StringComparer.Ordinal);
    private static readonly string[] SurfaceOperations = ["createSurface", "updateComponents", "updateDataModel", "deleteSurface"];
    private static readonly HashSet<string> ReservedWords = new(["_", "true", "false", "null"], StringComparer.Ordinal);

    private readonly List<CatalogHelper> _helperList = [];
    private readonly Dictionary<string, CatalogHelper> _helpers = new(StringComparer.Ordinal);

    internal ExpressDecompiler(IEnumerable<(string Id, JsonElement Definition)> catalogs)
    {
        foreach (var (id, definition) in catalogs)
        {
            var helper = new CatalogHelper(id, definition);
            _helperList.Add(helper);
            _helpers[id] = helper;
        }
    }

    private string? SoleCatalogId => _helperList.Count == 1 ? _helperList[0].CatalogId : null;

    private static ExpressException Invalid(string message) => ExpressException.Validation(message);

    /// <summary>One unit of a coalesced payload, with the catalog of the surface that the payload created.</summary>
    private sealed record Coalesced(PlainMap Message, string? SurfaceCatalogId)
    {
        internal string? Operation => SurfaceOperations.FirstOrDefault(Message.ContainsKey);

        internal bool IsUpdate => Operation is "updateComponents" or "updateDataModel";
    }

    internal string Decompile(IEnumerable<PlainMap> messages)
    {
        var blocks = new List<string>();
        var callCount = 0;
        var index = 0;
        foreach (var item in Coalesce(messages))
        {
            var isFirst = index++ == 0;
            if (item.Message.Count == 0)
                continue;
            string block;
            if (item.Message.ContainsKey("callRendererFunction") || item.Message.ContainsKey("callFunction"))
                block = DecompileRendererCall(item.Message, ++callCount);
            else
                block = DecompileItem(item, isFirst);
            if (block.Length > 0)
                blocks.Add(block);
        }

        return string.Join("\n", blocks);
    }

    /// <summary>
    /// Folds the follow-up messages of a surface into its <c>createSurface</c> while that does not change what the
    /// payload means, and keeps every other message in order. Each surface operation carries the catalog of the
    /// surface that the payload created.
    /// </summary>
    private static List<Coalesced> Coalesce(IEnumerable<PlainMap> messages)
    {
        var result = new List<PlainMap>();
        var surfaceIds = new List<string?>();
        var openCreates = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            var operation = SurfaceOperations.FirstOrDefault(key => message.Get(key) is PlainMap);
            if (operation is null)
            {
                // A message that is not a surface operation keeps its place relative to every surface message around it.
                openCreates.Clear();
                result.Add(message);
                surfaceIds.Add(null);
                continue;
            }

            var body = (PlainMap)message.Get(operation)!;
            var surfaceId = body.Get("surfaceId") as string;
            int? openIndex = surfaceId is not null && openCreates.TryGetValue(surfaceId, out var found) ? found : null;

            if (operation == "createSurface")
            {
                if (surfaceId is not null)
                    openCreates[surfaceId] = result.Count;
                result.Add(message);
                surfaceIds.Add(surfaceId);
                continue;
            }

            if (openIndex is { } open)
            {
                var createBody = (PlainMap)result[open].Get("createSurface")!;
                if (operation == "updateComponents" && !createBody.ContainsKey("components"))
                {
                    createBody["components"] = body.Get("components") ?? new List<object?>();
                    continue;
                }

                if (operation == "updateDataModel" && IsRootPath(body.Get("path")) && body.Get("value") is PlainMap rootValue)
                {
                    var copy = new PlainMap();
                    foreach (var pair in rootValue)
                        copy[pair.Key] = pair.Value;
                    createBody["dataModel"] = copy;
                    continue;
                }

                openCreates.Remove(surfaceId!);
            }

            if (operation == "deleteSurface" && surfaceId is not null)
                openCreates.Remove(surfaceId);
            result.Add(message);
            surfaceIds.Add(surfaceId);
        }

        var coalesced = new List<Coalesced>();
        var catalogs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < result.Count; i++)
        {
            var surfaceId = surfaceIds[i];
            if (surfaceId is not null && result[i].Get("createSurface") is PlainMap create)
            {
                if (create.Get("catalogId") is string { Length: > 0 } catalogId)
                    catalogs[surfaceId] = catalogId;
                else
                    catalogs.Remove(surfaceId);
            }

            coalesced.Add(new Coalesced(result[i], surfaceId is not null && catalogs.TryGetValue(surfaceId, out var known) ? known : null));
        }

        return coalesced;
    }

    private static bool IsRootPath(object? path) => path is null or "" or "/";

    private static bool IsVariableName(string name) => UnicodeIdentifiers.IsIdentifier(name) && !ReservedWords.Contains(name);

    private static string DeriveVariableName(string componentId)
    {
        var builder = new StringBuilder();
        foreach (var rune in componentId.EnumerateRunes())
            builder.Append(UnicodeIdentifiers.IsIdentifier("_" + rune) ? rune.ToString() : "_");
        var name = builder.ToString();
        while (!IsVariableName(name))
            name = "_" + name;
        return name;
    }

    private static Dictionary<string, string> AssignVariableNames(IReadOnlyList<string> componentIds)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in componentIds)
            if (IsVariableName(id))
                names[id] = id;
        var taken = new HashSet<string>(names.Values, StringComparer.Ordinal);
        foreach (var id in componentIds)
        {
            if (names.ContainsKey(id))
                continue;
            var baseName = DeriveVariableName(id);
            var name = baseName;
            var suffix = 2;
            while (taken.Contains(name))
                name = $"{baseName}_{(suffix++).ToString(CultureInfo.InvariantCulture)}";
            names[id] = name;
            taken.Add(name);
        }

        return names;
    }

    private static string DecompileReference(object? componentId, Dictionary<string, string> variableNames)
    {
        if (componentId is not string id)
            return DecompileString(ScalarText(componentId));
        if (variableNames.TryGetValue(id, out var name))
            return name;
        if (IsVariableName(id) && !variableNames.ContainsValue(id))
            return id;
        return DecompileString(id);
    }

    private static string ScalarText(object? value) => value switch
    {
        null => "None",
        bool flag => flag ? "True" : "False",
        string text => text,
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        _ => Encoding.UTF8.GetString(PlainJson.ToUtf8(value)),
    };

    private static List<(string Path, object? Value)> FlattenDataModel(PlainMap data)
    {
        var results = new List<(string, object?)>();
        Recurse(data, string.Empty);
        return results;

        void Recurse(object? current, string path)
        {
            if (current is PlainMap { Count: > 0 } map)
            {
                foreach (var (key, value) in map)
                    Recurse(value, $"{path}/{key}");
            }
            else
            {
                results.Add((path, current));
            }
        }
    }

    private static bool IsComponentReferenceProperty(object? schema)
    {
        if (schema is not PlainMap map)
            return false;
        if (map.Get("$ref") is string reference
            && (reference.Contains("ComponentId", StringComparison.Ordinal) || reference.Contains("Child", StringComparison.Ordinal)))
            return true;
        foreach (var key in new[] { "oneOf", "anyOf", "allOf" })
            if (map.Get(key) is List<object?> subs && subs.Any(IsComponentReferenceProperty))
                return true;
        return map.Get("type") is "array" && map.ContainsKey("items") && IsComponentReferenceProperty(map.Get("items"));
    }

    private static string DecompileString(string value)
    {
        var hasNewline = value.Contains('\n') || value.Contains('\r');
        var hasTab = value.Contains('\t');
        var hasQuote = value.Contains('"');
        var hasBackslash = value.Contains('\\');

        // Multi-line text goes in triple quotes, unless it ends in a quote, which would merge with the closing quotes.
        if (hasNewline && !value.EndsWith('"') && !value.Contains("\"\"\"", StringComparison.Ordinal))
        {
            if (hasBackslash && !hasTab)
                return $"r\"\"\"{value}\"\"\"";
            return $"\"\"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)}\"\"\"";
        }

        if (hasBackslash && !hasNewline && !hasTab && !hasQuote)
            return $"r\"{value}\"";

        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string DecompileKey(string key) => IsVariableName(key) ? key : DecompileString(key);

    private static string DecompilePath(string path) => path.StartsWith('/') ? "$/" + path[1..] : "$" + path;

    /// <summary>
    /// Splits a finite, non-negative double into its shortest digits and a decimal exponent, such that the value
    /// equals 0.digits times 10 to the power of the exponent. Zero is returned as ("0", 1).
    /// </summary>
    private static (string Digits, int Exponent) ShortestDigits(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var parts = text.Split('E');
        var mantissa = parts[0];
        var exponentText = parts.Length > 1 ? parts[1] : "0";
        var dot = mantissa.IndexOf('.');
        var integerPart = dot < 0 ? mantissa : mantissa[..dot];
        var fractionPart = dot < 0 ? string.Empty : mantissa[(dot + 1)..];
        var rawDigits = integerPart + fractionPart;
        var digits = rawDigits.TrimStart('0');
        if (digits.Length == 0)
            return ("0", 1);
        var exponent = int.Parse(exponentText, CultureInfo.InvariantCulture) + integerPart.Length - (rawDigits.Length - digits.Length);
        return (digits.TrimEnd('0'), exponent);
    }

    /// <summary>Spells a decimal as ECMAScript Number.prototype.toString does, keeping a ".0" on integral values.</summary>
    private static string EcmaScriptNotation(bool negative, string digits, int exponent)
    {
        var k = digits.Length;
        var n = exponent;
        string text;
        if (k <= n && n <= 21)
        {
            text = digits + new string('0', n - k) + ".0";
        }
        else if (n is > 0 and <= 21)
        {
            text = digits[..n] + "." + digits[n..];
        }
        else if (n is > -6 and <= 0)
        {
            text = "0." + new string('0', -n) + digits;
        }
        else
        {
            var fraction = k > 1 ? "." + digits[1..] : string.Empty;
            var power = n - 1;
            text = digits[0] + fraction + "e" + (power >= 0 ? "+" : "-") + Math.Abs(power).ToString(CultureInfo.InvariantCulture);
        }

        return negative ? "-" + text : text;
    }

    private static string DecompileNumber(object value)
    {
        if (value is long integer)
            return integer.ToString(CultureInfo.InvariantCulture);
        var number = PlainJson.AsDouble(value);
        if (!double.IsFinite(number))
            throw Invalid($"The number {number.ToString(CultureInfo.InvariantCulture)} has no Express notation.");
        var (digits, exponent) = ShortestDigits(Math.Abs(number));
        return EcmaScriptNotation(double.IsNegative(number), digits, exponent);
    }

    private static List<string> StripTrailingPlaceholders(List<string> arguments)
    {
        while (arguments.Count > 0 && arguments[^1] == "_")
            arguments.RemoveAt(arguments.Count - 1);
        return arguments;
    }

    private CatalogHelper HelperFor(string catalogId)
    {
        if (_helpers.TryGetValue(catalogId, out var helper))
            return helper;
        throw Invalid($"Unknown catalog '{catalogId}'. Available catalogs: [{string.Join(", ", _helpers.Keys)}]");
    }

    /// <summary>Returns the catalog of a surface that the payload names. A single catalog is always the surface's catalog.</summary>
    private string? SurfaceCatalog(string? catalogId)
    {
        if (SoleCatalogId is null)
            return catalogId;
        if (catalogId is not null)
            HelperFor(catalogId);
        return SoleCatalogId;
    }

    /// <summary>Finds the actual catalog of a component or call, and whether its identifier has to be written.</summary>
    private (CatalogHelper Helper, string? WriteCatalogId) Resolve(string kind, string name, string? catalogId, string? surfaceCatalogId, string label)
    {
        var actual = string.IsNullOrEmpty(catalogId) ? surfaceCatalogId : catalogId;
        if (actual is null)
            throw Invalid($"{char.ToUpperInvariant(label[0]) + label[1..]} '{name}' names no catalogId, and its surface has no catalog.");
        var helper = HelperFor(actual);
        var defined = kind == "component" ? helper.Components : helper.Functions;
        if (!defined.ContainsKey(name))
            throw Invalid($"Unknown {label} '{name}' not defined in catalog '{actual}'.");
        if (SoleCatalogId is not null)
            return (helper, null);
        var found = _helperList.Where(h => (kind == "component" ? h.Components : h.Functions).ContainsKey(name)).Select(h => h.CatalogId).ToList();
        return (helper, found.Count == 1 && found[0] == actual ? null : actual);
    }

    private CatalogHelper SystemFunctionHelper(PlainMap function, string name)
    {
        if (!CatalogHelper.IsSystemFunction(name))
            throw Invalid($"Unknown system function '{name}'.");
        if (function.ContainsKey("catalogId"))
            throw Invalid($"System function '{name}' is defined by the protocol, not a catalog, so it takes no catalogId.");
        return _helperList[0];
    }

    private string DecompileItem(Coalesced item, bool isFirst)
    {
        var envelope = item.Message;
        if (envelope.Get("deleteSurface") is PlainMap delete)
            return $"deleteSurface({DecompileString(delete.Get("surfaceId") as string ?? string.Empty)})";
        if (item.IsUpdate)
            return DecompileUpdate(item, isFirst);

        var create = envelope.Get("createSurface") as PlainMap ?? new PlainMap();
        var surfaceId = create.Get("surfaceId") as string;
        if (string.IsNullOrEmpty(surfaceId))
            surfaceId = DefaultSurfaceId;
        var declaredCatalog = create.Get("catalogId") as string;
        var catalogId = SurfaceCatalog(string.IsNullOrEmpty(declaredCatalog) ? item.SurfaceCatalogId : declaredCatalog);

        var sendDataModel = create.Get("sendDataModel") as bool?;
        var extensions = (create.Get("metadata") as PlainMap)?.Get("extensions") as PlainMap;
        var lines = new List<string>();
        if (!(isFirst && surfaceId == DefaultSurfaceId && sendDataModel is null && extensions is null))
        {
            var header = $"surface({DecompileString(surfaceId)})";
            if (sendDataModel is { } flag)
                header = $"{header[..^1]}, sendDataModel={(flag ? "true" : "false")})";
            if (extensions is not null)
                header = $"{header[..^1]}, extensions={DecompileValue(extensions, NoVariables, false, catalogId)})";
            lines.Add(header);
        }

        var components = create.Get("components") as List<object?> ?? [];
        if (create.Get("dataModel") is PlainMap { Count: > 0 } dataModel)
        {
            foreach (var (path, value) in FlattenDataModel(dataModel).OrderBy(entry => entry.Path, StringComparer.Ordinal))
                lines.Add($"${path} = {DecompileValue(value, NoVariables, false, catalogId)}");
        }

        lines.AddRange(DecompileComponents(components, catalogId));
        return string.Join("\n", lines);
    }

    private string DecompileUpdate(Coalesced item, bool isFirst)
    {
        var operation = item.Operation!;
        var body = (PlainMap)item.Message.Get(operation)!;
        var surfaceId = body.Get("surfaceId") as string;
        if (string.IsNullOrEmpty(surfaceId))
            surfaceId = DefaultSurfaceId;
        var catalogId = SurfaceCatalog(item.SurfaceCatalogId);
        var components = operation == "updateComponents" ? body.Get("components") as List<object?> ?? [] : [];
        var hasRoot = components.Any(component => component is PlainMap map && map.Get("id") as string == "root");
        var header = $"surface({DecompileString(surfaceId)})";
        if (hasRoot)
            header = $"{header[..^1]}, update=true)";

        var lines = new List<string>();
        if (hasRoot || !(isFirst && surfaceId == DefaultSurfaceId))
            lines.Add(header);

        if (operation == "updateComponents")
        {
            lines.AddRange(DecompileComponents(components, catalogId));
        }
        else
        {
            var rawPath = body.Get("path") as string;
            if (string.IsNullOrEmpty(rawPath))
                rawPath = "/";
            lines.Add($"{DecompilePath(rawPath)} = {DecompileValue(body.Get("value"), NoVariables, false, catalogId)}");
        }

        return string.Join("\n", lines);
    }

    private string DecompileRendererCall(PlainMap envelope, int callIndex)
    {
        string? functionCallId = null;
        PlainMap function;
        if (envelope.Get("callRendererFunction") is PlainMap { } rendererCall && rendererCall.Get("callFunction") is PlainMap inner)
        {
            function = inner;
            functionCallId = rendererCall.Get("functionCallId") as string;
        }
        else
        {
            function = envelope.Get("callFunction") as PlainMap ?? new PlainMap();
        }

        var call = DecompileCall(function, NoVariables, SoleCatalogId);
        if (!string.IsNullOrEmpty(functionCallId) && functionCallId != $"call_{callIndex.ToString(CultureInfo.InvariantCulture)}")
        {
            var separator = call.EndsWith("()", StringComparison.Ordinal) ? string.Empty : ", ";
            call = $"{call[..^1]}{separator}functionCallId={DecompileString(functionCallId)})";
        }

        return call;
    }

    private List<string> DecompileComponents(List<object?> components, string? surfaceCatalogId)
    {
        var maps = components.Select(component => component as PlainMap ?? throw Invalid("A component must be an object.")).ToList();
        var ids = maps.Select(map => map.Get("id") as string ?? throw Invalid("A component is missing its id.")).ToList();
        var invalid = ids.FirstOrDefault(id => !UnicodeIdentifiers.IsIdentifier(id));
        if (invalid is not null)
            throw Invalid($"Component id '{invalid}' must be a valid UAX #31 identifier");
        var variableNames = AssignVariableNames(ids);

        var lines = new List<string>();
        foreach (var component in maps)
        {
            var id = (string)component.Get("id")!;
            var variableName = variableNames[id];
            var componentName = component.Get("component") as string ?? throw Invalid($"Component '{id}' is missing its component name.");
            var (helper, writeCatalogId) = Resolve("component", componentName, ComponentCatalogId(component, surfaceCatalogId), surfaceCatalogId, "component");

            var properties = helper.GetComponentProperties(componentName);
            var customEnvelope = helper.GetCustomEnvelope(componentName);
            var checkable = helper.IsCheckable(componentName);
            var arguments = new List<string>();
            for (var index = 0; index < properties.Count; index++)
            {
                var propertyName = properties[index];
                if (propertyName == "checks" && !customEnvelope.Contains("checks") && checkable)
                {
                    var checks = EnvelopeValue(component, "checks", customEnvelope) as List<object?> ?? [];
                    if (checks.Count == 0)
                    {
                        arguments.Add("_");
                        continue;
                    }

                    arguments.Add(DecompileChecks(checks, component, variableNames, surfaceCatalogId));
                    continue;
                }

                if (component.ContainsKey(propertyName))
                {
                    var schema = helper.GetPropertySchema(componentName, propertyName);
                    arguments.Add(DecompileValue(component.Get(propertyName), variableNames, IsComponentReferenceProperty(schema), surfaceCatalogId));
                }
                else if (properties.Skip(index + 1).Any(later => (later != "checks" || customEnvelope.Contains("checks")) && component.ContainsKey(later)))
                {
                    // A placeholder keeps the position of a later property that has a value.
                    arguments.Add("_");
                }
            }

            StripTrailingPlaceholders(arguments);

            if (customEnvelope.Contains("checks") && checkable
                && EnvelopeValue(component, "checks", customEnvelope) is List<object?> { Count: > 0 } customChecks)
                arguments.Add(DecompileChecks(customChecks, component, variableNames, surfaceCatalogId));

            if (writeCatalogId is not null)
                arguments.Add($"catalogId={DecompileString(writeCatalogId)}");
            if (variableName != id)
                arguments.Add($"id={DecompileString(id)}");
            if (EnvelopeValue(component, "metadata", customEnvelope) is PlainMap metadata && metadata.Get("extensions") is PlainMap extensions)
                arguments.Add($"extensions={DecompileValue(extensions, variableNames, false, surfaceCatalogId)}");

            lines.Add($"{variableName} = {componentName}({string.Join(", ", arguments)})");
        }

        return lines;
    }

    private string DecompileChecks(List<object?> checks, PlainMap component, Dictionary<string, string> variableNames, string? surfaceCatalogId)
    {
        var texts = checks.Select(rule => DecompileCheck(rule, component, variableNames, surfaceCatalogId)).ToList();
        return texts.Count == 1 ? texts[0] : $"[{string.Join(", ", texts)}]";
    }

    /// <summary>
    /// Reads an envelope mixin property. The <c>@</c> key wins. The bare key is the legacy fallback, unless the
    /// component declares a property of that bare name itself.
    /// </summary>
    private static object? EnvelopeValue(PlainMap component, string name, ISet<string> customProperties)
    {
        var atKey = "@" + name;
        if (component.ContainsKey(atKey))
            return component.Get(atKey);
        if (customProperties.Contains(name))
            return null;
        return component.Get(name);
    }

    /// <summary>Returns the catalog override of a component, or null when it names none.</summary>
    private string? ComponentCatalogId(PlainMap component, string? surfaceCatalogId)
    {
        if (component.ContainsKey("@catalogId"))
        {
            if (component.Get("@catalogId") is not string { Length: > 0 } overrideId)
            {
                throw Invalid($"Component '{component.Get("id") as string ?? "<unknown>"}' has an invalid '@catalogId': " +
                    $"{Encoding.UTF8.GetString(PlainJson.ToUtf8(component.Get("@catalogId")))}.");
            }

            return overrideId;
        }

        // A bare catalogId, which payloads used before the cutover, is read as the override unless the surface
        // catalog declares catalogId as a property of the component.
        if (component.Get("catalogId") is not string legacy)
            return null;
        var home = surfaceCatalogId is not null && _helpers.TryGetValue(surfaceCatalogId, out var found) ? found : null;
        if (component.Get("component") is string componentName)
        {
            if (home is not null && home.Components.ContainsKey(componentName))
            {
                if (home.GetCustomEnvelope(componentName).Contains("catalogId"))
                    return null;
            }
            else
            {
                var declaring = _helperList.Where(h => h.Components.ContainsKey(componentName)).ToList();
                if (declaring.Count > 0 && declaring.All(h => h.GetCustomEnvelope(componentName).Contains("catalogId")))
                    return null;
            }
        }

        return legacy;
    }

    private static bool HasReservedKey(PlainMap map, string name) => map.ContainsKey("@" + name) || map.ContainsKey(name);

    private static object? ReservedValue(PlainMap map, string name) => map.TryGetValue("@" + name, out var value) ? value : map.Get(name);

    private string DecompileCheck(object? ruleValue, PlainMap component, Dictionary<string, string> variableNames, string? surfaceCatalogId)
    {
        var rule = ruleValue as PlainMap ?? throw Invalid("Express cannot write a check that is not an object.");
        // A rule written as a bare function call, without the condition wrapper, is read as its own condition.
        if (!rule.ContainsKey("condition") && HasReservedKey(rule, "call"))
            rule = new PlainMap { ["condition"] = rule };
        if (rule.Get("condition") is not PlainMap condition || !HasReservedKey(condition, "call"))
            throw Invalid("Express cannot write a check whose condition is not a function call.");

        var checkName = ReservedValue(condition, "call") as string ?? throw Invalid("A check names no function.");
        var checkArguments = condition.Get("args") as PlainMap ?? new PlainMap();
        var (helper, writeCatalogId) = Resolve("function", checkName, condition.Get("catalogId") as string, surfaceCatalogId, "check function");
        var parameters = helper.GetFunctionProperties(checkName);

        var componentValue = component.Get("value");
        var startIndex = 0;
        if (parameters.Count > 0 && parameters[0] == "value" && componentValue is PlainMap bound && HasReservedKey(bound, "path")
            && checkArguments.TryGetValue("value", out var currentValue) && PlainJson.DeepEquals(currentValue, componentValue))
            startIndex = 1;

        var arguments = new List<string>();
        foreach (var parameter in parameters.Skip(startIndex))
        {
            arguments.Add(checkArguments.TryGetValue(parameter, out var value)
                ? DecompileValue(value, variableNames, false, surfaceCatalogId)
                : "_");
        }

        var message = rule.Get("message") as string ?? string.Empty;
        if (message.Length > 0 && message != char.ToUpperInvariant(checkName[0]) + checkName[1..].ToLowerInvariant() + " check failed")
            arguments.Add(DecompileString(message));
        else
            StripTrailingPlaceholders(arguments);

        if (writeCatalogId is not null)
            arguments.Add($"{{catalogId: {DecompileString(writeCatalogId)}}}");
        return arguments.Count > 0 ? $"?{checkName}({string.Join(", ", arguments)})" : $"?{checkName}";
    }

    private string DecompileCall(PlainMap function, Dictionary<string, string> variableNames, string? surfaceCatalogId)
    {
        var name = ReservedValue(function, "call") as string ?? string.Empty;
        var args = function.Get("args") as PlainMap ?? new PlainMap();
        CatalogHelper helper;
        string? writeCatalogId = null;
        if (name.StartsWith('@'))
            helper = SystemFunctionHelper(function, name);
        else
            (helper, writeCatalogId) = Resolve("function", name, function.Get("catalogId") as string, surfaceCatalogId, "function");

        var arguments = new List<string>();
        foreach (var parameter in helper.GetFunctionProperties(name))
        {
            arguments.Add(args.TryGetValue(parameter, out var value)
                ? DecompileValue(value, variableNames, false, surfaceCatalogId)
                : "_");
        }

        StripTrailingPlaceholders(arguments);
        if (writeCatalogId is not null)
            arguments.Add($"catalogId={DecompileString(writeCatalogId)}");
        return $"{name}({string.Join(", ", arguments)})";
    }

    private string DecompileValue(object? value, Dictionary<string, string> variableNames, bool isReference, string? surfaceCatalogId)
    {
        switch (value)
        {
            case PlainMap map:
                return DecompileMap(map, variableNames, isReference, surfaceCatalogId);
            case List<object?> list:
                return $"[{string.Join(", ", list.Select(item => DecompileValue(item, variableNames, isReference, surfaceCatalogId)))}]";
            case string text:
                return isReference ? DecompileReference(text, variableNames) : DecompileString(text);
            case bool flag:
                return flag ? "true" : "false";
            case long or double or decimal:
                return DecompileNumber(value);
            case null:
                return "null";
            default:
                return ScalarText(value);
        }
    }

    private string DecompileMap(PlainMap map, Dictionary<string, string> variableNames, bool isReference, string? surfaceCatalogId)
    {
        // A child list template keeps the plain path key.
        if (map.ContainsKey("componentId") && map.Get("path") is string templatePath)
            return $"_template({DecompilePath(templatePath)}, {DecompileReference(map.Get("componentId"), variableNames)})";

        // A data binding is an object whose only key is the binding key, so an @path beside other keys is data.
        if (map.Get("@path") is string boundPath && map.Count == 1)
            return DecompilePath(boundPath);

        if (map.Get("event") is PlainMap eventBody)
        {
            var eventArguments = new List<string> { DecompileString(ScalarText(eventBody.Get("name") ?? string.Empty)) };
            // An empty context is written out so that it compiles back.
            if (eventBody.ContainsKey("context"))
            {
                var entries = (eventBody.Get("context") as PlainMap ?? new PlainMap())
                    .Select(pair => $"{DecompileKey(pair.Key)}: {DecompileValue(pair.Value, variableNames, false, surfaceCatalogId)}");
                eventArguments.Add($"{{{string.Join(", ", entries)}}}");
            }

            if (eventBody.Get("userMessage") is { } userMessage)
                eventArguments.Add($"userMessage={DecompileValue(userMessage, variableNames, false, surfaceCatalogId)}");
            return $"Event({string.Join(", ", eventArguments)})";
        }

        if (map.Get("functionCall") is PlainMap functionCall)
            return DecompileCall(functionCall, variableNames, surfaceCatalogId);

        // From v1.0 only @call names a function call, so an object with a plain call key is data.
        if (map.Get("@call") is string)
            return DecompileCall(map, variableNames, surfaceCatalogId);

        var items = new List<string>();
        foreach (var (key, entry) in map)
        {
            var entryIsReference = isReference || key is "child" or "componentId";
            items.Add($"{DecompileKey(key)}: {DecompileValue(entry, variableNames, entryIsReference, surfaceCatalogId)}");
        }

        return $"{{{string.Join(", ", items)}}}";
    }
}
