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

/// <summary>The outcome of one check rule on a component.</summary>
public enum CheckState
{
    /// <summary>The condition has no value yet: its binding has no data or its function call failed.</summary>
    Unresolved,

    /// <summary>The condition waits for an answer from the agent. Nothing produces this state until agent function routing exists.</summary>
    Pending,

    /// <summary>The condition passed.</summary>
    Valid,

    /// <summary>The condition failed.</summary>
    Invalid
}

/// <summary>A validation result produced by a check rule.</summary>
/// <param name="Valid">Whether the check passed.</param>
/// <param name="Code">A machine-readable reason from the validation function or data, or null.</param>
/// <param name="Message">The text to show for a failed check, or null.</param>
/// <param name="Severity">The severity: <c>error</c> blocks validity, while <c>warning</c> and <c>info</c> do not.</param>
public sealed record ValidationResult(bool Valid, string? Code, string? Message, string Severity);

/// <summary>A check rule with its current state.</summary>
/// <param name="State">The state of the rule.</param>
/// <param name="Result">The validation result, or null while the rule is unresolved or pending.</param>
public sealed record CheckResult(CheckState State, ValidationResult? Result);

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

    /// <summary>The check rules from <c>@checks</c> or <c>checks</c>, in declaration order.</summary>
    public IReadOnlyList<CheckResult> Checks { get; internal set; } = [];

    /// <summary>Whether no check with error severity has failed. Warnings, information and unresolved rules do not block.</summary>
    public bool IsValid => BlockingResult is null;

    /// <summary>The failed results, in declaration order, including warnings and information.</summary>
    public IReadOnlyList<ValidationResult> ValidationResults =>
        Checks.Where(check => check.State == CheckState.Invalid).Select(check => check.Result!).ToList();

    /// <summary>The messages of the failed results with error severity, in declaration order.</summary>
    public IReadOnlyList<string> ValidationErrors =>
        ValidationResults.Where(result => result.Severity == "error").Select(result => result.Message ?? "").ToList();

    /// <summary>The first failed result with error severity, or null when the node is valid.</summary>
    public ValidationResult? BlockingResult =>
        ValidationResults.FirstOrDefault(result => result.Severity == "error");

    internal IReadOnlyDictionary<string, JsonElement> Actions { get; set; } = new Dictionary<string, JsonElement>();

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
    private readonly FunctionEvaluator _functions;
    private readonly ActionDispatcher _actions;
    private readonly string _surfaceId;
    private Dictionary<string, ResolvedNode> _nodesByInstance = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates a resolver for one surface and starts tracking it.</summary>
    /// <param name="session">The session that owns the surface.</param>
    /// <param name="catalogs">The registry that supplies component schemas.</param>
    /// <param name="surfaceId">The identifier of the surface to resolve.</param>
    /// <param name="functions">The evaluator for computed bindings, check conditions and actions. Defaults to one over <paramref name="catalogs"/>.</param>
    /// <param name="timeProvider">The clock for action timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
    public NodeResolver(SurfaceSession session, CatalogRegistry catalogs, string surfaceId, FunctionEvaluator? functions = null,
        TimeProvider? timeProvider = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _functions = functions ?? new FunctionEvaluator(catalogs);
        _actions = new ActionDispatcher(_functions, timeProvider);
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

    /// <summary>Invokes an action property on a node against the current data.</summary>
    /// <param name="node">A node from <see cref="Nodes"/>.</param>
    /// <param name="property">The name of a property whose resolved kind is an action.</param>
    /// <param name="userInitiated">Whether the invocation follows a user gesture.</param>
    /// <param name="metadata">Optional <c>action.metadata</c> to attach to the action message.</param>
    /// <returns>The message to send or the error to report. Calling a property that is not an action reports an <c>INVALID_ACTION</c> error.</returns>
    /// <remarks>Context bindings are read when the action is invoked, not when the node resolved.</remarks>
    public ActionResult Invoke(ResolvedNode node, string property, bool userInitiated = true, JsonElement? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(property);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_session.TryGetSurface(_surfaceId, out var snapshot) || snapshot is null)
            return new(null, RendererError.FromDiagnostic(new("unknownSurface", "", $"Surface '{_surfaceId}' does not exist."), _surfaceId));
        if (!node.Actions.TryGetValue(property, out var action))
            return new(null, RendererError.FromDiagnostic(new("invalidAction", "", $"The property '{property}' is not an action."), _surfaceId));
        return _actions.Dispatch(snapshot, node.ComponentId, node.DataPath, action, userInitiated, metadata);
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
            var checks = ResolveChecks(occurrence, snapshot!);
            var signature = Signature(properties, checks);
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
            node.Actions = ReadActions(occurrence.Definition, properties);
            node.Checks = checks;
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

    private ResolvedProperty ResolveProperty(ComponentOccurrence occurrence, string name, JsonElement value,
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

        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("@call", out _))
        {
            // A failed call leaves the binding without a value, like a path with no data.
            JsonElement? result = _functions.TryEvaluate(surface, occurrence.DataPath, value, userInitiated: false,
                out var computed, out _) ? computed : null;
            return Binding(result, coerceBoolean, writable: false, null);
        }

        if (IsAction(value))
            return new(ResolvedPropertyKind.Action, null, false, null, []);

        return new(ResolvedPropertyKind.Value, value, false, null, []);
    }

    private static Dictionary<string, JsonElement> ReadActions(JsonElement definition, Dictionary<string, ResolvedProperty> properties)
    {
        var actions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, property) in properties)
            if (property.Kind == ResolvedPropertyKind.Action)
                actions[name] = definition.GetProperty(name);
        return actions;
    }

    private static ResolvedProperty Binding(JsonElement? value, bool coerceBoolean, bool writable, string? path)
    {
        if (coerceBoolean)
            value = BooleanElement(FunctionValues.ToBoolean(value));
        return new(ResolvedPropertyKind.Binding, value, writable, path, []);
    }

    // A rule's condition is a path or a function call that yields a boolean or a validation result object.
    private IReadOnlyList<CheckResult> ResolveChecks(ComponentOccurrence occurrence, SurfaceSnapshot surface)
    {
        if ((!occurrence.Definition.TryGetProperty("@checks", out var rules) &&
             !occurrence.Definition.TryGetProperty("checks", out rules)) || rules.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<CheckResult>();
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("condition", out var condition))
                continue;
            var message = rule.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;

            JsonElement value;
            bool resolved;
            if (condition.ValueKind == JsonValueKind.Object && condition.TryGetProperty("@path", out var pointer) &&
                pointer.ValueKind == JsonValueKind.String)
                resolved = surface.TryGetScopedData(occurrence.DataPath, pointer.GetString()!, out value) &&
                    value.ValueKind != JsonValueKind.Null;
            else
                resolved = _functions.TryEvaluate(surface, occurrence.DataPath, condition, userInitiated: false,
                    out value, out _);

            results.Add(resolved ? Evaluate(value, message) : new CheckResult(CheckState.Unresolved, null));
        }
        return results;
    }

    private static CheckResult Evaluate(JsonElement value, string? ruleMessage)
    {
        string? code = null, functionMessage = null, severity = "error";
        bool valid;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("valid", out var flag) &&
            flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            valid = flag.GetBoolean();
            code = ReadText(value, "code");
            functionMessage = ReadText(value, "message");
            severity = ReadText(value, "severity") ?? "error";
        }
        else
            valid = FunctionValues.ToBoolean(value);

        return valid
            ? new CheckResult(CheckState.Valid, new ValidationResult(true, code, null, severity))
            : new CheckResult(CheckState.Invalid, new ValidationResult(false, code, ruleMessage ?? functionMessage, severity));
    }

    private static string? ReadText(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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

    private static JsonElement BooleanElement(bool value) => value ? TrueValue : FalseValue;

    private static string Signature(IReadOnlyDictionary<string, ResolvedProperty> properties, IReadOnlyList<CheckResult> checks)
    {
        var builder = new StringBuilder();
        foreach (var check in checks)
            builder.Append(check.State).Append('|').Append(check.Result).Append(';');
        builder.Append('#');
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
