using System.Collections;
using System.Text.Json;

namespace A2UI.Agent;

/// <summary>
/// An insertion-ordered string-keyed map. The Express compiler builds messages as plain trees of
/// <see cref="PlainMap"/>, <see cref="List{T}"/> of <see cref="object"/>, strings, booleans, <see cref="long"/>
/// and <see cref="double"/> values (or <see langword="null"/>), which keeps their ownership simple and
/// lets one value be copied wherever the notation reuses it.
/// </summary>
internal sealed class PlainMap : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly List<string> _keys = [];
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    internal int Count => _keys.Count;

    internal IReadOnlyList<string> Keys => _keys;

    internal object? this[string key]
    {
        get => _values[key];
        set
        {
            if (!_values.ContainsKey(key))
                _keys.Add(key);
            _values[key] = value;
        }
    }

    internal bool ContainsKey(string key) => _values.ContainsKey(key);

    internal bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);

    internal object? Get(string key) => _values.GetValueOrDefault(key);

    internal void Clear()
    {
        _keys.Clear();
        _values.Clear();
    }

    internal bool Remove(string key)
    {
        if (!_values.Remove(key))
            return false;
        _keys.Remove(key);
        return true;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        foreach (var key in _keys.ToArray())
            yield return new KeyValuePair<string, object?>(key, _values[key]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class PlainJson
{
    internal static bool IsNumber(object? value) => value is long or double or decimal;

    internal static double AsDouble(object? value) => value switch
    {
        long integer => integer,
        decimal number => (double)number,
        double number => number,
        _ => throw new InvalidOperationException("Not a number."),
    };

    internal static object? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new PlainMap();
                foreach (var property in element.EnumerateObject())
                    map[property.Name] = FromElement(property.Value);
                return map;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                    list.Add(FromElement(item));
                return list;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                // A number written with a fraction or an exponent is a double, as it is in Python, so that
                // decompiling keeps `3.0` apart from `3`.
                var text = element.GetRawText();
                if (text.AsSpan().IndexOfAny('.', 'e', 'E') < 0 && element.TryGetInt64(out var integer))
                    return integer;
                return element.GetDouble();
            default:
                return null;
        }
    }

    internal static object? Clone(object? value)
    {
        switch (value)
        {
            case PlainMap map:
                var copy = new PlainMap();
                foreach (var pair in map)
                    copy[pair.Key] = Clone(pair.Value);
                return copy;
            case List<object?> list:
                return list.Select(Clone).ToList();
            default:
                return value;
        }
    }

    internal static byte[] ToUtf8(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(writer, value);
        return stream.ToArray();
    }

    internal static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case PlainMap map:
                writer.WriteStartObject();
                foreach (var pair in map)
                {
                    writer.WritePropertyName(pair.Key);
                    Write(writer, pair.Value);
                }

                writer.WriteEndObject();
                break;
            case List<object?> list:
                writer.WriteStartArray();
                foreach (var item in list)
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException($"Unsupported value type {value.GetType().Name}.");
        }
    }

    /// <summary>Compares two plain values the way JSON equality does, so that <c>1</c> equals <c>1.0</c>.</summary>
    internal static bool DeepEquals(object? left, object? right)
    {
        if (IsNumber(left) && IsNumber(right))
            return AsDouble(left) == AsDouble(right);
        switch (left)
        {
            case PlainMap leftMap when right is PlainMap rightMap:
                return leftMap.Count == rightMap.Count && leftMap.All(pair =>
                    rightMap.TryGetValue(pair.Key, out var other) && DeepEquals(pair.Value, other));
            case List<object?> leftList when right is List<object?> rightList:
                return leftList.Count == rightList.Count && leftList.Zip(rightList).All(pair => DeepEquals(pair.First, pair.Second));
            default:
                return Equals(left, right);
        }
    }
}
