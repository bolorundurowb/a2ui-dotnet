using System.Buffers;
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

/// <summary>The outcome of one check rule on a component.</summary>
public enum CheckState
{
    /// <summary>The condition has no value yet: its binding has no data or its function call failed.</summary>
    Unresolved,

    /// <summary>The condition waits for an answer from the agent. A pending rule does not block validity.</summary>
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

    /// <summary>Whether no check with error severity has failed. Warnings, information, pending and unresolved rules do not block.</summary>
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

    /// <summary>The component's <c>@metadata</c> envelope, if supplied.</summary>
    public JsonElement? Metadata { get; internal set; }

    /// <summary>The resolved <c>@accessibility</c> attributes, if supplied.</summary>
    /// <remarks>The hidden attribute is true when this node or an ancestor is hidden.</remarks>
    public JsonElement? Accessibility { get; internal set; }

    internal IReadOnlyDictionary<string, JsonElement> Actions { get; set; } = new Dictionary<string, JsonElement>();

    internal IReadOnlyList<string> ChildInstanceIds { get; set; } = [];

    internal string Signature { get; set; } = "";
}

/// <summary>Projects the mounted tree of one surface into resolved nodes and reports creation and property changes.</summary>
/// <remarks>
/// The resolver subscribes to the session and refreshes after each committed change to its surface. Callers serialise
/// access with the session. The resolver attaches when the surface appears and disposes when it is deleted.
/// Template items retain identity across moves by matching their JSON values, in list order for duplicates.
/// An unmatched item reuses the unmatched occurrence at its original index, allowing in-place value edits.
/// A resolver created with a <see cref="FunctionCallRouter"/> sends the calls the renderer cannot run to the agent. Deliver
/// the agent's answers through <see cref="FunctionCallRouter.ReceiveResponse"/>, serialised with the session.
/// </remarks>
public sealed class NodeResolver : IDisposable
{
    private static readonly JsonElement TrueValue = ProtocolSchemas.Parse("true");
    private static readonly JsonElement FalseValue = ProtocolSchemas.Parse("false");
    private readonly SurfaceSession _session;
    private readonly CatalogRegistry _catalogs;
    private readonly FunctionEvaluator _functions;
    private readonly FunctionCallRouter? _router;
    private readonly ActionDispatcher _actions;
    private readonly string _surfaceId;
    private Dictionary<string, ResolvedNode> _nodesByInstance = new(StringComparer.Ordinal);
    private Dictionary<string, TemplateItems> _templateItems = new(StringComparer.Ordinal);
    private Dictionary<string, Dictionary<string, BoundExpression>> _bindings = new(StringComparer.Ordinal);
    private List<(string InstanceId, string Key, BoundExpression Bound)>? _settledDuringRefresh;
    private long _nextItemId;
    private bool _refreshing;
    private bool _disposed;

    /// <summary>Creates a resolver for one surface and starts tracking it.</summary>
    /// <param name="session">The session that owns the surface.</param>
    /// <param name="catalogs">The registry that supplies component schemas.</param>
    /// <param name="surfaceId">The identifier of the surface to resolve.</param>
    /// <param name="functions">The evaluator for computed bindings, check conditions and actions. Defaults to one over <paramref name="catalogs"/>.</param>
    /// <param name="timeProvider">The clock for action timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
    public NodeResolver(SurfaceSession session, CatalogRegistry catalogs, string surfaceId, FunctionEvaluator? functions = null,
        TimeProvider? timeProvider = null)
        : this(session, catalogs, surfaceId, functions, (FunctionCallRouter?)null, timeProvider)
    {
    }

