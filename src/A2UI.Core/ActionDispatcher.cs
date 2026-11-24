using System.Globalization;
using System.Text.Json;

namespace A2UI.Core;

/// <summary>The outcome of invoking an action.</summary>
/// <param name="Message">The <c>action</c> message to send to the agent, or null when the action sends nothing.</param>
/// <param name="Error">The failure to report, or null when the action succeeded. A failed action sends no message.</param>
public sealed record ActionResult(WireMessage? Message, RendererError? Error);

/// <summary>Turns a component action into the renderer-to-agent <c>action</c> message, or runs it locally.</summary>
/// <remarks>
/// An <c>event</c> action resolves its <c>context</c> entries in declared order and then its <c>userMessage</c> against the
/// data model at the time of the call, and produces one <c>action</c> message. A <c>functionCall</c> action runs the
/// function locally and produces no message. Resolution stops at the first failure, so a failed action sends nothing.
/// Editing a bound value never produces an action. Only an invocation does.
/// </remarks>
public sealed class ActionDispatcher
{
    private readonly FunctionEvaluator _functions;
    private readonly TimeProvider _time;

    /// <summary>Creates a dispatcher.</summary>
    /// <param name="functions">The evaluator for function calls in actions and in action context.</param>
    /// <param name="timeProvider">The clock for the action timestamp. Defaults to <see cref="TimeProvider.System"/>.</param>
    public ActionDispatcher(FunctionEvaluator functions, TimeProvider? timeProvider = null)
    {
        _functions = functions ?? throw new ArgumentNullException(nameof(functions));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Invokes an action defined on a component.</summary>
    /// <param name="surface">The committed surface that owns the component. The action reads its data at this moment.</param>
    /// <param name="sourceComponentId">The identifier of the component that defines the action.</param>
    /// <param name="dataPath">The absolute pointer to the data scope of the component. Use "/" outside a collection.</param>
    /// <param name="action">The action definition, an object with one <c>event</c> or <c>functionCall</c> member.</param>
    /// <param name="userInitiated">Whether the invocation follows a user gesture. Functions that require user activation fail without it.</param>
    /// <param name="metadata">Optional <c>action.metadata</c>, an object whose <c>extensions</c> keys are identifiers.</param>
    /// <returns>The message to send, or the error to report.</returns>
    public ActionResult Dispatch(SurfaceSnapshot surface, string sourceComponentId, string dataPath, JsonElement action,
        bool userInitiated = true, JsonElement? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(sourceComponentId);
        ArgumentNullException.ThrowIfNull(dataPath);

        if (action.ValueKind != JsonValueKind.Object || action.EnumerateObject().Count() != 1)
            return Invalid(surface, "Expected an action with one event or functionCall.");
        if (action.TryGetProperty("functionCall", out var call))
            return RunFunction(surface, dataPath, call, userInitiated);
        if (!action.TryGetProperty("event", out var definition) || definition.ValueKind != JsonValueKind.Object)
            return Invalid(surface, "Expected an action with one event or functionCall.");

        if (!definition.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            name.GetString()!.Length == 0)
            return Invalid(surface, "The action event needs a name.");

        var context = new List<(string Key, JsonElement Value)>();
        if (definition.TryGetProperty("context", out var entries))
        {
            if (entries.ValueKind != JsonValueKind.Object)
                return Invalid(surface, "The action event context must be an object.");
            foreach (var entry in entries.EnumerateObject())
            {
                if (!_functions.TryResolveValue(surface, dataPath, entry.Value, userInitiated, out var resolved, out var problem))
                    return Failed(surface, problem!, $"Action event context entry '{entry.Name}' is invalid: ");
                context.Add((entry.Name, resolved));
            }
        }

        string? userMessage = null;
        if (definition.TryGetProperty("userMessage", out var template))
        {
            if (!_functions.TryResolveValue(surface, dataPath, template, userInitiated, out var resolved, out var problem))
                return Failed(surface, problem!, "Action event userMessage is invalid: ");
            if (resolved.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                return Invalid(surface, "Action event userMessage is invalid: expected a string.");
            if (resolved.ValueKind == JsonValueKind.String && resolved.GetString()!.Length > 0)
                userMessage = resolved.GetString();
        }

        var bytes = Write(surface.Id, sourceComponentId, name.GetString()!, userMessage, context, metadata);
        if (!WireMessage.TryDecode(bytes, WireDirection.RendererToAgent, out var message, out var rejection))
            return new(null, RendererError.FromDiagnostic(rejection!, surface.Id));
        return new(message, null);
    }

    private ActionResult RunFunction(SurfaceSnapshot surface, string dataPath, JsonElement call, bool userInitiated) =>
        _functions.TryEvaluate(surface, dataPath, call, userInitiated, out _, out var problem)
            ? new(null, null)
            : Failed(surface, problem!, "");

    private byte[] Write(string surfaceId, string sourceComponentId, string name, string? userMessage,
        List<(string Key, JsonElement Value)> context, JsonElement? metadata)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("version", WireMessage.Version);
            writer.WriteStartObject("action");
            writer.WriteString("name", name);
            if (userMessage is not null)
                writer.WriteString("userMessage", userMessage);
            writer.WriteString("surfaceId", surfaceId);
            writer.WriteString("sourceComponentId", sourceComponentId);
            writer.WriteString("timestamp", _time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WriteStartObject("context");
            foreach (var (key, value) in context)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
            if (metadata is { } extra)
            {
                writer.WritePropertyName("metadata");
                extra.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    // A function that cannot run is a refusal. Both the catalog and the expression errors keep their own wire codes.
    private static ActionResult Failed(SurfaceSnapshot surface, A2uiDiagnostic problem, string prefix)
    {
        var diagnostic = problem.Code == "CatalogError"
            ? problem with { Message = prefix + problem.Message }
            : new A2uiDiagnostic("ExpressionError", problem.Path, prefix + problem.Message);
        return new(null, RendererError.FromDiagnostic(diagnostic, surface.Id));
    }

    private static ActionResult Invalid(SurfaceSnapshot surface, string message) =>
        new(null, RendererError.FromDiagnostic(new("invalidAction", "", message), surface.Id));
}
