using System.Security.Claims;

namespace PNMTD.Auth.Rights;

/// <summary>
/// What a group gives rights in (docs/audit-redmine-zabbix-plan.md §5.2): 17 areas, fixed in code (Cookie since
/// docs/cookie-plan.md §10.1). Users and groups
/// themselves are no area: only the admin flag manages them (§5.1).
/// </summary>
public enum Area
{
    Monitoring, Commands, Mail, Decisions, Locations, KnowledgeBase, Redmine, Zabbix, UdpNodes, Mikrotik, Servers, SshKeys,
    Diagnostics, Ai, System, AuditLog, Cookie, Availability, Mcp, Certificates, Connectors, NetworkMap,
}

/// <summary>
/// The levels, by name. Each area has its own ordered list of them (<see cref="RightsCatalog"/>); a level includes the
/// ones before it in its area's list.
/// </summary>
public enum Level { None, View, Edit, Manage, Run, Read, Decide, Link, Details, Use, Instruct }

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
            (Level.View, "The dashboard, hosts, sensors, events, notification rules and the manager, DNS zones, mail input rules, the uplink, the search, the hosts' and sensors' history, and advanced sensors' reports."),
            (Level.Details, "View, plus what names people: servers' and routers' logins and the access cases and notices about them, a server's event log, journal and SSH keys, and the reports of advanced sensors whose data names people."),
            (Level.Edit, "Details, plus create, change and delete them; sensor tokens and URLs; importance; notification assignments and the Pushover priorities; UDP keys and counters; a router's WireGuard keys; an agent's settings; attaching Zabbix hosts; advanced sensors' rules and fields, and deleting their reports.")]),
        new(Area.Commands, "Commands", [
            (Level.Run, "UDP commands, a MikroTik router's poll, backup now, network scan and commands, an agent's Collect now, a diagnostics run.")]),
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
        new(Area.Cookie, "Cookie", [
            (Level.Use, "Cookie's window: the cases and comments on them, the trace (within the person's other rights), and the chat with Cookie."),
            (Level.Instruct, "Use, plus stopping a case and letting Cookie go on with it, mute and suppress, and asking the big brother."),
            (Level.Manage, "Instruct, plus Admin → Cookie: the configuration, start, stop and run now.")]),
        new(Area.Availability, "Availability plan", [
            (Level.View, "The availability plan: who is responsible, who works and where. Everyone in the plan sees it and edits their own column."),
            (Level.Edit, "View, plus every column, the past, the responsible, the fallback, the comments, and Fill for everyone."),
            (Level.Manage, "Edit, plus the plan's people and settings: the types, the count columns, the working hours and holidays, the global fallback.")]),
        new(Area.Mcp, "MCP", [
            (Level.View, "Admin → MCP: the MCP log of everyone, and every user's tokens and their state."),
            (Level.Manage, "View, plus turning anyone's token off.")]),
        // docs/ssl-certificate-plan.md §6.4: the sensors themselves, their state and events stay Monitoring's.
        new(Area.Certificates, "Certificates", [
            (Level.View, "The SSL certificate sensors' Certificate tab: the certificate, its chain, the connection, the certificates seen and their PEM; the list of private CAs."),
            (Level.Edit, "View, plus creating, changing and deleting SSL certificate sensors (with Monitoring: Edit), Check now, and accepting a changed certificate."),
            (Level.Manage, "Edit, plus Admin → Certificates: adding and removing private CAs.")]),
        // docs/connectors-plan.md §12.1: as strong as running scripts on servers, for the callers it lets in.
        new(Area.Connectors, "3rd Party Connectors", [
            (Level.View, "3rd Party Connectors: every call, the connectors and their callers, and the output of their runs."),
            (Level.Manage, "View, plus adding, changing and deleting connectors, including the script a connector runs on a server and the account it runs as; adding and changing callers, their networks and grants, showing and rotating their keys; redacting and cancelling calls. As strong as running scripts on servers, for the callers it lets in.")]),
        // docs/network-map-plan.md §9.1: a scan of one router is Commands: Run, as a poll is.
        new(Area.NetworkMap, "Network map", [
            (Level.View, "The network map, its connections, suggestions and scans, with the names of the routers and hosts on it."),
            (Level.Details, "View, plus the network interfaces: MAC and IP addresses, DHCP host names, Wi-Fi clients, which can name people."),
            (Level.Edit, "Details, plus creating, changing and deleting connections; accepting, dismissing and restoring suggestions; assigning and ignoring interfaces; saving where the routers are on the map, and putting them back to automatic."),
            (Level.Manage, "Edit, plus scans of all routers or of a location, cancelling scans, and the schedule and settings.")]),
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
