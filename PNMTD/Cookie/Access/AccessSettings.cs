using System.Net;
using PNMTD.Api;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Access;

/// <summary>A verdict on a login (docs/cookie-access-plan.md §5.4): how suspicious it looks.</summary>
public enum AccessVerdict { Normal, Unusual, Suspicious, Alarming }

/// <summary>Who a verdict's notice reaches outside working hours (§6.1): nobody until they begin, the contacts who allow it, or every contact.</summary>
public enum AccessHours { Wait, Allowed, Everyone }

/// <summary>One verdict's row (§6.1): whether it tells the contacts, by which channels, who at night, and whether the person is asked.</summary>
public sealed record AccessVerdictRule(bool Notify = true, bool Email = true, bool Pushover = false, AccessHours Hours = AccessHours.Wait, bool Ask = true);

/// <summary>The three verdicts' rows (§6.1), selectable: one level only is three rows set alike.</summary>
public sealed record AccessVerdictRules
{
    public AccessVerdictRule Unusual { get; init; } = new(true, true, false, AccessHours.Wait, true);

    public AccessVerdictRule Suspicious { get; init; } = new(true, true, true, AccessHours.Allowed, true);

    public AccessVerdictRule Alarming { get; init; } = new(true, true, true, AccessHours.Everyone, true);

    public AccessVerdictRule? Of(AccessVerdict verdict) => verdict switch
    {
        AccessVerdict.Unusual => Unusual,
        AccessVerdict.Suspicious => Suspicious,
        AccessVerdict.Alarming => Alarming,
        _ => null,
    };
}

/// <summary>A contact (§6.2): a PNMT user or a group; for which targets; and whether they may be reached outside working hours.</summary>
public sealed record AccessContact(Guid? UserId, Guid? GroupId, bool Servers = true, bool Routers = true, bool Pnmt = true, bool OutsideHours = false)
{
    public bool For(AccessTargetKind target) => target switch
    {
        AccessTargetKind.Server => Servers,
        AccessTargetKind.Router => Routers,
        _ => Pnmt,
    };
}

/// <summary>Which logins are watched (§4.6).</summary>
public sealed record AccessKinds
{
    // Servers.
    public bool Console { get; init; } = true;

    public bool Rdp { get; init; } = true;

    public bool Ssh { get; init; } = true;

    public bool WinRm { get; init; } = true;

    /// <summary>Admins' network logons (SMB, RPC, WMI): on by default (review, answer 4).</summary>
    public bool AdminNetwork { get; init; } = true;

    /// <summary>sudo and su: only <i>new target</i> and <i>success after failures</i> count.</summary>
    public bool Elevation { get; init; } = true;

    public bool Explicit { get; init; }

    public bool Batch { get; init; }

    public bool Unlock { get; init; }

    public bool Cached { get; init; }

    public bool Sftp { get; init; }

    public bool RdpConnection { get; init; }

    // Routers.
    public bool RouterWinbox { get; init; } = true;

    public bool RouterWeb { get; init; } = true;

    public bool RouterApi { get; init; } = true;

    public bool RouterSsh { get; init; } = true;

    public bool RouterTelnet { get; init; } = true;

    public bool RouterFtp { get; init; } = true;

    public bool RouterConsole { get; init; } = true;

    // PNMT.
    public bool PnmtWeb { get; init; } = true;

    public bool PnmtMcp { get; init; } = true;

    public bool Watched(AccessTargetKind target, LogonKind? kind, bool isAdmin, string way) => target switch
    {
        AccessTargetKind.Pnmt => way == "mcp" ? PnmtMcp : PnmtWeb,
        AccessTargetKind.Router => kind switch
        {
            LogonKind.Winbox => RouterWinbox,
            LogonKind.Web => RouterWeb,
            LogonKind.Api => RouterApi,
            LogonKind.Ssh => RouterSsh,
            LogonKind.Telnet => RouterTelnet,
            LogonKind.Ftp => RouterFtp,
            LogonKind.Console => RouterConsole,
            _ => false,
        },
        _ => kind switch
        {
            LogonKind.Console => Console,
            LogonKind.Rdp => Rdp,
            LogonKind.Ssh => Ssh,
            LogonKind.WinRm => WinRm,
            LogonKind.Network => AdminNetwork && isAdmin,
            LogonKind.Sudo or LogonKind.Su => Elevation,
            LogonKind.Explicit => Explicit,
            LogonKind.Batch => Batch,
            LogonKind.Unlock => Unlock,
            LogonKind.Cached => Cached,
            LogonKind.Sftp => Sftp,
            LogonKind.RdpConnection => RdpConnection,
            _ => false,
        },
    };
}

/// <summary>An account left out (§4.6): a backup or monitoring account; on one target, or everywhere. Its strong signals still count.</summary>
public sealed record AccessExcludedAccount(string Account, Guid? SensorId = null, string Comment = "");

/// <summary>How an account is linked to a person (§5.3, review answer 3): each way on or off.</summary>
public sealed record AccessAccountLinks(bool Key = true, bool ServerAccounts = true, bool Username = true)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Any => Key || ServerAccounts || Username;
}

