using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;

namespace PNMTD.Lib.Mikrotik;

/// <summary>A line of the router's log, as <c>/log print as-value</c> gives it: its time in the router's clock, and its text.</summary>
public sealed record RouterLogLine(string? Time, string Message);

/// <summary>A user of the router (<c>/user print</c>): its name, group and whether it is disabled.</summary>
public sealed record RouterUser(string Name, string? Group, bool Disabled);

/// <summary>A group of the router's users (<c>/user group print</c>) and its policies, the "!" ones left out.</summary>
public sealed record RouterGroup(string Name, IReadOnlyList<string> Policies);

/// <summary>Who is logged in now (<c>/user active print</c>).</summary>
public sealed record RouterActiveUser(string Name, string? When, string? Address, string? Via);

public enum RouterLoginEvent { LoggedIn, LoggedOut, Failure }

/// <summary>A login line of the router's log, read: what happened, the account, where from (an address, or a MAC for WinBox), and how.</summary>
public sealed record RouterLoginLine(RouterLoginEvent Event, string User, string? Source, string Via, LogonKind? Kind);

/// <summary>
/// The routers' logins (docs/cookie-access-plan.md §4.2): RouterOS writes them to its log under the topic <c>account</c>, in
/// lines that aren't localised: <c>user admin logged in from 10.0.0.5 via winbox</c>, <c>user admin logged out from 10.0.0.5 via
/// winbox</c>, <c>login failure for user admin from 203.0.113.7 via ssh</c>, and <c>user admin logged in via local</c> at the
/// console. The poll reads them with its other reads; these functions turn them into logins. The log's time is the router's
/// clock: today's lines carry only the time, older ones a date in one of RouterOS's formats.
/// </summary>
public static partial class RouterLogins
{
    /// <summary>The channel of a router's logins in <c>agent_logins</c>.</summary>
    public const string Channel = "routeros";

