using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Lib.Helper;

namespace PNMTD.Advanced;

/// <summary>
/// A number from a report or an operand (docs/advanced-sensor-plan.md §6.3): a <c>decimal</c> when it fits, so 0.1 + 0.2 has
/// no rounding surprises, else a <c>double</c>.
/// </summary>
public readonly record struct AdvancedNumber(decimal? Decimal, double Double) : IComparable<AdvancedNumber>
{
    public static bool TryParse(string? text, out AdvancedNumber number)
    {
        number = default;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }
        if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            number = new AdvancedNumber(d, (double)d);
            return true;
        }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && double.IsFinite(f))
        {
            number = new AdvancedNumber(null, f);
            return true;
        }
        return false;
    }

    public int CompareTo(AdvancedNumber other) =>
        Decimal is { } a && other.Decimal is { } b ? a.CompareTo(b) : Double.CompareTo(other.Double);

    public static bool operator <(AdvancedNumber a, AdvancedNumber b) => a.CompareTo(b) < 0;

    public static bool operator >(AdvancedNumber a, AdvancedNumber b) => a.CompareTo(b) > 0;

    public static bool operator <=(AdvancedNumber a, AdvancedNumber b) => a.CompareTo(b) <= 0;

    public static bool operator >=(AdvancedNumber a, AdvancedNumber b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        Decimal is { } d ? d.ToString(CultureInfo.InvariantCulture) : Double.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>Reading values of a report: numbers, texts, times, durations, health words, and showing them in messages.</summary>
public static class AdvancedValues
{
    private static readonly HashSet<string> HealthyWords = new(StringComparer.OrdinalIgnoreCase)
        { "ok", "pass", "passed", "up", "healthy", "success", "green", "true" };

    private static readonly HashSet<string> WarningWords = new(StringComparer.OrdinalIgnoreCase)
        { "warn", "warning", "degraded", "yellow" };

    /// <summary>A number, or a string holding one (invariant culture, <c>.</c> as the decimal point).</summary>
    public static bool TryNumber(JsonElement value, out AdvancedNumber number)
    {
        number = default;
        return value.ValueKind switch
        {
            JsonValueKind.Number => AdvancedNumber.TryParse(value.GetRawText(), out number),
            JsonValueKind.String => AdvancedNumber.TryParse(value.GetString(), out number),
            _ => false,
        };
    }

    /// <summary>A value as text for the text operators: a string as it is, a number as its JSON text, true and false.</summary>
    public static bool TryText(JsonElement value, out string text)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                text = value.GetString() ?? "";
                return true;
            case JsonValueKind.Number:
                text = value.GetRawText();
                return true;
            case JsonValueKind.True:
                text = "true";
                return true;
            case JsonValueKind.False:
                text = "false";
                return true;
            default:
                text = "";
                return false;
        }
    }

    /// <summary>Pass, Warn or Fail for <c>healthy</c> (§6.3): the IETF, Spring and ASP.NET status words, case-insensitive.</summary>
    public static AdvancedOutcome Health(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => AdvancedOutcome.Pass,
        JsonValueKind.String when HealthyWords.Contains(value.GetString()!.Trim()) => AdvancedOutcome.Pass,
        JsonValueKind.String when WarningWords.Contains(value.GetString()!.Trim()) => AdvancedOutcome.Warn,
        _ => AdvancedOutcome.Fail,
    };

    /// <summary>
    /// A time (§6.3): ISO 8601 with an offset, ISO 8601 without one (read as UTC, <paramref name="assumedUtc"/>), or Unix
    /// seconds; numbers above 10¹¹ are milliseconds.
    /// </summary>
    public static bool TryTime(JsonElement value, out DateTime utc, out bool assumedUtc)
    {
        utc = default;
        assumedUtc = false;
        if (value.ValueKind == JsonValueKind.Number || (value.ValueKind == JsonValueKind.String && LooksNumeric(value.GetString())))
        {
            if (!TryNumber(value, out var n))
            {
                return false;
            }
            var seconds = n.Double;
            if (Math.Abs(seconds) > 1e11)
            {
                seconds /= 1000;
            }
            if (seconds is < -62_135_596_800 or > 253_402_300_799)
            {
                return false;
            }
            utc = DateTime.UnixEpoch.AddTicks((long)(seconds * TimeSpan.TicksPerSecond));
            return true;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        return TryTime(value.GetString(), out utc, out assumedUtc);
    }

    private static readonly Regex WithOffset = new(@"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}(:\d{2}(\.\d{1,9})?)?\s*([Zz]|[+-]\d{2}(:?\d{2})?)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex WithoutOffset = new(@"^\d{4}-\d{2}-\d{2}([Tt ]\d{2}:\d{2}(:\d{2}(\.\d{1,9})?)?)?$", RegexOptions.CultureInvariant);

    public static bool TryTime(string? text, out DateTime utc, out bool assumedUtc)
    {
        utc = default;
        assumedUtc = false;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 40)
        {
            return false;
        }
        if (WithOffset.IsMatch(s))
        {
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            {
                utc = dto.UtcDateTime;
                return true;
            }
            return false;
        }
        if (WithoutOffset.IsMatch(s)
            && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
        {
            utc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            assumedUtc = true;
            return true;
        }
        return false;
    }

    private static bool LooksNumeric(string? s) => s is { Length: > 0 } && AdvancedNumber.TryParse(s, out _) && !s.Contains('-', StringComparison.Ordinal);

    /// <summary>A duration (§6.3): <c>90s</c>, <c>15m</c>, <c>26h</c>, <c>7d</c>, <c>2w</c>, combinations as <c>1h30m</c>, or ISO 8601 (<c>PT26H</c>).</summary>
    public static bool TryDuration(string? text, out TimeSpan duration)
    {
        duration = default;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }
        if (s[0] is 'P' or 'p')
        {
            try
            {
                duration = System.Xml.XmlConvert.ToTimeSpan(s.ToUpperInvariant());
                return duration > TimeSpan.Zero;
            }
            catch (FormatException)
            {
                return false;
            }
        }
        var match = Regex.Match(s.Replace(" ", ""), @"^(?:(\d+(?:\.\d+)?)(w|d|h|m|min|s))+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }
        var total = TimeSpan.Zero;
        for (var i = 0; i < match.Groups[1].Captures.Count; i++)
        {
            var amount = double.Parse(match.Groups[1].Captures[i].Value, CultureInfo.InvariantCulture);
            total += match.Groups[2].Captures[i].Value.ToLowerInvariant() switch
            {
                "w" => TimeSpan.FromDays(amount * 7),
                "d" => TimeSpan.FromDays(amount),
                "h" => TimeSpan.FromHours(amount),
                "m" or "min" => TimeSpan.FromMinutes(amount),
                _ => TimeSpan.FromSeconds(amount),
            };
        }
        duration = total;
        return total > TimeSpan.Zero && total <= TimeSpan.FromDays(3660);
    }

    /// <summary>A duration in short words: <c>26 h</c>, <c>1 h 30 min</c>, <c>45 s</c>.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = span.Negate();
        }
        if (span.TotalSeconds < 60)
        {
            return $"{Math.Round(span.TotalSeconds):0} s";
        }
        var parts = new List<string>();
        if (span.Days > 0)
        {
            parts.Add($"{span.Days} d");
        }
        if (span.Hours > 0)
        {
            parts.Add($"{span.Hours} h");
        }
        if (span.Minutes > 0 && span.Days == 0)
        {
            parts.Add($"{span.Minutes} min");
        }
        if (span.Seconds > 0 && span.TotalMinutes < 10)
        {
            parts.Add($"{span.Seconds} s");
        }
        return parts.Count == 0 ? "0 s" : string.Join(" ", parts);
    }

    /// <summary>An instant in PNMT's time zone (<c>TZ</c>), absolute: <c>17 Nov 2026 10:02</c>.</summary>
    public static string Absolute(DateTime utc) =>
        Clock.ToLocal(utc).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// A value as text for a person: strings in quotes, numbers as their JSON text, lists and objects in brief, cut at
    /// <paramref name="max"/> characters, without control characters (§3.1). The app's text is untrusted.
    /// </summary>
    public static string Display(JsonElement value, int max = AdvancedLimits.MaxActualLength)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => "\"" + Clean(value.GetString() ?? "") + "\"",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => value.GetArrayLength() == 0 ? "[]" : $"[{value.GetArrayLength()} items]",
            JsonValueKind.Object => !value.EnumerateObject().Any() ? "{}" : $"{{{value.EnumerateObject().Count()} keys}}",
            _ => "",
        };
        return Cut(text, max);
    }

    public static string Cut(string text, int max) => text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";

    /// <summary>Control characters out, runs of whitespace as one space.</summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (!space && sb.Length > 0)
                {
                    sb.Append(' ');
                }
                space = true;
                continue;
            }
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Format)
            {
                continue;
            }
            sb.Append(c);
            space = false;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The operand as text in a condition: a number as it is, other text in quotes.</summary>
    public static string Operand(string? operand)
    {
        var s = operand ?? "";
        if (AdvancedNumber.TryParse(s, out _) || s is "true" or "false" or "null")
        {
            return s.Trim();
        }
        return "\"" + Cut(Clean(s), 80) + "\"";
    }
}
