using System.Text.Json.Serialization;

namespace PNMTD.Redmine;

// Redmine's REST API as PNMT reads it (docs/audit-redmine-zabbix-plan.md §6.2), checked against 5.1 and 6.0 (R0).
// snake_case on the wire; only what PNMT shows.

public sealed record RmRef(int Id, string? Name);

public sealed record RmStatus(int Id, string Name, [property: JsonPropertyName("is_closed")] bool IsClosed);

public sealed record RmPriority(int Id, string Name, [property: JsonPropertyName("is_default")] bool IsDefault);

public sealed record RmCustomField(int Id, string Name, System.Text.Json.JsonElement? Value);

public sealed record RmDetail(string Property, string Name, [property: JsonPropertyName("old_value")] string? OldValue,
    [property: JsonPropertyName("new_value")] string? NewValue);

public sealed record RmJournal(int Id, RmRef? User, string? Notes, [property: JsonPropertyName("created_on")] DateTime CreatedOn,
    [property: JsonPropertyName("private_notes")] bool PrivateNotes, IReadOnlyList<RmDetail>? Details);

public sealed record RmAttachment(int Id, string Filename, long Filesize, [property: JsonPropertyName("content_type")] string? ContentType,
    string? Description, RmRef? Author, [property: JsonPropertyName("created_on")] DateTime CreatedOn);

public sealed record RmRelation(int Id, [property: JsonPropertyName("issue_id")] int IssueId, [property: JsonPropertyName("issue_to_id")] int IssueToId,
    [property: JsonPropertyName("relation_type")] string RelationType);

public sealed record RmChild(int Id, RmRef? Tracker, string? Subject);

public sealed record RmIssue(
    int Id,
    RmRef Project,
    RmRef? Tracker,
    RmStatus Status,
    RmRef? Priority,
    RmRef? Author,
    [property: JsonPropertyName("assigned_to")] RmRef? AssignedTo,
    RmRef? Parent,
    string Subject,
    string? Description,
    [property: JsonPropertyName("start_date")] string? StartDate,
    [property: JsonPropertyName("due_date")] string? DueDate,
    [property: JsonPropertyName("done_ratio")] int? DoneRatio,
    [property: JsonPropertyName("is_private")] bool IsPrivate,
    [property: JsonPropertyName("estimated_hours")] double? EstimatedHours,
    [property: JsonPropertyName("created_on")] DateTime CreatedOn,
    [property: JsonPropertyName("updated_on")] DateTime UpdatedOn,
    [property: JsonPropertyName("closed_on")] DateTime? ClosedOn,
    [property: JsonPropertyName("custom_fields")] IReadOnlyList<RmCustomField>? CustomFields,
    IReadOnlyList<RmJournal>? Journals,
    IReadOnlyList<RmAttachment>? Attachments,
    IReadOnlyList<RmRelation>? Relations,
    IReadOnlyList<RmChild>? Children);

public sealed record RmIssues(IReadOnlyList<RmIssue> Issues, [property: JsonPropertyName("total_count")] int TotalCount, int Offset, int Limit);

public sealed record RmIssueEnvelope(RmIssue Issue);

public sealed record RmProject(int Id, string Name, string Identifier, RmRef? Parent, int Status);

public sealed record RmProjectEnvelope(RmProject Project);

public sealed record RmProjects(IReadOnlyList<RmProject> Projects, [property: JsonPropertyName("total_count")] int TotalCount);

public sealed record RmStatuses([property: JsonPropertyName("issue_statuses")] IReadOnlyList<RmStatus> IssueStatuses);

public sealed record RmTrackers(IReadOnlyList<RmRef> Trackers);

public sealed record RmPriorities([property: JsonPropertyName("issue_priorities")] IReadOnlyList<RmPriority> IssuePriorities);

/// <summary>A project's member: a user, or a group (which has no user, and can't be a PNMT user's Redmine user).</summary>
public sealed record RmMembership(RmRef? User, RmRef? Project = null, IReadOnlyList<RmRef>? Roles = null, RmRef? Group = null);

public sealed record RmMemberships(IReadOnlyList<RmMembership> Memberships, [property: JsonPropertyName("total_count")] int TotalCount);

public sealed record RmUser(int Id, string Login, string? Firstname, string? Lastname);

public sealed record RmUserEnvelope(RmUser User);

public sealed record RmRoles(IReadOnlyList<RmRef> Roles);
