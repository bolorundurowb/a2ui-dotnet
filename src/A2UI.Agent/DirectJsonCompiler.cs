using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

namespace A2UI.Agent;

public static partial class DirectJson
{
    /// <summary>Reads one Direct JSON block and validates its envelopes and catalog items.</summary>
    /// <param name="raw">The unwrapped model output.</param>
    /// <param name="catalogs">The active catalogs. Unqualified items on mixed surfaces must have a unique declaring catalog.</param>
    /// <param name="messages">The validated messages in input order on success.</param>
    /// <returns>A parse or validation diagnostic on failure.</returns>
    public static A2uiDiagnostic? Compile(string raw, CatalogRegistry catalogs, out IReadOnlyList<WireMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var parseError = Decode(raw, out var decoded, catalogs);
        messages = Array.Empty<WireMessage>();
        if (parseError is not null)
            return parseError;
        var created = decoded.Where(m => m.Kind == "createSurface")
            .Select(m => m.Payload.GetProperty("surfaceId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var surfaces = new Dictionary<string, string?>(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in decoded)
        {
            var payload = message.Payload;
            var id = payload.TryGetProperty("surfaceId", out var surface) ? surface.GetString() : null;
            var catalog = id is not null && surfaces.TryGetValue(id, out var known) ? known :
                catalogs.CatalogIds.Count == 1 ? catalogs.CatalogIds.Single() : null;
            if (message.Kind == "createSurface")
            {
                catalog = payload.TryGetProperty("catalogId", out var named) ? named.GetString() : null;
                if (catalog is not null && !catalogs.TryGetDefinition(catalog, out _))
                    return Invalid("catalogId", catalog);
                if (!surfaces.TryAdd(id!, catalog))
                    return Invalid("surfaceId", "Duplicate live surface.");
                deleted.Remove(id!);
            }
            else if (message.Kind == "deleteSurface")
            {
                surfaces.Remove(id!);
                deleted.Add(id!);
            }
            else if (id is not null && (deleted.Contains(id) || created.Contains(id) && !surfaces.ContainsKey(id)))
                return Invalid("surfaceId", "Surface does not exist.");
            if (payload.TryGetProperty("components", out var components))
                foreach (var component in components.EnumerateArray())
                    if (catalogs.ValidateComponent(component, catalog) is { } componentError)
                        return componentError with { Code = "ValidationError" };
            if (message.Kind == "callRendererFunction" &&
                CheckCall(payload.GetProperty("callFunction"), catalog, catalogs, true, id is not null) is { } callError)
                return callError;
        }
        messages = decoded;
        return null;
    }

    internal static A2uiDiagnostic? Decode(string raw, out IReadOnlyList<WireMessage> messages,
        CatalogRegistry? catalogs = null, Dictionary<string, bool>? scopedSurfaces = null,
        RendererState? state = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        messages = Array.Empty<WireMessage>();
        try
        {
            using var document = ReadBlock(Clean(raw));
            var root = document.RootElement;
            var entries = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
            var decoded = new List<WireMessage>(entries.Length);
            foreach (var entry in entries)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    if (catalogs?.CatalogIds.Count > 1 && entry.ValueKind == JsonValueKind.Object)
                    {
                        var envelope = JsonNode.Parse(entry.GetRawText())!.AsObject();
                        if (QualifyCatalogItems(envelope, catalogs, scopedSurfaces ??= new(StringComparer.Ordinal), state) is { } qualificationError)
                            return qualificationError;
                        envelope.WriteTo(writer);
                    }
                    else
                        entry.WriteTo(writer);
                }
                if (!WireMessage.TryDecode(buffer.WrittenMemory, WireDirection.AgentToRenderer, out var message, out var error))
                    return error! with { Code = "ValidationError" };
                decoded.Add(message!);
            }
            messages = decoded.AsReadOnly();
            return null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            return new("ParseError", "", "Expected a Direct JSON message block.");
        }
    }

