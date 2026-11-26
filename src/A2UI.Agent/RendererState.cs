using A2UI.Core;

namespace A2UI.Agent;

/// <summary>Mirrors committed renderer state across ordered turns. Callers serialize access.</summary>
public sealed class RendererState : IDisposable
{
    private SurfaceSession _session;
    private long _revision;
    private bool _disposed;

    /// <summary>Creates an empty mirror.</summary>
    /// <param name="catalogs">The configured request-local catalog registry.</param>
    public RendererState(CatalogRegistry catalogs)
    {
        _session = new(catalogs);
        Catalogs = catalogs;
    }

    internal CatalogRegistry Catalogs { get; }

    /// <summary>Returns a committed snapshot for a live surface.</summary>
    /// <param name="id">The surface identifier.</param>
    /// <param name="snapshot">The snapshot when found.</param>
    /// <returns>Whether the surface exists.</returns>
    public bool TryGetSurface(string id, out SurfaceSnapshot? snapshot) => _session.TryGetSurface(id, out snapshot);

    /// <summary>Validates a candidate turn without publishing any mutations.</summary>
    /// <param name="messages">Ordered surface messages produced by the host or a compiler.</param>
    /// <param name="turn">The validated candidate on success.</param>
    /// <param name="cancellationToken">Cancels validation before commit.</param>
    /// <returns>The first validation diagnostic, or null on success.</returns>
    public A2uiDiagnostic? Prepare(IEnumerable<WireMessage> messages, out RendererTurn? turn,
        CancellationToken cancellationToken = default)
        => Prepare(messages, out turn, null, true, cancellationToken);

    internal A2uiDiagnostic? Prepare(IEnumerable<WireMessage> messages, out RendererTurn? turn,
        IEnumerable<string>? affected, bool final, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);
        turn = null;
        var revision = _revision;
        var candidate = _session.Fork();
        try
        {
            var ordered = new List<WireMessage>();
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (message.Direction == WireDirection.AgentToRenderer && message.Kind == "callRendererFunction")
                {
                    string? defaultId = null;
                    var hasSurface = message.Payload.TryGetProperty("surfaceId", out var surface);
                    if (hasSurface)
                    {
                        if (!candidate.TryGetSurface(surface.GetString()!, out var snapshot))
                            return new("ValidationError", "/surfaceId", "Surface does not exist.");
                        defaultId = snapshot!.CatalogId;
                    }
                    if (DirectJson.CheckCall(message.Payload.GetProperty("callFunction"), defaultId, Catalogs, true, hasSurface) is { } rpcError)
                        return rpcError;
                }
                else if (message.Direction != WireDirection.AgentToRenderer || message.Kind != "agentFunctionResponse")
                {
                    if (candidate.Apply(message, cancellationToken) is { } error)
                        return error;
                    touched.Add(message.Payload.GetProperty("surfaceId").GetString()!);
                }
                ordered.Add(message);
            }
            if (final)
            {
                if (affected is not null)
                    touched.UnionWith(affected);
                foreach (var id in touched)
                    if (candidate.ValidateCompleteGraph(id, cancellationToken) is { } error)
                        return error;
            }
            cancellationToken.ThrowIfCancellationRequested();
            turn = new RendererTurn(this, revision, candidate, ordered.AsReadOnly());
            return null;
        }
        finally
        {
            if (turn is null)
                candidate.Dispose();
        }
    }

    internal void Commit(long revision, SurfaceSession candidate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_revision != revision)
            throw new InvalidOperationException("Another turn has already committed.");
        if (PendingDelivery is not null)
            throw new InvalidOperationException("A previous turn still has undelivered messages.");
        _revision++;
        _session.Dispose();
        _session = candidate;
    }

    /// <summary>The committed turn with unacknowledged messages, available for delivery recovery.</summary>
    public RendererTurn? PendingDelivery { get; private set; }

    internal void Track(RendererTurn turn) => PendingDelivery = turn.NextMessage is null ? null : turn;

    /// <summary>Releases the mirror's session state.</summary>
    public void Dispose()
    {
        _disposed = true;
        PendingDelivery = null;
        _session.Dispose();
    }
}

/// <summary>A validated transaction with ordered delivery acknowledgement after commit.</summary>
public sealed class RendererTurn : IDisposable
{
    private readonly RendererState _owner;
    private readonly long _revision;
    private SurfaceSession? _candidate;
    private readonly IReadOnlyList<WireMessage> _messages;
    private bool _committed;

    internal RendererTurn(RendererState owner, long revision, SurfaceSession candidate, IReadOnlyList<WireMessage> messages)
        => (_owner, _revision, _candidate, _messages) = (owner, revision, candidate, messages);

    /// <summary>The number of messages acknowledged by the transport.</summary>
    public int EmittedCount { get; private set; }

    /// <summary>The next committed message awaiting confirmed delivery.</summary>
    public WireMessage? NextMessage => _committed && EmittedCount < _messages.Count ? _messages[EmittedCount] : null;

    /// <summary>Atomically commits this turn if the base revision is still current.</summary>
    /// <param name="cancellationToken">Cancels the commit before any state changes.</param>
    public void Commit(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_candidate is null)
            throw new InvalidOperationException("The candidate is no longer available.");
        _owner.Commit(_revision, _candidate);
        _committed = true;
        _candidate = null;
        _owner.Track(this);
    }

    /// <summary>Advances delivery by one after the host confirms the current message was sent.</summary>
    /// <param name="message">The exact message returned by NextMessage.</param>
    public void Acknowledge(WireMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!ReferenceEquals(message, NextMessage))
            throw new InvalidOperationException("The message is not the next committed message.");
        EmittedCount++;
        _owner.Track(this);
    }

    /// <summary>Discards an uncommitted candidate.</summary>
    public void Dispose()
    {
        _candidate?.Dispose();
        _candidate = null;
    }
}
