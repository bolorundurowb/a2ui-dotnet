using System.Globalization;
using System.Text;
using System.Text.Json;

namespace A2UI.Core;

/// <summary>The shape of a resolved node property.</summary>
public enum ResolvedPropertyKind
{
    /// <summary>A literal JSON value that does not read the data model.</summary>
    Value,

    /// <summary>A data binding or computed value. The value is null when the binding has no data.</summary>
    Binding,

    /// <summary>A single child node.</summary>
    Child,

    /// <summary>An ordered list of child nodes.</summary>
    Children,

    /// <summary>An event action that a renderer can invoke.</summary>
    Action
}

/// <summary>A component property after resolution against the current data model.</summary>
/// <param name="Kind">The property shape.</param>
/// <param name="Value">The resolved value for literal and binding properties, or null when a binding has no data.</param>
/// <param name="Writable">Whether a renderer write can update the bound data.</param>
/// <param name="BindingPath">The pointer relative to the node's data scope for a writable binding.</param>
/// <param name="ChildPositions">The mounted positions of child nodes, in order.</param>
public sealed record ResolvedProperty(ResolvedPropertyKind Kind, JsonElement? Value, bool Writable,
    string? BindingPath, IReadOnlyList<string> ChildPositions);

/// <summary>A mounted component occurrence with its resolved properties.</summary>
/// <remarks>Instances are reused for as long as their occurrence stays mounted, so callers can compare node references across updates.</remarks>
public sealed class ResolvedNode
{
    internal ResolvedNode(string instanceId) => InstanceId = instanceId;

    /// <summary>The occurrence identifier, stable while the occurrence stays mounted.</summary>
    public string InstanceId { get; }

    /// <summary>The mounted position, such as <c>root/children/0</c>. Positions can change when siblings move.</summary>
    public string Position { get; internal set; } = "";

    /// <summary>The component definition ID.</summary>
    public string ComponentId { get; internal set; } = "";

    /// <summary>The catalog component type.</summary>
    public string ComponentType { get; internal set; } = "";

    /// <summary>The absolute pointer to the node's data scope.</summary>
    public string DataPath { get; internal set; } = "/";

    /// <summary>The resolved properties by name.</summary>
    public IReadOnlyDictionary<string, ResolvedProperty> Props { get; internal set; } =
        new Dictionary<string, ResolvedProperty>(StringComparer.Ordinal);

    internal string Signature { get; set; } = "";
}

