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

    /// <summary>Resolves an absolute data pointer in this snapshot.</summary>
    /// <param name="path">The pointer, or "/" for the root.</param>
    /// <param name="value">The resolved JSON value, including explicit null.</param>
    /// <returns>Whether the path resolves.</returns>
    public bool TryGetData(string path, out JsonElement value)
    {
        value = default;
        return DataPointer.TryParse(path, out var segments) && DataPointer.TryGet(DataModel, segments, out value);
    }


    /// <summary>Resolves a binding in a collection occurrence's data scope.</summary>
    /// <param name="scope">The absolute pointer to the occurrence's data.</param>
    /// <param name="path">An absolute or relative pointer. An empty string selects the scope.</param>
    /// <param name="value">The resolved JSON value, including explicit null.</param>
    /// <returns>Whether the binding resolves.</returns>
    public bool TryGetScopedData(string scope, string path, out JsonElement value)
    {
        value = default;
        return TryResolveScope(scope, path, out var absolutePath) && TryGetData(absolutePath, out value);
    }

    internal static bool TryResolveScope(string scope, string path, out string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(path);

        absolutePath = path switch
        {
            "" => scope,
            _ when path.StartsWith('/') => path,
            _ when scope == "/" => "/" + path,
            _ => scope + "/" + path
        };
        return DataPointer.TryParse(scope, out _) && DataPointer.TryParse(absolutePath, out _);
    }

    private static readonly JsonElement SurfaceContainer = ProtocolSchemas.Parse("""{"component":"Surface","child":"root"}""");
}

/// <summary>A notification emitted after a surface mutation commits.</summary>
/// <param name="Kind">The operation that committed.</param>
/// <param name="SurfaceId">The affected surface identifier.</param>
/// <param name="Snapshot">The committed snapshot, or null after deletion.</param>
public sealed record SurfaceChange(string Kind, string SurfaceId, SurfaceSnapshot? Snapshot)
{
    /// <summary>The changed data pointer for a data write, or null for other operations.</summary>
    public string? DataPath { get; init; }
}

/// <summary>Owns one renderer session. Callers serialize access and supply one registry per configuration.</summary>
public sealed class SurfaceSession : IDisposable
{
    private static readonly JsonElement EmptyDataModel = ProtocolSchemas.Parse("{}");
    private readonly Dictionary<string, SurfaceSnapshot> _surfaces = new(StringComparer.Ordinal);
    private bool _notifying;
    private bool _disposed;
    private readonly CatalogRegistry _catalogs;
    private readonly GraphValidationOptions _graphValidation;

    /// <summary>Creates a session that accepts progressively delivered component trees.</summary>
    /// <param name="catalogs">The registry used for component and function validation.</param>
    public SurfaceSession(CatalogRegistry catalogs) : this(catalogs, GraphValidationOptions.Progressive) { }

    /// <summary>Creates a session with explicit topology validation rules.</summary>
    /// <param name="catalogs">The registry used for component and function validation.</param>
    /// <param name="graphValidation">The permitted forms of incomplete component trees.</param>
    public SurfaceSession(CatalogRegistry catalogs, GraphValidationOptions graphValidation)
    {
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _graphValidation = graphValidation ?? throw new ArgumentNullException(nameof(graphValidation));
    }

