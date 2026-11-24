using System.Collections.ObjectModel;
using System.Text.Json;

namespace A2UI.Core;

/// <summary>Answers a <c>callRendererFunction</c> message from the agent with a <c>rendererFunctionResponse</c>.</summary>
/// <remarks>
/// The handler checks, in this order: the surface, the catalog, the function declaration, who may call it, user activation,
/// the arguments and then the run. Every failure becomes an error reply with the call's <c>functionCallId</c>, and the
/// reply codes are <c>INVALID_FUNCTION_CALL</c> for a call the renderer refuses and <c>EXECUTION_ERROR</c> for a function
/// that ran and failed. A function may be called by the agent only when its declaration allows <c>agentOnly</c> or
/// <c>rendererOrAgent</c>. A call that names no surface resolves its bindings against an empty data model, so it never reads
/// the data of a live surface. A function that a catalog declares needs an implementation, which is a Basic Catalog function
/// or one registered with <see cref="FunctionEvaluator.RegisterHostFunction"/>.
/// </remarks>
public sealed class RendererFunctionHandler
{
    private static readonly SurfaceSnapshot IsolatedSurface = new("", null, false, ProtocolSchemas.Parse("{}"),
        new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>()));

    private readonly CatalogRegistry _catalogs;
    private readonly FunctionEvaluator _functions;
    private readonly SurfaceSession? _session;

    /// <summary>Creates a handler.</summary>
    /// <param name="catalogs">The registry that holds the catalogs the agent may name.</param>
    /// <param name="functions">The evaluator that resolves arguments and runs the functions.</param>
    /// <param name="session">The session that owns the surfaces a call may name. Without one, a call that names a surface fails.</param>
    public RendererFunctionHandler(CatalogRegistry catalogs, FunctionEvaluator functions, SurfaceSession? session = null)
    {
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _functions = functions ?? throw new ArgumentNullException(nameof(functions));
        _session = session;
    }

    /// <summary>Runs the call and builds the reply.</summary>
    /// <param name="call">A decoded <c>callRendererFunction</c> message.</param>
    /// <param name="userActivation">Whether a user gesture is active. A function that requires user activation fails without it.</param>
    /// <returns>The <c>rendererFunctionResponse</c> message to send to the agent.</returns>
    public WireMessage Handle(WireMessage call, bool userActivation = false)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Direction != WireDirection.AgentToRenderer || call.Kind != "callRendererFunction")
            throw new ArgumentException("Expected a callRendererFunction message.", nameof(call));

        var payload = call.Payload;
        var id = payload.GetProperty("functionCallId").GetString()!;
        var function = payload.GetProperty("callFunction");
        var name = function.GetProperty("@call").GetString()!;
        var catalogId = function.GetProperty("catalogId").GetString()!;

        var surface = IsolatedSurface;
        if (payload.TryGetProperty("surfaceId", out var surfaceId) &&
            !TryGetSurface(surfaceId.GetString()!, out surface))
            return Reply(id, "INVALID_FUNCTION_CALL", $"Surface not found: {surfaceId.GetString()}");

        if (!_catalogs.TryGetDefinition(catalogId, out var definition))
            return Reply(id, "INVALID_FUNCTION_CALL", $"Catalog not found: {catalogId}");
        if (!definition.TryGetProperty("functions", out var declared) || !declared.TryGetProperty(name, out var declaration))
            return Reply(id, "INVALID_FUNCTION_CALL", $"Function not found: {name}");

        var callers = declaration.TryGetProperty("allowedCallers", out var allowed) ? allowed.GetString() : "rendererOnly";
        if (callers == "rendererOnly")
            return Reply(id, "INVALID_FUNCTION_CALL",
                $"Function '{name}' cannot be called by agent (allowedCallers is rendererOnly).");
        if (!userActivation && declaration.TryGetProperty("requiresUserActivation", out var activation) &&
            activation.ValueKind == JsonValueKind.True)
            return Reply(id, "INVALID_FUNCTION_CALL", $"Function '{name}' requires user activation context to execute.");

        var arguments = function.TryGetProperty("args", out var wireArguments) ? wireArguments : ProtocolSchemas.Parse("{}");
        if (!_functions.TryResolveValue(surface, "/", arguments, userActivation, out var resolved, out var problem))
            return Reply(id, problem!.Code == "CatalogError" ? "INVALID_FUNCTION_CALL" : "EXECUTION_ERROR", problem.Message);
        if (_catalogs.ParseArguments(catalogId, name, resolved, out var parsed) is { } invalid)
            return Reply(id, "INVALID_FUNCTION_CALL", $"Invalid arguments for function '{name}': {invalid.Message}");
        if (!_functions.IsImplemented(catalogId, name))
            return Reply(id, "EXECUTION_ERROR", $"Function '{name}' has no implementation.");

        var values = parsed.EnumerateObject().ToDictionary(member => member.Name, member => member.Value, StringComparer.Ordinal);
        return _functions.TryInvoke(surface, "/", catalogId, name, values, userActivation, out var result, out problem)
            ? Reply(id, result)
            : Reply(id, "EXECUTION_ERROR", problem!.Message);
    }

    private bool TryGetSurface(string surfaceId, out SurfaceSnapshot surface)
    {
        surface = IsolatedSurface;
        if (_session is null || !_session.TryGetSurface(surfaceId, out var found) || found is null)
            return false;
        surface = found;
        return true;
    }

    private static WireMessage Reply(string functionCallId, JsonElement value) =>
        Write(functionCallId, writer =>
        {
            writer.WritePropertyName("value");
            (value.ValueKind == JsonValueKind.Undefined ? ProtocolSchemas.Parse("null") : value).WriteTo(writer);
        });

    private static WireMessage Reply(string functionCallId, string code, string message) =>
        Write(functionCallId, writer =>
        {
            writer.WriteStartObject("error");
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });

    private static WireMessage Write(string functionCallId, Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("version", WireMessage.Version);
            writer.WriteStartObject("rendererFunctionResponse");
            writer.WriteString("functionCallId", functionCallId);
            body(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (!WireMessage.TryDecode(stream.ToArray(), WireDirection.RendererToAgent, out var message, out var problem))
            throw new InvalidOperationException($"The reply does not form a valid message: {problem}");
        return message!;
    }
}
