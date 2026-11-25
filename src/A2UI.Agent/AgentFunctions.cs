using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>The resolved input to an agent function.</summary>
/// <param name="CatalogId">The catalog that registered the handler.</param>
/// <param name="Name">The declared function name.</param>
/// <param name="SurfaceId">The surface identified by the renderer.</param>
/// <param name="FunctionCallId">The identifier the response echoes.</param>
/// <param name="Arguments">The validated arguments with schema defaults applied.</param>
public sealed record AgentFunctionCall(string CatalogId, string Name, string SurfaceId,
    string FunctionCallId, JsonElement Arguments);

/// <summary>The host implementation of an agent function.</summary>
/// <param name="call">The validated input.</param>
/// <param name="cancellationToken">Cancellation requested by the host.</param>
/// <returns>The result. An undefined JSON value is sent as null.</returns>
/// <exception cref="AgentFunctionException">An intentional failure with a code and message safe to send to the renderer.</exception>
public delegate ValueTask<JsonElement> AgentFunctionHandler(AgentFunctionCall call, CancellationToken cancellationToken);


/// <summary>A host handler with either a new declaration or a reference to an existing catalog function.</summary>
/// <param name="Name">The function name.</param>
/// <param name="Handler">The function implementation.</param>
/// <param name="ArgumentSchema">The new function's argument schema. An undefined value uses the existing declaration.</param>
/// <param name="ReturnType">The new function's A2UI return type.</param>
/// <param name="Description">The new function's description.</param>
/// <param name="RequiresUserActivation">Whether the renderer must enforce user activation before invoking a new function.</param>
public sealed record AgentFunctionRegistration(string Name, AgentFunctionHandler Handler,
    JsonElement ArgumentSchema = default, string? ReturnType = null, string? Description = null,
    bool RequiresUserActivation = false);

/// <summary>Application-owned agent function registrations. Configure these before creating a message processor.</summary>
public sealed class AgentFunctions
{
    private readonly Dictionary<string, RegisteredCatalog> _registrations = new(StringComparer.Ordinal);

    /// <summary>Registers a catalog and its agent functions atomically.</summary>
    /// <param name="catalogs">The agent's catalog registry.</param>
    /// <param name="catalog">The source catalog document.</param>
    /// <param name="registrations">The functions declared or bound by the agent.</param>
    /// <returns>A diagnostic if the catalog or a registration is invalid.</returns>
    public A2uiDiagnostic? Register(CatalogRegistry catalogs, JsonElement catalog,
        IEnumerable<AgentFunctionRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(registrations);
        if (catalog.ValueKind != JsonValueKind.Object ||
            !catalog.TryGetProperty("catalogId", out var id) || id.ValueKind != JsonValueKind.String)
            return Error("A catalog identifier is required.");
        var catalogId = id.GetString()!;
        if (catalogs.TryGetDefinition(catalogId, out _) || _registrations.ContainsKey(catalogId))
            return Error($"Catalog '{catalogId}' is already registered.");

        var document = JsonNode.Parse(catalog.GetRawText())!.AsObject();
        if (document["functions"] is { } existing && existing is not JsonObject)
            return Error("Catalog functions must be an object.");
        var declared = (JsonObject?)document["functions"] ?? new();
        var pending = new Dictionary<string, AgentFunctionHandler>(StringComparer.Ordinal);
        var added = false;
        foreach (var registration in registrations)
        {
            if (registration is null || string.IsNullOrEmpty(registration.Name) ||
                !SchemaValidator.IsIdentifier(registration.Name) || registration.Handler is null ||
                !pending.TryAdd(registration.Name, registration.Handler))
                return Error($"Invalid or duplicate agent function '{registration?.Name}'.");

            if (registration.ArgumentSchema.ValueKind == JsonValueKind.Undefined)
            {
                if (declared[registration.Name] is not JsonObject definition ||
                    definition["allowedCallers"]?.ToJsonString() == "\"agentOnly\"")
                    return Error($"'{registration.Name}' is not declared for renderer callers.");
                continue;
            }

            if (declared.ContainsKey(registration.Name))
                return Error($"Catalog already declares function '{registration.Name}'.");
            if (registration.ArgumentSchema.ValueKind != JsonValueKind.Object || registration.ReturnType is null)
                return Error($"Function '{registration.Name}' requires an argument schema and return type.");
            var args = JsonNode.Parse(registration.ArgumentSchema.GetRawText())!.AsObject();
            if (MergeDefinitions(document, args, registration.Name) is { } error)
                return error;
            var function = new JsonObject
            {
                ["type"] = "object",
                ["returnType"] = registration.ReturnType,
                ["allowedCallers"] = "rendererOnly",
                ["properties"] = new JsonObject
                {
                    ["@call"] = new JsonObject { ["const"] = registration.Name },
                    ["args"] = args
                },
                ["required"] = new JsonArray("@call", "args")
            };
            if (registration.Description is { } description)
                function["description"] = description;
            if (registration.RequiresUserActivation)
                function["requiresUserActivation"] = true;
            declared.Add(registration.Name, function);
            added = true;
        }

        if (added && declared.Parent is null)
            document["functions"] = declared;
        if (document["$defs"] is JsonObject definitions)
        {
            definitions["anyComponent"] ??= CreateUnion("components", document["components"] as JsonObject);
            if (added)
                definitions["anyFunction"] = CreateUnion("functions", declared);
        }

        var json = document.ToJsonString();
        var candidate = ProtocolSchemas.Parse(json);
        foreach (var name in pending.Keys)
        {
            var definition = candidate.GetProperty("functions").GetProperty(name);
            if (definition.TryGetProperty("properties", out var properties) && WireJson.IsObject(properties) &&
                properties.TryGetProperty("args", out var schema) &&
                CheckParameterNames(schema, candidate, new(StringComparer.Ordinal)) is { } invalid)
                return Error($"Invalid parameter name '{invalid}' in function '{name}'.");
        }
        if (!catalogs.TryRegister(System.Text.Encoding.UTF8.GetBytes(json), out var problem))
            return Error(problem!.Message, problem.Path);
        _registrations.Add(catalogId, new(candidate, pending));
        return null;
    }

