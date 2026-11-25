namespace PNMTD.Api.V1.Contracts;

// Responsibilities (docs/responsibilities-plan.md §11): who owns an area of work, with a deputy, its links, the global
// responsible and deputy. Information only: nothing is sent to these people. Not the availability plan's responsible of the day.

/// <summary>A person a responsibility names: <see cref="Active"/> false once the account is deactivated.</summary>
public sealed record ResponsibilityPerson(Guid Id, string Name, bool Active);

/// <summary>
/// The global responsible and deputy (§5): they own what no responsibility covers, and stand in for a responsibility without an
/// active responsible or deputy.
/// </summary>
public sealed record ResponsibilityGlobal(ResponsibilityPerson? Responsible, ResponsibilityPerson? Deputy, DateTime UpdatedAt, string? UpdatedByName);

/// <summary>
/// A row of the list (§9.2). <see cref="GlobalStandsIn"/>: neither its responsible nor its deputy is set and active.
/// <see cref="Attention"/>: nobody set, or a person inactive. <see cref="Color"/>: <c>#rrggbb</c> or null
/// (docs/responsibilities-extensions-plan.md §4).
/// </summary>
public sealed record ResponsibilityItem(
    Guid Id,
    string Name,
    string? Summary,
    string? Color,
    ResponsibilityPerson? Responsible,
    ResponsibilityPerson? Deputy,
    bool GlobalStandsIn,
    bool Attention,
    int Links,
    int Combinations,
    DateTime UpdatedAt,
    string? UpdatedByName);

/// <summary>The page's list, with the global people on top (§9.2). Without Redmine configured, no combination counts.</summary>
public sealed record ResponsibilityList(ResponsibilityGlobal Global, IReadOnlyList<ResponsibilityItem> Items, bool RedmineConfigured);

/// <summary>A responsibility's link: the object as a page's links show it (<see cref="KbLinkItem"/>), and the link's own id.</summary>
public sealed record ResponsibilityLinkItem(Guid LinkId, KbLinkKind Kind, Guid Id, string Name, string? Detail, bool Exists, int? Number, bool Archived);

/// <summary>
/// A responsibility in full (§9.3). <see cref="Revision"/> is what a save of the description sends back; <see cref="Files"/>
/// are those its description references, for attachment chips.
/// </summary>
public sealed record ResponsibilityDetail(
    Guid Id,
    string Name,
    string? Summary,
    string? Color,
    string Description,
    int Revision,
    ResponsibilityPerson? Responsible,
    ResponsibilityPerson? Deputy,
    bool GlobalStandsIn,
    bool Attention,
    ResponsibilityGlobal Global,
    IReadOnlyList<ResponsibilityLinkItem> Links,
    IReadOnlyList<FileItem> Files,
    bool RedmineConfigured,
    IReadOnlyList<RedmineCombinationItem> Redmine,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime UpdatedAt,
    string? UpdatedByName);

/// <summary>
/// A responsibility as a linked host, location or page shows it (§8): only when one links it, and only with
/// <i>Responsibilities: View</i>.
/// </summary>
public sealed record ResponsibilityRef(Guid Id, string Name, string? Summary, string? Color, ResponsibilityPerson? Responsible, ResponsibilityPerson? Deputy,
    bool GlobalStandsIn, ResponsibilityPerson? GlobalResponsible, ResponsibilityPerson? GlobalDeputy);

/// <summary>
/// Create and change (§11): the whole form. A null person means nobody; a null <see cref="Color"/> no colour
/// (docs/responsibilities-extensions-plan.md §4.2).
/// </summary>
public sealed class ResponsibilityRequest
{
    public string? Name { get; init; }

    public string? Summary { get; init; }

    /// <summary><c>#rrggbb</c>, or null for none.</summary>
    public string? Color { get; init; }

    public Guid? ResponsibleId { get; init; }

    public Guid? DeputyId { get; init; }
}

/// <summary>
/// The long description (§4.1): the Markdown, the revision it started from (409 <c>edit_conflict</c>), and whether lines that
/// look like credentials were acknowledged (422 <c>looks_like_credential</c>).
/// </summary>
public sealed class ResponsibilityDescriptionRequest
{
    public string? Content { get; init; }

