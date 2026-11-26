using System.Buffers;
using System.Text.Json;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>A renderer function message and the task that settles when the renderer answers.</summary>
/// <param name="Message">The message for the application to send.</param>
/// <param name="Result">The value returned by the renderer. Failures use <see cref="AgentFunctionException"/>.</param>
public sealed record RendererFunctionInvocation(WireMessage Message, Task<JsonElement> Result);

/// <summary>Builds renderer function calls and correlates their responses within one conversation.</summary>
/// <remarks>The application owns delivery and must isolate responses by conversation. Supply a fresh call identifier for
/// every call if providing identifiers yourself. The client supports concurrent calls and responses.</remarks>
public sealed class RendererFunctionClient : IDisposable
{
    private static readonly TimeSpan StandardTimeout = TimeSpan.FromSeconds(30);
    private readonly CatalogRegistry? _catalogs;
    private readonly TimeProvider _time;
    private readonly TimeSpan _defaultTimeout;
    private readonly int _maxPendingCalls;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates a client for one conversation.</summary>
    /// <param name="catalogs">The active catalogs for target validation. Keep the registry stable while using the client.
    /// Null delegates target validation to the renderer.</param>
    /// <param name="maxPendingCalls">The maximum number of concurrent calls. The default is 256.</param>
    /// <param name="defaultTimeout">The timeout used when an invocation does not specify one. The default is 30 seconds.</param>
    /// <param name="timeProvider">The clock used to time calls. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The pending limit or the default timeout is outside the supported range.</exception>
    public RendererFunctionClient(CatalogRegistry? catalogs = null, int maxPendingCalls = 256,
        TimeSpan? defaultTimeout = null, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingCalls);
        _defaultTimeout = defaultTimeout ?? StandardTimeout;
        CheckTimeout(_defaultTimeout, nameof(defaultTimeout));
        _maxPendingCalls = maxPendingCalls;
        _catalogs = catalogs;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The number of calls awaiting a response.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    /// <summary>Registers a pending call and returns the message for the host to send.</summary>
    /// <param name="catalogId">The identifier of the catalog that declares the function.</param>
    /// <param name="name">The declared function name.</param>
    /// <param name="arguments">An object with function arguments. An undefined value writes an empty object.</param>
    /// <param name="surfaceId">An optional surface context. An empty value omits the surface.</param>
    /// <param name="functionCallId">An optional unique call identifier. Null generates one.</param>
    /// <param name="timeout">A timeout for this call, measured from registration.</param>
    /// <returns>The message and the task that receives the response.</returns>
    /// <exception cref="AgentFunctionException">The call is invalid, the target is unknown, the call ID is already pending,
    /// the pending limit was reached, or the client has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is outside the supported range.</exception>
    public RendererFunctionInvocation Call(string catalogId, string name, JsonElement arguments = default,
        string? surfaceId = null, string? functionCallId = null, TimeSpan? timeout = null)
    {
        lock (_gate)
        {
            if (_disposed)
                throw new AgentFunctionException("DISPOSED", "The client is disposed.");
        }

        if (string.IsNullOrEmpty(name) || !SchemaValidator.IsIdentifier(name))
            throw Invalid("A valid function name is required.");
        if (string.IsNullOrEmpty(catalogId))
            throw Invalid("A catalogId is required.");
        if (functionCallId is "")
            throw Invalid("The functionCallId must be nonempty.");
        if (arguments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            throw Invalid("Expected an arguments object.");
        if (arguments.ValueKind == JsonValueKind.Object)
            foreach (var property in arguments.EnumerateObject())
                if (!SchemaValidator.IsIdentifier(property.Name))
                    throw Invalid($"Invalid argument key '{property.Name}'.");

        if (_catalogs is { } catalogs)
        {
            if (!catalogs.TryGetDefinition(catalogId, out var catalog) ||
                !catalog.TryGetProperty("functions", out var functions) ||
                !functions.TryGetProperty(name, out var definition))
                throw new AgentFunctionException("UNKNOWN_FUNCTION", $"Catalog '{catalogId}' does not declare '{name}'.");
            if (!definition.TryGetProperty("allowedCallers", out var callers) ||
                !(callers.ValueEquals("agentOnly") || callers.ValueEquals("rendererOrAgent")) ||
                definition.TryGetProperty("requiresUserActivation", out var activation) && activation.ValueKind == JsonValueKind.True)
                throw Invalid($"The agent cannot call '{name}'.");
        }

        var limit = timeout ?? _defaultTimeout;
        CheckTimeout(limit, nameof(timeout));
        var id = functionCallId ?? Guid.NewGuid().ToString("N");
        var message = Encode(catalogId, name, arguments, surfaceId, id);
        if (_catalogs?.ValidateFunction(message.Payload.GetProperty("callFunction"), catalogId) is { } problem)
            throw Invalid(problem.Message);
        var pending = new PendingCall();
        lock (_gate)
        {
            if (_disposed)
                throw new AgentFunctionException("DISPOSED", "The client is disposed.");
            if (_pending.ContainsKey(id))
                throw Invalid($"A functionCallId of '{id}' is already pending.");
            if (_pending.Count >= _maxPendingCalls)
                throw new AgentFunctionException("TOO_MANY_PENDING", "The pending renderer function call limit was reached.");

            _pending.Add(id, pending);
            try
            {
                pending.Timer = _time.CreateTimer(_ =>
                {
                    var expired = Remove(id, pending);
                    expired?.Result.TrySetException(new AgentFunctionException("TIMEOUT", "The renderer did not answer in time."));
                }, null, limit, Timeout.InfiniteTimeSpan);
                // A custom clock may invoke the callback synchronously from CreateTimer.
                if (!_pending.ContainsKey(id))
                    pending.Timer.Dispose();
            }
            catch
            {
                _pending.Remove(id);
                throw;
            }
        }
        return new(message, pending.Result.Task);
    }

    /// <summary>Delivers a decoded response to the matching pending call.</summary>
    /// <param name="response">The complete renderer message.</param>
    /// <returns>Whether the response settled a pending call.</returns>
    public bool TryHandleResponse(WireMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Direction != WireDirection.RendererToAgent || response.Kind != "rendererFunctionResponse")
            return false;

        var payload = response.Payload;
        var pending = Remove(payload.GetProperty("functionCallId").GetString()!);
        if (pending is null)
            return false;

        if (payload.TryGetProperty("error", out var error))
        {
            var message = error.GetProperty("message").GetString()!;
            pending.Result.SetException(new AgentFunctionException(error.GetProperty("code").GetString()!,
                message));
        }
        else
        {
            pending.Result.SetResult(payload.GetProperty("value"));
        }
        return true;
    }

