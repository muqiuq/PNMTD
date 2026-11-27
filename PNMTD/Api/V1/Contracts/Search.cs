using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// <c>GET /search?q=</c>, for the global search: hosts and sensors whose name contains q, locations by name with their
/// paths, and pages (docs/locations-knowledge-plan.md §6.7) by number (<c>KB-42</c>, <c>42</c>), title, summary and tag.
/// <c>#backup</c> finds only the pages with a tag starting so. Archived pages are left out. With Redmine's View, tickets:
/// <c>1234</c> or <c>#1234</c> by number, anything else among the linked ones by subject (docs/audit-redmine-zabbix-plan.md §6.6).
/// With Zabbix's View, Zabbix hosts from the mirror by name or address (§7.6).
/// </summary>
public sealed record SearchResult(
    IReadOnlyList<SearchHost> Hosts,
    IReadOnlyList<SearchSensor> Sensors,
    IReadOnlyList<SearchLocation> Locations,
    IReadOnlyList<SearchPage> Pages,
    IReadOnlyList<SearchTicket> Tickets,
    IReadOnlyList<SearchZabbixHost> ZabbixHosts,
    IReadOnlyList<SearchLicense>? Licenses = null);

/// <summary>A Zabbix host: its visible name, the technical one, its first address.</summary>
public sealed record SearchZabbixHost(long Id, string Name, string TechnicalName, string? Address);

/// <summary>A Redmine ticket: <i>#1234 · Printer on floor 2</i> with its status.</summary>
public sealed record SearchTicket(int Id, string Subject, string Status, bool IsClosed);

public sealed record SearchHost(Guid Id, string Name, string? Location, bool Enabled);

public sealed record SearchSensor(Guid Id, string Name, SensorType Type, Guid HostId, string HostName);

public sealed record SearchLocation(Guid Id, string Name, string Path, string? Description);

/// <summary>A page, shown as <i>KB-42 · Printer setup</i> with its tags (§10.4).</summary>
public sealed record SearchPage(Guid Id, int Number, string Title, string? Summary, IReadOnlyList<string> Tags, KbPageStatus Status, bool Sensitive);