    /// <summary>Writes an ordered list of messages as JSON for a model example.</summary>
    /// <param name="messages">The messages to write.</param>
    /// <returns>The raw JSON block.</returns>
    public static string Decompile(IEnumerable<WireMessage> messages)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var message in messages)
                message.WriteTo(writer);
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static A2uiDiagnostic Invalid(string key, string reason) => new("ValidationError", "/" + key, reason);

    internal static A2uiDiagnostic? CheckCall(JsonElement call, string? defaultId, CatalogRegistry catalogs, bool outer, bool hasSurface)
    {
        var id = call.TryGetProperty("catalogId", out var explicitId) && explicitId.ValueKind == JsonValueKind.String
            ? explicitId.GetString() : defaultId;
        if (!call.TryGetProperty("@call", out var name) || name.ValueKind != JsonValueKind.String)
            return Invalid("@call", "Invalid @call.");
        if (call.TryGetProperty("args", out var arguments) && arguments.ValueKind != JsonValueKind.Object)
            return Invalid("args", "Expected an args object.");
        if (id is null || !catalogs.TryGetDefinition(id, out var definition))
            return Invalid("catalogId", id ?? "Missing catalogId and no default catalog is available.");
        if (!definition.TryGetProperty("functions", out var functions) ||
            !functions.TryGetProperty(name.GetString()!, out var schema))
            return Invalid("@call", name.GetString()!);
        var caller = schema.TryGetProperty("allowedCallers", out var permitted) ? permitted.GetString() : "rendererOnly";
        if (outer ? caller == "rendererOnly" : caller == "agentOnly")
            return Invalid("allowedCallers", $"Function {name.GetString()} has caller restriction {caller}.");
        if (schema.TryGetProperty("requiresUserActivation", out var activation) && activation.ValueKind == JsonValueKind.True)
            return Invalid("requiresUserActivation", $"Function {name.GetString()} requires user activation.");

        var nestedDefault = hasSurface ? defaultId : id;
        if (call.TryGetProperty("args", out var args) && Walk(args) is { } issue)
            return issue;
        return catalogs.ValidateFunction(call, nestedDefault) is { } error
            ? error with { Code = "ValidationError" }
            : null;

        A2uiDiagnostic? Walk(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    if (Walk(item) is { } issue) return issue;
            }
            if (value.ValueKind != JsonValueKind.Object)
                return null;
            if (value.TryGetProperty("@call", out var nested) && nested.ValueKind == JsonValueKind.String)
                return CheckCall(value, nestedDefault, catalogs, false, true);
            if (value.TryGetProperty("@path", out var path))
            {
                foreach (var property in value.EnumerateObject())
                    if (property.Name != "@path") return Invalid(property.Name, property.Name);
                return path.ValueKind == JsonValueKind.String ? null : Invalid("@path", "Invalid @path.");
            }
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.StartsWith('@') && property.Name != "@call" && !property.Name.StartsWith("@@", StringComparison.Ordinal))
                    return Invalid(property.Name, property.Name);
                if (Walk(property.Value) is { } issue) return issue;
            }
            return null;
        }
    }

    private static JsonDocument ReadBlock(string raw)
    {
        var options = new JsonDocumentOptions { AllowTrailingCommas = true };
        try
        {
            return JsonDocument.Parse(raw, options);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(Repair(raw), options);
        }
    }

    private static string Repair(string raw)
    {
        var output = new StringBuilder(raw.Length);
        var contexts = new Stack<(string? Key, bool Data)>();
        string? key = null;
        var data = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c is '{' or '[')
            {
                contexts.Push((key, data));
                data |= key is "dataModel" or "value";
                output.Append(c);
                continue;
            }
            if (c is '}' or ']' && contexts.TryPop(out var parent))
                (key, data) = parent;
            if (c is not ('"' or '“'))
            {
                output.Append(c);
                continue;
            }
            var delimiter = c == '"' ? '"' : '”';
            var begin = ++i;
            while (i < raw.Length && raw[i] != delimiter)
            {
                if (raw[i] == '\\') i++;
                i++;
            }
            if (i >= raw.Length) throw new JsonException();
            var literal = raw[begin..i];
            var isKey = raw.AsSpan(i + 1).TrimStart().StartsWith(":", StringComparison.Ordinal);
            if (isKey || !data && key is ("surfaceId" or "id" or "component" or "child" or "children" or "catalogId"))
                literal = literal.Replace("\r", "").Replace("\n", "");
            else
                literal = literal.Replace("\r", "\\r").Replace("\n", "\\n");
            if (isKey) key = literal;
            output.Append('"').Append(literal).Append('"');
        }
        return output.ToString();
    }
}