/// <summary>Projects the mounted tree of one surface into resolved nodes and reports creation and property changes.</summary>
/// <remarks>
/// The resolver subscribes to the session and refreshes after each committed change to its surface. Callers serialize
/// access with the session. Create the resolver before or after the surface exists; it attaches when the surface appears.
/// </remarks>
public sealed class NodeResolver : IDisposable
{
    private static readonly JsonElement TrueValue = ProtocolSchemas.Parse("true");
    private static readonly JsonElement FalseValue = ProtocolSchemas.Parse("false");
    private readonly SurfaceSession _session;
    private readonly CatalogRegistry _catalogs;
    private readonly string _surfaceId;
    private Dictionary<string, ResolvedNode> _nodesByInstance = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates a resolver for one surface and starts tracking it.</summary>
    /// <param name="session">The session that owns the surface.</param>
    /// <param name="catalogs">The registry that supplies component schemas.</param>
    /// <param name="surfaceId">The identifier of the surface to resolve.</param>
    public NodeResolver(SurfaceSession session, CatalogRegistry catalogs, string surfaceId)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _surfaceId = surfaceId ?? throw new ArgumentNullException(nameof(surfaceId));
        _session.Changed += OnChanged;
        Refresh();
    }

    /// <summary>The mounted nodes in depth-first order. Empty when the surface or its root is absent.</summary>
    public IReadOnlyList<ResolvedNode> Nodes { get; private set; } = [];

    /// <summary>Raised after a node joins the mounted tree. The node's first properties do not raise <see cref="PropsChanged"/>.</summary>
    public event Action<ResolvedNode>? NodeCreated;

    /// <summary>Raised once for each retained node whose resolved properties change after a committed update.</summary>
    public event Action<ResolvedNode>? PropsChanged;

    /// <summary>Writes a value through a writable binding on a node.</summary>
    /// <param name="node">A node from <see cref="Nodes"/>.</param>
    /// <param name="property">The name of a property whose resolved kind is a writable binding.</param>
    /// <param name="value">The replacement JSON value. Null deletes the bound data.</param>
    /// <returns>The rejection diagnostic, or null on success.</returns>
    public A2uiDiagnostic? Write(ResolvedNode node, string property, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(property);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!node.Props.TryGetValue(property, out var resolved) || resolved.Kind != ResolvedPropertyKind.Binding ||
            !resolved.Writable || resolved.BindingPath is null)
            return new("notWritable", "/" + property, "The property is not a writable binding.");

        return _session.WriteScopedData(_surfaceId, node.DataPath, resolved.BindingPath, value);
    }

    /// <summary>Stops tracking the surface and clears the mounted nodes.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.Changed -= OnChanged;
        _nodesByInstance.Clear();
        Nodes = [];
    }

    private void OnChanged(SurfaceChange change)
    {
        if (change.SurfaceId == _surfaceId)
            Refresh();
    }

    private void Refresh()
    {
        var occurrences = _session.GetComponentOccurrences(_surfaceId);
        var snapshot = occurrences is null || !_session.TryGetSurface(_surfaceId, out var current) ? null : current;
        var previous = _nodesByInstance;
        var next = new Dictionary<string, ResolvedNode>(StringComparer.Ordinal);
        var ordered = new List<ResolvedNode>();
        var created = new List<ResolvedNode>();
        var changed = new List<ResolvedNode>();

        if (occurrences is not null && snapshot is not null)
            Visit(occurrences);

        _nodesByInstance = next;
        Nodes = ordered.AsReadOnly();
        foreach (var node in created)
            NodeCreated?.Invoke(node);
        foreach (var node in changed)
            PropsChanged?.Invoke(node);

        void Visit(ComponentOccurrence occurrence)
        {
            var properties = ResolveProperties(occurrence, snapshot!);
            var signature = Signature(properties);
            var isNew = !previous.TryGetValue(occurrence.InstanceId, out var node);
            if (isNew)
            {
                node = new ResolvedNode(occurrence.InstanceId);
                created.Add(node);
            }
            else if (node!.Signature != signature)
            {
                changed.Add(node);
            }

            node!.Position = occurrence.Position;
            node.ComponentId = occurrence.ComponentId;
            node.ComponentType = occurrence.ComponentType;
            node.DataPath = occurrence.DataPath;
            node.Props = properties;
            node.Signature = signature;
            next[occurrence.InstanceId] = node;
            ordered.Add(node);
            foreach (var child in occurrence.Children)
                Visit(child);
        }
    }

    private Dictionary<string, ResolvedProperty> ResolveProperties(ComponentOccurrence occurrence, SurfaceSnapshot surface)
    {
        var properties = new Dictionary<string, ResolvedProperty>(StringComparer.Ordinal);
        var booleanProperties = BooleanProperties(occurrence.Definition, surface.CatalogId);
        foreach (var property in occurrence.Definition.EnumerateObject())
        {
            if (property.Name is "component" or "id" or "metadata" || property.Name.StartsWith('@'))
                continue;
            properties[property.Name] = ResolveProperty(occurrence, property.Name, property.Value, surface,
                booleanProperties.Contains(property.Name));
        }
        return properties;
    }

    private static ResolvedProperty ResolveProperty(ComponentOccurrence occurrence, string name, JsonElement value,
        SurfaceSnapshot surface, bool coerceBoolean)
    {
        var childPositions = ChildPositions(occurrence, name);
        if (childPositions.Count > 0 && value.ValueKind == JsonValueKind.String)
            return new(ResolvedPropertyKind.Child, null, false, null, childPositions);
        if (childPositions.Count > 0 && (value.ValueKind == JsonValueKind.Array || IsChildList(value)))
            return new(ResolvedPropertyKind.Children, null, false, null, childPositions);

        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("@path", out var pointer) &&
            pointer.ValueKind == JsonValueKind.String)
        {
            var path = pointer.GetString()!;
            JsonElement? data = surface.TryGetScopedData(occurrence.DataPath, path, out var bound) ? bound : null;
            return Binding(data, coerceBoolean, writable: true, path);
        }

        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("@call", out var function))
        {
            JsonElement? result = function.ValueEquals("@index")
                ? IndexValue(occurrence, value, surface)
                : null;
            return Binding(result, coerceBoolean, writable: false, null);
        }

        if (IsAction(value))
            return new(ResolvedPropertyKind.Action, null, false, null, []);

        return new(ResolvedPropertyKind.Value, value, false, null, []);
    }

    private static ResolvedProperty Binding(JsonElement? value, bool coerceBoolean, bool writable, string? path)
    {
        if (coerceBoolean)
            value = BooleanElement(CoerceBoolean(value));
        return new(ResolvedPropertyKind.Binding, value, writable, path, []);
    }

    // Only the reserved @index function is evaluated here. Its value is the item index in the enclosing collection plus
    // the offset argument, so a computed binding outside a collection has no value.
    private static JsonElement? IndexValue(ComponentOccurrence occurrence, JsonElement call, SurfaceSnapshot surface)
    {
        var segment = occurrence.DataPath[(occurrence.DataPath.LastIndexOf('/') + 1)..];
        if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
            !call.TryGetProperty("args", out var args) || !args.TryGetProperty("offset", out var offsetArgument))
            return null;

        JsonElement? offset = offsetArgument.TryGetProperty("@path", out var pointer) &&
            pointer.ValueKind == JsonValueKind.String
            ? surface.TryGetScopedData(occurrence.DataPath, pointer.GetString()!, out var bound) ? bound : null
            : offsetArgument;
        if (offset is not { ValueKind: JsonValueKind.Number } number || !number.TryGetInt64(out var amount))
            return null;

        return ProtocolSchemas.Parse((index + amount).ToString(CultureInfo.InvariantCulture));
    }

    private static bool IsAction(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 1 &&
        (value.TryGetProperty("event", out _) || value.TryGetProperty("functionCall", out _));

    private static bool IsChildList(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("componentId", out _);

    private static List<string> ChildPositions(ComponentOccurrence occurrence, string name)
    {
        var key = occurrence.Position + "/" + name;
        return occurrence.Children
            .Where(child => child.Position == key || child.Position.StartsWith(key + "/", StringComparison.Ordinal))
            .Select(child => child.Position)
            .ToList();
    }

    private HashSet<string> BooleanProperties(JsonElement definition, string? catalogId)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (_catalogs.TryGetComponentSchema(definition, catalogId, out _, out var schema) &&
            schema.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject())
                if (AllowsBoolean(property.Value, 0))
                    names.Add(property.Name);
        return names;
    }

    private static bool AllowsBoolean(JsonElement schema, int depth)
    {
        if (depth > 16 || schema.ValueKind != JsonValueKind.Object)
            return false;
        if (schema.TryGetProperty("type", out var type) &&
            (type.ValueEquals("boolean") || type.ValueKind == JsonValueKind.Array &&
                type.EnumerateArray().Any(item => item.ValueEquals("boolean"))))
            return true;
        foreach (var combiner in new[] { "oneOf", "anyOf" })
            if (schema.TryGetProperty(combiner, out var branches) && branches.ValueKind == JsonValueKind.Array &&
                branches.EnumerateArray().Any(branch => AllowsBoolean(branch, depth + 1)))
                return true;
        return false;
    }

    // Coercion follows the core blueprint's Boolean rules for DynamicBoolean properties.
    private static bool CoerceBoolean(JsonElement? value) => value switch
    {
        null => false,
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.String } text => string.Equals(text.GetString(), "true", StringComparison.OrdinalIgnoreCase),
        { ValueKind: JsonValueKind.Number } number => number.GetDouble() != 0,
        { ValueKind: JsonValueKind.Object } validation when validation.TryGetProperty("valid", out var valid) &&
            valid.ValueKind is JsonValueKind.True or JsonValueKind.False => valid.GetBoolean(),
        _ => false
    };

    private static JsonElement BooleanElement(bool value) => value ? TrueValue : FalseValue;

    private static string Signature(IReadOnlyDictionary<string, ResolvedProperty> properties)
    {
        var builder = new StringBuilder();
        foreach (var (name, property) in properties.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            builder.Append(name).Append('|').Append(property.Kind).Append('|').Append(property.Writable).Append('|')
                .Append(property.BindingPath).Append('|')
                .Append(property.Value is { } value ? value.GetRawText() : "\u0000").Append('|')
                .Append(string.Join(",", property.ChildPositions)).Append(';');
        }
        return builder.ToString();
    }
}