/// <summary><i>Was this you?</i> (§6.6): on, for which targets, the link's validity, at most so many a day per person.</summary>
public sealed record AccessAskSettings(bool On = true, bool Pnmt = true, bool Servers = true, bool Routers = true, int ValidHours = 24, int PerPersonDay = 5)
{
    public bool For(AccessTargetKind target) => On && target switch
    {
        AccessTargetKind.Server => Servers,
        AccessTargetKind.Router => Routers,
        _ => Pnmt,
    };
}

/// <summary>A trusted network (§4.4): context for Cookie, never a pass.</summary>
public sealed record AccessTrustedNetwork(string Name, string Cidr);

/// <summary>The access notices' limits (§6.4): per contact a day and an hour. <i>Alarming</i> is never held back.</summary>
public sealed record AccessLimits(int PerContactDay = 20, int PerContactHour = 5);

/// <summary>
/// Admin → Cookie → Access (docs/cookie-access-plan.md §8): the watch, its targets, the routers' default, the kinds watched,
/// excluded accounts, how accounts are linked to people, the verdicts' rows, <i>Was this you?</i>, the contacts, trusted networks,
/// the history and learning period, and code's notice when Cookie can't judge. Versioned like every section.
/// </summary>
public sealed record AccessSettings : ICookieSection
{
    public static string Section => "access";

    public const int MaxContacts = 50;

    /// <summary>Off until an admin turns it on. It runs while Cookie is stopped too, for code's notice (§6.5).</summary>
    public bool On { get; init; }

    public bool Servers { get; init; } = true;

    public bool Routers { get; init; } = true;

    public bool Pnmt { get; init; } = true;

    /// <summary>For servers: only these locations' and hosts'; every server when both are empty.</summary>
    public List<Guid> ServerLocations { get; init; } = [];

    public List<Guid> ServerHosts { get; init; } = [];

    public List<Guid> ExcludedHosts { get; init; } = [];

    /// <summary>A new router's switch (§4.2), and the existing routers' when the router watch is first switched on.</summary>
    public bool WatchNewRouters { get; init; } = true;

    public AccessKinds Kinds { get; init; } = new();

    public List<AccessExcludedAccount> Excluded { get; init; } = [];

    public AccessAccountLinks Links { get; init; } = new();

    public AccessVerdictRules Notices { get; init; } = new();

    public AccessAskSettings WasThisYou { get; init; } = new();

    public List<AccessContact> Contacts { get; init; } = [];

    public List<AccessTrustedNetwork> Trusted { get; init; } = [];

    public int HistoryDays { get; init; } = 90;

    public int LearningDays { get; init; } = 14;

    /// <summary>Code's notice for strong signals when Cookie can't judge (§6.5): on by default (review, answer 6).</summary>
    public bool CodeNotice { get; init; } = true;

    public AccessLimits Limits { get; init; } = new();

    public bool Watches(AccessTargetKind target) => On && target switch
    {
        AccessTargetKind.Server => Servers,
        AccessTargetKind.Router => Routers,
        _ => Pnmt,
    };

    public void Validate(ApiValidator v)
    {
        v.Check(HistoryDays is >= 7 and <= 180, "historyDays", "7 to 180 days (the logins are kept 180).");
        v.Check(LearningDays is >= 0 and <= 60, "learningDays", "0 to 60 days.");
        v.Check(Contacts.Count <= MaxContacts, "contacts", $"At most {MaxContacts}.");
        for (var i = 0; i < Contacts.Count; i++)
        {
            var c = Contacts[i];
            v.Check(c.UserId is null != (c.GroupId is null), $"contacts.{i}", "A user or a group.");
            v.Check(c.Servers || c.Routers || c.Pnmt, $"contacts.{i}", "For at least one kind of target.");
        }
        for (var i = 0; i < Trusted.Count; i++)
        {
            v.Check(Trusted[i].Name.Trim().Length is > 0 and <= 100, $"trusted.{i}.name", "1 to 100 characters.");
            v.Check(IPNetwork.TryParse(Trusted[i].Cidr?.Trim() ?? "", out _), $"trusted.{i}.cidr", "A network such as 10.0.0.0/8 or 2001:db8::/32.");
        }
        v.Check(Trusted.Count <= 100, "trusted", "At most 100.");
        for (var i = 0; i < Excluded.Count; i++)
        {
            v.Check(Excluded[i].Account.Trim().Length is > 0 and <= 520, $"excluded.{i}.account", "1 to 520 characters.");
            v.Check(Excluded[i].Comment.Length <= 300, $"excluded.{i}.comment", "At most 300 characters.");
        }
        v.Check(Excluded.Count <= 200, "excluded", "At most 200.");
        v.Check(WasThisYou.ValidHours is >= 1 and <= 168, "wasThisYou.validHours", "1 to 168 hours.");
        v.Check(WasThisYou.PerPersonDay is >= 1 and <= 20, "wasThisYou.perPersonDay", "1 to 20 a day.");
        v.Check(Limits.PerContactDay is >= 1 and <= 100, "limits.perContactDay", "1 to 100 a day.");
        v.Check(Limits.PerContactHour >= 1 && Limits.PerContactHour <= Limits.PerContactDay, "limits.perContactHour", "1 to the limit a day.");
        v.Check(ServerLocations.Count + ServerHosts.Count + ExcludedHosts.Count <= 2000, "serverHosts", "At most 2000 locations and hosts.");
    }
}
