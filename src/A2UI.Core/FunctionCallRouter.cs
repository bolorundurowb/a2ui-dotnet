using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2UI.Core;

/// <summary>Runs bindings and actions that call functions, sending the calls the renderer cannot run to the agent.</summary>
/// <remarks>
/// <para>
/// A call runs in the catalog its own <c>catalogId</c> names, else in the surface default. The renderer runs the call when
/// the catalog implements the function. When the catalog does not implement it, the call goes to the agent as a
/// <c>callAgentFunction</c> message with its arguments resolved. This covers a function the catalog declares without an
/// implementation, which first has its arguments checked against the declared schema with defaults applied, and a function the
/// catalog does not declare. A call that cannot be routed fails locally: an unknown catalog is a <c>CATALOG_ERROR</c>, and
/// everything else, such as an <c>agentOnly</c> function, a missing user activation or a router without a way to reach the
/// agent, is an <c>EXPRESSION_ERROR</c>. A call inside a <c>formatString</c> template never goes to the agent.
/// </para>
/// <para>
/// A binding from <see cref="Bind"/> is reactive. Its value is null while an agent call is pending, and it is evaluated again
/// when the surface data changes and when an answer arrives. Identical calls, meaning the same function, catalog and resolved
/// arguments, share one request on a surface, and the router keeps the answer for later bindings. A failed call is not kept
/// as an answer. The binding that waited for it remembers the failure and sends the call again only when the arguments of
/// that call change. A call that was cancelled leaves no trace, so the next evaluation sends it again. Disposing a binding
/// cancels the agent call it waits for when no other live binding shares that call, so the agent's late answer is ignored.
/// </para>
/// <para>
/// An action from <see cref="Invoke"/> is imperative. It never shares or caches a call, because each invocation must run the
/// function. The router sends the calls of one function in parallel, and resolves the context entries of an event in order,
/// stopping at the first failure. Deleting a surface cancels its pending calls and abandons its actions without an error.
/// </para>
/// Call every member from one thread at a time, as for <see cref="SurfaceSession"/>.
/// </remarks>
public sealed class FunctionCallRouter : IDisposable
{
    private readonly SurfaceSession _session;
    private readonly CatalogRegistry _catalogs;
    private readonly FunctionEvaluator _functions;
    private readonly AgentFunctionClient? _agent;
    private readonly ActionDispatcher _actions;
    private readonly Dictionary<string, SurfaceState> _surfaces = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates a router for the surfaces of a session.</summary>
    /// <param name="session">The session that owns the surfaces. The router follows its changes until it is disposed.</param>
    /// <param name="catalogs">The registry that holds the catalogs the calls name.</param>
    /// <param name="functions">The evaluator that runs the calls the renderer implements.</param>
    /// <param name="agent">The client that sends calls to the agent. Without one, every call that needs the agent fails locally.</param>
    /// <param name="timeProvider">The clock for action timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
    public FunctionCallRouter(SurfaceSession session, CatalogRegistry catalogs, FunctionEvaluator functions,
        AgentFunctionClient? agent, TimeProvider? timeProvider = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _functions = functions ?? throw new ArgumentNullException(nameof(functions));
        _agent = agent;
        _actions = new ActionDispatcher(functions, timeProvider);
        _session.Changed += OnSessionChanged;
    }

    /// <summary>Raised when a binding or an action fails. The failure belongs to a surface, or to a call the agent answered with an error.</summary>
    public event Action<RendererError>? ErrorReported;

    /// <summary>Raised with the <c>action</c> message when an event action has resolved its context and user message.</summary>
    public event Action<WireMessage>? ActionDispatched;

    /// <summary>Binds an expression to a surface and keeps its value current.</summary>
    /// <param name="surfaceId">The live surface the expression reads.</param>
    /// <param name="dataPath">The absolute pointer to the data scope of the expression. Use "/" outside a collection.</param>
    /// <param name="expression">A literal, a <c>@path</c> binding, a <c>@call</c> or an object or array that contains them. A check rule passes its condition.</param>
    /// <returns>The binding. Dispose it when the component that owns the expression goes away.</returns>
    /// <exception cref="InvalidOperationException">The surface does not exist.</exception>
    public FunctionBinding Bind(string surfaceId, string dataPath, JsonElement expression) =>
        BindCore(surfaceId, dataPath, expression, managed: false);