    public int Revision { get; init; }

    public bool AcknowledgeCredentials { get; init; }
}

/// <summary>The colour alone, from the overview's side panel (docs/responsibilities-extensions-plan.md §4.2): <c>#rrggbb</c> or null.</summary>
public sealed class ResponsibilityColorRequest
{
    public string? Color { get; init; }
}

/// <summary>The global responsible and deputy: both, a null meaning nobody.</summary>
public sealed class ResponsibilityGlobalRequest
{
    public Guid? ResponsibleId { get; init; }

    public Guid? DeputyId { get; init; }
}

/// <summary>A link to a page, a location or a host (§4.1); a sensor is refused.</summary>
public sealed class ResponsibilityLinkRequest
{
    public KbLinkKind? Kind { get; init; }

    public Guid? Id { get; init; }
}

/// <summary>Whom a responsibility can name: the active users, with whether a Redmine user is linked (never contact details).</summary>
public sealed record ResponsibilityPeople(IReadOnlyList<ResponsibilityPersonOption> Users);

public sealed record ResponsibilityPersonOption(Guid Id, string Name, string Username, bool RedmineLinked);

/// <summary>A history row (§10.5); a description's texts come with <c>GET …/history/{rowId}</c>.</summary>
/// <remarks><see cref="Via"/>: "mcp" for a change made over MCP (docs/mcp-extensions-plan.md §8.2).</remarks>
public sealed record ResponsibilityHistoryItem(Guid Id, DateTime At, string ByName, Models.Db.ResponsibilityHistoryKind Kind, string? Detail,
    string? Via);

/// <summary>A history row with the description before and after it.</summary>
public sealed record ResponsibilityHistoryDetail(Guid Id, DateTime At, string ByName, Models.Db.ResponsibilityHistoryKind Kind, string? Detail,
    string? OldValue, string? NewValue, string? Via);

/// <summary>
/// A Redmine project and a tracker (null: every tracker), listed on a responsibility with the user who handles it (§6). Names are
/// Redmine's current ones where PNMT has its lookups, else those copied at the last save. <see cref="Warning"/>: <i>Not a project
/// PNMT reads</i>, <i>Unknown tracker #7</i>. <see cref="User"/> null: the user was removed.
/// </summary>
public sealed record RedmineCombinationItem(
    Guid Id,
    Guid ResponsibilityId,
    string ResponsibilityName,
    string? ResponsibilityColor,
    int ProjectId,
    string ProjectName,
    string ProjectPath,
    int? TrackerId,
    string? TrackerName,
    ResponsibilityPerson? User,
    bool UserRedmineLinked,
    string? Note,
    string? Warning,
    DateTime UpdatedAt);

/// <summary>Every combination (§9.5), ordered by project path, then every tracker first, then the tracker's name.</summary>
public sealed record RedmineCombinationList(IReadOnlyList<RedmineCombinationItem> Items);

/// <summary>A Redmine project PNMT reads, for the picker: its path through its parents ("IT Support › Printers").</summary>
public sealed record RedmineCombinationProject(int Id, string Name, int? ParentId, string Path, int Depth);

/// <summary>What the combination dialog picks from (§6.2), from the worker's lookups; <see cref="Error"/> when Redmine can't be read.</summary>
public sealed record ResponsibilityRedmineLookups(bool Configured, IReadOnlyList<RedmineCombinationProject> Projects, IReadOnlyList<RedmineNamed> Trackers,
    string? Error);

/// <summary>
/// A combination (§6.1): a project PNMT reads, a tracker or none (every tracker), an active user, a note. On a change,
/// <see cref="ResponsibilityId"/> moves it to another responsibility.
/// </summary>
public sealed class RedmineCombinationRequest
{
    public int? ProjectId { get; init; }

    public int? TrackerId { get; init; }

    public Guid? UserId { get; init; }

    public string? Note { get; init; }

    public Guid? ResponsibilityId { get; init; }
}

// --- the Links tab (docs/responsibilities-extensions-plan.md §5) -------------------------------------------------------------