    /// <summary>Creates an independent session initially sharing immutable committed snapshots.</summary>
    /// <returns>A session using the same configured catalog registry and graph policy.</returns>
    public SurfaceSession Fork()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var copy = new SurfaceSession(_catalogs, _graphValidation);
        foreach (var surface in _surfaces)
            copy._surfaces.Add(surface.Key, surface.Value);
        return copy;
    }

    /// <summary>Checks the current component tree at the end of a progressive batch.</summary>
    /// <param name="surfaceId">The surface to check. Deleted surfaces have no tree to check.</param>
    /// <param name="cancellationToken">Cancels graph traversal.</param>
    /// <returns>A diagnostic if the committed graph is incomplete or invalid.</returns>
    public A2uiDiagnostic? ValidateCompleteGraph(string surfaceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TryGetSurface(surfaceId, out var surface)
            ? ComponentGraph.Validate(surface!, _catalogs, GraphValidationOptions.Strict, "", new(), cancellationToken)
            : null;
    }

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

    /// <summary>Projects the current mounted component tree with data scopes.</summary>
    /// <param name="surfaceId">The live surface identifier.</param>
    /// <param name="maxOccurrences">The maximum number of occurrences to project.</param>
    /// <param name="cancellationToken">Cancels traversal of the tree.</param>
    /// <returns>The root occurrence, or null when the surface or its root is absent.</returns>
    /// <exception cref="InvalidOperationException">The tree exceeds the occurrence limit.</exception>
    public ComponentOccurrence? GetComponentOccurrences(string surfaceId, int maxOccurrences = 10_000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOccurrences);
        return TryGetSurface(surfaceId, out var snapshot)
            ? ComponentOccurrences.Resolve(snapshot!, _catalogs, maxOccurrences, cancellationToken)
            : null;
    }

    /// <summary>Collects the data models of the live surfaces that asked for synchronization with <c>sendDataModel</c>.</summary>
    /// <returns>The <c>surfaces</c> metadata to attach to each message that goes to the agent, or null when no surface opted in.</returns>
    /// <remarks>
    /// A session belongs to one agent connection, so the metadata only carries data that surface's owning agent created
    /// the surface for. Surfaces that did not opt in never appear. Two sessions never share surfaces, even with equal IDs.
    /// </remarks>
    public WireMetadata? GetRendererDataModel() => GetRendererDataModel(WireMessage.Version);

    /// <summary>Collects opted-in data models for the requested protocol version.</summary>
    /// <param name="protocolVersion">The exact wire version to report.</param>
    /// <returns>The renderer data model metadata, or null when no live surface matches.</returns>
    public WireMetadata? GetRendererDataModel(string protocolVersion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(protocolVersion);
        if (protocolVersion != WireMessage.Version)
            return null;

        using var stream = new MemoryStream();
        var count = 0;
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("version", WireMessage.Version);
            writer.WriteStartObject("surfaces");
            foreach (var surface in _surfaces.Values.Where(item => item.SendDataModel).OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                writer.WritePropertyName(surface.Id);
                surface.DataModel.WriteTo(writer);
                count++;
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (count == 0)
            return null;
        if (!WireMetadata.TryDecode(stream.ToArray(), WireMetadataKind.RendererDataModel, out var metadata, out var problem))
            throw new InvalidOperationException($"The data models do not form valid metadata: {problem}");
        return metadata;
    }

    /// <summary>Applies a local data write using the same ordering and pointer semantics as a remote update.</summary>
    /// <param name="surfaceId">The identifier of the live surface.</param>
    /// <param name="path">The absolute pointer. Use "/" for the root.</param>
    /// <param name="value">The replacement JSON value. Null deletes the target.</param>
    /// <param name="cancellationToken">Cancels processing before commit.</param>
    /// <returns>The rejection diagnostic, or null on success.</returns>
    public A2uiDiagnostic? WriteData(string surfaceId, string path, JsonElement value,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_notifying)
            return new("reentrantUpdate", "", "Complete the current notification before updating the session.");
        if (!_surfaces.TryGetValue(surfaceId, out var snapshot))
            return new("unknownSurface", "/surfaceId", $"Surface '{surfaceId}' does not exist.");
        if (value.ValueKind == JsonValueKind.Undefined)
            return new("invalidDataValue", "/value", "Expected a JSON value.");
        if (!WireJson.TryRead(System.Text.Encoding.UTF8.GetBytes(value.GetRawText()), out var owned, out var error))
            return error! with { Path = "/value" + error.Path };
        return UpdateData(snapshot, path, owned, "/path", cancellationToken);
    }


    /// <summary>Writes data through a binding in a collection occurrence's scope.</summary>
    /// <param name="surfaceId">The identifier of the live surface.</param>
    /// <param name="scope">The absolute pointer to the occurrence's data.</param>
    /// <param name="path">An absolute or relative pointer. An empty string selects the scope.</param>
    /// <param name="value">The replacement JSON value. Null deletes the target.</param>
    /// <param name="cancellationToken">Cancels processing before commit.</param>
    /// <returns>The rejection diagnostic, or null on success.</returns>
    public A2uiDiagnostic? WriteScopedData(string surfaceId, string scope, string path, JsonElement value,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!SurfaceSnapshot.TryResolveScope(scope, path, out var absolutePath))
            return new("invalidDataPath", "/path", "The data model cannot be updated at this pointer.");

        return WriteData(surfaceId, absolutePath, value, cancellationToken);
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
                creating ? $"Surface '{id}' already exists." : $"Surface '{id}' does not exist.");

        if (creating)
        {
            var catalogId = payload.TryGetProperty("catalogId", out var catalog) ? catalog.GetString() : null;
            if (catalogId is not null && !_catalogs.TryGetDefinition(catalogId, out _))
                return new("unknownCatalog", kindPath + "/catalogId", $"Catalog not found: {catalogId}");
            var send = payload.TryGetProperty("sendDataModel", out var sendFlag) && sendFlag.GetBoolean();
            var data = payload.TryGetProperty("dataModel", out var inlineData) ? inlineData : EmptyDataModel;
            snapshot = new(id, catalogId, send, data,
                new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>(StringComparer.Ordinal)));
        }

        if (payload.TryGetProperty("components", out var updates))
        {
            var components = new Dictionary<string, JsonElement>(snapshot!.Components, StringComparer.Ordinal);
            var updatePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var component in updates.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var componentPath = kindPath + "/components/" + index++.ToString(CultureInfo.InvariantCulture);
                var componentId = component.GetProperty("id").GetString()!;
                if (!updatePaths.TryAdd(componentId, componentPath))
                    return new("duplicateComponent", componentPath + "/id", $"Duplicate component ID '{componentId}' in the update.");
                if (_catalogs.ValidateComponent(component, snapshot.CatalogId) is { } problem)
                    return problem with { Path = componentPath + problem.Path };
                components[componentId] = component;
            }
            snapshot = snapshot with { Components = new ReadOnlyDictionary<string, JsonElement>(components) };
            if (ComponentGraph.Validate(snapshot, _catalogs, _graphValidation, kindPath + "/components",
                    updatePaths, cancellationToken) is { } graphError)
                return graphError;
        }

        if (message.Kind == "updateDataModel")
        {
            var path = payload.TryGetProperty("path", out var pointer) ? pointer.GetString()! : "/";
            return UpdateData(snapshot!, path, payload.GetProperty("value"), kindPath + "/path", cancellationToken);
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

        Notify(new(message.Kind, id, snapshot));
        return null;
    }

    private A2uiDiagnostic? UpdateData(SurfaceSnapshot snapshot, string path, JsonElement value,
        string diagnosticPath, CancellationToken cancellationToken)
    {
        if (DataPointer.TryParse(path, out var parsed) &&
            parsed.FirstOrDefault(segment => segment is "__proto__" or "constructor" or "prototype") is { } forbidden)
            return new("forbiddenPathKey", diagnosticPath, $"The path '{path}' uses the forbidden key '{forbidden}'.");
        if (!DataPointer.TryParse(path, out var segments) ||
            !DataPointer.TryUpdate(snapshot.DataModel, segments, value, out var result))
            return new("invalidDataPath", diagnosticPath, "The data model cannot be updated at this pointer.");

        cancellationToken.ThrowIfCancellationRequested();
        var updated = snapshot with { DataModel = result };
        _surfaces[snapshot.Id] = updated;
        Notify(new("updateDataModel", snapshot.Id, updated) { DataPath = path });
        return null;
    }

    private void Notify(SurfaceChange change)
    {
        _notifying = true;
        try
        {
            Changed?.Invoke(change);
        }
        finally
        {
            _notifying = false;
        }
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