    /// <summary>Creates a resolver for one surface whose properties and check rules can wait for agent function calls.</summary>
    /// <param name="session">The session that owns the surface. It must be the session of <paramref name="router"/>.</param>
    /// <param name="catalogs">The registry that supplies component schemas. It must be the registry of <paramref name="router"/>.</param>
    /// <param name="surfaceId">The identifier of the surface to resolve.</param>
    /// <param name="router">The router that sends calls to the agent. The resolver also uses the router's evaluator for local calls.</param>
    /// <param name="timeProvider">The clock for action timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <remarks>
    /// A property or check condition that calls a function the agent runs has no value while the call is pending. When the
    /// answer arrives, the resolver raises <see cref="PropsChanged"/> for that node. A pending check rule reports
    /// <see cref="CheckState.Pending"/> and does not block validity. A check rule whose call failed is
    /// <see cref="CheckState.Invalid"/> with the rule's message. Detaching a node, deleting the surface or disposing the
    /// resolver cancels the node's pending calls unless another binding shares them, and the agent's late answers are ignored.
    /// Without an agent client that can send, or after the router is disposed, such calls fail locally as they do without a router.
    /// </remarks>
    /// <exception cref="ArgumentException">The router belongs to another session or catalog registry.</exception>
    public NodeResolver(SurfaceSession session, CatalogRegistry catalogs, string surfaceId, FunctionCallRouter router,
        TimeProvider? timeProvider = null)
        : this(session, catalogs, surfaceId, (router ?? throw new ArgumentNullException(nameof(router))).Functions, router,
            timeProvider)
    {
    }