/// <summary>A responsibility as a row names it: its name and colour.</summary>
public sealed record ResponsibilityBrief(Guid Id, string Name, string? Color);

/// <summary>
/// A link on the Links tab (§5.1); <see cref="Id"/> is the link's. The object by name, with <see cref="Detail"/> where it is: a host's
/// location path, a location's parent's path. A page has its <see cref="Number"/> instead.
/// </summary>
public sealed record ResponsibilityLinkRow(Guid Id, ResponsibilityBrief Responsibility, KbLinkKind Kind, Guid TargetId, string Name, string? Detail,
    int? Number, bool Archived, DateTime CreatedAt, string? CreatedByName);

/// <summary>
/// A host or a location that no responsibility links itself (§5.1); <see cref="Id"/> is the object's. <see cref="ThroughLocation"/>: the
/// responsibilities that link its location or a parent of it, which it doesn't inherit.
/// </summary>
public sealed record ResponsibilityUnlinkedRow(Guid Id, KbLinkKind Kind, string Name, string? Detail, IReadOnlyList<ResponsibilityBrief> ThroughLocation);

/// <summary>The Links tab's filters (§5.1). Sort: <c>responsibility</c> (the default), <c>object</c>, <c>kind</c>, <c>linked</c>.</summary>
public sealed class ResponsibilityLinksQuery : PageQuery
{
    /// <summary>Only these responsibilities' links.</summary>
    public List<Guid>? Responsibility { get; set; }

    /// <summary>The links of the responsibilities where this user is the responsible or the deputy.</summary>
    public Guid? Person { get; set; }

    public List<KbLinkKind>? Kind { get; set; }

    /// <summary>The hosts in it and the location itself, with <see cref="Below"/> its sub-locations too. Pages have no location.</summary>
    public Guid? Location { get; set; }

    public bool Below { get; set; } = true;
}

/// <summary>The hosts or locations no responsibility links (§5.1). Sort: <c>object</c> (the default), <c>where</c>.</summary>
public sealed class ResponsibilityUnlinkedQuery : PageQuery
{
    public Guid? Location { get; set; }

    public bool Below { get; set; } = true;

    /// <summary>Leaves out those whose location or a parent of it is linked: covered, in this view only.</summary>
    public bool Covered { get; set; }
}

/// <summary>Pages, locations and hosts linked to one responsibility at once (§5.2), 1 to 500.</summary>
public sealed class ResponsibilityBulkLinkRequest
{
    public Guid? ResponsibilityId { get; init; }

    public List<ResponsibilityLinkRequest>? Targets { get; init; }
}

/// <summary>Links unlinked at once, of any responsibilities (§5.2), 1 to 500.</summary>
public sealed class ResponsibilityBulkUnlinkRequest
{
    public List<Guid>? LinkIds { get; init; }
}

/// <summary>
/// The links' objects linked to another responsibility (§5.2): <i>Move to…</i>, or with <see cref="Keep"/> <i>Also link to…</i>.
/// </summary>
public sealed class ResponsibilityMoveLinksRequest
{
    public List<Guid>? LinkIds { get; init; }

    public Guid? ResponsibilityId { get; init; }

    public bool Keep { get; init; }
}

/// <summary>An object a bulk change left alone: <c>already_linked</c>, or <c>not_found</c> when it is gone.</summary>
public sealed record ResponsibilitySkipped(KbLinkKind Kind, Guid Id, string Name, string Reason);

public sealed record ResponsibilityBulkResult(int Linked, int Unlinked, IReadOnlyList<ResponsibilitySkipped> Skipped);

/// <summary>The error codes of responsibilities (§11).</summary>
public static class ResponsibilityErrors
{
    public const string DuplicateName = "duplicate_name";
    public const string AlreadyLinked = "already_linked";
    public const string NotFound = "not_found";
    public const string TooManyLinks = "too_many_links";
    public const string DuplicateCombination = "duplicate_combination";
    public const string RedmineNotConfigured = "redmine_not_configured";
    public const string RedmineUnreachable = "redmine_unreachable";
}