    // A managed binding is evaluated again only by its owner through Reevaluate, or when an answer it waits for settles.
    // NodeResolver owns its bindings this way so that a data change never evaluates a node that the change detaches.
    internal FunctionBinding BindManaged(string surfaceId, string dataPath, JsonElement expression) =>
        BindCore(surfaceId, dataPath, expression, managed: true);

    private FunctionBinding BindCore(string surfaceId, string dataPath, JsonElement expression, bool managed)
    {
        ArgumentNullException.ThrowIfNull(surfaceId);
        ArgumentNullException.ThrowIfNull(dataPath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = StateFor(surfaceId);
        var binding = new FunctionBinding(this, surfaceId, dataPath, expression, managed);
        state.Bindings.Add(binding);
        Evaluate(binding);
        return binding;
    }

    internal SurfaceSession Session => _session;

    internal CatalogRegistry Catalogs => _catalogs;

    internal FunctionEvaluator Functions => _functions;

    // Whether a binding can wait for the agent. Without that, a call the renderer cannot run fails as it does locally.
    internal bool CanRoute => !_disposed && _agent is { CanSend: true };

    internal void Reevaluate(FunctionBinding binding) => Evaluate(binding);

    /// <summary>Runs an action that a component defines.</summary>
    /// <param name="surfaceId">The live surface that owns the component.</param>
    /// <param name="sourceComponentId">The identifier of the component that defines the action.</param>
    /// <param name="dataPath">The absolute pointer to the data scope of the component. Use "/" outside a collection.</param>
    /// <param name="action">The action, an object with one <c>event</c> or <c>functionCall</c> member.</param>
    /// <param name="userInitiated">Whether the invocation follows a user gesture. Functions that require user activation fail without it.</param>
    /// <param name="metadata">Optional <c>action.metadata</c>, attached to the <c>action</c> message of an event.</param>
    /// <remarks>
    /// The action may wait for the agent, so the method returns before it finishes. An event raises <see cref="ActionDispatched"/>
    /// when it completes. A failure raises <see cref="ErrorReported"/> and the action sends nothing.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The surface does not exist.</exception>
    public void Invoke(string surfaceId, string sourceComponentId, string dataPath, JsonElement action,
        bool userInitiated = true, JsonElement? metadata = null) =>
        InvokeCore(surfaceId, sourceComponentId, dataPath, action, null, userInitiated, metadata);

    /// <summary>Invokes a function action and delivers its successful result to the caller.</summary>
    /// <param name="surfaceId">The surface that owns the action.</param>
    /// <param name="sourceComponentId">The component that raised the action.</param>
    /// <param name="dataPath">The absolute pointer to the component's data scope.</param>
    /// <param name="action">The action object containing a functionCall member.</param>
    /// <param name="completed">Receives the successful function result, possibly synchronously. Failures are reported through <see cref="ErrorReported"/>.</param>
    /// <param name="userInitiated">Whether a user gesture started the action.</param>
    public void InvokeFunctionAction(string surfaceId, string sourceComponentId, string dataPath, JsonElement action,
        Action<JsonElement> completed, bool userInitiated = true)
    {
        ArgumentNullException.ThrowIfNull(completed);
        InvokeCore(surfaceId, sourceComponentId, dataPath, action, completed, userInitiated, null);
    }

    private void InvokeCore(string surfaceId, string sourceComponentId, string dataPath, JsonElement action,
        Action<JsonElement>? completed, bool userInitiated, JsonElement? metadata)
    {
        ArgumentNullException.ThrowIfNull(surfaceId);
        ArgumentNullException.ThrowIfNull(sourceComponentId);
        ArgumentNullException.ThrowIfNull(dataPath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = StateFor(surfaceId);

        if (action.ValueKind != JsonValueKind.Object || action.EnumerateObject().Count() != 1)
        {
            ReportInvalidAction(surfaceId, "Expected an action with one event or functionCall.");
            return;
        }

        var operation = new Operation(state, surfaceId, sourceComponentId, dataPath, userInitiated, metadata);
        if (action.TryGetProperty("functionCall", out var call))
        {
            operation.Call = call;
            operation.Completed = completed;
        }
        else if (completed is null && action.TryGetProperty("event", out var definition) && definition.ValueKind == JsonValueKind.Object)
        {
            if (!definition.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                name.GetString()!.Length == 0)
            {
                ReportInvalidAction(surfaceId, "The action event needs a name.");
                return;
            }
            operation.EventName = name.GetString();
            if (definition.TryGetProperty("context", out var context))
            {
                if (context.ValueKind != JsonValueKind.Object)
                {
                    ReportInvalidAction(surfaceId, "The action event context must be an object.");
                    return;
                }
                operation.Entries.AddRange(context.EnumerateObject().Select(entry => (entry.Name, entry.Value)));
            }
            if (definition.TryGetProperty("userMessage", out var message))
                operation.UserMessage = message;
        }
        else
        {
            ReportInvalidAction(surfaceId, "Expected an action with one event or functionCall.");
            return;
        }

        state.Operations.Add(operation);
        Advance(operation);
    }

    /// <summary>Delivers an <c>agentFunctionResponse</c> to the call it answers.</summary>
    /// <param name="response">A decoded agent-to-renderer message.</param>
    /// <returns>Whether the response matched a pending call. A response for an unknown or finished call is ignored.</returns>
    public bool ReceiveResponse(WireMessage response) =>
        (_agent ?? throw new InvalidOperationException("The router has no agent client.")).TryHandleResponse(response);

    /// <summary>Cancels the pending calls of a surface and leaves the surface live.</summary>
    /// <param name="surfaceId">The surface whose calls to cancel.</param>
    /// <returns>The number of calls cancelled. A cancelled call reports no error, and the next evaluation of its binding sends it again.</returns>
    public int CancelPendingCalls(string surfaceId) => _agent?.CancelSurface(surfaceId) ?? 0;

    /// <summary>Stops following the session, cancels the pending calls of every surface and ends every binding.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.Changed -= OnSessionChanged;
        foreach (var surfaceId in _surfaces.Keys.ToArray())
            DropSurface(surfaceId);
    }

    internal void Release(FunctionBinding binding)
    {
        if (!_surfaces.TryGetValue(binding.SurfaceId, out var state))
            return;
        state.Bindings.Remove(binding);
        foreach (var entry in state.Cache.Values.ToArray())
        {
            if (entry.Settled || entry.Waiters.RemoveWhere(waiter => waiter.Binding == binding) == 0 ||
                entry.Waiters.Any(waiter => !waiter.Binding.Ended))
                continue;
            // Removing the entry first makes the cancellation callback a no-op, so nothing is reported.
            state.Cache.Remove(entry.Key);
            if (entry.CallId is { } callId)
                _agent?.Cancel(callId);
        }
    }

    private SurfaceState StateFor(string surfaceId)
    {
        if (_surfaces.TryGetValue(surfaceId, out var state))
            return state;
        if (!_session.TryGetSurface(surfaceId, out _))
            throw new InvalidOperationException($"Surface '{surfaceId}' does not exist.");
        return _surfaces[surfaceId] = new SurfaceState();
    }

    private void OnSessionChanged(SurfaceChange change)
    {
        if (!_surfaces.TryGetValue(change.SurfaceId, out var state))
            return;
        if (change.Kind == "deleteSurface")
            DropSurface(change.SurfaceId);
        else if (change.Kind is "updateDataModel" or "updateComponents")
            foreach (var binding in state.Bindings.ToArray())
                if (!binding.Managed)
                    Evaluate(binding);
    }

    private void DropSurface(string surfaceId)
    {
        if (!_surfaces.Remove(surfaceId, out var state))
            return;
        foreach (var binding in state.Bindings)
            binding.End();
        foreach (var operation in state.Operations)
            operation.Done = true;
        state.Bindings.Clear();
        state.Operations.Clear();
        state.Cache.Clear();
        _agent?.CancelSurface(surfaceId);
    }

    // ---- bindings ----

    private void Evaluate(FunctionBinding binding)
    {
        if (binding.Ended || !_session.TryGetSurface(binding.SurfaceId, out var surface) || surface is null)
            return;
        HashSet<string>? reported = null;
        void ReportOnce(A2uiDiagnostic failure)
        {
            reported ??= new(StringComparer.Ordinal);
            var key = failure.Code + "|" + failure.Message;
            if (reported.Add(key) && !binding.Reported.Contains(key))
                Report(binding.SurfaceId, failure, null, "");
        }

        var outcome = Eval(new(surface, binding.DataPath, false, binding, null, ReportOnce), binding.Expression, "");
        if (outcome.Diagnostic is { } failure)
            ReportOnce(failure);
        binding.Reported.Clear();
        if (reported is not null)
            binding.Reported.UnionWith(reported);
        binding.Update(outcome.Status, outcome.Value);
    }

    private void OnEntryOutcome(string surfaceId, CacheEntry entry, AgentFunctionOutcome outcome)
    {
        if (!_surfaces.TryGetValue(surfaceId, out var state) ||
            !state.Cache.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
            return;

        if (outcome.Succeeded)
        {
            entry.Settled = true;
            entry.Value = outcome.Value;
        }
        else
        {
            state.Cache.Remove(entry.Key);
            // A cancelled call leaves no trace. The next evaluation of its bindings sends it again.
            if (IsCancellation(outcome))
                return;
            entry.Failed = true;
            foreach (var (binding, site) in entry.Waiters)
                binding.FailedSites[site] = entry.Key;
            ReportAgentFailure(surfaceId, entry.CallId, outcome);
        }

        foreach (var binding in entry.Waiters.Select(waiter => waiter.Binding).Distinct().ToArray())
            Evaluate(binding);
    }

    // ---- actions ----

    private void Advance(Operation operation)
    {
        if (operation.Done || !_session.TryGetSurface(operation.SurfaceId, out var surface) || surface is null)
            return;
        var context = new Context(surface, operation.DataPath, operation.UserInitiated, null, operation,
            problem => Report(operation.SurfaceId, problem, null, ""));

        if (operation.Call is { } call)
        {
            var outcome = Eval(context, call, "");
            if (outcome.Status == EvaluationStatus.Pending)
                return;
            Finish(operation);
            if (outcome.Status == EvaluationStatus.Value)
                operation.Completed?.Invoke(outcome.Value);
            else if (outcome.Diagnostic is { } failure)
                Report(operation.SurfaceId, failure, null, "");
            return;
        }

        while (operation.Resolved.Count < operation.Entries.Count)
        {
            var (name, expression) = operation.Entries[operation.Resolved.Count];
            var outcome = Eval(context, expression, "/context/" + name);
            if (outcome.Status == EvaluationStatus.Pending)
                return;
            if (outcome.Status == EvaluationStatus.Failed)
            {
                Finish(operation);
                if (outcome.Diagnostic is { } failure)
                    Report(operation.SurfaceId, failure, null, $"Action event context entry '{name}' is invalid: ");
                return;
            }
            operation.Resolved.Add((name, outcome.Value));
        }

        string? userMessage = null;
        if (operation.UserMessage is { } template)
        {
            var outcome = Eval(context, template, "/userMessage");
            if (outcome.Status == EvaluationStatus.Pending)
                return;
            Finish(operation);
            if (outcome.Status == EvaluationStatus.Failed)
            {
                if (outcome.Diagnostic is { } failure)
                    Report(operation.SurfaceId, failure, null, "Action event userMessage is invalid: ");
                return;
            }
            if (outcome.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                ReportInvalidAction(operation.SurfaceId, "Action event userMessage is invalid: expected a string.");
                return;
            }
            if (outcome.Value.ValueKind == JsonValueKind.String && outcome.Value.GetString()!.Length > 0)
                userMessage = outcome.Value.GetString();
        }

        Finish(operation);
        var result = _actions.Compose(operation.SurfaceId, operation.SourceComponentId, operation.EventName!, userMessage,
            operation.Resolved, operation.Metadata);
        if (result.Error is { } error)
            ErrorReported?.Invoke(error);
        else
            ActionDispatched?.Invoke(result.Message!);
    }

    private static void Finish(Operation operation)
    {
        operation.Done = true;
        operation.State.Operations.Remove(operation);
    }

    private void OnSlotOutcome(Operation operation, Slot slot, AgentFunctionOutcome outcome)
    {
        if (operation.Done)
            return;
        if (outcome.Succeeded)
        {
            slot.Settled = true;
            slot.Value = outcome.Value;
            Advance(operation);
            return;
        }

        slot.Failed = true;
        Finish(operation);
        if (!IsCancellation(outcome))
            ReportAgentFailure(operation.SurfaceId, slot.CallId, outcome);
    }

    // ---- evaluation ----

    private Outcome Eval(Context context, JsonElement node, string site)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty("@path", out var pointer) && pointer.ValueKind == JsonValueKind.String)
                    return Outcome.Of(context.Surface.TryGetScopedData(context.DataPath, pointer.GetString()!, out var data)
                        ? data
                        : FunctionValues.Null);
                if (node.TryGetProperty("@call", out _))
                    return EvalCall(context, node, site);
                return EvalMembers(context, node, site);
            case JsonValueKind.Array:
                var items = new List<(string?, Outcome)>();
                var index = 0;
                foreach (var item in node.EnumerateArray())
                    items.Add((null, Eval(context, item, site + "/" + index++)));
                return Combine(items, isObject: false);
            default:
                return Outcome.Of(node);
        }
    }

    private Outcome EvalMembers(Context context, JsonElement node, string site)
    {
        var members = new List<(string?, Outcome)>();
        foreach (var member in node.EnumerateObject())
        {
            var name = member.Name;
            if (name.StartsWith("@@", StringComparison.Ordinal))
                name = name[1..];
            else if (name.StartsWith('@'))
                return Outcome.Fail(FunctionValues.ExpressionError($"Unknown key '{name}'. Write '@{name}' for a literal key."));
            members.Add((name, Eval(context, member.Value, site + "/" + name)));
        }
        return Combine(members, isObject: true);
    }

    // Every child is evaluated, so sibling calls to the agent go out together. A failure outranks a pending call.
    private static Outcome Combine(List<(string? Name, Outcome Outcome)> children, bool isObject)
    {
        Outcome? failure = null;
        var pending = false;
        foreach (var (_, outcome) in children)
        {
            if (outcome.Status == EvaluationStatus.Failed && (failure is null || failure.Value.Diagnostic is null))
                failure = outcome;
            pending |= outcome.Status == EvaluationStatus.Pending;
        }
        if (failure is { } failed)
            return failed;
        if (pending)
            return Outcome.Waiting;

        JsonNode result;
        if (isObject)
        {
            var members = new JsonObject();
            foreach (var (name, outcome) in children)
                members[name!] = JsonNode.Parse(outcome.Value.GetRawText());
            result = members;
        }
        else
        {
            var items = new JsonArray();
            foreach (var (_, outcome) in children)
                items.Add(JsonNode.Parse(outcome.Value.GetRawText()));
            result = items;
        }
        return Outcome.Of(ProtocolSchemas.Parse(result.ToJsonString()));
    }

    private Outcome EvalCall(Context context, JsonElement call, string site)
    {
        if (context.Operation?.LocalResults.TryGetValue(site, out var previous) == true)
            return previous;

        var surface = context.Surface;
        var name = call.GetProperty("@call").GetString();
        if (name is null)
            return Outcome.Fail(FunctionValues.ExpressionError("Expected a function call."));
        if (name.StartsWith('@'))
            return _functions.TryEvaluate(surface, context.DataPath, call, context.UserInitiated, out var system, out var problem)
                ? Outcome.Of(system)
                : Outcome.Fail(problem);

        string? explicitCatalog = null;
        if (call.TryGetProperty("catalogId", out var named))
        {
            if (named.ValueKind != JsonValueKind.String)
                return Outcome.Fail(new A2uiDiagnostic("CatalogError", "", "The catalogId of a call must be a string."));
            explicitCatalog = named.GetString();
        }

        var route = _functions.Classify(surface.Id, surface.CatalogId, explicitCatalog, name, context.UserInitiated,
            out var catalogId, out var declared, out var diagnostic);
        if (route == CallRoute.Refused)
            return Outcome.Fail(diagnostic);

        var arguments = EvalArguments(context, call, site);
        if (arguments.Status != EvaluationStatus.Value)
            return arguments;

        if (route == CallRoute.Local)
        {
            var values = arguments.Value.EnumerateObject().ToDictionary(member => member.Name, member => member.Value, StringComparer.Ordinal);
            var outcome = _functions.TryInvoke(surface, context.DataPath, catalogId!, name, values, context.UserInitiated,
                out var result, out diagnostic, context.ReportPartError)
                ? Outcome.Of(result)
                : Outcome.Fail(diagnostic);
            if (context.Operation is { } operation)
                operation.LocalResults[site] = outcome;
            return outcome;
        }

        var sendable = arguments.Value;
        if (declared && _catalogs.ParseArguments(catalogId!, name, sendable, out sendable) is { } invalid)
            return Outcome.Fail(FunctionValues.ExpressionError($"Validation failed for function '{name}': {invalid.Message}"));
        if (_agent is not { CanSend: true })
            return Outcome.Fail(FunctionValues.ExpressionError(
                $"Function '{name}' is not implemented and the renderer has no way to send it to the agent."));
        return Send(context, site, catalogId!, name, sendable);
    }

    private Outcome EvalArguments(Context context, JsonElement call, string site)
    {
        var members = new List<(string?, Outcome)>();
        if (call.TryGetProperty("args", out var declared) && declared.ValueKind == JsonValueKind.Object)
            foreach (var argument in declared.EnumerateObject())
                members.Add((argument.Name, Eval(context, argument.Value, site + "/args/" + argument.Name)));
        return Combine(members, isObject: true);
    }

    private Outcome Send(Context context, string site, string catalogId, string name, JsonElement arguments)
    {
        var surfaceId = context.Surface.Id;
        var wire = new JsonObject
        {
            ["@call"] = name,
            ["catalogId"] = catalogId,
            ["args"] = JsonNode.Parse(arguments.GetRawText())
        };
        var callFunction = ProtocolSchemas.Parse(wire.ToJsonString());

        try
        {
            if (context.Operation is { } operation)
                return SendImperative(operation, site, surfaceId, callFunction);
            return SendReactive(context.Binding!, site, surfaceId, catalogId, name, arguments, callFunction);
        }
        catch (AgentFunctionException failure)
        {
            return Outcome.Fail(FunctionValues.ExpressionError($"Function '{name}' could not be sent to the agent: {failure.Message}"));
        }
    }

    private Outcome SendReactive(FunctionBinding binding, string site, string surfaceId, string catalogId, string name,
        JsonElement arguments, JsonElement callFunction)
    {
        var state = _surfaces[surfaceId];
        var key = string.Join('\n', catalogId, name, Canonical(arguments));
        state.Cache.TryGetValue(key, out var entry);
        if (entry is { Settled: true })
        {
            binding.FailedSites.Remove(site);
            return Outcome.Of(entry.Value);
        }
        if (binding.FailedSites.TryGetValue(site, out var failedKey))
        {
            if (failedKey == key)
                return Outcome.Fail(null);
            binding.FailedSites.Remove(site);
        }

        if (entry is null)
        {
            entry = new CacheEntry(key);
            state.Cache[key] = entry;
            try
            {
                entry.CallId = _agent!.Send(surfaceId, callFunction, outcome => OnEntryOutcome(surfaceId, entry, outcome));
            }
            catch
            {
                state.Cache.Remove(key);
                throw;
            }

            // An in-process agent can answer inside Send, before this binding is registered as a waiter.
            if (entry.Settled)
                return Outcome.Of(entry.Value);
            if (entry.Failed)
            {
                binding.FailedSites[site] = key;
                return Outcome.Fail(null);
            }
            if (!state.Cache.ContainsKey(key))
                return Outcome.Fail(null);
        }
        entry.Waiters.Add((binding, site));
        return Outcome.Waiting;
    }

    private Outcome SendImperative(Operation operation, string site, string surfaceId, JsonElement callFunction)
    {
        if (operation.Slots.TryGetValue(site, out var slot))
        {
            if (slot.Settled)
                return Outcome.Of(slot.Value);
            return slot.Failed ? Outcome.Fail(null) : Outcome.Waiting;
        }

        slot = new Slot();
        operation.Slots[site] = slot;
        try
        {
            slot.CallId = _agent!.Send(surfaceId, callFunction, outcome => OnSlotOutcome(operation, slot, outcome));
        }
        catch
        {
            operation.Slots.Remove(site);
            throw;
        }
        return Outcome.Waiting;
    }

    private static string Canonical(JsonElement value)
    {
        var text = new StringBuilder();
        WriteCanonical(text, value);
        return text.ToString();
    }

    // Objects list their members in ordinal order, so equal arguments give equal text whatever their order.
    private static void WriteCanonical(StringBuilder text, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                text.Append('{');
                foreach (var member in value.EnumerateObject().OrderBy(member => member.Name, StringComparer.Ordinal))
                {
                    text.Append(JsonValue.Create(member.Name)!.ToJsonString()).Append(':');
                    WriteCanonical(text, member.Value);
                    text.Append(',');
                }
                text.Append('}');
                break;
            case JsonValueKind.Array:
                text.Append('[');
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(text, item);
                    text.Append(',');
                }
                text.Append(']');
                break;
            default:
                text.Append(value.GetRawText());
                break;
        }
    }

    // ---- reporting ----

    private static bool IsCancellation(AgentFunctionOutcome outcome) => outcome.ErrorCode is "CANCELLED" or "DISPOSED";

    private void ReportAgentFailure(string surfaceId, string? callId, AgentFunctionOutcome outcome) =>
        Report(surfaceId, FunctionValues.ExpressionError(outcome.ErrorMessage ?? outcome.ErrorCode ?? "The agent call failed."),
            callId, "");

    private void ReportInvalidAction(string surfaceId, string message) =>
        ErrorReported?.Invoke(RendererError.FromDiagnostic(new("invalidAction", "", message), surfaceId));

    // A refusal keeps the catalog and execution codes. Any other failure to run a function is an expression error.
    private void Report(string surfaceId, A2uiDiagnostic problem, string? functionCallId, string prefix)
    {
        var diagnostic = problem.Code is "CatalogError" or "ExecutionError"
            ? problem with { Message = prefix + problem.Message }
            : new A2uiDiagnostic("ExpressionError", problem.Path, prefix + problem.Message);
        ErrorReported?.Invoke(RendererError.FromDiagnostic(diagnostic, surfaceId, functionCallId));
    }

    // A failure carries no diagnostic once it has been reported, as when the agent answered with an error.
    private readonly record struct Outcome(EvaluationStatus Status, JsonElement Value, A2uiDiagnostic? Diagnostic)
    {
        public static Outcome Waiting => new(EvaluationStatus.Pending, FunctionValues.Null, null);

        public static Outcome Of(JsonElement value) => new(EvaluationStatus.Value, value, null);

        public static Outcome Fail(A2uiDiagnostic? diagnostic) => new(EvaluationStatus.Failed, FunctionValues.Null, diagnostic);
    }

    private sealed record Context(SurfaceSnapshot Surface, string DataPath, bool UserInitiated, FunctionBinding? Binding,
        Operation? Operation, Action<A2uiDiagnostic>? ReportPartError = null);

    private sealed class SurfaceState
    {
        public Dictionary<string, CacheEntry> Cache { get; } = new(StringComparer.Ordinal);
        public List<FunctionBinding> Bindings { get; } = [];
        public List<Operation> Operations { get; } = [];
    }

    private sealed class CacheEntry(string key)
    {
        public string Key { get; } = key;
        public string? CallId { get; set; }
        public bool Settled { get; set; }
        public bool Failed { get; set; }
        public JsonElement Value { get; set; }
        public HashSet<(FunctionBinding Binding, string Site)> Waiters { get; } = [];
    }

    private sealed class Slot
    {
        public string? CallId { get; set; }
        public bool Settled { get; set; }
        public bool Failed { get; set; }
        public JsonElement Value { get; set; }
    }

    private sealed class Operation(SurfaceState state, string surfaceId, string sourceComponentId, string dataPath,
        bool userInitiated, JsonElement? metadata)
    {
        public SurfaceState State { get; } = state;
        public string SurfaceId { get; } = surfaceId;
        public string SourceComponentId { get; } = sourceComponentId;
        public string DataPath { get; } = dataPath;
        public bool UserInitiated { get; } = userInitiated;
        public JsonElement? Metadata { get; } = metadata;
        public bool Done { get; set; }
        public JsonElement? Call { get; set; }
        public Action<JsonElement>? Completed { get; set; }
        public string? EventName { get; set; }
        public JsonElement? UserMessage { get; set; }
        public List<(string Name, JsonElement Expression)> Entries { get; } = [];
        public List<(string Key, JsonElement Value)> Resolved { get; } = [];
        public Dictionary<string, Slot> Slots { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Outcome> LocalResults { get; } = new(StringComparer.Ordinal);
    }

}

