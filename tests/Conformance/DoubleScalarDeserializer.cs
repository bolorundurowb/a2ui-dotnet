using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

/// <summary>
/// Reads plain decimal scalars as doubles. YamlDotNet reads them as single-precision numbers when the target is an
/// object, which turns 123456.789 into 123456.79 before a case can see it.
/// </summary>
internal sealed partial class DoubleScalarDeserializer : INodeDeserializer
{
    [GeneratedRegex(@"^[-+]?(?:\d+\.\d*|\.\d+|\d+[eE][-+]?\d+)(?:[eE][-+]?\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Decimal();

    public bool Deserialize(IParser parser, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value, ObjectDeserializer rootDeserializer)
    {
        value = null;
        if (expectedType != typeof(object) || !parser.Accept<Scalar>(out var scalar) || scalar.Style != ScalarStyle.Plain ||
            !scalar.Tag.IsEmpty || !Decimal().IsMatch(scalar.Value) ||
            !double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return false;
        parser.MoveNext();
        value = number;
        return true;
    }
}
