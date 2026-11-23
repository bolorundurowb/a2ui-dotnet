using System.Text.Json;

namespace A2UI.Core;

/// <summary>The outcome of a function call that the renderer sent to the agent.</summary>
/// <param name="Value">The value the agent returned. It is a JSON null when the call failed.</param>
/// <param name="ErrorCode">The error code, or null when the call succeeded. See <see cref="AgentFunctionClient"/> for the codes the renderer sets.</param>
/// <param name="ErrorMessage">The error description, or null when the call succeeded.</param>
public readonly record struct AgentFunctionOutcome(JsonElement Value, string? ErrorCode, string? ErrorMessage)
{
    /// <summary>Whether the agent returned a value.</summary>
    public bool Succeeded => ErrorCode is null;
}

/// <summary>A function call to the agent that did not return a value.</summary>
/// <remarks>The <see cref="Code"/> is an agent error code, or one that the renderer sets: <c>TIMEOUT</c>, <c>CANCELLED</c>, <c>DUPLICATE</c>, <c>DISPOSED</c> or <c>NO_LISTENER</c>.</remarks>
public sealed class AgentFunctionException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="message">The error description.</param>
    public AgentFunctionException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>The error code.</summary>
    public string Code { get; }
}

/// <summary>Sends <c>callAgentFunction</c> messages and matches each <c>agentFunctionResponse</c> to its call.</summary>
/// <remarks>
/// The client keeps one table of pending calls, so a response is matched by its <c>functionCallId</c> alone. The renderer sets
/// these error codes: <c>NO_LISTENER</c> when the client has no way to send, <c>DUPLICATE</c> for a call identifier that is
/// still pending, <c>DISPOSED</c> for a call after <see cref="Dispose"/>, <c>TIMEOUT</c> when the agent does not answer in
/// time and <c>CANCELLED</c> when <see cref="CancelSurface"/> or <see cref="Dispose"/> ends a pending call. A response that
/// matches no pending call is ignored, which covers answers that arrive after a timeout or cancellation.
/// The client is safe to use from several threads. Completion callbacks run on the thread that completes the call, outside
/// the client's lock, and a callback may call back into the client.
/// </remarks>
public sealed class AgentFunctionClient : IDisposable
{
    private readonly Action<WireMessage>? _send;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates a client.</summary>
    /// <param name="send">Sends a message to the agent. A null value means the renderer has no way to reach the agent, so every call fails with <c>NO_LISTENER</c>.</param>
    /// <param name="timeProvider">The clock for call timeouts. Defaults to <see cref="TimeProvider.System"/>.</param>
    public AgentFunctionClient(Action<WireMessage>? send, TimeProvider? timeProvider = null)
    {
        _send = send;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Whether the client can send calls. A client without a send callback fails every call with <c>NO_LISTENER</c>.</summary>
    public bool CanSend => _send is not null;

    /// <summary>The number of calls that wait for an answer.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    /// <summary>Sends a call to the agent and reports the outcome to a callback.</summary>
    /// <param name="surfaceId">The surface the call belongs to.</param>
    /// <param name="callFunction">The wire-level function call, with <c>@call</c>, <c>catalogId</c> and resolved <c>args</c>.</param>
    /// <param name="completed">Receives the outcome exactly once, unless the method throws.</param>
    /// <param name="functionCallId">The identifier to correlate the response with. A new unique identifier is used when it is null.</param>
    /// <param name="timeout">How long to wait for the answer. A null value waits until the call is answered or cancelled.</param>
    /// <returns>The identifier of the call.</returns>
    /// <exception cref="AgentFunctionException">The call was not sent. The code is <c>DISPOSED</c>, <c>NO_LISTENER</c> or <c>DUPLICATE</c>.</exception>
    public string Send(string surfaceId, JsonElement callFunction, Action<AgentFunctionOutcome> completed,
        string? functionCallId = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(surfaceId);
        ArgumentNullException.ThrowIfNull(completed);
        var id = functionCallId ?? Guid.NewGuid().ToString("N");
        var message = Encode(surfaceId, id, callFunction);

        var call = new PendingCall(surfaceId, completed);
        lock (_gate)
        {
            if (_disposed)
                throw new AgentFunctionException("DISPOSED", "The client is disposed.");
            if (_send is null)
                throw new AgentFunctionException("NO_LISTENER", "The renderer has no way to send messages to the agent.");
            if (!_pending.TryAdd(id, call))
                throw new AgentFunctionException("DUPLICATE", $"A call with functionCallId '{id}' is already pending.");
            if (timeout is { } limit)
                call.Timer = _time.CreateTimer(_ => Complete(id, Failure("TIMEOUT", "The agent did not answer in time.")),
                    null, limit, Timeout.InfiniteTimeSpan);
        }

        try
        {
            _send(message);
        }
        catch
        {
            Remove(id);
            throw;
        }
        return id;
    }

    /// <summary>Sends a call to the agent and waits for the value.</summary>
    /// <param name="surfaceId">The surface the call belongs to.</param>
    /// <param name="callFunction">The wire-level function call.</param>
    /// <param name="functionCallId">The identifier to correlate the response with. A new unique identifier is used when it is null.</param>
    /// <param name="timeout">How long to wait for the answer. A null value waits until the call is answered or cancelled.</param>
    /// <param name="cancellationToken">Cancels the call, which fails with <c>CANCELLED</c>.</param>
    /// <returns>The value the agent returned. A call that fails produces an <see cref="AgentFunctionException"/>.</returns>
    public Task<JsonElement> CallAsync(string surfaceId, JsonElement callFunction, string? functionCallId = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        string id;
        try
        {
            id = Send(surfaceId, callFunction, outcome =>
            {
                if (outcome.Succeeded)
                    source.TrySetResult(outcome.Value);
                else
                    source.TrySetException(new AgentFunctionException(outcome.ErrorCode!, outcome.ErrorMessage ?? ""));
            }, functionCallId, timeout);
        }
        catch (AgentFunctionException failure)
        {
            return Task.FromException<JsonElement>(failure);
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() => Complete(id, Failure("CANCELLED", "The call was cancelled.")));
            source.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }
        return source.Task;
    }

