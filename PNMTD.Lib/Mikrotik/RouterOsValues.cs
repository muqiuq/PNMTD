using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PNMTD.Lib.Mikrotik;

/// <summary>
/// Values as RouterOS prints and serializes them (docs/mikrotik-plan.md §4.5). Recorded on 7.13 (M0):
/// <list type="bullet">
/// <item><c>:serialize to=json</c> writes a duration as a time since the Unix epoch: an uptime of
/// 43 seconds is <c>"1970-01-01 00:00:43"</c>. The console prints <c>00:00:43</c>, <c>1d02:03:04</c>,
/// <c>1w2d03:04:05</c> or <c>3h4m5s</c>.</item>
/// <item>A string that looks like a number becomes a JSON number: the version "7.13" of
/// <c>/system package update</c> is <c>7.130000</c>, so 7.2 and 7.20 cannot be told apart. Versions are
/// therefore read from <c>/system resource</c>, whose "7.13 (stable)" stays a string.</item>
/// </list>
/// </summary>
public static partial class RouterOsValues
{
    [GeneratedRegex(@"^\s*(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2}):(\d{2})(?:\.\d+)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EpochTime();

    [GeneratedRegex(@"^\s*(?:(\d+)w)?(?:(\d+)d)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d{1,3}))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ClockDuration();

    [GeneratedRegex(@"^\s*(?:(\d+)w)?(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m(?!s))?(?:(\d+)s)?(?:(\d+)ms)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitDuration();

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*(B|KiB|MiB|GiB|TiB|KB|MB|GB|TB|k|M|G|T)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Size();

    /// <summary>A duration in any of the forms above; null when it is none of them.</summary>
    public static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (EpochTime().Match(text) is { Success: true } e)
        {
            var at = new DateTime(N(e, 1), N(e, 2), N(e, 3), N(e, 4), N(e, 5), N(e, 6), DateTimeKind.Utc);
            return at - DateTime.UnixEpoch;
        }
        if (ClockDuration().Match(text) is { Success: true } c)
        {
            return TimeSpan.FromDays(7 * N(c, 1) + N(c, 2)) + new TimeSpan(N(c, 3), N(c, 4), N(c, 5))
                + TimeSpan.FromMilliseconds(c.Groups[6].Success ? int.Parse(c.Groups[6].Value.PadRight(3, '0'), CultureInfo.InvariantCulture) : 0);
        }
        if (UnitDuration().Match(text) is { Success: true } u && u.Groups.Cast<Group>().Skip(1).Any(g => g.Success))
        {
            return TimeSpan.FromDays(7 * N(u, 1) + N(u, 2)) + TimeSpan.FromHours(N(u, 3)) + TimeSpan.FromMinutes(N(u, 4))
                + TimeSpan.FromSeconds(N(u, 5)) + TimeSpan.FromMilliseconds(N(u, 6));
        }
        return null;
    }

    /// <summary>Bytes, from a number or a size such as <c>12.5MiB</c> (binary units, as RouterOS uses them).</summary>
    public static long? Bytes(string? text)
    {
        if (text is null || Size().Match(text) is not { Success: true } m)
        {
            return null;
        }
        var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var factor = m.Groups[2].Value switch
        {
            "KiB" or "KB" or "k" => 1024d,
            "MiB" or "MB" or "M" => 1024d * 1024,
            "GiB" or "GB" or "G" => 1024d * 1024 * 1024,
            "TiB" or "TB" or "T" => 1024d * 1024 * 1024 * 1024,
            _ => 1d,
        };
        return (long)Math.Round(value * factor);
    }

    private static int N(Match m, int group) => m.Groups[group].Success ? int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : 0;

    // --- JSON from :serialize -----------------------------------------------------------------------

    /// <summary>A property as text, whether RouterOS wrote it as a string, a number or a boolean.</summary>
    public static string? Text(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? AsText(value) : null;

    public static string? AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => Number(value.GetRawText()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    /// <summary>
    /// A JSON number as RouterOS meant it: <c>22.000000</c> (a port, M0 on 7.13 and 7.25.1) is "22", and
    /// <c>7.130000</c> is "7.13". Trailing zeros are lost to <c>:serialize</c>: "7.20" and "7.2" both come back
    /// as "7.2", which is why versions are never read from numbers (<see cref="RouterPoll"/>).
    /// </summary>
    public static string Number(string raw)
    {
        if (!raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
        {
            return raw;
        }
        var trimmed = raw.TrimEnd('0');
        return trimmed.EndsWith('.') ? trimmed[..^1] : trimmed;
    }

    public static long? Long(this JsonElement element, string name) => element.Text(name) is { } text
        ? long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : Bytes(text)
        : null;

    public static int? Int(this JsonElement element, string name) => element.Long(name) is { } n and >= int.MinValue and <= int.MaxValue ? (int)n : null;

    public static bool? Bool(this JsonElement element, string name) => element.Text(name) switch
    {
        "true" or "yes" => true,
        "false" or "no" => false,
        _ => null,
    };

    /// <summary>A list property (<c>policy</c>, <c>address</c>): its items as text; a single value as one item.</summary>
    public static IReadOnlyList<string> List(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return [];
        }
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(AsText).OfType<string>().ToList()
            : AsText(value) is { Length: > 0 } single ? single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    }

    /// <summary>
    /// A menu's <c>print as-value</c>: an object for a menu with one item (<c>/system resource</c>), an
    /// array for a list (<c>/user group</c>). Both give their items here.
    /// </summary>
    public static IEnumerable<JsonElement> Items(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return [];
        }
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToList(),
            JsonValueKind.Object => [value],
            _ => [],
        };
    }

    public static JsonElement? Item(this JsonElement element, string name) => element.Items(name).Cast<JsonElement?>().FirstOrDefault();
}
