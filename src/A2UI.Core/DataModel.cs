using System.Globalization;
using System.Text.Json;

namespace A2UI.Core;

/// <summary>
/// A JSON data model with path reads, writes, deletions and path observers. It follows the A2UI Core blueprint for the
/// <c>DataModel</c> type.
/// </summary>
/// <remarks>
/// <para>
/// Paths are absolute JSON Pointers (RFC 6901). The empty path and <c>/</c> select the root. Unlike the wire
/// <c>updateDataModel</c> path, repeated and trailing slashes collapse, so <c>/a/b/</c> and <c>//a///b</c> both name <c>/a/b</c>.
/// A path that contains <c>__proto__</c>, <c>constructor</c> or <c>prototype</c> is rejected on every operation.
/// </para>
/// <para>
/// A write creates the missing containers on its way. The next segment decides the kind: digits with no leading zero create
/// a list, anything else creates an object. Writing past the end of a list pads the gap with null, up to index 10,000.
/// A write cannot continue through a string, number or boolean, but it replaces an explicit null with a container.
/// </para>
/// <para>
/// The model owns the values it stores. It copies each written value, so a later change or disposal of the caller's
/// document does not alter the model. The model is not thread-safe. Callers serialise access, including from observer callbacks.
/// </para>
/// </remarks>
public sealed class DataModel : IDisposable
{
    private static readonly JsonElement EmptyRoot = ProtocolSchemas.Parse("{}");
    private static readonly JsonElement NullValue = ProtocolSchemas.Parse("null");

    private readonly List<DataSubscription> _subscriptions = [];
    private JsonElement _root;
    private bool _disposed;

    /// <summary>Creates a model that holds an empty object.</summary>
    public DataModel() => _root = EmptyRoot;

    /// <summary>Creates a model that holds a copy of an initial value.</summary>
    /// <param name="initial">The initial root value. An undefined or null value starts the model as an empty object.</param>
    public DataModel(JsonElement initial) =>
        _root = initial.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? EmptyRoot : initial.Clone();

    /// <summary>The current root value. It is an immutable snapshot that later writes do not change.</summary>
    public JsonElement Value => _root;

    /// <summary>Reads the value at a path.</summary>
    /// <param name="path">The absolute JSON Pointer.</param>
    /// <param name="value">The value, which may be an explicit null. It is an undefined element when the path holds no value.</param>
    /// <returns>A diagnostic when the path is invalid or forbidden, otherwise null. A missing value is not an error.</returns>
    /// <remarks>An out-of-range, negative, non-numeric or leading-zero list index reads as no value.</remarks>
    public A2uiDiagnostic? Get(string path, out JsonElement value)
    {
        value = default;
        if (Parse(path, out var segments) is { } problem)
            return problem;

        DataPointer.TryGet(_root, segments, out value);
        return null;
    }

    /// <summary>Writes a value at a path and notifies the observers whose value changed.</summary>
    /// <param name="path">The absolute JSON Pointer.</param>
    /// <param name="value">The value to store. Null deletes the path, as in <see cref="Delete"/>.</param>
    /// <returns>A diagnostic when the write is refused, otherwise null. A refused write changes nothing and notifies no one.</returns>
    /// <remarks>
    /// The write is refused for an invalid or forbidden path, for a path that runs through a string, number or boolean, for a
    /// list segment that is not a plain index, for a list index above 10,000, and for a value nested more than 50 levels deep.
    /// Writing the root replaces the whole model.
    /// </remarks>
    public A2uiDiagnostic? Set(string path, JsonElement value)
    {
        if (Parse(path, out var segments) is { } problem)
            return problem;

        var deleting = value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
        if (!deleting && (segments.Length > WireJson.RecursionLimit + 1 || WireJson.ExceedsContainerDepth(value, segments.Length)))
            return new("dataModelDepth", path, "Global recursion limit exceeded.");
        if (!DataPointer.TryUpdate(_root, segments, deleting ? NullValue : value, out var updated, out var failure))
            return Describe(failure, path);

        _root = updated;
        Notify(segments);
        return null;
    }

    /// <summary>Removes the value at a path and notifies the observers whose value changed.</summary>
    /// <param name="path">The absolute JSON Pointer.</param>
    /// <returns>A diagnostic when the path is invalid or forbidden, otherwise null.</returns>
    /// <remarks>
    /// An object member is removed. A list element becomes null, so the list keeps its length. Deleting the root resets the
    /// model to an empty object. Deleting a path that holds no value does nothing. It creates no containers and notifies no one.
    /// </remarks>
    public A2uiDiagnostic? Delete(string path) => Set(path, NullValue);

    /// <summary>Observes the value at a path.</summary>
    /// <param name="path">The absolute JSON Pointer. The path does not need to exist yet.</param>
    /// <param name="observer">
    /// Called with the new value after a write changes the value at the path, or an undefined element when the value is gone.
    /// A write at the path, at one of its ancestors or at one of its descendants can change the value. A write at an unrelated
    /// path, or at a sibling whose name only shares a prefix, cannot. A write that leaves the value equal does not call it.
    /// </param>
    /// <param name="subscription">The observer registration, or null when the path is rejected.</param>
    /// <returns>A diagnostic when the path is invalid or forbidden, otherwise null.</returns>
    /// <remarks>
    /// Observers run synchronously, in registration order, after the write has committed and every affected
    /// <see cref="DataSubscription.Value"/> has been refreshed. An exception from an observer reaches the writer and skips the
    /// remaining observers for that write.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The model has been disposed.</exception>
    public A2uiDiagnostic? Observe(string path, Action<JsonElement> observer, out DataSubscription? subscription)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        subscription = null;
        if (Parse(path, out var segments) is { } problem)
            return problem;