    private NodeResolver(SurfaceSession session, CatalogRegistry catalogs, string surfaceId, FunctionEvaluator? functions,
        FunctionCallRouter? router, TimeProvider? timeProvider)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        if (router is not null && (!ReferenceEquals(router.Session, session) || !ReferenceEquals(router.Catalogs, catalogs)))
            throw new ArgumentException("The router must use the same session and catalog registry as the resolver.", nameof(router));
        _router = router;
        _functions = functions ?? new FunctionEvaluator(catalogs);
        _actions = new ActionDispatcher(_functions, timeProvider);
        _surfaceId = surfaceId ?? throw new ArgumentNullException(nameof(surfaceId));
        Refresh();
        _session.Changed += OnChanged;
    }

    /// <summary>The mounted nodes in depth-first order. Empty when the surface or its root is absent.</summary>
    public IReadOnlyList<ResolvedNode> Nodes { get; private set; } = [];

    /// <summary>Raised after a node joins the mounted tree. The node's first properties do not raise <see cref="PropsChanged"/>.</summary>
    public event Action<ResolvedNode>? NodeCreated;

    /// <summary>Raised once for each retained node whose resolved properties change after a committed update.</summary>
    public event Action<ResolvedNode>? PropsChanged;

    /// <summary>Raised after the metadata of a retained node changes, including when it is cleared.</summary>
    public event Action<ResolvedNode>? MetadataChanged;

    /// <summary>Raised after the resolved accessibility attributes of a retained node change.</summary>
    public event Action<ResolvedNode>? AccessibilityChanged;

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
        if (!IsMounted(node) || !node.Props.TryGetValue(property, out var resolved) || resolved.Kind != ResolvedPropertyKind.Binding ||
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
        if (!IsMounted(node) || !node.Actions.TryGetValue(property, out var action))
            return new(null, RendererError.FromDiagnostic(new("invalidAction", "", $"The property '{property}' is not an action."), _surfaceId));
        return _actions.Dispatch(snapshot, node.ComponentId, node.DataPath, action, userInitiated, metadata);
    }

    /// <summary>Runs an action property on a node through the router, so that calls in the action can wait for the agent.</summary>
    /// <param name="node">A node from <see cref="Nodes"/>.</param>
    /// <param name="property">The name of a property whose resolved kind is an action.</param>
    /// <param name="userInitiated">Whether the invocation follows a user gesture.</param>
    /// <param name="metadata">Optional <c>action.metadata</c> to attach to the <c>action</c> message of an event.</param>
    /// <returns>An <c>INVALID_ACTION</c> error when the property is not an action of a mounted node, or null when the router runs the action.</returns>
    /// <remarks>
    /// The router raises <see cref="FunctionCallRouter.ActionDispatched"/> when an event completes and
    /// <see cref="FunctionCallRouter.ErrorReported"/> when the action fails. Context bindings are read while the action runs.
    /// An action keeps running after its node detaches. Deleting the surface abandons it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The resolver was created without a router.</exception>
    public RendererError? RouteAction(ResolvedNode node, string property, bool userInitiated = true, JsonElement? metadata = null)
    {
        if (FindRoutedAction(node, property, out var error) is not { } action)
            return error;
        _router!.Invoke(_surfaceId, node.ComponentId, node.DataPath, action, userInitiated, metadata);
        return null;
    }

    /// <summary>Runs a function action property on a node through the router and delivers its successful result.</summary>
    /// <param name="node">A node from <see cref="Nodes"/>.</param>
    /// <param name="property">The name of a property whose resolved kind is an action with a <c>functionCall</c> member.</param>
    /// <param name="completed">Receives the function result, possibly before the method returns. Failures are reported through <see cref="FunctionCallRouter.ErrorReported"/>.</param>
    /// <param name="userInitiated">Whether the invocation follows a user gesture.</param>
    /// <returns>An <c>INVALID_ACTION</c> error when the property is not an action of a mounted node, or null when the router runs the action.</returns>
    /// <exception cref="InvalidOperationException">The resolver was created without a router.</exception>
    public RendererError? RouteFunctionAction(ResolvedNode node, string property, Action<JsonElement> completed,
        bool userInitiated = true)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (FindRoutedAction(node, property, out var error) is not { } action)
            return error;
        _router!.InvokeFunctionAction(_surfaceId, node.ComponentId, node.DataPath, action, completed, userInitiated);
        return null;
    }

    private JsonElement? FindRoutedAction(ResolvedNode node, string property, out RendererError? error)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(property);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_router is null)
            throw new InvalidOperationException("The resolver was created without a router.");
        error = null;
        if (!_session.TryGetSurface(_surfaceId, out _))
            error = RendererError.FromDiagnostic(new("unknownSurface", "", $"Surface '{_surfaceId}' does not exist."), _surfaceId);
        else if (!IsMounted(node) || !node.Actions.TryGetValue(property, out var action))
            error = RendererError.FromDiagnostic(new("invalidAction", "", $"The property '{property}' is not an action."), _surfaceId);
        else
            return action;
        return null;
    }

    /// <summary>Stops tracking the surface, clears the mounted nodes and cancels their pending agent calls.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.Changed -= OnChanged;
        foreach (var slots in _bindings.Values)
            foreach (var bound in slots.Values)
                bound.Binding.Dispose();
        _bindings.Clear();
        _nodesByInstance.Clear();
        _templateItems.Clear();
        Nodes = [];
        NodeCreated = null;
        PropsChanged = null;
        MetadataChanged = null;
        AccessibilityChanged = null;
    }

    private bool IsMounted(ResolvedNode node) =>
        _nodesByInstance.TryGetValue(node.InstanceId, out var mounted) && ReferenceEquals(node, mounted);

    private void OnChanged(SurfaceChange change)
    {
        if (_disposed || change.SurfaceId != _surfaceId)
            return;
        if (change.Snapshot is null)
        {
            Dispose();
            return;
        }

        Refresh();
    }

    private void Refresh()
    {
        _session.TryGetSurface(_surfaceId, out var snapshot);
        var templates = new Dictionary<string, TemplateItems>(StringComparer.Ordinal);
        var occurrences = snapshot is null ? null
            : _session.ProjectOccurrences(snapshot, 10_000, default, IdentifyItems);
        var previous = _nodesByInstance;
        var next = new Dictionary<string, ResolvedNode>(StringComparer.Ordinal);
        var ordered = new List<ResolvedNode>();
        var created = new List<ResolvedNode>();
        var changed = new List<ResolvedNode>();
        var metadataChanged = new List<ResolvedNode>();
        var accessibilityChanged = new List<ResolvedNode>();
        var previousBindings = _bindings;
        var nextBindings = new Dictionary<string, Dictionary<string, BoundExpression>>(StringComparer.Ordinal);

        _refreshing = true;
        try
        {
            if (occurrences is not null && snapshot is not null)
                Visit(occurrences, ancestorHidden: false);
        }
        finally
        {
            _refreshing = false;
        }

        _templateItems = templates;
        _nodesByInstance = next;
        _bindings = nextBindings;
        Nodes = ordered.AsReadOnly();
        // Bindings of detached nodes, and of expressions that changed, cancel the calls no other binding shares.
        foreach (var slots in previousBindings.Values)
            foreach (var bound in slots.Values)
                bound.Binding.Dispose();
        foreach (var node in created)
            NodeCreated?.Invoke(node);
        foreach (var node in changed)
            PropsChanged?.Invoke(node);
        foreach (var node in metadataChanged)
            MetadataChanged?.Invoke(node);
        foreach (var node in accessibilityChanged)
            AccessibilityChanged?.Invoke(node);
        if (_settledDuringRefresh is { } settled)
        {
            _settledDuringRefresh = null;
            foreach (var (instanceId, key, bound) in settled)
                OnBindingChanged(instanceId, key, bound);
        }

        IReadOnlyList<string> IdentifyItems(string template, JsonElement list)
        {
            _templateItems.TryGetValue(template, out var previousItems);
            var items = TemplateItems.Reconcile(list, previousItems, ref _nextItemId);
            templates.Add(template, items);
            return items.Ids;
        }

        void Visit(ComponentOccurrence occurrence, bool ancestorHidden)
        {
            previousBindings.TryGetValue(occurrence.InstanceId, out var previousSlots);
            var scope = new NodeScope(snapshot!, occurrence.InstanceId, occurrence.DataPath, previousSlots,
                new(StringComparer.Ordinal));
            var properties = ResolveProperties(occurrence, scope);
            var checks = ResolveChecks(occurrence, scope);
            if (scope.Next.Count > 0)
                nextBindings[occurrence.InstanceId] = scope.Next;
            var childInstanceIds = occurrence.Children.Select(child => child.InstanceId).ToArray();
            var signature = Signature(properties, checks, childInstanceIds);
            JsonElement? metadata = occurrence.Definition.TryGetProperty("@metadata", out var envelope) ? envelope : null;
            var accessibility = ResolveAccessibility(occurrence, snapshot!, ancestorHidden);
            var hidden = accessibility is { } attributes && attributes.TryGetProperty("hidden", out var hiddenValue) &&
                hiddenValue.ValueKind == JsonValueKind.True;
            var isNew = !previous.TryGetValue(occurrence.InstanceId, out var node);
            if (isNew)
            {
                node = new ResolvedNode(occurrence.InstanceId);
                created.Add(node);
            }
            else
            {
                if (node!.Signature != signature)
                    changed.Add(node);
                if (node.Metadata?.GetRawText() != metadata?.GetRawText())
                    metadataChanged.Add(node);
                if (node.Accessibility?.GetRawText() != accessibility?.GetRawText())
                    accessibilityChanged.Add(node);
            }

            node!.Position = occurrence.Position;
            node.ComponentId = occurrence.ComponentId;
            node.ComponentType = occurrence.ComponentType;
            node.DataPath = occurrence.DataPath;
            node.Props = properties;
            node.Actions = ReadActions(occurrence.Definition, properties);
            node.Checks = checks;
            node.Metadata = metadata;
            node.Accessibility = accessibility;
            node.ChildInstanceIds = childInstanceIds;
            node.Signature = signature;
            next[occurrence.InstanceId] = node;
            ordered.Add(node);
            foreach (var child in occurrence.Children)
                Visit(child, hidden);
        }
    }

    private sealed record TemplateItems(string[] Values, string[] Ids)
    {
        internal static TemplateItems Reconcile(JsonElement list, TemplateItems? previous, ref long nextId)
        {
            // A data write may re-serialise the model. Ignore differences in JSON whitespace.
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
                list.WriteTo(writer);
            using var normalized = JsonDocument.Parse(buffer.WrittenMemory);
            var values = normalized.RootElement.EnumerateArray().Select(item => item.GetRawText()).ToArray();
            var ids = new string[values.Length];
            if (previous is null)
            {
                for (var index = 0; index < ids.Length; index++)
                    ids[index] = (++nextId).ToString(CultureInfo.InvariantCulture);
                return new(values, ids);
            }

            var indexByValue = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            for (var index = 0; index < previous.Values.Length; index++)
            {
                if (!indexByValue.TryGetValue(previous.Values[index], out var indexes))
                    indexByValue.Add(previous.Values[index], indexes = new());
                indexes.Enqueue(index);
            }

            var retained = new bool[previous.Ids.Length];
            for (var index = 0; index < values.Length; index++)
            {
                if (!indexByValue.TryGetValue(values[index], out var indexes) || !indexes.TryDequeue(out var match))
                    continue;
                ids[index] = previous.Ids[match];
                retained[match] = true;
            }
            for (var index = 0; index < ids.Length; index++)
                ids[index] ??= index < retained.Length && !retained[index]
                    ? previous.Ids[index]
                    : (++nextId).ToString(CultureInfo.InvariantCulture);
            return new(values, ids);
        }
    }

    private Dictionary<string, ResolvedProperty> ResolveProperties(ComponentOccurrence occurrence, NodeScope scope)
    {
        var properties = new Dictionary<string, ResolvedProperty>(StringComparer.Ordinal);
        var booleanProperties = BooleanProperties(occurrence.Definition, scope.Surface.CatalogId);
        foreach (var property in occurrence.Definition.EnumerateObject())
        {
            if (property.Name is "component" or "id" || property.Name.StartsWith('@'))
                continue;
            properties[property.Name] = ResolveProperty(occurrence, property.Name, property.Value, scope,
                booleanProperties.Contains(property.Name));
        }
        return properties;
    }

    private ResolvedProperty ResolveProperty(ComponentOccurrence occurrence, string name, JsonElement value,
        NodeScope scope, bool coerceBoolean)
    {
        var surface = scope.Surface;
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
            if (Route(scope, name, value) is { } routed)
                return routed.AsProperty(name, ResolvedPropertyKind.Binding, coerceBoolean);
            // A failed call leaves the binding without a value, like a path with no data.
            JsonElement? result = _functions.TryEvaluate(surface, occurrence.DataPath, value, userInitiated: false,
                out var computed, out _) ? computed : null;
            return Binding(result, coerceBoolean, writable: false, null);
        }

        if (IsAction(value))
            return new(ResolvedPropertyKind.Action, null, false, null, []);

        if (Route(scope, name, value) is { } nested)
            return nested.AsProperty(name, ResolvedPropertyKind.Value, coerceBoolean: false);
        JsonElement? resolved = _functions.TryResolveValue(surface, occurrence.DataPath, value, userInitiated: false,
            out var literal, out _) ? literal : null;
        return new(ResolvedPropertyKind.Value, resolved, false, null, []);
    }

    // Binds an expression that calls the agent through the router, reusing the node's binding while the expression and
    // scope stay the same. Returns null when the expression resolves locally.
    private BoundExpression? Route(NodeScope scope, string key, JsonElement expression)
    {
        if (_router is not { CanRoute: true } router || !CallsAgent(expression, scope.Surface))
            return null;

        var text = expression.GetRawText();
        BoundExpression? previous = null;
        scope.Previous?.Remove(key, out previous);
        if (previous is { Binding.Ended: false } && previous.Expression == text && previous.DataPath == scope.DataPath)
        {
            router.Reevaluate(previous.Binding);
            scope.Next[key] = previous;
            return previous;
        }

        // Bind before releasing the previous binding, so that a call both share stays pending.
        var binding = router.BindManaged(_surfaceId, scope.DataPath, expression);
        var bound = new BoundExpression(binding, text, scope.DataPath);
        var instanceId = scope.InstanceId;
        binding.Changed += () => OnBindingChanged(instanceId, key, bound);
        previous?.Binding.Dispose();
        scope.Next[key] = bound;
        return bound;
    }

    // Updates only the property or check whose call settled, as the Core blueprint requires.
    private void OnBindingChanged(string instanceId, string key, BoundExpression bound)
    {
        if (_disposed)
            return;
        if (_refreshing)
        {
            (_settledDuringRefresh ??= []).Add((instanceId, key, bound));
            return;
        }
        if (!_nodesByInstance.TryGetValue(instanceId, out var node) || !_bindings.TryGetValue(instanceId, out var slots) ||
            !slots.TryGetValue(key, out var current) || !ReferenceEquals(current, bound))
            return;

        if (bound.Property is { } name)
        {
            node.Props = new Dictionary<string, ResolvedProperty>(node.Props, StringComparer.Ordinal)
            {
                [name] = bound.ToProperty()
            };
        }
        else
        {
            var checks = node.Checks.ToArray();
            checks[bound.CheckIndex] = bound.ToCheck();
            node.Checks = checks;
        }

        var signature = Signature(node.Props, node.Checks, node.ChildInstanceIds);
        if (signature == node.Signature)
            return;
        node.Signature = signature;
        PropsChanged?.Invoke(node);
    }

    // Whether the expression contains a call that the router sends to the agent. Local and refused calls resolve locally.
    private bool CallsAgent(JsonElement value, SurfaceSnapshot surface)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(item => CallsAgent(item, surface));
        if (value.ValueKind != JsonValueKind.Object)
            return false;

        if (value.TryGetProperty("@call", out var name) && name.ValueKind == JsonValueKind.String &&
            !name.GetString()!.StartsWith('@'))
        {
            var named = value.TryGetProperty("catalogId", out var catalogId);
            if ((!named || catalogId.ValueKind == JsonValueKind.String) &&
                _functions.Classify(surface.Id, surface.CatalogId, named ? catalogId.GetString() : null, name.GetString()!,
                    userInitiated: false, out _, out _, out _) == CallRoute.Agent)
                return true;
        }
        foreach (var member in value.EnumerateObject())
            if (CallsAgent(member.Value, surface))
                return true;
        return false;
    }

    private JsonElement? ResolveAccessibility(ComponentOccurrence occurrence, SurfaceSnapshot surface, bool ancestorHidden)
    {
        if (!occurrence.Definition.TryGetProperty("@accessibility", out var attributes) && !ancestorHidden)
            return null;

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        if (attributes.ValueKind == JsonValueKind.Object)
        {
            foreach (var attribute in attributes.EnumerateObject())
            {
                if (attribute.Name == "hidden" && ancestorHidden)
                    continue;
                if (_functions.TryResolveValue(surface, occurrence.DataPath, attribute.Value, userInitiated: false,
                    out var value, out _))
                {
                    writer.WritePropertyName(attribute.Name);
                    (attribute.Name == "hidden" ? BooleanElement(FunctionValues.ToBoolean(value)) : value).WriteTo(writer);
                }
            }
        }
        if (ancestorHidden)
            writer.WriteBoolean("hidden", true);
        writer.WriteEndObject();
        writer.Flush();
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
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
    private IReadOnlyList<CheckResult> ResolveChecks(ComponentOccurrence occurrence, NodeScope scope)
    {
        if ((!occurrence.Definition.TryGetProperty("@checks", out var rules) &&
             !occurrence.Definition.TryGetProperty("checks", out rules)) || rules.ValueKind != JsonValueKind.Array)
            return [];

        var surface = scope.Surface;
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
            else if (Route(scope, "@checks/" + results.Count.ToString(CultureInfo.InvariantCulture), condition) is { } routed)
            {
                results.Add(routed.AsCheck(results.Count, message));
                continue;
            }
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

    private static string Signature(IReadOnlyDictionary<string, ResolvedProperty> properties,
        IReadOnlyList<CheckResult> checks, IReadOnlyList<string> childInstanceIds)
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
        foreach (var child in childInstanceIds)
            builder.Append(child.Length).Append(':').Append(child);
        return builder.ToString();
    }

    private sealed record NodeScope(SurfaceSnapshot Surface, string InstanceId, string DataPath,
        Dictionary<string, BoundExpression>? Previous, Dictionary<string, BoundExpression> Next);

    // A property or check condition of one node that waits for the agent through a router binding.
    private sealed class BoundExpression(FunctionBinding binding, string expression, string dataPath)
    {
        public FunctionBinding Binding { get; } = binding;
        public string Expression { get; } = expression;
        public string DataPath { get; } = dataPath;
        public string? Property { get; private set; }
        public ResolvedPropertyKind Kind { get; private set; }
        public bool CoerceBoolean { get; private set; }
        public int CheckIndex { get; private set; }
        public string? Message { get; private set; }

        public ResolvedProperty AsProperty(string name, ResolvedPropertyKind kind, bool coerceBoolean)
        {
            Property = name;
            Kind = kind;
            CoerceBoolean = coerceBoolean;
            return ToProperty();
        }

        public CheckResult AsCheck(int index, string? message)
        {
            Property = null;
            CheckIndex = index;
            Message = message;
            return ToCheck();
        }

        // A pending or failed call leaves the property without a value, like a path with no data.
        public ResolvedProperty ToProperty()
        {
            JsonElement? value = Binding.IsPending || Binding.HasFailed ? null : Binding.Value;
            return Kind == ResolvedPropertyKind.Binding
                ? NodeResolver.Binding(value, CoerceBoolean, writable: false, null)
                : new(ResolvedPropertyKind.Value, value, false, null, []);
        }

        public CheckResult ToCheck()
        {
            if (Binding.IsPending)
                return new(CheckState.Pending, null);
            if (Binding.HasFailed)
                return new(CheckState.Invalid, new ValidationResult(false, null, Message, "error"));
            return Evaluate(Binding.Value, Message);
        }
    }
}
