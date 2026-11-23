using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace A2UI.Core;

/// <summary>
/// Parses the A2UI regular expression dialect, applies its static safety check, and translates it to a .NET pattern.
/// </summary>
/// <remarks>
/// The dialect is a portable subset of ECMAScript pattern syntax in Unicode mode with no flags. The same parsed form
/// feeds the safety check and the translation, so a pattern is never handed to the .NET engine as written.
/// </remarks>
internal static class RegexDialect
{
    private const int MaxPatternCodePoints = 256;
    private const int MaxCodePoint = 0x10FFFF;
    private const int Unbounded = int.MaxValue;

    internal static bool TryCompile(string pattern, out Regex? regex, out A2uiDiagnostic? diagnostic)
    {
        regex = null;
        var codePoints = ToCodePoints(pattern);
        if (codePoints.Length > MaxPatternCodePoints)
        {
            diagnostic = Failure("PATTERN_TOO_LONG", "The pattern is longer than 256 code points.");
            return false;
        }

        Node tree;
        try
        {
            tree = new Parser(codePoints).Parse();
        }
        catch (DialectException error)
        {
            diagnostic = Failure("UNSUPPORTED_SYNTAX", error.Message);
            return false;
        }

        diagnostic = Check(tree);
        if (diagnostic is not null)
            return false;

        var translated = new StringBuilder();
        Emit(tree, translated);
        try
        {
            regex = new Regex(translated.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException error)
        {
            diagnostic = Failure("UNSUPPORTED_SYNTAX", "The pattern could not be translated: " + error.Message);
            return false;
        }
        return true;
    }

    internal static int CodePointCount(string text)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                index++;
            count++;
        }
        return count;
    }

    private static A2uiDiagnostic Failure(string token, string detail) => new("ExpressionError", "", token + ": " + detail);

    private static int[] ToCodePoints(string text)
    {
        var result = new List<int>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                result.Add(char.ConvertToUtf32(text[index], text[index + 1]));
                index++;
            }
            else
                result.Add(text[index]);
        }
        return result.ToArray();
    }

    // ---- Character sets ----

    private sealed class CodePointSet
    {
        internal static readonly CodePointSet Empty = new([]);
        internal static readonly CodePointSet Digits = new([(0x30, 0x39)]);
        internal static readonly CodePointSet Word = new([(0x30, 0x39), (0x41, 0x5A), (0x5F, 0x5F), (0x61, 0x7A)]);
        internal static readonly CodePointSet Space = new([(0x09, 0x0D), (0x20, 0x20), (0xA0, 0xA0), (0x1680, 0x1680),
            (0x2000, 0x200A), (0x2028, 0x2029), (0x202F, 0x202F), (0x205F, 0x205F), (0x3000, 0x3000), (0xFEFF, 0xFEFF)]);
        internal static readonly CodePointSet Dot = new CodePointSet([(0x0A, 0x0A), (0x0D, 0x0D), (0x2028, 0x2029)]).Negate();

        internal CodePointSet((int Low, int High)[] ranges) => Ranges = Normalise(ranges);

        internal (int Low, int High)[] Ranges { get; }

        internal static CodePointSet Single(int codePoint) => new([(codePoint, codePoint)]);

        internal CodePointSet Union(CodePointSet other) => new([.. Ranges, .. other.Ranges]);

        internal CodePointSet Negate()
        {
            var result = new List<(int, int)>();
            var next = 0;
            foreach (var (low, high) in Ranges)
            {
                if (low > next)
                    result.Add((next, low - 1));
                next = high + 1;
            }
            if (next <= MaxCodePoint)
                result.Add((next, MaxCodePoint));
            return new(result.ToArray());
        }

        internal bool Overlaps(CodePointSet other)
        {
            foreach (var (low, high) in Ranges)
                foreach (var (otherLow, otherHigh) in other.Ranges)
                    if (low <= otherHigh && otherLow <= high)
                        return true;
            return false;
        }

        internal CodePointSet Intersect(int low, int high) =>
            new(Ranges.Where(range => range.Low <= high && range.High >= low)
                .Select(range => (Math.Max(range.Low, low), Math.Min(range.High, high))).ToArray());

        private static (int Low, int High)[] Normalise((int Low, int High)[] ranges)
        {
            var sorted = ranges.OrderBy(range => range.Low).ToList();
            var result = new List<(int Low, int High)>();
            foreach (var range in sorted)
            {
                if (result.Count > 0 && range.Low <= result[^1].High + 1)
                    result[^1] = (result[^1].Low, Math.Max(result[^1].High, range.High));
                else
                    result.Add(range);
            }
            return result.ToArray();
        }
    }

    // ---- Syntax tree ----

    private abstract record Node;

    private sealed record SetNode(CodePointSet Set) : Node;

    private sealed record AssertionNode(AssertionKind Kind) : Node;

    private sealed record LookaheadNode(Node Body, bool Negative) : Node;

    private sealed record GroupNode(Node Body) : Node;

    private sealed record SequenceNode(IReadOnlyList<Node> Items) : Node;

    private sealed record AlternationNode(IReadOnlyList<Node> Branches) : Node;

    private sealed record QuantifiedNode(Node Atom, int Minimum, int Maximum, bool Lazy) : Node;

    private enum AssertionKind { Start, End, WordBoundary, NotWordBoundary }

    private sealed class DialectException(string message) : Exception(message);

    // ---- Parser ----

    private sealed class Parser(int[] source)
    {
        private int _position;

        internal Node Parse()
        {
            var tree = ParseAlternation();
            if (_position < source.Length)
                throw new DialectException("An unmatched ')' is not allowed.");
            return tree;
        }

        private Node ParseAlternation()
        {
            var branches = new List<Node> { ParseSequence() };
            while (Take('|'))
                branches.Add(ParseSequence());
            return branches.Count == 1 ? branches[0] : new AlternationNode(branches);
        }

        private Node ParseSequence()
        {
            var items = new List<Node>();
            while (_position < source.Length && source[_position] is not ('|' or ')'))
                items.Add(ParseTerm());
            return items.Count == 1 ? items[0] : new SequenceNode(items);
        }

        private Node ParseTerm()
        {
            var atom = ParseAtom(out var quantifiable);
            if (_position >= source.Length || source[_position] is not ('*' or '+' or '?' or '{'))
                return atom;
            if (!quantifiable)
                throw new DialectException("An assertion cannot be repeated.");

            var (minimum, maximum) = ParseQuantifier();
            var lazy = Take('?');
            if (_position < source.Length && source[_position] is '*' or '+' or '?' or '{')
                throw new DialectException("Nothing to repeat.");
            return new QuantifiedNode(atom, minimum, maximum, lazy);
        }

        private (int Minimum, int Maximum) ParseQuantifier()
        {
            var symbol = source[_position++];
            switch (symbol)
            {
                case '*':
                    return (0, Unbounded);
                case '+':
                    return (1, Unbounded);
                case '?':
                    return (0, 1);
            }

            var minimum = ReadCount();
            var maximum = minimum;
            if (Take(','))
                maximum = _position < source.Length && source[_position] == '}' ? Unbounded : ReadCount();
            if (!Take('}'))
                throw new DialectException("Incomplete quantifier.");
            if (maximum < minimum)
                throw new DialectException("Quantifier bounds are out of order.");
            return (minimum, maximum);
        }

        private int ReadCount()
        {
            long value = 0;
            var start = _position;
            while (_position < source.Length && source[_position] is >= '0' and <= '9')
            {
                value = value * 10 + (source[_position++] - '0');
                if (value >= Unbounded)
                    throw new DialectException("A quantifier bound is too large.");
            }
            if (_position == start)
                throw new DialectException("A '{' must start a quantifier.");
            return (int)value;
        }

        private Node ParseAtom(out bool quantifiable)
        {
            quantifiable = true;
            var symbol = source[_position++];
            switch (symbol)
            {
                case '(':
                    return ParseGroup(out quantifiable);
                case '[':
                    return new SetNode(ParseClass());
                case '.':
                    return new SetNode(CodePointSet.Dot);
                case '^':
                    quantifiable = false;
                    return new AssertionNode(AssertionKind.Start);
                case '$':
                    quantifiable = false;
                    return new AssertionNode(AssertionKind.End);
                case '\\':
                    return ParseEscape(out quantifiable);
                case '*' or '+' or '?':
                    throw new DialectException("Nothing to repeat.");
                case '{' or '}' or ']':
                    throw new DialectException("A bare '" + (char)symbol + "' must be escaped.");
                default:
                    return new SetNode(CodePointSet.Single(symbol));
            }
        }

        private Node ParseGroup(out bool quantifiable)
        {
            quantifiable = true;
            var lookahead = false;
            var negative = false;
            if (Take('?'))
            {
                if (Take(':'))
                {
                }
                else if (Take('='))
                    lookahead = true;
                else if (Take('!'))
                {
                    lookahead = true;
                    negative = true;
                }
                else
                    throw new DialectException("Lookbehind, named groups and inline flags are not allowed.");
            }

            var body = ParseAlternation();
            if (!Take(')'))
                throw new DialectException("A group is not closed.");
            if (!lookahead)
                return new GroupNode(body);
            quantifiable = false;
            return new LookaheadNode(body, negative);
        }

        private Node ParseEscape(out bool quantifiable)
        {
            quantifiable = true;
            if (_position >= source.Length)
                throw new DialectException("A pattern cannot end with a backslash.");
            var symbol = source[_position];
            switch (symbol)
            {
                case 'b':
                    _position++;
                    quantifiable = false;
                    return new AssertionNode(AssertionKind.WordBoundary);
                case 'B':
                    _position++;
                    quantifiable = false;
                    return new AssertionNode(AssertionKind.NotWordBoundary);
                default:
                    return new SetNode(ReadEscape(inClass: false));
            }
        }

        // Reads the escape after its backslash and returns the set of code points it matches.
        private CodePointSet ReadEscape(bool inClass)
        {
            var symbol = source[_position++];
            switch (symbol)
            {
                case 'd':
                    return CodePointSet.Digits;
                case 'D':
                    return CodePointSet.Digits.Negate();
                case 'w':
                    return CodePointSet.Word;
                case 'W':
                    return CodePointSet.Word.Negate();
                case 's':
                    return CodePointSet.Space;
                case 'S':
                    return CodePointSet.Space.Negate();
                case 't':
                    return CodePointSet.Single('\t');
                case 'n':
                    return CodePointSet.Single('\n');
                case 'v':
                    return CodePointSet.Single('\v');
                case 'f':
                    return CodePointSet.Single('\f');
                case 'r':
                    return CodePointSet.Single('\r');
                case 'b' when inClass:
                    return CodePointSet.Single('\b');
                case '-' when inClass:
                    return CodePointSet.Single('-');
                case '0':
                    if (_position < source.Length && source[_position] is >= '0' and <= '9')
                        throw new DialectException("Octal escapes are not allowed.");
                    return CodePointSet.Single(0);
                case >= '1' and <= '9':
                    throw new DialectException("Backreferences are not allowed.");
                case 'k':
                    throw new DialectException("Named backreferences are not allowed.");
                case 'p' or 'P':
                    throw new DialectException("Unicode property escapes are not allowed.");
                case 'c':
                    throw new DialectException("Control letter escapes are not allowed.");
                case 'x':
                    return CodePointSet.Single(ReadHex(2));
                case 'u':
                    return CodePointSet.Single(ReadUnicodeEscape());
                case '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|' or '/':
                    return CodePointSet.Single(symbol);
                default:
                    throw new DialectException("The escape '\\" + char.ConvertFromUtf32(symbol) + "' is not allowed.");
            }
        }

        private int ReadUnicodeEscape()
        {
            if (Take('{'))
            {
                long value = 0;
                var digits = 0;
                while (_position < source.Length && IsHex(source[_position]))
                {
                    value = value * 16 + Convert.ToInt32(char.ConvertFromUtf32(source[_position++]), 16);
                    digits++;
                    if (value > MaxCodePoint)
                        throw new DialectException("A code point escape is out of range.");
                }
                if (digits == 0 || !Take('}'))
                    throw new DialectException("A code point escape is malformed.");
                return (int)value;
            }

            var first = ReadHex(4);
            if (first is >= 0xD800 and <= 0xDBFF && _position + 1 < source.Length &&
                source[_position] == '\\' && source[_position + 1] == 'u')
            {
                var saved = _position;
                _position += 2;
                if (_position + 4 <= source.Length && Enumerable.Range(_position, 4).All(index => IsHex(source[index])))
                {
                    var second = ReadHex(4);
                    if (second is >= 0xDC00 and <= 0xDFFF)
                        return char.ConvertToUtf32((char)first, (char)second);
                }
                _position = saved;
            }
            return first;
        }

        private int ReadHex(int count)
        {
            if (_position + count > source.Length || !Enumerable.Range(_position, count).All(index => IsHex(source[index])))
                throw new DialectException("A hexadecimal escape is malformed.");
            var value = 0;
            for (var index = 0; index < count; index++)
                value = value * 16 + Convert.ToInt32(((char)source[_position++]).ToString(), 16);
            return value;
        }

        private static bool IsHex(int symbol) => symbol is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

        private CodePointSet ParseClass()
        {
            var negate = Take('^');
            var set = CodePointSet.Empty;
            while (true)
            {
                if (_position >= source.Length)
                    throw new DialectException("A character class is not closed.");
                if (Take(']'))
                    break;

                var first = ReadClassAtom(out var firstIsSingle, out var firstCodePoint);
                if (_position + 1 < source.Length && source[_position] == '-' && source[_position + 1] != ']')
                {
                    _position++;
                    var second = ReadClassAtom(out var secondIsSingle, out var secondCodePoint);
                    if (!firstIsSingle || !secondIsSingle)
                        throw new DialectException("A class escape cannot be a range endpoint.");
                    if (firstCodePoint > secondCodePoint)
                        throw new DialectException("A class range is out of order.");
                    first = new CodePointSet([(firstCodePoint, secondCodePoint)]);
                }
                set = set.Union(first);
            }
            return negate ? set.Negate() : set;
        }

        private CodePointSet ReadClassAtom(out bool single, out int codePoint)
        {
            var symbol = source[_position++];
            if (symbol == '\\')
            {
                if (_position >= source.Length)
                    throw new DialectException("A pattern cannot end with a backslash.");
                var escape = source[_position];
                var set = ReadEscape(inClass: true);
                single = escape is not ('d' or 'D' or 'w' or 'W' or 's' or 'S');
                codePoint = single ? set.Ranges[0].Low : 0;
                return set;
            }
            single = true;
            codePoint = symbol;
            return CodePointSet.Single(symbol);
        }

        private bool Take(int symbol)
        {
            if (_position >= source.Length || source[_position] != symbol)
                return false;
            _position++;
            return true;
        }
    }

    // ---- Safety check ----

    private static A2uiDiagnostic? Check(Node tree)
    {
        if (FindNestedQuantifier(tree))
            return Failure("NESTED_QUANTIFIER", "A repeated subexpression contains an unbounded quantifier.");
        if (FindOverlappingAlternation(tree))
            return Failure("OVERLAPPING_ALTERNATION", "A repeated alternation has branches that can start with the same character.");
        if (FindAdjacentOverlap(tree))
            return Failure("ADJACENT_OVERLAP", "Adjacent unbounded repeats can match the same characters.");
        return null;
    }

    private static IEnumerable<Node> Children(Node node) => node switch
    {
        LookaheadNode lookahead => [lookahead.Body],
        GroupNode group => [group.Body],
        SequenceNode sequence => sequence.Items,
        AlternationNode alternation => alternation.Branches,
        QuantifiedNode quantified => [quantified.Atom],
        _ => []
    };

    private static IEnumerable<Node> Descendants(Node node)
    {
        yield return node;
        foreach (var child in Children(node))
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }

    private static bool FindNestedQuantifier(Node tree) =>
        Descendants(tree).OfType<QuantifiedNode>().Any(quantified => quantified.Maximum > 1 &&
            Descendants(quantified.Atom).OfType<QuantifiedNode>().Any(inner => inner.Maximum == Unbounded));

    private static bool FindOverlappingAlternation(Node tree)
    {
        foreach (var quantified in Descendants(tree).OfType<QuantifiedNode>().Where(item => item.Maximum == Unbounded))
        {
            if (Unwrap(quantified.Atom) is not AlternationNode alternation)
                continue;
            var starts = alternation.Branches.Select(branch => First(branch).Set).ToList();
            for (var left = 0; left < starts.Count; left++)
                for (var right = left + 1; right < starts.Count; right++)
                    if (starts[left].Overlaps(starts[right]))
                        return true;
        }
        return false;
    }

    private static bool FindAdjacentOverlap(Node tree)
    {
        foreach (var sequence in Descendants(tree).OfType<SequenceNode>())
        {
            var window = new List<CodePointSet>();
            foreach (var item in sequence.Items)
            {
                var unbounded = AsUnboundedAtom(item);
                if (unbounded is not null)
                {
                    var characters = Characters(unbounded);
                    if (window.Any(previous => previous.Overlaps(characters)))
                        return true;
                    window.Add(characters);
                }

                if (!IsNullable(item))
                {
                    window.Clear();
                    if (unbounded is not null)
                        window.Add(Characters(unbounded));
                }
            }
        }
        return false;
    }

    // A group whose only content is one quantified atom counts as that atom.
    private static QuantifiedNode? AsUnboundedAtom(Node node) =>
        Unwrap(node) is QuantifiedNode { Maximum: Unbounded } quantified ? quantified : null;

    private static Node Unwrap(Node node)
    {
        while (true)
        {
            switch (node)
            {
                case GroupNode group:
                    node = group.Body;
                    break;
                case SequenceNode { Items.Count: 1 } sequence:
                    node = sequence.Items[0];
                    break;
                default:
                    return node;
            }
        }
    }

    private static bool IsNullable(Node node) => node switch
    {
        SetNode => false,
        AssertionNode or LookaheadNode => true,
        GroupNode group => IsNullable(group.Body),
        SequenceNode sequence => sequence.Items.All(IsNullable),
        AlternationNode alternation => alternation.Branches.Any(IsNullable),
        QuantifiedNode quantified => quantified.Minimum == 0 || IsNullable(quantified.Atom),
        _ => false
    };

    private static (CodePointSet Set, bool Nullable) First(Node node)
    {
        switch (node)
        {
            case SetNode set:
                return (set.Set, false);
            case GroupNode group:
                return First(group.Body);
            case QuantifiedNode quantified:
                var atom = First(quantified.Atom);
                return (atom.Set, quantified.Minimum == 0 || atom.Nullable);
            case AlternationNode alternation:
                var union = CodePointSet.Empty;
                var nullable = false;
                foreach (var branch in alternation.Branches)
                {
                    var first = First(branch);
                    union = union.Union(first.Set);
                    nullable |= first.Nullable;
                }
                return (union, nullable);
            case SequenceNode sequence:
                var collected = CodePointSet.Empty;
                foreach (var item in sequence.Items)
                {
                    var first = First(item);
                    collected = collected.Union(first.Set);
                    if (!first.Nullable)
                        return (collected, false);
                }
                return (collected, true);
            default:
                return (CodePointSet.Empty, true);
        }
    }

    private static CodePointSet Characters(Node node)
    {
        var union = CodePointSet.Empty;
        foreach (var descendant in Descendants(node))
            if (descendant is SetNode set)
                union = union.Union(set.Set);
        return union;
    }

    // ---- Translation to .NET ----

    private const string WordClass = "[0-9A-Za-z_]";

    private static void Emit(Node node, StringBuilder output)
    {
        switch (node)
        {
            case SetNode set:
                EmitSet(set.Set, output);
                break;
            case AssertionNode assertion:
                output.Append(assertion.Kind switch
                {
                    AssertionKind.Start => @"\A",
                    AssertionKind.End => @"\z",
                    AssertionKind.WordBoundary =>
                        $"(?:(?<={WordClass})(?!{WordClass})|(?<!{WordClass})(?={WordClass}))",
                    _ => $"(?:(?<={WordClass})(?={WordClass})|(?<!{WordClass})(?!{WordClass}))"
                });
                break;
            case LookaheadNode lookahead:
                output.Append(lookahead.Negative ? "(?!" : "(?=");
                Emit(lookahead.Body, output);
                output.Append(')');
                break;
            case GroupNode group:
                output.Append("(?:");
                Emit(group.Body, output);
                output.Append(')');
                break;
            case SequenceNode sequence:
                foreach (var item in sequence.Items)
                    Emit(item, output);
                break;
            case AlternationNode alternation:
                for (var index = 0; index < alternation.Branches.Count; index++)
                {
                    if (index > 0)
                        output.Append('|');
                    Emit(alternation.Branches[index], output);
                }
                break;
            case QuantifiedNode quantified:
                Emit(quantified.Atom, output);
                output.Append((quantified.Minimum, quantified.Maximum) switch
                {
                    (0, Unbounded) => "*",
                    (1, Unbounded) => "+",
                    (0, 1) => "?",
                    (var minimum, Unbounded) => "{" + minimum.ToString(CultureInfo.InvariantCulture) + ",}",
                    (var minimum, var maximum) when minimum == maximum => "{" + minimum.ToString(CultureInfo.InvariantCulture) + "}",
                    (var minimum, var maximum) =>
                        "{" + minimum.ToString(CultureInfo.InvariantCulture) + "," + maximum.ToString(CultureInfo.InvariantCulture) + "}"
                });
                if (quantified.Lazy)
                    output.Append('?');
                break;
        }
    }

    // Emits a set as one atom. The .NET engine works on UTF-16 code units, so a code point above U+FFFF is matched as a
    // surrogate pair, and a lone surrogate matches only when it is not half of a pair, as in Unicode mode.
    private static void EmitSet(CodePointSet set, StringBuilder output)
    {
        var alternatives = new List<string>();
        foreach (var (low, high) in set.Intersect(0x10000, MaxCodePoint).Ranges)
            alternatives.AddRange(SurrogatePairs(low, high));

        var loneHigh = set.Intersect(0xD800, 0xDBFF);
        if (loneHigh.Ranges.Length > 0)
            alternatives.Add(Class(loneHigh) + @"(?![\uDC00-\uDFFF])");
        var loneLow = set.Intersect(0xDC00, 0xDFFF);
        if (loneLow.Ranges.Length > 0)
            alternatives.Add(@"(?<![\uD800-\uDBFF])" + Class(loneLow));

        var basic = set.Intersect(0, 0xD7FF).Union(set.Intersect(0xE000, 0xFFFF));
        string? basicClass = basic.Ranges.Length > 0 ? Class(basic) : null;
        if (basicClass is not null)
            alternatives.Add(basicClass);

        if (alternatives.Count == 0)
            output.Append("(?!)");
        else if (alternatives.Count == 1 && ReferenceEquals(alternatives[0], basicClass))
            output.Append(basicClass);
        else
            output.Append("(?:").Append(string.Join("|", alternatives)).Append(')');
    }

    private static IEnumerable<string> SurrogatePairs(int low, int high)
    {
        var (lowHigh, lowLow) = Split(low);
        var (highHigh, highLow) = Split(high);
        if (lowHigh == highHigh)
        {
            yield return Unit(lowHigh) + "[" + Unit(lowLow) + "-" + Unit(highLow) + "]";
            yield break;
        }

        var firstFull = lowLow == 0xDC00;
        var lastFull = highLow == 0xDFFF;
        var middleStart = firstFull ? lowHigh : lowHigh + 1;
        var middleEnd = lastFull ? highHigh : highHigh - 1;
        if (!firstFull)
            yield return Unit(lowHigh) + "[" + Unit(lowLow) + @"-\uDFFF]";
        if (middleStart <= middleEnd)
            yield return "[" + Unit(middleStart) + "-" + Unit(middleEnd) + @"][\uDC00-\uDFFF]";
        if (!lastFull)
            yield return Unit(highHigh) + @"[\uDC00-" + Unit(highLow) + "]";
    }

    private static (int High, int Low) Split(int codePoint)
    {
        var offset = codePoint - 0x10000;
        return (0xD800 + (offset >> 10), 0xDC00 + (offset & 0x3FF));
    }

    private static string Unit(int codeUnit) => @"\u" + codeUnit.ToString("X4", CultureInfo.InvariantCulture);

    private static string Class(CodePointSet set)
    {
        var text = new StringBuilder("[");
        foreach (var (low, high) in set.Ranges)
        {
            text.Append(Unit(low));
            if (high > low)
                text.Append('-').Append(Unit(high));
        }
        return text.Append(']').ToString();
    }
}