        DataPointer.TryGet(_root, segments, out var current);
        subscription = new DataSubscription(this, segments, observer, current);
        _subscriptions.Add(subscription);
        return null;
    }

    /// <summary>Detaches every observer. The model stays readable and writable, and further writes notify no one.</summary>
    public void Dispose()
    {
        _disposed = true;
        foreach (var subscription in _subscriptions)
            subscription.Detach();
        _subscriptions.Clear();
    }

    internal void Remove(DataSubscription subscription) => _subscriptions.Remove(subscription);

    private void Notify(string[] written)
    {
        List<DataSubscription>? changed = null;
        foreach (var subscription in _subscriptions)
        {
            if (!IsRelated(subscription.Segments, written))
                continue;

            DataPointer.TryGet(_root, subscription.Segments, out var current);
            if (Equal(subscription.Value, current))
                continue;

            subscription.Value = current;
            (changed ??= []).Add(subscription);
        }

        if (changed is null)
            return;
        foreach (var subscription in changed)
            if (subscription.Attached)
                subscription.Observer(subscription.Value);
    }

    // Two paths are related when one is the other or an ancestor of it. Segments are compared whole, so /foo and /foobar are not related.
    private static bool IsRelated(string[] observed, string[] written)
    {
        var shared = Math.Min(observed.Length, written.Length);
        for (var i = 0; i < shared; i++)
            if (!string.Equals(observed[i], written[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    private static A2uiDiagnostic? Parse(string path, out string[] segments)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!DataPointer.TryParse(path, dropEmptySegments: true, out segments))
            return new("invalidDataPath", path, "The path is not a valid JSON Pointer or is nested too deeply.");

        foreach (var segment in segments)
            if (segment is "__proto__" or "constructor" or "prototype")
                return new("forbiddenPathKey", path, $"Forbidden path segment '{segment}'.");
        return null;
    }

    private static A2uiDiagnostic Describe(DataWriteFailure failure, string path) => failure.Kind switch
    {
        DataWriteFailureKind.ThroughPrimitive => new("invalidDataPath", path,
            $"Cannot set path: segment '{failure.Segment}' would run through a value that is not an object or list."),
        DataWriteFailureKind.NonNumericSegment => new("invalidDataPath", path,
            $"Cannot set path: '{failure.Segment}' is a non-numeric segment for a list. An index is digits only, with no leading zero."),
        DataWriteFailureKind.IndexTooLarge => new("invalidDataPath", path,
            $"Cannot set path: list index '{failure.Segment}' is above the maximum of {DataPointer.MaxListIndex.ToString(CultureInfo.InvariantCulture)}."),
        _ => new("invalidDataPath", path, "Cannot set path: the resulting value cannot be represented.")
    };

    private static bool Equal(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;
        switch (left.ValueKind)
        {
            case JsonValueKind.String:
                return left.ValueEquals(right.GetString());
            case JsonValueKind.Number:
                return left.TryGetDecimal(out var leftNumber) && right.TryGetDecimal(out var rightNumber)
                    ? leftNumber == rightNumber
                    : left.GetDouble() == right.GetDouble();
            case JsonValueKind.Array:
                if (left.GetArrayLength() != right.GetArrayLength())
                    return false;
                using (var rightItems = right.EnumerateArray().GetEnumerator())
                {
                    foreach (var item in left.EnumerateArray())
                        if (!rightItems.MoveNext() || !Equal(item, rightItems.Current))
                            return false;
                }
                return true;
            case JsonValueKind.Object:
                var count = 0;
                foreach (var member in left.EnumerateObject())
                {
                    count++;
                    if (!right.TryGetProperty(member.Name, out var other) || !Equal(member.Value, other))
                        return false;
                }
                return count == right.EnumerateObject().Count();
            default:
                return true;
        }
    }
}

/// <summary>The registration of one observer on a <see cref="DataModel"/> path. Dispose it to stop observing.</summary>
public sealed class DataSubscription : IDisposable
{
    private readonly DataModel _model;

    internal DataSubscription(DataModel model, string[] segments, Action<JsonElement> observer, JsonElement value)
    {
        _model = model;
        Segments = segments;
        Observer = observer;
        Value = value;
        Attached = true;
    }

    /// <summary>The latest value at the observed path, or an undefined element when the path holds no value.</summary>
    public JsonElement Value { get; internal set; }

    internal string[] Segments { get; }

    internal Action<JsonElement> Observer { get; }

    internal bool Attached { get; private set; }

    /// <summary>Stops the observer. Disposing more than once has no further effect.</summary>
    public void Dispose()
    {
        if (!Attached)
            return;
        Detach();
        _model.Remove(this);
    }

    internal void Detach() => Attached = false;
}