    /// <summary>Delivers an <c>agentFunctionResponse</c> to the call it answers.</summary>
    /// <param name="response">A decoded agent-to-renderer message.</param>
    /// <returns>Whether the response matched a pending call. A response for an unknown, finished or cancelled call is ignored.</returns>
    public bool TryHandleResponse(WireMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Direction != WireDirection.AgentToRenderer || response.Kind != "agentFunctionResponse")
            return false;

        var payload = response.Payload;
        var id = payload.GetProperty("functionCallId").GetString()!;
        AgentFunctionOutcome outcome;
        if (payload.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()!
                : "The agent reported an error.";
            outcome = Failure(error.GetProperty("code").GetString()!, message);
        }
        else
        {
            outcome = new(payload.TryGetProperty("value", out var value) ? value : ProtocolSchemas.Parse("null"), null, null);
        }
        return Complete(id, outcome);
    }

    /// <summary>Ends the pending calls of a surface with <c>CANCELLED</c>.</summary>
    /// <param name="surfaceId">The surface whose calls to cancel.</param>
    /// <returns>The number of calls cancelled.</returns>
    /// <remarks>Use this when a surface is deleted. The surface itself stays usable and may send new calls.</remarks>
    public int CancelSurface(string surfaceId)
    {
        ArgumentNullException.ThrowIfNull(surfaceId);
        string[] ids;
        lock (_gate)
            ids = _pending.Where(entry => entry.Value.SurfaceId == surfaceId).Select(entry => entry.Key).ToArray();
        return ids.Count(id => Complete(id, Failure("CANCELLED", "The call was cancelled.")));
    }

    /// <summary>Cancels every pending call with <c>CANCELLED</c> and rejects later calls with <c>DISPOSED</c>.</summary>
    public void Dispose()
    {
        string[] ids;
        lock (_gate)
        {
            _disposed = true;
            ids = _pending.Keys.ToArray();
        }
        foreach (var id in ids)
            Complete(id, Failure("CANCELLED", "The client was disposed."));
    }

    private bool Complete(string id, AgentFunctionOutcome outcome)
    {
        var call = Remove(id);
        if (call is null)
            return false;
        call.Completed(outcome);
        return true;
    }

    private PendingCall? Remove(string id)
    {
        PendingCall? call;
        lock (_gate)
        {
            if (!_pending.Remove(id, out call))
                return null;
        }
        call.Timer?.Dispose();
        return call;
    }

    private static AgentFunctionOutcome Failure(string code, string message) =>
        new(ProtocolSchemas.Parse("null"), code, message);

    private static WireMessage Encode(string surfaceId, string functionCallId, JsonElement callFunction)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("version", WireMessage.Version);
            writer.WriteStartObject("callAgentFunction");
            writer.WriteString("surfaceId", surfaceId);
            writer.WriteString("functionCallId", functionCallId);
            writer.WritePropertyName("callFunction");
            callFunction.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (!WireMessage.TryDecode(stream.ToArray(), WireDirection.RendererToAgent, out var message, out var problem))
            throw new ArgumentException($"The call does not form a valid message: {problem}", nameof(callFunction));
        return message!;
    }

    private sealed class PendingCall(string surfaceId, Action<AgentFunctionOutcome> completed)
    {
        public string SurfaceId { get; } = surfaceId;
        public Action<AgentFunctionOutcome> Completed { get; } = completed;
        public ITimer? Timer { get; set; }
    }
}
