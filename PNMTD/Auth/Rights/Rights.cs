using System.Security.Claims;

namespace PNMTD.Auth.Rights;

/// <summary>
/// What a group gives rights in (docs/audit-redmine-zabbix-plan.md §5.2): 16 areas, fixed in code. Users and groups
/// themselves are no area: only the admin flag manages them (§5.1).
/// </summary>
public enum Area
{
    Monitoring, Commands, Mail, Decisions, Locations, KnowledgeBase, Redmine, Zabbix, UdpNodes, Mikrotik, Servers, SshKeys,
    Diagnostics, Ai, System, AuditLog,
}

/// <summary>
/// The levels, by name. Each area has its own ordered list of them (<see cref="RightsCatalog"/>); a level includes the
/// ones before it in its area's list.
/// </summary>
public enum Level { None, View, Edit, Manage, Run, Read, Decide, Link, Details }

/// <summary>An area, its levels in order, and what each allows (the text of §5.2, for the group page and Your access).</summary>
public sealed record AreaInfo(Area Area, string Label, IReadOnlyList<(Level Level, string Allows)> Levels)
{
    /// <summary>1 for the first level, 0 for None or a level the area doesn't have.</summary>
    public int Rank(Level level) => Levels.Select((l, i) => (l.Level, i)).Where(x => x.Level == level).Select(x => x.i + 1).FirstOrDefault();
}

/// <summary>The fixed areas and levels of §5.2.</summary>
public static class RightsCatalog
{
    public static readonly IReadOnlyList<AreaInfo> Areas =
    [
        new(Area.Monitoring, "Monitoring", [
            (Level.View, "The dashboard, hosts, sensors, events, notification rules and the manager, DNS zones, mail input rules, the uplink, the search, and the hosts' and sensors' history."),
            (Level.Edit, "Create, change and delete them; sensor tokens and URLs; importance; notification assignments; UDP keys and counters; a router's WireGuard keys; an agent's settings; attaching Zabbix hosts.")]),
        new(Area.Commands, "Commands", [
            (Level.Run, "UDP commands, a MikroTik router's poll, backup now and commands, an agent's Collect now, a diagnostics run.")]),
        new(Area.Mail, "Mail", [(Level.Read, "The mail log and the mails' contents.")]),
        new(Area.Decisions, "Decisions", [(Level.Decide, "The decisions inbox. Decisions marked for admins stay with admins.")]),
        new(Area.Locations, "Locations", [
            (Level.View, "Locations, their fields' values and history."),
            (Level.Edit, "Create, rename and move locations, fill in their fields, set hosts' locations."),
            (Level.Manage, "Admin → Location fields, and deleting locations.")]),
        new(Area.KnowledgeBase, "Knowledge base", [
            (Level.View, "Pages, their history and files."),
            (Level.Edit, "Write, archive and restore pages, AI tags, the sensitive mark, uploads."),
            (Level.Manage, "Deleting pages and files, redacting, tags, Admin → Knowledge base.")]),
        new(Area.Redmine, "Redmine", [
            (Level.View, "The Redmine page, tickets, their panels and chips."),
            (Level.Link, "Linking and unlinking tickets.")]),
        new(Area.Zabbix, "Zabbix", [
            (Level.View, "The overview, problems, hosts and host pages."),
            (Level.Details, "Inventory and acknowledgement messages, which name people.")]),
        new(Area.UdpNodes, "UDP nodes", [(Level.Manage, "Admin → UDP nodes: firmware, updates, unknown senders.")]),
        new(Area.Mikrotik, "MikroTik & WireGuard", [
            (Level.Manage, "Admin → MikroTik & WireGuard: update jobs, PNMT's SSH keys, backup settings and keys, accepting host keys, deleting backups, tunnels, redacting commands.")]),
        new(Area.Servers, "Servers", [
            (Level.Manage, "Admin → Servers: join tokens, revoking, scripts and running them, run-as accounts, WMI, the agents' files and logs.")]),
        new(Area.SshKeys, "SSH keys", [(Level.Manage, "Admin → SSH keys: the key manager and removals.")]),
        new(Area.Diagnostics, "Diagnostics", [(Level.Manage, "Admin → Diagnostics: sets and assignments.")]),
        new(Area.Ai, "AI", [(Level.Manage, "Admin → AI: the work queue, backfills, analysis runs, the request log.")]),
        new(Area.System, "System", [
            (Level.Manage, "Key values, system health and task health, the debug page and its tests, checking the uplink now.")]),
        new(Area.AuditLog, "Audit log", [(Level.View, "Admin → Audit log and its export.")]),
    ];

    private static readonly Dictionary<Area, AreaInfo> ByArea = Areas.ToDictionary(a => a.Area);

    public static AreaInfo Of(Area area) => ByArea[area];

    /// <summary>"Monitoring:Edit" → (Monitoring, Edit), if the area has the level.</summary>
    public static bool TryParse(string? value, out Area area, out Level level)
    {
        area = default;
        level = Level.None;
        var parts = value?.Split(':');
        return parts is [var a, var l] && Enum.TryParse(a, out area) && Enum.TryParse(l, out level) && Of(area).Rank(level) > 0;
    }

    public static string Name(Area area, Level level) => $"{area}:{level}";
}

/// <summary>
/// What a signed-in user may do: the admin flag, or the highest level their groups give per area (§5.1). Read per request
/// from the session's claims (<see cref="SessionClaims.Admin"/>, <see cref="SessionClaims.Right"/>), never from the cookie.
/// </summary>
public sealed record UserRights(bool IsAdmin, IReadOnlyDictionary<Area, Level> Levels)
{
    public static readonly UserRights None = new(false, new Dictionary<Area, Level>());

    public bool Has(Area area, Level level)
    {
        if (IsAdmin)
        {
            return true;
        }
        var info = RightsCatalog.Of(area);
        return Levels.TryGetValue(area, out var held) && info.Rank(held) >= info.Rank(level) && info.Rank(level) > 0;
    }

    /// <summary>Several groups' levels together: the highest per area.</summary>
    public static IReadOnlyDictionary<Area, Level> Combine(IEnumerable<(Area Area, Level Level)> levels)
    {
        var result = new Dictionary<Area, Level>();
        foreach (var (area, level) in levels)
        {
            var info = RightsCatalog.Of(area);
            if (info.Rank(level) > 0 && (!result.TryGetValue(area, out var held) || info.Rank(level) > info.Rank(held)))
            {
                result[area] = level;
            }
        }
        return result;
    }

    public static UserRights Of(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return None;
        }
        var levels = Combine(principal.FindAll(SessionClaims.Right)
            .Select(c => RightsCatalog.TryParse(c.Value, out var a, out var l) ? (a, l) : (Area.Monitoring, Level.None)));
        return new UserRights(principal.HasClaim(SessionClaims.Admin, "true"), levels);
    }

    /// <summary>The claims a request carries for these rights.</summary>
    public IEnumerable<Claim> Claims() =>
        (IsAdmin ? [new Claim(SessionClaims.Admin, "true")] : Array.Empty<Claim>())
        .Concat(Levels.Select(kv => new Claim(SessionClaims.Right, RightsCatalog.Name(kv.Key, kv.Value))));
}

public static class RightsExtensions
{
    public static UserRights Rights(this ClaimsPrincipal principal) => UserRights.Of(principal);

    public static bool Has(this ClaimsPrincipal principal, Area area, Level level) => UserRights.Of(principal).Has(area, level);

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.HasClaim(SessionClaims.Admin, "true");
}
