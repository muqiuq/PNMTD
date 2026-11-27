using PNMTD.Auth.Rights;
using PNMTD.Models.Db;

namespace PNMTD.Mcp;

/// <summary>
/// What a token may do (docs/mcp-plan.md §5.3): its user's rights, an admin counting as every area's highest level, capped
/// by the MCP ceiling, and for the read-only token by the reading levels. Never the admin flag. One place, in code.
/// </summary>
public static class McpRights
{
    /// <summary>
    /// The most the read-write token gets per area. The areas that aren't here (UDP nodes, MikroTik &amp; WireGuard, Servers,
    /// SSH keys, Diagnostics, AI, System, Audit log, MCP, Decisions, Connectors, Lounge) are PNMT's settings and admin pages, or approvals.
    /// </summary>
    public static readonly IReadOnlyDictionary<Area, Level> Ceiling = new Dictionary<Area, Level>
    {
        [Area.Monitoring] = Level.Edit,
        [Area.Commands] = Level.Run,
        [Area.Mail] = Level.Read,
        [Area.Locations] = Level.Edit,
        [Area.KnowledgeBase] = Level.Edit,
        [Area.Redmine] = Level.Link,
        [Area.Zabbix] = Level.Details,
        [Area.Cookie] = Level.Instruct,
        [Area.Availability] = Level.View,
        // docs/ssl-certificate-plan.md §6.4: the sensors and Check now; accepting a change and the private CAs are no tools.
        [Area.Certificates] = Level.Edit,
        // docs/network-map-plan.md §10.2: connections, suggestions and assignments; scans of many routers and the schedule stay in the UI.
        [Area.NetworkMap] = Level.Edit,
        // docs/responsibilities-plan.md §7.2: reading them; docs/mcp-extensions-plan.md §8: creating and changing them (Edit), never deleting.
        [Area.Responsibilities] = Level.Edit,
        // docs/mcp-extensions-plan.md §9: the boards without their URLs, and messages to them.
        [Area.Boards] = Level.Message,
        // docs/licences-plan.md §9, §11: reading; keys and documents are no tool's at any level.
        [Area.Licenses] = Level.View,
    };

    /// <summary>
    /// The read-only token: the reading levels only, Monitoring's <i>Details</i> among them (docs/cookie-object-notes-plan.md §4.4).
    /// <i>Cookie: Use</i> is for questions.
    /// </summary>
    public static readonly IReadOnlyDictionary<Area, Level> ReadOnlyCap = new Dictionary<Area, Level>
    {
        [Area.Monitoring] = Level.Details,
        [Area.Mail] = Level.Read,
        [Area.Locations] = Level.View,
        [Area.KnowledgeBase] = Level.View,
        [Area.Redmine] = Level.View,
        [Area.Zabbix] = Level.Details,
        [Area.Cookie] = Level.Use,
        [Area.Availability] = Level.View,
        [Area.Certificates] = Level.View,
        [Area.NetworkMap] = Level.Details,
        [Area.Responsibilities] = Level.View,
        [Area.Boards] = Level.View,
        [Area.Licenses] = Level.View,
    };

    public static UserRights For(UserRights user, McpTokenKind kind)
    {
        var cap = kind == McpTokenKind.Write ? Ceiling : ReadOnlyCap;
        var levels = new Dictionary<Area, Level>();
        foreach (var (area, max) in cap)
        {
            var info = RightsCatalog.Of(area);
            var held = user.IsAdmin ? info.Levels[^1].Level : user.Levels.GetValueOrDefault(area, Level.None);
            var rank = Math.Min(info.Rank(held), info.Rank(max));
            if (rank > 0)
            {
                levels[area] = info.Levels[rank - 1].Level;
            }
        }
        return new UserRights(false, levels);
    }
}