    private static JsonNode CreateUnion(string group, JsonObject? entries)
    {
        if (entries is null || entries.Count == 0)
            return JsonValue.Create(false);
        var choices = new JsonArray();
        foreach (var entry in entries)
            choices.Add((JsonNode)new JsonObject { ["$ref"] = "#" + WireJson.Path("/" + group, entry.Key) });
        return new JsonObject { ["oneOf"] = choices };
    }

    private static string? CheckParameterNames(JsonElement schema, JsonElement document, HashSet<string> visited)
    {
        if (!WireJson.IsObject(schema))
            return null;
        if (schema.TryGetProperty("properties", out var properties) && WireJson.IsObject(properties))
            foreach (var property in properties.EnumerateObject())
                if (!SchemaValidator.IsIdentifier(property.Name))
                    return property.Name;
        if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
        {
            var pointer = reference.GetString()!;
            if (pointer.StartsWith('#') && visited.Add(pointer) &&
                SchemaValidator.TryResolvePointer(document, pointer[1..], out var target) &&
                CheckParameterNames(target, document, visited) is { } invalid)
                return invalid;
        }
        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
            if (schema.TryGetProperty(keyword, out var branches) && branches.ValueKind == JsonValueKind.Array)
                foreach (var branch in branches.EnumerateArray())
                    if (CheckParameterNames(branch, document, visited) is { } invalid)
                        return invalid;
        return null;
    }

    private static A2uiDiagnostic? MergeDefinitions(JsonObject catalog, JsonObject args, string name)
    {
        if (args["$defs"] is null)
            return null;
        if (args["$defs"] is not JsonObject local || catalog["$defs"] is { } existing && existing is not JsonObject)
            return Error("Argument schema definitions must be an object.");
        var definitions = catalog["$defs"] as JsonObject;
        if (definitions is null)
        {
            definitions = new();
            catalog["$defs"] = definitions;
        }
        foreach (var (key, value) in local)
        {
            if (definitions.TryGetPropertyValue(key, out var current))
            {
                if (!JsonNode.DeepEquals(current, value))
                    return Error($"Argument schema definition '{key}' in function '{name}' conflicts with the catalog.");
            }
            else
                definitions.Add(key, value?.DeepClone());
        }
        args.Remove("$defs");
        return null;
    }

    internal Dictionary<(string CatalogId, string Name), AgentFunctionHandler> Snapshot(CatalogRegistry catalogs)
    {
        var handlers = new Dictionary<(string, string), AgentFunctionHandler>();
        foreach (var (catalogId, registered) in _registrations)
        {
            if (!catalogs.TryGetDefinition(catalogId, out var active) ||
                !JsonNode.DeepEquals(JsonNode.Parse(active.GetRawText()), JsonNode.Parse(registered.Definition.GetRawText())))
                continue;
            foreach (var (name, handler) in registered.Handlers)
                handlers.Add((catalogId, name), handler);
        }
        return handlers;
    }

    private sealed record RegisteredCatalog(JsonElement Definition, Dictionary<string, AgentFunctionHandler> Handlers);

    private static A2uiDiagnostic Error(string message, string path = "") => new("CatalogError", path, message);
}
