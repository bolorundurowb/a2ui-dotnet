using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace A2UI.Core;

/// <summary>Everything a Basic Catalog function can read besides its arguments.</summary>
/// <param name="DataModel">The root data model of the surface.</param>
/// <param name="Scope">The current collection item, or an undefined value at the root.</param>
/// <param name="CatalogId">The catalog the enclosing call runs in, inherited by calls inside formatString templates.</param>
/// <param name="Invoke">Runs a function call that appears inside a formatString template.</param>
/// <param name="ReportPartError">Reports an interpolation failure while preserving the rest of the text.</param>
/// <param name="OpenUrl">The host handler for openUrl, or null when the host opens no URLs.</param>
internal sealed record FunctionContext(JsonElement DataModel, JsonElement Scope, string? CatalogId,
    FunctionInvoker Invoke, Action<Uri>? OpenUrl, Action<A2uiDiagnostic>? ReportPartError = null);

/// <summary>Runs one function call with already resolved arguments.</summary>
internal delegate bool FunctionInvoker(string? catalogId, string name, IReadOnlyDictionary<string, JsonElement> args,
    out JsonElement value, out A2uiDiagnostic? diagnostic);

/// <summary>The renderer-side functions of the A2UI Basic Catalog.</summary>
/// <remarks>
/// The functions format numbers, currencies, dates and plural forms with en-US conventions. Locale selection is not
/// implemented. Dates are read and written as ISO 8601 text without time zone conversion, except for the ISO format.
/// </remarks>
public static class BasicFunctions
{
    private static readonly string[] FullMonths =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private static readonly string[] FullDays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    private static readonly Dictionary<string, string> CurrencySymbols = new(StringComparer.Ordinal)
    {
        ["USD"] = "$", ["EUR"] = "€", ["GBP"] = "£", ["JPY"] = "¥", ["INR"] = "₹", ["KRW"] = "₩", ["CNY"] = "CN¥",
        ["CAD"] = "CA$", ["AUD"] = "A$", ["NZD"] = "NZ$", ["HKD"] = "HK$", ["MXN"] = "MX$", ["BRL"] = "R$",
        ["ILS"] = "₪", ["VND"] = "₫", ["TWD"] = "NT$"
    };

    private static readonly HashSet<string> NoMinorUnits = new(StringComparer.Ordinal)
        { "JPY", "KRW", "VND", "CLP", "ISK", "UGX", "XAF", "XOF", "XPF", "PYG", "RWF" };

    private static readonly HashSet<string> ThreeMinorUnits = new(StringComparer.Ordinal)
        { "BHD", "KWD", "OMR", "JOD", "TND", "LYD" };

    private static readonly FunctionContext DirectContext =
        new(ProtocolSchemas.Parse("{}"), default, null, InvokeDirect, null);

    /// <summary>Gets the names of the functions the Basic Catalog defines.</summary>
    public static IReadOnlyCollection<string> Names { get; } = new[]
    {
        "required", "regex", "length", "numeric", "email", "formatString", "formatNumber", "formatCurrency",
        "formatDate", "pluralize", "openUrl", "and", "or", "not"
    };

    /// <summary>Runs a Basic Catalog function directly, without catalog routing or data bindings.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="args">The resolved arguments. A formatString template can read no data and call only Basic Catalog functions.</param>
    /// <param name="result">The result. Functions without a return value produce a JSON null.</param>
    /// <param name="diagnostic">The failure, with the category in <see cref="A2uiDiagnostic.Code"/>.</param>
    /// <returns>Whether the function ran.</returns>
    public static bool TryInvoke(string name, IReadOnlyDictionary<string, JsonElement> args,
        out JsonElement result, out A2uiDiagnostic? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(args);
        return TryInvoke(name, args, DirectContext, out result, out diagnostic);
    }

