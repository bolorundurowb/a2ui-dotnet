using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace A2UI.Core;

/// <summary>The committed state of a surface at a point in time.</summary>
/// <param name="Id">The wire surface identifier.</param>
/// <param name="CatalogId">The default catalog identifier, if present.</param>
/// <param name="SendDataModel">Whether the surface requests synchronized data model snapshots.</param>
/// <param name="DataModel">The owned root data model value.</param>
/// <param name="Components">The registered component definitions by ID.</param>
public sealed record SurfaceSnapshot(
    string Id, string? CatalogId, bool SendDataModel, JsonElement DataModel,
    IReadOnlyDictionary<string, JsonElement> Components)
{
    /// <summary>The immutable canonical Surface container.</summary>
    public JsonElement Container => SurfaceContainer;
    /// <summary>The ID reserved for the root component.</summary>
    public string RootComponentId => "root";

    private static readonly JsonElement SurfaceContainer = ProtocolSchemas.Parse("""{"component":"Surface","child":"root"}""");
}

/// <summary>A notification emitted after a surface mutation commits.</summary>
/// <param name="Kind">The operation that committed.</param>
/// <param name="SurfaceId">The affected surface identifier.</param>
/// <param name="Snapshot">The committed snapshot, or null after deletion.</param>
public sealed record SurfaceChange(string Kind, string SurfaceId, SurfaceSnapshot? Snapshot);

/// <summary>Owns one renderer session. Callers serialize access and supply one registry per configuration.</summary>
/// <param name="catalogs">The registry used for component and function validation.</param>
public sealed class SurfaceSession(CatalogRegistry catalogs) : IDisposable
{
    private static readonly JsonElement EmptyDataModel = ProtocolSchemas.Parse("{}");
    private readonly Dictionary<string, SurfaceSnapshot> _surfaces = new(StringComparer.Ordinal);
    private bool _notifying;
    private bool _disposed;

    /// <summary>Runs synchronously after commit. Subscribers can read the committed state.</summary>
    public event Action<SurfaceChange>? Changed;

    /// <summary>Looks up the current snapshot of a live surface.</summary>
    /// <param name="id">The wire surface identifier.</param>
    /// <param name="snapshot">The committed snapshot, if the surface exists.</param>
    /// <returns>Whether the surface exists.</returns>
    public bool TryGetSurface(string id, out SurfaceSnapshot? snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _surfaces.TryGetValue(id, out snapshot);
    }

    /// <summary>Applies a surface operation and raises <see cref="Changed"/> after a successful commit.</summary>
    /// <param name="message">A decoded agent-to-renderer surface message.</param>
    /// <param name="cancellationToken">Cancels processing before the commit.</param>
    /// <returns>The rejection diagnostic, or null on success.</returns>
    /// <remarks>Subscriber exceptions propagate after the state commits. Reentrant writes are rejected.</remarks>
    public A2uiDiagnostic? Apply(WireMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_notifying)
            return new("reentrantUpdate", "", "Complete the current notification before updating the session.");
        if (message.Direction != WireDirection.AgentToRenderer ||
            message.Kind is not ("createSurface" or "updateComponents" or "updateDataModel" or "deleteSurface"))
            return new("unsupportedMessage", "", "Expected a surface state message.");

        var payload = message.Payload;
        var id = payload.GetProperty("surfaceId").GetString()!;
        var kindPath = WireJson.Path("", message.Kind);
        var exists = _surfaces.TryGetValue(id, out var snapshot);
        var creating = message.Kind == "createSurface";
        if (!exists && message.Kind == "deleteSurface")
            return null;
        if (creating == exists)
            return new(creating ? "duplicateSurface" : "unknownSurface", kindPath + "/surfaceId",
                creating ? "A live surface already uses this ID." : "No live surface uses this ID.");

        if (creating)
        {
            var catalogId = payload.TryGetProperty("catalogId", out var catalog) ? catalog.GetString() : null;
            if (catalogId is not null && !catalogs.TryGetDefinition(catalogId, out _))
                return new("unknownCatalog", kindPath + "/catalogId", "Surface catalog is unavailable.");
            var send = payload.TryGetProperty("sendDataModel", out var sendFlag) && sendFlag.GetBoolean();
            var data = payload.TryGetProperty("dataModel", out var inlineData) ? inlineData : EmptyDataModel;
            snapshot = new(id, catalogId, send, data,
                new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>(StringComparer.Ordinal)));
        }

        if (payload.TryGetProperty("components", out var updates))
        {
            var components = new Dictionary<string, JsonElement>(snapshot!.Components, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var component in updates.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var componentPath = kindPath + "/components/" + index++.ToString(CultureInfo.InvariantCulture);
                var componentId = component.GetProperty("id").GetString()!;
                if (!seen.Add(componentId))
                    return new("duplicateComponent", componentPath + "/id", "Component ID is repeated in the update.");
                if (catalogs.ValidateComponent(component, snapshot.CatalogId) is { } problem)
                    return problem with { Path = componentPath + problem.Path };
                components[componentId] = component;
            }
            snapshot = snapshot with { Components = new ReadOnlyDictionary<string, JsonElement>(components) };
        }

        if (message.Kind == "updateDataModel")
        {
            if (payload.TryGetProperty("path", out var path) && path.GetString() != "/")
                return new("unsupportedPath", kindPath + "/path", "Only root updates are supported.");
            var value = payload.GetProperty("value");
            snapshot = snapshot! with { DataModel = value.ValueKind == JsonValueKind.Null ? EmptyDataModel : value };
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (message.Kind == "deleteSurface")
        {
            _surfaces.Remove(id);
            snapshot = null;
        }
        else
        {
            _surfaces[id] = snapshot!;
        }

        _notifying = true;
        try
        {
            Changed?.Invoke(new(message.Kind, id, snapshot));
        }
        finally
        {
            _notifying = false;
        }
        return null;
    }

    /// <summary>Releases live surface state and event subscribers.</summary>
    public void Dispose()
    {
        if (_notifying)
            throw new InvalidOperationException("Complete the current notification before disposing the session.");
        _disposed = true;
        _surfaces.Clear();
        Changed = null;
    }
}