internal enum EvaluationStatus
{
    Value,
    Pending,
    Failed
}

/// <summary>An expression kept current by a <see cref="FunctionCallRouter"/>.</summary>
/// <remarks>The value is a JSON null while an agent call is pending and after a failure, so read <see cref="IsPending"/> and <see cref="HasFailed"/> to tell a null value from no value.</remarks>
public sealed class FunctionBinding : IDisposable
{
    private readonly FunctionCallRouter _router;
    private EvaluationStatus _status;
    private JsonElement _value = FunctionValues.Null;

    internal FunctionBinding(FunctionCallRouter router, string surfaceId, string dataPath, JsonElement expression, bool managed)
    {
        _router = router;
        SurfaceId = surfaceId;
        DataPath = dataPath;
        Expression = expression;
        Managed = managed;
    }

    /// <summary>The current value, or a JSON null while the call is pending and after it fails.</summary>
    public JsonElement Value => _value;

    /// <summary>Whether the expression waits for an answer from the agent.</summary>
    public bool IsPending => _status == EvaluationStatus.Pending;

    /// <summary>Whether the expression failed. The failure was reported through <see cref="FunctionCallRouter.ErrorReported"/>.</summary>
    public bool HasFailed => _status == EvaluationStatus.Failed;

    /// <summary>Whether the expression passes as the condition of a check rule.</summary>
    /// <remarks>A condition that waits for the agent passes, so a pending answer does not block the user. A condition that failed does not pass.</remarks>
    public bool Passes => _status switch
    {
        EvaluationStatus.Pending => true,
        EvaluationStatus.Failed => false,
        _ => FunctionValues.ToBoolean(_value)
    };

    /// <summary>Raised when the value or the state of the binding changes.</summary>
    public event Action? Changed;

    /// <summary>Stops evaluating the binding and cancels the agent call it waits for, unless another live binding shares that call.</summary>
    public void Dispose()
    {
        if (Ended)
            return;
        End();
        _router.Release(this);
    }

    internal string SurfaceId { get; }
    internal string DataPath { get; }
    internal JsonElement Expression { get; }
    internal bool Managed { get; }
    internal bool Ended { get; private set; }
    internal Dictionary<string, string> FailedSites { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Reported { get; } = new(StringComparer.Ordinal);

    internal void End()
    {
        Ended = true;
        Changed = null;
    }

    internal void Update(EvaluationStatus next, JsonElement value)
    {
        var same = next == _status && _value.GetRawText() == value.GetRawText();
        _status = next;
        _value = value;
        if (!same)
            Changed?.Invoke();
    }
}