    internal static bool TryInvoke(string name, IReadOnlyDictionary<string, JsonElement> args, FunctionContext context,
        out JsonElement result, out A2uiDiagnostic? diagnostic)
    {
        var outcome = name switch
        {
            "required" => Required(args),
            "regex" => MatchPattern(args),
            "length" => Length(args),
            "numeric" => Numeric(args),
            "email" => Email(args),
            "formatString" => FormatString(args, context),
            "formatNumber" => FormatNumber(args),
            "formatCurrency" => FormatCurrency(args),
            "formatDate" => FormatDate(args),
            "pluralize" => Pluralize(args),
            "openUrl" => OpenUrl(args, context),
            "and" => Logical(args, "and"),
            "or" => Logical(args, "or"),
            "not" => Ok(FunctionValues.Boolean(!FunctionValues.ToBoolean(Argument(args, "value")))),
            _ => Fail($"Function '{name}' has no implementation.")
        };
        result = outcome.Value;
        diagnostic = outcome.Error;
        return outcome.Error is null;
    }

    private static bool InvokeDirect(string? catalogId, string name, IReadOnlyDictionary<string, JsonElement> args,
        out JsonElement value, out A2uiDiagnostic? diagnostic)
    {
        if (name.StartsWith('@'))
        {
            value = default;
            diagnostic = FunctionValues.ExpressionError(FunctionEvaluator.IndexOutsideCollection);
            return false;
        }
        return TryInvoke(name, args, DirectContext, out value, out diagnostic);
    }

    private readonly record struct Outcome(JsonElement Value, A2uiDiagnostic? Error);

    private static Outcome Ok(JsonElement value) => new(value, null);

    private static Outcome Fail(string message) => new(default, FunctionValues.ExpressionError(message));

    private static JsonElement Argument(IReadOnlyDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) ? value : default;

    // ---- Validation functions ----

