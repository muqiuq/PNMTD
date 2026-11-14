using PNMTD.Models.Db;
using PNMTD.Zabbix;

namespace PNMTD.Api.V1.Contracts;

// Zabbix in PNMT (docs/audit-redmine-zabbix-plan.md §7): read-only; the hosts mirrored, the open problems in memory.

/// <summary>
/// <c>GET /zabbix/status</c>: whether Zabbix is configured, its version, where browsers open it, the host groups PNMT
/// shows (empty: all the token sees) and those Zabbix doesn't know, and when problems and hosts were last read.
/// </summary>
public sealed record ZabbixStatus(bool Enabled, string? Version, string? Url, IReadOnlyList<string> HostGroups, IReadOnlyList<string> GroupsNotFound,
    DateTime? AsOf, DateTime? HostsSyncedAt, string? Error);

/// <summary>An interface: Agent, SNMP, IPMI or JMX, its address and port, its availability with Zabbix's error.</summary>
public sealed record ZabbixInterfaceItem(string Type, string Address, string Port, ZabbixAvailability Availability, string? Error, bool Main);

public sealed record ZabbixTagItem(string Tag, string Value);

/// <summary>A severity's open problems, and how many of them nobody acknowledged.</summary>
public sealed record ZabbixSeverityCount(ZabbixSeverity Severity, int Count, int Unacknowledged);

/// <summary>A row of the overview's matrix: a host group or a tag's value, its counts by severity (index 0 Not classified to 5 Disaster).</summary>
public sealed record ZabbixCategoryRow(string Name, IReadOnlyList<int> Counts, int Total);

/// <summary>Hosts at a glance (§7.4): attached means a Zabbix sensor in PNMT.</summary>
public sealed record ZabbixHostCounts(int Total, int Monitored, int Disabled, int Unavailable, int InMaintenance, int Attached);

/// <summary>
/// <c>GET /zabbix/summary?category=group|tag:&lt;name&gt;&amp;suppressed=</c> (§7.4): tiles per severity, the matrix by
/// category, the tags the open problems carry, the hosts, from the snapshot as of <see cref="AsOf"/>.
/// </summary>
public sealed record ZabbixSummary(DateTime? AsOf, bool Stale, string? Error, string Category, IReadOnlyList<string> TagNames,
    IReadOnlyList<ZabbixSeverityCount> Severities, IReadOnlyList<ZabbixCategoryRow> Categories, ZabbixHostCounts Hosts, int Suppressed,
    string DashboardUrl);

/// <summary>A Zabbix host named in a problem.</summary>
public sealed record ZabbixHostRef(long Id, string Name);

/// <summary>An open problem (§7.5): what, how bad, since when, on which host (and its PNMT host), and its page in Zabbix.</summary>
public sealed record ZabbixProblemItem(string Id, string Name, string? OperationalData, ZabbixSeverity Severity, DateTime Since, bool Acknowledged,
    int Updates, bool Suppressed, IReadOnlyList<ZabbixTagItem> Tags, IReadOnlyList<ZabbixHostRef> Hosts, Ref? PnmtHost, string Url);

/// <summary>
/// <c>GET /zabbix/problems</c>: from <c>minSeverity</c>, of a host group, with a tag (<c>component</c> or
/// <c>component:storage</c>), on a host (<c>hostId</c>, or text in <c>host</c>), acknowledged or not, suppressed ones only
/// with <c>suppressed=true</c>, older than some minutes. Severity first, then newest; 50 a page.
/// </summary>
public sealed class ZabbixProblemQuery : PageQuery
{
    public ZabbixSeverity? MinSeverity { get; set; }

    public string? Group { get; set; }

    public string? Tag { get; set; }

    public string? Host { get; set; }

    public long? HostId { get; set; }

    public bool? Acknowledged { get; set; }

    public bool Suppressed { get; set; }

    public int? OlderThanMinutes { get; set; }
}

/// <summary>An update of a problem: who (Zabbix's user id: PNMT's token can't read names), when, what, the message.</summary>
public sealed record ZabbixUpdateItem(DateTime At, string User, IReadOnlyList<string> Actions, string? Message);

/// <summary>
/// <c>GET /zabbix/problems/{eventId}</c>, when a row opens: the trigger's description and URL, and, with Zabbix's Details,
/// the acknowledgements and their messages.
/// </summary>
public sealed record ZabbixProblemDetail(string Id, string? Description, string? TriggerUrl, IReadOnlyList<ZabbixUpdateItem>? Updates);

/// <summary>A host of the mirror (§7.6), with its open problems (not suppressed) and their worst severity.</summary>
public sealed record ZabbixHostItem(long Id, string Name, string TechnicalName, bool Monitored, bool InMaintenance, IReadOnlyList<string> Groups,
    IReadOnlyList<string> Addresses, IReadOnlyList<ZabbixInterfaceItem> Interfaces, ZabbixAvailability Availability, int Problems,
    ZabbixSeverity? WorstSeverity, Ref? PnmtHost, Guid? SensorId, bool Gone);

/// <summary>
/// <c>GET /zabbix/hosts</c>: <c>search</c> (names and addresses), a host group, <c>status</c> (monitored, disabled,
/// maintenance), an availability, attached to PNMT or not. By name, 50 a page.
/// </summary>
public sealed class ZabbixHostQuery : PageQuery
{
    public string? Group { get; set; }

    public string? Status { get; set; }

    public ZabbixAvailability? Availability { get; set; }

    public bool? Attached { get; set; }
}

/// <summary>The links into Zabbix of a host (§7.9).</summary>
public sealed record ZabbixHostLinks(string Open, string Dashboards, string Problems, string Latest, string Configuration);

public sealed record ZabbixFieldItem(string Name, string Value);

/// <summary>
/// <c>GET /zabbix/hosts/{hostId}</c> (§7.6), asked of Zabbix: its interfaces, groups, templates, tags, description, open
/// problems and links; the inventory with Zabbix's Details. When Zabbix doesn't answer, the mirror's, with the error.
/// </summary>
public sealed record ZabbixHostDetail(ZabbixHostItem Host, string? Description, IReadOnlyList<string> Templates, IReadOnlyList<ZabbixTagItem> Tags,
    IReadOnlyList<ZabbixFieldItem>? Inventory, IReadOnlyList<ZabbixProblemItem> Problems, ZabbixHostLinks Links, bool FromMirror, string? Error);

/// <summary>An item's latest value (§7.6), with its graph in Zabbix.</summary>
public sealed record ZabbixItemItem(string Id, string Name, string Key, string? Value, string? Units, DateTime? At, string? Error, string GraphUrl);

/// <summary><c>POST /zabbix/test</c>: the version, what the token sees, the host groups found and not found.</summary>
public sealed record ZabbixTestResult(string Version, int Hosts, int Problems, IReadOnlyList<string> HostGroups, IReadOnlyList<string> NotFound);