    /// <summary>Declines subsequent calls and fails pending calls with <c>CANCELLED</c>.</summary>
    public void Dispose() => Dispose("The renderer function client was disposed.");

    /// <summary>Declines subsequent calls and fails pending calls with the supplied cancellation reason.</summary>
    /// <param name="reason">The error message delivered to pending callers.</param>
    public void Dispose(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        PendingCall[] pending;
        lock (_gate)
        {
            _disposed = true;
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }

        foreach (var call in pending)
        {
            call.Timer?.Dispose();
            call.Result.SetException(new AgentFunctionException("CANCELLED", reason));
        }
    }

    private PendingCall? Remove(string id, PendingCall? expected = null)
    {
        PendingCall call;
        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out call!) || expected is not null && !ReferenceEquals(expected, call))
                return null;
            _pending.Remove(id);
        }
        call.Timer?.Dispose();
        return call;
    }

    private static AgentFunctionException Invalid(string message) => new("INVALID_FUNCTION_CALL", message);

    private static void CheckTimeout(TimeSpan timeout, string parameter)
    {
        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(parameter, "The timeout must be between zero and 4294967294 milliseconds.");
    }

    private static WireMessage Encode(string catalogId, string name, JsonElement arguments, string? surfaceId, string id)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("version", WireMessage.Version);
            writer.WriteStartObject("callRendererFunction");
            writer.WriteString("functionCallId", id);
            if (!string.IsNullOrEmpty(surfaceId))
                writer.WriteString("surfaceId", surfaceId);
            writer.WriteStartObject("callFunction");
            writer.WriteString("@call", name);
            writer.WriteString("catalogId", catalogId);
            writer.WritePropertyName("args");
            if (arguments.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
                arguments.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        if (!WireMessage.TryDecode(buffer.WrittenMemory, WireDirection.AgentToRenderer, out var message, out var problem))
            throw Invalid(problem!.Message);
        return message!;
    }

    private sealed class PendingCall
    {
        public TaskCompletionSource<JsonElement> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ITimer? Timer { get; set; }
    }
}