    private static Outcome Required(IReadOnlyDictionary<string, JsonElement> args)
    {
        var value = Argument(args, "value");
        var present = value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => false,
            JsonValueKind.String => value.GetString()!.Length > 0,
            JsonValueKind.Array => value.GetArrayLength() > 0,
            _ => true
        };
        return Ok(FunctionValues.Validation(present));
    }

    private static Outcome MatchPattern(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (Argument(args, "pattern") is not { ValueKind: JsonValueKind.String } pattern)
            return Fail("regex requires a pattern string.");
        if (!RegexDialect.TryCompile(pattern.GetString()!, out var regex, out var diagnostic))
            return new(default, diagnostic);

        var text = FunctionValues.ToText(Argument(args, "value"));
        if (RegexDialect.CodePointCount(text) > 4096)
            return Ok(FunctionValues.Validation(false));
        try
        {
            return Ok(FunctionValues.Validation(regex!.IsMatch(text)));
        }
        catch (RegexMatchTimeoutException)
        {
            return Fail("The regular expression took too long to evaluate.");
        }
    }

    private static Outcome Length(IReadOnlyDictionary<string, JsonElement> args)
    {
        var value = Argument(args, "value");
        var length = value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : RegexDialect.CodePointCount(FunctionValues.ToText(value));
        return Ok(FunctionValues.Validation(WithinBounds(length, args)));
    }

    private static Outcome Numeric(IReadOnlyDictionary<string, JsonElement> args) =>
        Ok(FunctionValues.Validation(FunctionValues.TryGetNumber(Argument(args, "value"), out var number) &&
            WithinBounds(number, args)));

    private static bool WithinBounds(double value, IReadOnlyDictionary<string, JsonElement> args) =>
        (!FunctionValues.TryGetNumber(Argument(args, "min"), out var minimum) || value >= minimum) &&
        (!FunctionValues.TryGetNumber(Argument(args, "max"), out var maximum) || value <= maximum);

    private static Outcome Email(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (IsEmail(Argument(args, "value")))
            return Ok(FunctionValues.Validation(true));
        return Ok(FunctionValues.Validation(false, "INVALID_EMAIL", "Please enter a valid email address (e.g. user@example.com)."));
    }

    // Matches ^[^\s@]+@[^\s@]+\.[^\s@]{2,}$ without a regular expression engine.
    private static bool IsEmail(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return false;
        var text = value.GetString()!;
        var at = text.IndexOf('@');
        if (at < 1 || text.IndexOf('@', at + 1) >= 0 || text.Any(char.IsWhiteSpace))
            return false;
        var domain = text.AsSpan(at + 1);
        for (var dot = 1; dot < domain.Length; dot++)
            if (domain[dot] == '.' && domain.Length - dot - 1 >= 2)
                return true;
        return false;
    }

    // ---- Logic ----

    private static Outcome Logical(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        if (Argument(args, "values") is not { ValueKind: JsonValueKind.Array } values || values.GetArrayLength() < 2)
            return Fail($"{name} requires an array of at least two values.");
        var items = values.EnumerateArray().Select(item => FunctionValues.ToBoolean(item));
        return Ok(FunctionValues.Boolean(name == "and" ? items.All(item => item) : items.Any(item => item)));
    }

    // ---- Formatting ----

    private static Outcome FormatString(IReadOnlyDictionary<string, JsonElement> args, FunctionContext context)
    {
        var template = Argument(args, "value");
        if (template.ValueKind != JsonValueKind.String)
            return Ok(FunctionValues.String(FunctionValues.ToText(template)));
        return ExpressionTemplate.TryEvaluateWith(template.GetString()!, context.DataModel, context.Scope, context.CatalogId,
            context.Invoke, out var text, out var diagnostic, context.ReportPartError)
            ? Ok(FunctionValues.String(text!))
            : new(default, diagnostic);
    }

    private static Outcome FormatNumber(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!FunctionValues.TryGetNumber(Argument(args, "value"), out var number))
            return Fail("formatNumber requires a numeric value.");
        if (!TryDecimals(args, out var decimals, out var error))
            return error;
        var grouping = !args.TryGetValue("grouping", out var option) || FunctionValues.ToBoolean(option);
        return Ok(FunctionValues.String(Digits(number, decimals, 3, grouping)));
    }

    private static Outcome FormatCurrency(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!FunctionValues.TryGetNumber(Argument(args, "value"), out var number))
            return Fail("formatCurrency requires a numeric value.");
        if (Argument(args, "currency") is not { ValueKind: JsonValueKind.String } currencyArgument ||
            currencyArgument.GetString()!.Length != 3 || !currencyArgument.GetString()!.All(char.IsAsciiLetter))
            return Fail("formatCurrency requires an ISO 4217 currency code.");
        if (!TryDecimals(args, out var decimals, out var error))
            return error;

        var currency = currencyArgument.GetString()!.ToUpperInvariant();
        var minorUnits = NoMinorUnits.Contains(currency) ? 0 : ThreeMinorUnits.Contains(currency) ? 3 : 2;
        var digits = Digits(Math.Abs(number), decimals ?? minorUnits, decimals ?? minorUnits, true);
        var symbol = CurrencySymbols.TryGetValue(currency, out var known) ? known : currency + "\u00A0";
        var negative = number < 0 && digits.Any(character => character is >= '1' and <= '9');
        return Ok(FunctionValues.String((negative ? "-" : "") + symbol + digits));
    }

    private static bool TryDecimals(IReadOnlyDictionary<string, JsonElement> args, out int? decimals, out Outcome error)
    {
        decimals = null;
        error = default;
        if (!args.TryGetValue("decimals", out var value) || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;
        if (!FunctionValues.TryGetNumber(value, out var count) || count < 0 || count > 20 || count != Math.Floor(count))
        {
            error = Fail("decimals must be a whole number from 0 to 20.");
            return false;
        }
        decimals = (int)count;
        return true;
    }

    // Rounds half away from zero, as Intl.NumberFormat does, and formats with en-US separators.
    private static string Digits(double number, int? decimals, int defaultMaximum, bool grouping)
    {
        var minimum = decimals ?? 0;
        var maximum = decimals ?? defaultMaximum;
        decimal rounded;
        try
        {
            rounded = Math.Round((decimal)number, maximum, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return number.ToString("R", CultureInfo.InvariantCulture);
        }

        var format = (grouping ? "#,##0" : "0") +
            (maximum > 0 ? "." + new string('0', minimum) + new string('#', maximum - minimum) : "");
        return rounded.ToString(format, CultureInfo.InvariantCulture);
    }

    private static Outcome Pluralize(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (!FunctionValues.TryGetNumber(Argument(args, "value"), out var number))
            return Fail("pluralize requires a numeric value.");
        if (!args.TryGetValue("other", out var other))
            return Fail("pluralize requires an 'other' string.");

        // An explicit string for 0, 1 or 2 wins. Otherwise the English CLDR rules apply: one for 1, other for the rest.
        var category = number switch
        {
            0 when args.ContainsKey("zero") => "zero",
            1 when args.ContainsKey("one") => "one",
            2 when args.ContainsKey("two") => "two",
            _ => "other"
        };
        return Ok(FunctionValues.String(FunctionValues.ToText(category == "other" ? other : args[category])));
    }

    private static Outcome OpenUrl(IReadOnlyDictionary<string, JsonElement> args, FunctionContext context)
    {
        if (Argument(args, "url") is not { ValueKind: JsonValueKind.String } url ||
            !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return Fail("openUrl only opens absolute http and https URLs.");
        context.OpenUrl?.Invoke(uri);
        return Ok(FunctionValues.Null);
    }

    // ---- Dates ----

    private readonly record struct DateParts(int Year, int Month, int Day, int Hour, int Minute, int Second,
        int Nanosecond, int? OffsetMinutes);

    private static Outcome FormatDate(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (Argument(args, "format") is not { ValueKind: JsonValueKind.String } pattern)
            return Fail("formatDate requires a format string.");
        if (Argument(args, "value") is not { ValueKind: JsonValueKind.String } value ||
            !TryParseDate(value.GetString()!, out var parts))
            return Ok(FunctionValues.String(""));

        var format = pattern.GetString()!;
        if (format == "ISO")
            return Ok(FunctionValues.String(Iso(parts)));
        return TryFormatDate(parts, format, out var text, out var error)
            ? Ok(FunctionValues.String(text))
            : Fail(error!);
    }

    private static string Iso(DateParts parts)
    {
        try
        {
            var moment = new DateTimeOffset(parts.Year, parts.Month, parts.Day, parts.Hour, parts.Minute, parts.Second,
                TimeSpan.FromMinutes(parts.OffsetMinutes ?? 0));
            return moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "." +
                (parts.Nanosecond / 1_000_000).ToString("D3", CultureInfo.InvariantCulture) + "Z";
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }

    // Accepts the ISO 8601 and RFC 3339 forms: a date, optionally followed by a time and a UTC or numeric offset.
    private static bool TryParseDate(string text, out DateParts parts)
    {
        parts = default;
        if (text.Length < 10 || text[4] != '-' || text[7] != '-' ||
            !TryDigits(text, 0, 4, out var year) || !TryDigits(text, 5, 2, out var month) || !TryDigits(text, 8, 2, out var day) ||
            year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;
        if (text.Length == 10)
        {
            parts = new(year, month, day, 0, 0, 0, 0, null);
            return true;
        }

        if (text[10] is not ('T' or 't' or ' ') || text.Length < 16 || text[13] != ':' ||
            !TryDigits(text, 11, 2, out var hour) || !TryDigits(text, 14, 2, out var minute) || hour > 23 || minute > 59)
            return false;

        var position = 16;
        int second = 0, nanosecond = 0;
        if (position < text.Length && text[position] == ':')
        {
            if (!TryDigits(text, position + 1, 2, out second) || second > 59)
                return false;
            position += 3;
            if (position < text.Length && text[position] == '.')
            {
                var start = ++position;
                while (position < text.Length && char.IsAsciiDigit(text[position]))
                    position++;
                var fraction = text[start..position];
                if (fraction.Length is < 1 or > 9)
                    return false;
                nanosecond = int.Parse(fraction.PadRight(9, '0'), CultureInfo.InvariantCulture);
            }
        }

        int? offset = null;
        if (position < text.Length)
        {
            if (text[position] is 'Z' or 'z')
            {
                offset = 0;
                position++;
            }
            else if (text[position] is '+' or '-' && TryOffset(text.AsSpan(position + 1), out var magnitude))
            {
                offset = text[position] == '-' ? -magnitude : magnitude;
                position = text.Length;
            }
        }
        if (position != text.Length)
            return false;

        parts = new(year, month, day, hour, minute, second, nanosecond, offset);
        return true;
    }

    private static bool TryOffset(ReadOnlySpan<char> text, out int minutes)
    {
        minutes = 0;
        var digits = text.Length switch
        {
            2 or 4 => text.ToString(),
            5 when text[2] == ':' => string.Concat(text[..2], text[3..]),
            _ => ""
        };
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
            return false;
        var hours = int.Parse(digits.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var rest = digits.Length > 2 ? int.Parse(digits.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        if (hours > 23 || rest > 59)
            return false;
        minutes = hours * 60 + rest;
        return true;
    }

    private static bool TryDigits(string text, int start, int count, out int value)
    {
        value = 0;
        if (start + count > text.Length)
            return false;
        for (var index = start; index < start + count; index++)
        {
            if (!char.IsAsciiDigit(text[index]))
                return false;
            value = value * 10 + (text[index] - '0');
        }
        return true;
    }

    // Expands the Unicode TR35 date pattern fields that the Basic Catalog documents, in English.
    private static bool TryFormatDate(DateParts parts, string pattern, out string text, out string? error)
    {
        var output = new StringBuilder();
        text = "";
        error = null;
        var weekday = (int)new DateTime(parts.Year, parts.Month, parts.Day).DayOfWeek;
        for (var index = 0; index < pattern.Length;)
        {
            var symbol = pattern[index];
            if (symbol == '\'')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '\'')
                {
                    output.Append('\'');
                    index += 2;
                    continue;
                }
                index++;
                while (index < pattern.Length)
                {
                    if (pattern[index] == '\'')
                    {
                        if (index + 1 < pattern.Length && pattern[index + 1] == '\'')
                        {
                            output.Append('\'');
                            index += 2;
                            continue;
                        }
                        break;
                    }
                    output.Append(pattern[index++]);
                }
                index++;
                continue;
            }

            if (!char.IsAsciiLetter(symbol))
            {
                output.Append(symbol);
                index++;
                continue;
            }

            var count = 1;
            while (index + count < pattern.Length && pattern[index + count] == symbol)
                count++;
            index += count;
            var field = Field(parts, symbol, count, weekday);
            if (field is null)
            {
                error = $"Unsupported date pattern field '{new string(symbol, count)}'.";
                return false;
            }
            output.Append(field);
        }
        text = output.ToString();
        return true;
    }

    private static string? Field(DateParts parts, char symbol, int count, int weekday)
    {
        var hour12 = parts.Hour % 12 == 0 ? 12 : parts.Hour % 12;
        switch (symbol)
        {
            case 'y':
                return count == 2 ? Pad(parts.Year % 100, 2) : Pad(parts.Year, count);
            case 'M' or 'L':
                return count switch
                {
                    <= 2 => Pad(parts.Month, count),
                    3 => FullMonths[parts.Month - 1][..3],
                    4 => FullMonths[parts.Month - 1],
                    _ => FullMonths[parts.Month - 1][..1]
                };
            case 'd':
                return Pad(parts.Day, count);
            case 'E':
                return count switch
                {
                    <= 3 => FullDays[weekday][..3],
                    4 => FullDays[weekday],
                    5 => FullDays[weekday][..1],
                    _ => FullDays[weekday][..2]
                };
            case 'a':
                return parts.Hour < 12 ? "AM" : "PM";
            case 'h':
                return Pad(hour12, count);
            case 'H':
                return Pad(parts.Hour, count);
            case 'K':
                return Pad(parts.Hour % 12, count);
            case 'k':
                return Pad(parts.Hour == 0 ? 24 : parts.Hour, count);
            case 'm':
                return Pad(parts.Minute, count);
            case 's':
                return Pad(parts.Second, count);
            case 'S':
                return parts.Nanosecond.ToString("D9", CultureInfo.InvariantCulture).PadRight(count, '0')[..count];
            case 'Z' or 'X' or 'x':
                return Offset(parts.OffsetMinutes ?? 0, symbol, count);
            default:
                return null;
        }
    }

    private static string Offset(int minutes, char symbol, int count)
    {
        var zero = minutes == 0;
        var sign = minutes < 0 ? "-" : "+";
        var hours = Math.Abs(minutes) / 60;
        var rest = Math.Abs(minutes) % 60;
        var basic = sign + Pad(hours, 2) + Pad(rest, 2);
        var extended = sign + Pad(hours, 2) + ":" + Pad(rest, 2);
        switch (symbol)
        {
            case 'Z':
                return count switch { <= 3 => basic, 4 => zero ? "GMT" : "GMT" + extended, _ => zero ? "Z" : extended };
            default:
                if (symbol == 'X' && zero)
                    return "Z";
                return count switch { 1 => sign + Pad(hours, 2) + (rest == 0 ? "" : Pad(rest, 2)), 2 or 4 => basic, _ => extended };
        }
    }

    private static string Pad(int value, int width) => value.ToString("D" + width, CultureInfo.InvariantCulture);
}
