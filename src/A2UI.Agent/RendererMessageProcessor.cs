using System.Text.Json;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>The outcome of routing a message from the renderer.</summary>
/// <param name="Handled">Whether a host callback or an agent function accepted the message.</param>
/// <param name="Reply">The correlated response, when the renderer called an agent function.</param>
/// <param name="Diagnostic">The reason the message could not be processed, if any.</param>
public sealed record RendererMessageResult(bool Handled, WireMessage? Reply = null, A2uiDiagnostic? Diagnostic = null);

/// <summary>A host callback for a validated renderer message.</summary>
/// <param name="message">The complete decoded message.</param>
/// <param name="cancellationToken">Cancellation requested by the host.</param>
/// <returns>Whether the host took responsibility for the message.</returns>
public delegate ValueTask<bool> RendererMessageHandler(WireMessage message, CancellationToken cancellationToken);

/// <summary>Routes inbound A2UI 1.0 messages and answers agent function calls.</summary>
/// <remarks>Host applications own model retries, persistence, authorisation and business side effects.
/// A renderer must enforce any declared user activation requirement before sending a function call.
/// The transport is responsible for authenticating the renderer and isolating conversations.</remarks>
public sealed class RendererMessageProcessor
{
    private readonly CatalogRegistry _catalogs;
    private readonly Dictionary<(string CatalogId, string Name), AgentFunctionHandler> _functions;
    private readonly RendererMessageHandler? _onAction;
    private readonly RendererMessageHandler? _onError;
    private readonly RendererMessageHandler? _onRendererFunctionResponse;

    /// <summary>Creates a message processor for the supplied active catalogs.</summary>
    /// <param name="catalogs">The configured active catalogs. Keep this registry stable while using the processor.</param>
    /// <param name="functions">Application handlers associated with those catalogs.</param>
    /// <param name="onAction">Optional callback for validated action messages.</param>
    /// <param name="onError">Optional callback for validated renderer errors.</param>
    /// <param name="onRendererFunctionResponse">Optional callback for validated renderer function responses.</param>
    public RendererMessageProcessor(CatalogRegistry catalogs, AgentFunctions? functions = null,
        RendererMessageHandler? onAction = null, RendererMessageHandler? onError = null,
        RendererMessageHandler? onRendererFunctionResponse = null)
    {
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _functions = functions?.Snapshot(catalogs) ?? new();
        _onAction = onAction;
        _onError = onError;
        _onRendererFunctionResponse = onRendererFunctionResponse;
    }

