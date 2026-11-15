using System.Net;
using System.Net.Sockets;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Access;

public enum SignalStrength { Medium, Strong }

/// <summary>A signal (docs/cookie-access-plan.md §4.5): its key, its strength, and a sentence with its numbers.</summary>
public sealed record AccessSignal(string Key, SignalStrength Strength, string Text);

/// <summary>A login to judge, whatever reported it: an agent, a router's log, or PNMT's audit log.</summary>
public sealed record AccessLogin(
    AccessTargetKind Target,
    Guid? SensorId,
    string TargetName,
    Guid? HostId,
    Guid? LocationId,
    Guid? AgentLoginId,
    Guid? AuditLogId,
    DateTime At,
    string User,
    string? Domain,
    bool IsAdmin,
    LogonKind? Kind,
    string Way,
    string? Source,
    string? Device = null,
    string? Workstation = null,
    string? KeyFingerprint = null,
    string? TargetUser = null,
    string? AuthPackage = null)
{
    /// <summary>The account as the target names it: <c>CONTOSO\jdoe</c>, <c>root</c>.</summary>
    public string Account => Domain is { Length: > 0 } d ? $"{d}\\{User}" : User;

    public string Group => AccessSignals.GroupOf(Target, Kind, Way);
}

/// <summary>The account's person (§5.3), and what the availability plan says of them today.</summary>
public sealed record AccessPerson(Guid UserId, string Name, bool Active, bool Works, string? Away);

/// <summary>What code knows about a login when it computes the signals: its history, its context, its person.</summary>
public sealed record AccessFacts
{
    public required AccessLogin Login { get; init; }

    public int HistoryDays { get; init; } = 90;

    public bool Learning { get; init; }

    public bool AccountSeen { get; init; }

    public bool NetworkSeen { get; init; }

    public bool GroupSeen { get; init; }

    public bool AdminSeen { get; init; }

    public bool TargetUserSeen { get; init; }

    public bool DeviceSeen { get; init; }

    /// <summary>The account logged in here in this hour of the week, an hour either side.</summary>
    public bool HourSeen { get; init; }

    /// <summary>The account has come from a public address before.</summary>
    public bool PublicSeen { get; init; }

    public bool CleartextSeen { get; init; }

    /// <summary>Failed logins from the same source on this target within the hour before.</summary>
    public int FailuresHere { get; init; }

    /// <summary>Failed logins from the same source anywhere within the hour before.</summary>
    public int FailuresAnywhere { get; init; }

    /// <summary>PNMT: the account was locked out, or failed its 2FA, within the hour before.</summary>
    public bool LockedOutBefore { get; init; }

    /// <summary>An SSH key login: whether PNMT's key manager knows the key; null when it isn't one.</summary>
    public bool? KeyKnown { get; init; }

    public bool KeyRetired { get; init; }

    public Guid? KeyOwnerId { get; init; }

    public AccessPerson? Person { get; init; }

    /// <summary>The person is deactivated, or the account's key is a retired key's.</summary>
    public bool FormerPerson { get; init; }

    /// <summary>Targets the source logged in to within the 15 minutes before.</summary>
    public int FanOut { get; init; }

    /// <summary>…with an account new on at least one of them.</summary>
    public bool FanOutNew { get; init; }

    /// <summary>A router: the login came from PNMTD's own address.</summary>
    public bool FromPnmt { get; init; }

    /// <summary>A router: one of PNMT's jobs ran as this account then.</summary>
    public bool PnmtJob { get; init; }

    public bool WorkingHours { get; init; } = true;

    public string? TrustedName { get; init; }
}

/// <summary>
/// The access watch's signals (docs/cookie-access-plan.md §4.4, §4.5) and the rules that hold a verdict to them (§5.4), as pure
/// functions of a login and its facts: tested without a database or a model.
/// </summary>
public static class AccessSignals
{
    public const string Remote = "remote";
    public const string Console = "console";
    public const string Network = "network";
    public const string Elevation = "elevation";
    public const string FileTransfer = "file-transfer";
    public const string Management = "management";
    public const string Web = "web";
    public const string Mcp = "mcp";

    public static readonly IReadOnlyList<string> Groups = [Remote, Console, Network, Elevation, FileTransfer, Management, Web, Mcp];

