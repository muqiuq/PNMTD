using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// <c>GET /search?q=</c>, for the global search: hosts and sensors whose name contains q, locations by name with their
/// paths, and pages (docs/locations-knowledge-plan.md §6.7) by number (<c>KB-42</c>, <c>42</c>), title, summary and tag.
/// <c>#backup</c> finds only the pages with a tag starting so. Archived pages are left out.
/// </summary>
public sealed record SearchResult(
    IReadOnlyList<SearchHost> Hosts,
    IReadOnlyList<SearchSensor> Sensors,
    IReadOnlyList<SearchLocation> Locations,
    IReadOnlyList<SearchPage> Pages);

public sealed record SearchHost(Guid Id, string Name, string? Location, bool Enabled);

public sealed record SearchSensor(Guid Id, string Name, SensorType Type, Guid HostId, string HostName);

public sealed record SearchLocation(Guid Id, string Name, string Path, string? Description);

/// <summary>A page, shown as <i>KB-42 · Printer setup</i> with its tags (§10.4).</summary>
public sealed record SearchPage(Guid Id, int Number, string Title, string? Summary, IReadOnlyList<string> Tags, KbPageStatus Status, bool Sensitive);