    /// <summary>Routes one UTF-8 renderer envelope to the matching callback or function.</summary>
    /// <param name="utf8">The complete renderer message.</param>
    /// <param name="cancellationToken">Cancellation requested by the host.</param>
    /// <returns>The routing outcome and any correlated reply.</returns>
    public ValueTask<RendererMessageResult> ProcessAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken = default) =>
        ProcessAsync(utf8, WireMessage.DefaultMaxEncodedBytes, cancellationToken);

    /// <summary>Routes one UTF-8 renderer envelope with a host-supplied byte limit.</summary>
    /// <param name="utf8">The complete renderer message.</param>
    /// <param name="maxEncodedBytes">The positive limit on the entire encoded envelope, including whitespace.</param>
    /// <param name="cancellationToken">Cancellation requested by the host.</param>
    /// <returns>The routing outcome and any correlated reply, or a diagnostic.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The byte limit is not positive.</exception>
    public ValueTask<RendererMessageResult> ProcessAsync(ReadOnlyMemory<byte> utf8, int maxEncodedBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WireJson.TryReadMessage(utf8, maxEncodedBytes, out var envelope, out var problem))
            return ValueTask.FromResult(problem!.Code == "messageTooLarge"
                ? new RendererMessageResult(false, Diagnostic: problem)
                : Reject(problem.Path, problem.Message));
        if (!WireJson.IsObject(envelope) || !envelope.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.String || !version.ValueEquals(WireMessage.Version))
            return ValueTask.FromResult(Reject("/version", "Expected A2UI version 1.0."));

        string? kind = null;
        foreach (var property in envelope.EnumerateObject())
        {
            if (property.NameEquals("version"))
                continue;
            if (kind is not null || property.Name is not ("action" or "error" or "rendererFunctionResponse" or "callAgentFunction"))
                return ValueTask.FromResult(Reject("", "Expected exactly one renderer message kind."));
            kind = property.Name;
        }

        if (kind is null)
            return ValueTask.FromResult(Reject("", "Expected a renderer message kind."));
        if (kind == "callAgentFunction")
            return HandleCallAsync(envelope, cancellationToken);
        var handler = kind switch
        {
            "action" => _onAction,
            "error" => _onError,
            _ => _onRendererFunctionResponse
        };
        if (handler is null)
            return ValueTask.FromResult(new RendererMessageResult(false));
        if (!WireMessage.TryDecode(utf8, WireDirection.RendererToAgent, maxEncodedBytes, out var message, out problem))
            return ValueTask.FromResult(Reject(problem!.Path, problem.Message));
        return RouteAsync(handler, message!, cancellationToken);
    }

    private static async ValueTask<RendererMessageResult> RouteAsync(RendererMessageHandler handler, WireMessage message,
        CancellationToken cancellationToken) => new(await handler(message, cancellationToken).ConfigureAwait(false));

    private async ValueTask<RendererMessageResult> HandleCallAsync(JsonElement envelope, CancellationToken cancellationToken)
    {
        var payload = envelope.GetProperty("callAgentFunction");
        var callId = ReadString(payload, "functionCallId");
        if (string.IsNullOrEmpty(callId))
            return Reject("/callAgentFunction/functionCallId", "A nonempty functionCallId is required for a reply.");
        var surfaceId = ReadString(payload, "surfaceId");
        if (string.IsNullOrEmpty(surfaceId) ||
            ProtocolSchemas.ValidateMessage(envelope, "callAgentFunction", WireDirection.RendererToAgent) is not null)
            return Reply(callId, "INVALID_FUNCTION_CALL", "The renderer function call is malformed.");

        var call = payload.GetProperty("callFunction");
        var name = ReadString(call, "@call");
        if (string.IsNullOrEmpty(name) || call.TryGetProperty("returnType", out _))
            return Reply(callId, "INVALID_FUNCTION_CALL", "The function call has an invalid name or wire member.");
        var catalogId = ReadString(call, "catalogId");
        if (catalogId is null)
        {
            foreach (var key in _functions.Keys)
            {
                if (key.Name != name)
                    continue;
                if (catalogId is not null)
                    return Reply(callId, "INVALID_FUNCTION_CALL", "More than one catalog declares the named agent function.");
                catalogId = key.CatalogId;
            }
        }
        if (catalogId is null || !_functions.TryGetValue((catalogId, name), out var handler))
            return Reply(callId, "UNKNOWN_FUNCTION", "The requested agent function is unavailable.");
        var args = call.TryGetProperty("args", out var supplied) ? supplied : ProtocolSchemas.Parse("{}");
        if (_catalogs.ParseArguments(catalogId, name, args, out var parsed, validateBeforeDefaults: true) is { } problem)
            return Reply(callId, "INVALID_FUNCTION_CALL", $"Invalid arguments for function '{name}': {problem.Message}");

        try
        {
            var result = await handler(new(catalogId, name, surfaceId, callId, parsed), cancellationToken).ConfigureAwait(false);
            return Reply(callId, value: result);
        }
        catch (AgentFunctionException failure) when (!string.IsNullOrEmpty(failure.Code))
        {
            return Reply(callId, failure.Code, failure.Message);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Reply(callId, "EXECUTION_ERROR", "The agent function failed.");
        }
    }

    private static string? ReadString(JsonElement value, string property) =>
        WireJson.IsObject(value) && value.TryGetProperty(property, out var member) && member.ValueKind == JsonValueKind.String
            ? member.GetString() : null;

    private static RendererMessageResult Reject(string path, string message) =>
        new(false, Diagnostic: new("ValidationError", path, message));

    private static RendererMessageResult Reply(string callId, string? code = null, string? message = null,
        JsonElement value = default)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("version", WireMessage.Version);
            writer.WriteStartObject("agentFunctionResponse");
            writer.WriteString("functionCallId", callId);
            if (code is not null)
            {
                writer.WriteStartObject("error");
                writer.WriteString("code", code);
                writer.WriteString("message", message);
                writer.WriteEndObject();
            }
            else
            {
                writer.WritePropertyName("value");
                if (value.ValueKind == JsonValueKind.Undefined)
                    writer.WriteNullValue();
                else
                    value.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (!WireMessage.TryDecode(stream.ToArray(), WireDirection.AgentToRenderer, out var reply, out var problem))
            throw new InvalidOperationException($"The agent function response is invalid: {problem}");
        return new(true, reply);
    }
}