    /// <summary>The keys of the strong signals.</summary>
    public static readonly IReadOnlySet<string> StrongKeys = new HashSet<string>
    {
        "after-failures", "public-source", "unknown-key", "retired-key", "former-person", "fan-out", "pnmt-address",
    };

    /// <summary>A login's kind group (§4.4).</summary>
    public static string GroupOf(AccessTargetKind target, LogonKind? kind, string way) => target == AccessTargetKind.Pnmt
        ? way == Mcp ? Mcp : Web
        : kind switch
        {
            LogonKind.Rdp or LogonKind.RdpConnection or LogonKind.Ssh or LogonKind.WinRm or LogonKind.Telnet => Remote,
            LogonKind.Console or LogonKind.Cached or LogonKind.Unlock => Console,
            LogonKind.Network or LogonKind.Explicit or LogonKind.Batch => Network,
            LogonKind.Sudo or LogonKind.Su => Elevation,
            LogonKind.Sftp or LogonKind.Ftp => FileTransfer,
            LogonKind.Winbox or LogonKind.Web or LogonKind.Api => Management,
            _ => Remote,
        };

    /// <summary>The way, in lower case, as people read it: rdp, ssh, winbox, web, mcp.</summary>
    public static string WayOf(LogonKind kind) => kind switch
    {
        LogonKind.RdpConnection => "rdp",
        LogonKind.WinRm => "winrm",
        LogonKind.Web => "webfig",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static bool Reaches(string group) => group is Remote or Management or Web or Mcp;

    /// <summary>The signals of a login (§4.5); in a learning period only the strong ones; for sudo and su only two.</summary>
    public static IReadOnlyList<AccessSignal> Compute(AccessFacts f)
    {
        var l = f.Login;
        var group = l.Group;
        var days = f.HistoryDays;
        var list = new List<AccessSignal>();
        void Strong(string key, string text) => list.Add(new AccessSignal(key, SignalStrength.Strong, text));
        void Medium(string key, string text) => list.Add(new AccessSignal(key, SignalStrength.Medium, text));

        if (f.FailuresHere > 0 || f.FailuresAnywhere >= 10 || f.LockedOutBefore)
        {
            var parts = new List<string>();
            if (f.FailuresHere > 0)
            {
                parts.Add($"{f.FailuresHere} failed {(f.FailuresHere == 1 ? "login" : "logins")} from {l.Source} here");
            }
            if (f.FailuresAnywhere >= 10 && f.FailuresAnywhere > f.FailuresHere)
            {
                parts.Add($"{f.FailuresAnywhere} from {l.Source} anywhere");
            }
            if (f.LockedOutBefore)
            {
                parts.Add("the account was locked out or failed its 2FA");
            }
            Strong("after-failures", $"Success after failures within the hour before: {string.Join(", ", parts)}");
        }
        if (Reaches(group) && l.Source is { } src && IsPublic(src) && f.TrustedName is null && !f.PublicSeen)
        {
            Strong("public-source", $"From {src}, a public address, for an account that came from none in {days} days");
        }
        if (f.KeyKnown == false)
        {
            Strong("unknown-key", $"An SSH key PNMT's key manager doesn't know ({l.KeyFingerprint})");
        }
        else if (f.KeyRetired)
        {
            Strong("retired-key", $"An SSH key PNMT's key manager retired ({l.KeyFingerprint})");
        }
        if (f.FormerPerson)
        {
            Strong("former-person", f.Person is { Active: false } p
                ? $"The account belongs to {p.Name}, whose PNMT user is deactivated"
                : "The account's key belongs to someone whose key was retired");
        }
        if (f.FanOut >= 3 && f.FanOutNew)
        {
            Strong("fan-out", $"{l.Source} logged in to {f.FanOut} targets within 15 minutes, with an account new on at least one");
        }
        if (l.Target == AccessTargetKind.Router)
        {
            var pnmtUser = string.Equals(l.User, Lib.Mikrotik.RouterPoll.UserName, StringComparison.OrdinalIgnoreCase);
            if (f.FromPnmt && !pnmtUser && !f.PnmtJob)
            {
                Strong("pnmt-address", $"From PNMTD's own address ({l.Source}), but neither PNMT's poll nor one of its jobs");
            }
            else if (pnmtUser && !f.FromPnmt)
            {
                Strong("pnmt-address", $"PNMT's own user from {l.Source ?? "the console"}, not from PNMTD's address");
            }
        }

        if (l.Target != AccessTargetKind.Pnmt && !f.AccountSeen)
        {
            Medium("new-account", $"The account never logged in here in {days} days");
        }
        else
        {
            if (l.Source is not null && !f.NetworkSeen)
            {
                Medium("new-network", $"The account never came from {NetworkOf(l.Source)} here in {days} days");
            }
            if (!f.GroupSeen)
            {
                Medium("new-way", $"The account never logged in by {Label(group)} here in {days} days");
            }
            if (l.Target == AccessTargetKind.Pnmt && l.Device is not null && !f.DeviceSeen)
            {
                Medium("new-device", $"{l.Device}: a device the user never signed in with in {days} days");
            }
            if (l.Target != AccessTargetKind.Pnmt && l.IsAdmin && !f.AdminSeen)
            {
                Medium("first-admin", "The account logs in as an admin here for the first time");
            }
        }
        if (group == Elevation && l.TargetUser is { Length: > 0 } target && !f.TargetUserSeen)
        {
            Medium("new-target", $"The account became {target} here for the first time in {days} days");
        }
        if (l.Target == AccessTargetKind.Router && l.Kind is LogonKind.Telnet or LogonKind.Ftp && !f.CleartextSeen)
        {
            Medium("cleartext", $"{l.Kind}, a cleartext way, for an account that never used it here");
        }
        if (f.Person is { Works: false } person && !f.FormerPerson)
        {
            Medium("not-working", $"{person.Name} doesn't work today{(person.Away is { } away ? $" ({away})" : "")}");
        }
        if (!f.WorkingHours && !f.HourSeen)
        {
            Medium("odd-hour", "Outside working hours, in an hour of the week the account never logged in here");
        }
        if (f.Person is { } owner && f.KeyOwnerId is { } keyOwner && keyOwner != owner.UserId)
        {
            Medium("someone-elses-key", "The SSH key belongs to someone other than the account's person");
        }

        if (group == Elevation)
        {
            list.RemoveAll(s => s.Key is not ("new-target" or "after-failures"));
        }
        if (f.Learning)
        {
            list.RemoveAll(s => s.Strength != SignalStrength.Strong);
        }
        return list;
    }

    public static string Label(string group) => group switch
    {
        Remote => "remote login (RDP, SSH, WinRM, Telnet)",
        Console => "the console",
        Network => "a network logon",
        Elevation => "sudo or su",
        FileTransfer => "file transfer",
        Management => "WinBox, WebFig or the API",
        Web => "the web UI",
        Mcp => "MCP",
        _ => group,
    };

    /// <summary>The verdict, held to the signals (§5.4): <i>alarming</i> needs a strong signal; <i>normal</i> on one needs 0.8; a flagged text is at least <i>suspicious</i>; a late login at most.</summary>
    public static AccessVerdict Hold(AccessVerdict verdict, double confidence, bool strong, bool flagged, bool late)
    {
        var v = verdict;
        if (v == AccessVerdict.Alarming && !strong)
        {
            v = AccessVerdict.Suspicious;
        }
        if (v == AccessVerdict.Normal && strong && confidence < 0.8)
        {
            v = AccessVerdict.Unusual;
        }
        if (flagged && v < AccessVerdict.Suspicious)
        {
            v = AccessVerdict.Suspicious;
        }
        if (late && v == AccessVerdict.Alarming)
        {
            v = AccessVerdict.Suspicious;
        }
        return v;
    }

    /// <summary>Code's verdict when Cookie can't judge (§6.5): <i>alarming</i> with two strong signals or a flagged text, else <i>suspicious</i>.</summary>
    public static AccessVerdict CodeVerdict(int strongSignals, bool flagged, bool late) =>
        !late && (strongSignals >= 2 || flagged) ? AccessVerdict.Alarming : AccessVerdict.Suspicious;

    /// <summary>One lower, for a verdict below the threshold (§6.4).</summary>
    public static AccessVerdict Lower(AccessVerdict v) => v switch
    {
        AccessVerdict.Alarming => AccessVerdict.Suspicious,
        AccessVerdict.Suspicious => AccessVerdict.Unusual,
        _ => v,
    };

    public static AccessReviewState StateOf(AccessVerdict v) => v switch
    {
        AccessVerdict.Normal => AccessReviewState.Normal,
        AccessVerdict.Unusual => AccessReviewState.Unusual,
        AccessVerdict.Suspicious => AccessReviewState.Suspicious,
        _ => AccessReviewState.Alarming,
    };

    public static AccessVerdict? VerdictOf(string? text) => Enum.TryParse<AccessVerdict>(text, true, out var v) ? v : null;

    public static string Name(AccessVerdict v) => v.ToString().ToLowerInvariant();

    /// <summary>The source's network (§4.4): IPv4 /24, IPv6 /64; a MAC (WinBox) as it is.</summary>
    public static string? NetworkOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }
        if (!IPAddress.TryParse(address.Trim(), out var ip))
        {
            return address.Trim().ToLowerInvariant();
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        }
        for (var i = 8; i < 16; i++)
        {
            bytes[i] = 0;
        }
        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>
    /// An address that isn't private, CGNAT, link-local, loopback or unique-local. A MAC, or anything that isn't an address,
    /// isn't public.
    /// </summary>
    public static bool IsPublic(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address.Trim(), out var ip))
        {
            return false;
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (IPAddress.IsLoopback(ip))
        {
            return false;
        }
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 169 && b[1] == 254) || b[0] == 0);
        }
        return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC || ip.Equals(IPAddress.IPv6None));
    }

    public static bool InNetwork(string? address, string cidr)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }
        if (!IPAddress.TryParse(address.Trim(), out var ip))
        {
            return string.Equals(address.Trim(), cidr.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        return IPNetwork.TryParse(cidr.Trim(), out var network) ? network.Contains(ip)
            : IPAddress.TryParse(cidr.Trim(), out var single) && single.Equals(ip);
    }

    /// <summary>The trusted network the address is in, by name, or null (§4.4).</summary>
    public static string? TrustedName(string? address, IReadOnlyList<AccessTrustedNetwork> trusted) =>
        trusted.FirstOrDefault(t => InNetwork(address, t.Cidr))?.Name;

    /// <summary>
    /// Whether two accounts are the same: equal without case, or equal without their domains (<c>CONTOSO\jdoe</c>,
    /// <c>jdoe@contoso.local</c> and <c>jdoe</c>).
    /// </summary>
    public static bool SameAccount(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(Bare(a), Bare(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The account without its domain.</summary>
    public static string Bare(string account)
    {
        var a = account.Trim();
        if (a.LastIndexOf('\\') is var i and >= 0)
        {
            a = a[(i + 1)..];
        }
        if (a.IndexOf('@') is var j and > 0)
        {
            a = a[..j];
        }
        return a;
    }

    /// <summary>The hour of the week, in PNMT's local time: Sunday 0:00 is 0.</summary>
    public static int HourOfWeek(DateTime local) => (int)local.DayOfWeek * 24 + local.Hour;

    /// <summary>Whether an hour of the week is among the seen ones, an hour either side, round the week.</summary>
    public static bool NearHour(int hour, IReadOnlySet<int> seen) =>
        seen.Contains(hour) || seen.Contains((hour + 1) % 168) || seen.Contains((hour + 167) % 168);

    /// <summary>Whether a known access entry covers a login (§4.4).</summary>
    public static bool Covers(AccessKnownEntity k, AccessLogin l, DateTime now)
    {
        if (k.EndedAt is not null || k.Until is { } until && until <= now)
        {
            return false;
        }
        var scoped = k.Scope switch
        {
            "target" => k.Target == l.Target && (l.Target == AccessTargetKind.Pnmt || k.SensorId == l.SensorId),
            "location" => k.LocationId is not null && k.LocationId == l.LocationId && (k.Target is null || k.Target == l.Target),
            "all" => k.Target is null || k.Target == l.Target,
            _ => false,
        };
        return scoped
            && (k.Account is null || SameAccount(k.Account, l.Account))
            && (k.Source is null || InNetwork(l.Source, k.Source))
            && (k.Groups.Count == 0 || k.Groups.Contains(l.Group));
    }

    /// <summary>Whether an account is left out on this target (§4.6).</summary>
    public static bool Excluded(IReadOnlyList<AccessExcludedAccount> excluded, AccessLogin l) =>
        excluded.Any(e => SameAccount(e.Account, l.Account) && (e.SensorId is null || e.SensorId == l.SensorId));

    /// <summary>The shared accounts that name nobody (§5.3): Administrator, root, admin.</summary>
    public static bool Shared(string account) => Bare(account).ToLowerInvariant() is "administrator" or "admin" or "root" or "administrateur" or "administrador";
}