    [GeneratedRegex(@"^user (?<user>.+?) logged (?<what>in|out)(?: from (?<source>\S+))? via (?<via>[A-Za-z0-9-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SessionLine();

    [GeneratedRegex(@"^login failure for user (?<user>.+?)(?: from (?<source>\S+))? via (?<via>[A-Za-z0-9-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FailureLine();

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>The line as a login, or null when it is something else of the topic (a user changed, a group added).</summary>
    public static RouterLoginLine? Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }
        var text = message.Trim();
        if (SessionLine().Match(text) is { Success: true } s)
        {
            var via = s.Groups["via"].Value;
            return new RouterLoginLine(s.Groups["what"].Value.Equals("in", StringComparison.OrdinalIgnoreCase) ? RouterLoginEvent.LoggedIn : RouterLoginEvent.LoggedOut,
                s.Groups["user"].Value, Source(s.Groups["source"]), via, KindOf(via));
        }
        if (FailureLine().Match(text) is { Success: true } f)
        {
            var via = f.Groups["via"].Value;
            return new RouterLoginLine(RouterLoginEvent.Failure, f.Groups["user"].Value, Source(f.Groups["source"]), via, KindOf(via));
        }
        return null;
    }

    /// <summary>A line that speaks of a login but matches no pattern: counted and reported, never dropped silently.</summary>
    public static bool LooksLikeLogin(string? message) =>
        message is { } m && (m.Contains("logged in", StringComparison.OrdinalIgnoreCase) || m.Contains("login failure", StringComparison.OrdinalIgnoreCase)
            || m.Contains("logged out", StringComparison.OrdinalIgnoreCase));

    private static string? Source(Group g) => g.Success && g.Value.Length > 0 ? g.Value : null;

    /// <summary>RouterOS's <c>via</c>: WinBox, WebFig, the API, SSH, Telnet, FTP, or the console (<c>local</c>); null for one it doesn't know.</summary>
    public static LogonKind? KindOf(string via) => via.ToLowerInvariant() switch
    {
        "winbox" or "mac-winbox" or "romon" => LogonKind.Winbox,
        "web" or "www" or "www-ssl" or "webfig" => LogonKind.Web,
        "api" or "api-ssl" or "rest-api" => LogonKind.Api,
        "ssh" => LogonKind.Ssh,
        "telnet" or "mac-telnet" => LogonKind.Telnet,
        "ftp" => LogonKind.Ftp,
        "local" or "console" or "serial" => LogonKind.Console,
        _ => null,
    };

    /// <summary>
    /// The router's clock from <c>/system clock</c>'s date and time joined by a space: <c>2026-11-15 10:14:32</c> (7.10 on) or
    /// <c>nov/15/2026 10:14:32</c>.
    /// </summary>
    public static DateTime? Clock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !TimeOnly.TryParseExact(parts[1], "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return null;
        }
        return Date(parts[0], null) is { } date ? date.ToDateTime(time) : null;
    }

    /// <summary>
    /// A log line's time in the router's clock: <c>10:14:32</c> (today), <c>nov/15 10:14:32</c> (this year), <c>nov/15/2026 10:14:32</c>
    /// or <c>2026-11-15 10:14:32</c>. A time of today later than the router's clock is yesterday's.
    /// </summary>
    public static DateTime? Time(string? text, DateTime routerNow)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var timeText = parts[^1];
        if (!TimeOnly.TryParseExact(timeText, ["HH:mm:ss", "H:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return null;
        }
        if (parts.Length == 1)
        {
            var today = DateOnly.FromDateTime(routerNow).ToDateTime(time);
            return today > routerNow.AddMinutes(1) ? today.AddDays(-1) : today;
        }
        return parts.Length == 2 && Date(parts[0], routerNow.Year) is { } date ? date.ToDateTime(time) : null;
    }

    private static DateOnly? Date(string text, int? year)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso;
        }
        var bits = text.Split('/');
        if (bits.Length is < 2 or > 3)
        {
            return null;
        }
        var month = Array.IndexOf(Months, bits[0].ToLowerInvariant()) + 1;
        if (month == 0 || !int.TryParse(bits[1], NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return null;
        }
        int y;
        if (bits.Length == 3)
        {
            if (!int.TryParse(bits[2], NumberStyles.None, CultureInfo.InvariantCulture, out y))
            {
                return null;
            }
        }
        else if (year is { } given)
        {
            y = given;
        }
        else
        {
            return null;
        }
        try
        {
            return new DateOnly(y, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The router's clock minus UTC, rounded to a quarter of an hour, so a few seconds' drift don't change a login's time.</summary>
    public static TimeSpan Offset(DateTime routerNow, DateTime utcNow)
    {
        var minutes = (routerNow - utcNow).TotalMinutes;
        return TimeSpan.FromMinutes(Math.Round(minutes / 15) * 15);
    }

    /// <summary>
    /// A login line's record id: a 64-bit hash of its time (to the second, in UTC) and its text. RouterOS's <c>.id</c> starts again
    /// after a reboot; this doesn't, so a line read twice is stored once.
    /// </summary>
    public static long RecordId(DateTime atUtc, string message) => Hash($"{atUtc:yyyyMMddHHmmss}|{message.Trim()}");

    /// <summary>The record id of an aggregate of failures: the account, the source and the way, per 10 minutes.</summary>
    public static long FailureId(DateTime atUtc, string user, string? source, string via)
    {
        var bucket = new DateTime(atUtc.Ticks - atUtc.Ticks % TimeSpan.FromMinutes(10).Ticks, DateTimeKind.Utc);
        return Hash($"failure|{bucket:yyyyMMddHHmm}|{user}|{source}|{via}".ToLowerInvariant());
    }

    /// <summary>The start of a failure's 10 minutes.</summary>
    public static DateTime FailureBucket(DateTime atUtc) => new(atUtc.Ticks - atUtc.Ticks % TimeSpan.FromMinutes(10).Ticks, DateTimeKind.Utc);

    /// <summary>Whether a group's policies make its users admins: <i>write</i> or <i>policy</i>.</summary>
    public static bool IsAdminGroup(IReadOnlyList<string> policies) =>
        policies.Any(p => p.Equals("write", StringComparison.OrdinalIgnoreCase) || p.Equals("policy", StringComparison.OrdinalIgnoreCase));

    private static long Hash(string text)
    {
        // FNV-1a, 64 bits: stable across processes, unlike string.GetHashCode.
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return unchecked((long)hash);
    }
}
