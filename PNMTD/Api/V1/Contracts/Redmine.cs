namespace PNMTD.Api.V1.Contracts;

// Redmine in PNMT (docs/audit-redmine-zabbix-plan.md §6): read-only, PNMT's links, a copy of linked tickets' headers.

/// <summary><c>GET /redmine/status</c>: whether Redmine is configured, where browsers open it, and the projects PNMT shows.</summary>
public sealed record RedmineStatus(bool Enabled, string? Url, IReadOnlyList<RedmineProjectItem> Projects, string? Error);

public sealed record RedmineProjectItem(int Id, string Identifier, string Name, int? ParentId, string Url);

public sealed record RedmineNamed(int Id, string Name);

public sealed record RedmineStatusItem(int Id, string Name, bool IsClosed);

public sealed record RedminePriorityItem(int Id, string Name, bool IsDefault);

/// <summary><c>GET /redmine/lookups</c>: for the filters and the names in a ticket's history; cached an hour.</summary>
public sealed record RedmineLookupsItem(IReadOnlyList<RedmineProjectItem> Projects, IReadOnlyList<RedmineNamed> Trackers,
    IReadOnlyList<RedmineStatusItem> Statuses, IReadOnlyList<RedminePriorityItem> Priorities, IReadOnlyList<RedmineNamed> Members);

/// <summary>A ticket in a list: number, what it is, who has it, and how many links it has in PNMT.</summary>
public sealed record RedmineIssueItem(int Id, int ProjectId, string ProjectName, string? Tracker, string Status, bool IsClosed, string? Priority,
    string Subject, string? AssignedTo, DateTime UpdatedOn, string? DueDate, string Url, int Links);

/// <summary>
/// <c>GET /redmine/issues</c>: <c>search</c> (subject, description, notes; a number opens it), the configured projects or
/// one of them, status (open by default, closed, all), tracker, priority, assignee (an id, or "none"), overdue, only
/// tickets linked in PNMT, only open tickets without a change for <c>Redmine__StaleDays</c> (Cookie's notes don't count). Sort:
/// updated (default), created, priority, id; with "-" for ascending.
/// </summary>
public sealed class RedmineQuery : PageQuery
{
    public int? Project { get; set; }

    public string? Status { get; set; }

    public int? Tracker { get; set; }

    public int? Priority { get; set; }

    public string? Assignee { get; set; }

    public bool Overdue { get; set; }

    public bool Linked { get; set; }

    /// <summary>Open tickets nobody but Cookie changed for <c>Redmine__StaleDays</c>: the overview's tile.</summary>
    public bool Stale { get; set; }
}

/// <summary><c>GET /redmine/overview</c> (§6.3): the most important tickets, cached a minute.</summary>
public sealed record RedmineOverview(DateTime AsOf, int StaleDays, RedmineOverviewTiles Tiles, IReadOnlyList<RedmineIssueItem> Urgent,
    IReadOnlyList<RedmineIssueItem> Unassigned, IReadOnlyList<RedmineIssueItem> Overdue, IReadOnlyList<RedmineIssueItem> Problems,
    IReadOnlyList<RedmineIssueItem> Stale, IReadOnlyList<RedmineIssueItem> Recent);

public sealed record RedmineOverviewTiles(int Open, int Urgent, int Unassigned, int Overdue, int Stale);

/// <summary>A text of Redmine's, ready for the UI: Markdown for <c>MarkdownView</c>, or plain text.</summary>
public sealed record RedmineText(string Format, string Text);

public sealed record RedmineChange(string Field, string? Old, string? New);

public sealed record RedmineJournalItem(int Id, string? User, DateTime CreatedOn, RedmineText? Notes, IReadOnlyList<RedmineChange> Changes,
    bool Private);

public sealed record RedmineAttachmentItem(int Id, string Filename, long Size, string? ContentType, string? Author, DateTime CreatedOn, string Url);

public sealed record RedmineRelatedItem(int Id, string Relation, string? Subject, string? Status, bool? IsClosed);

public sealed record RedmineFieldItem(string Name, string Value);

/// <summary><c>GET /redmine/issues/{id}</c> (§6.4): a ticket in full, and where it is linked in PNMT.</summary>
public sealed record RedmineIssueDetail(
    int Id,
    RedmineProjectItem Project,
    string? Tracker,
    string Status,
    bool IsClosed,
    string? Priority,
    string? Author,
    string? AssignedTo,
    string Subject,
    RedmineText? Description,
    string? StartDate,
    string? DueDate,
    int? DoneRatio,
    double? EstimatedHours,
    DateTime CreatedOn,
    DateTime UpdatedOn,
    DateTime? ClosedOn,
    bool IsPrivate,
    IReadOnlyList<RedmineFieldItem> Fields,
    IReadOnlyList<RedmineJournalItem> Journals,
    IReadOnlyList<RedmineAttachmentItem> Attachments,
    IReadOnlyList<RedmineRelatedItem> Related,
    IReadOnlyList<RedmineLinkItem> Links,
    string Url);

public enum RedmineLinkKind { Page, Location, Host, Sensor }

/// <summary>Where a ticket is linked: the object (its name now, or that it is gone), the note, who linked it.</summary>
public sealed record RedmineLinkItem(Guid Id, int IssueId, RedmineLinkKind Kind, Guid TargetId, string TargetName, string? Note, DateTime CreatedAt,
    string? CreatedByName);

/// <summary><c>POST /redmine/links</c>: a ticket of the configured projects, to exactly one page, location, host or sensor.</summary>
public sealed class RedmineLinkRequest
{
    public int IssueId { get; init; }

    public Guid? PageId { get; init; }

    public Guid? LocationId { get; init; }

    public Guid? HostId { get; init; }

    public Guid? SensorId { get; init; }

    public string? Note { get; init; }
}

public sealed class RedmineLinkNoteRequest
{
    public string? Note { get; init; }
}

/// <summary>A linked ticket in a panel: the link, and the ticket from PNMT's copy.</summary>
public sealed record RedmineLinkedTicket(Guid LinkId, string? Note, RedmineIssueItem Ticket);

public sealed record RedmineRelatedGroup(RedmineLinkKind Kind, Guid? Id, string Name, IReadOnlyList<RedmineLinkedTicket> Tickets);

/// <summary><c>GET /redmine/related</c> (§6.6): an object's own tickets, then those of what it belongs to; as of the copy.</summary>
public sealed record RedmineRelated(IReadOnlyList<RedmineLinkedTicket> Own, IReadOnlyList<RedmineRelatedGroup> Via, DateTime? AsOf);

/// <summary><c>GET /redmine/problems</c>: failing sensors with an open linked ticket, for the dashboard's chips (§6.6).</summary>
public sealed record RedmineProblemTicket(Guid SensorId, RedmineIssueItem Ticket);

/// <summary><c>POST /redmine/test</c>: whose key it is, the projects found and those not found.</summary>
public sealed record RedmineTestResult(string Account, IReadOnlyList<RedmineProjectItem> Projects, IReadOnlyList<string> NotFound);
