using System.Text.Json.Serialization;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// The knowledge base (docs/locations-knowledge-plan.md §6, §7, §9): pages by number (GET) and by id (everything else),
// their revisions, tags, links, views, who is editing, the lists and the content search, and the AI tags.

/// <summary>
/// What a page links to (§6.5): exactly one location, host or sensor, or another page, mutually
/// (docs/knowledge-base-extensions-plan.md §7).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KbLinkKind>))]
public enum KbLinkKind
{
    Location,
    Host,
    Sensor,
    Page,
}

/// <summary>A page's tag (§6.4): <c>Ai</c> shows a spark until a person confirms or removes it.</summary>
public sealed record KbPageTag(string Name, KbTagSource Source);

/// <summary>
/// A link as the UI shows it: <see cref="Detail"/> is a location's path, a sensor's host, a host's location, a page's
/// <c>KB-12</c>. A target that is gone (only in old revisions) has the name <i>deleted</i> and <see cref="Exists"/> false.
/// For a page, <see cref="Number"/> routes to it, and <see cref="Archived"/> says its link can't change until it is restored.
/// </summary>
public sealed record KbLinkItem(KbLinkKind Kind, Guid Id, string Name, string? Detail, bool Exists = true, int? Number = null, bool Archived = false);

/// <summary>Another page by number: <see cref="Title"/> null for a missing page (§6.5).</summary>
public sealed record KbPageRef(int Number, string? Title, bool Archived);

/// <summary>Someone else on the page's editing page (§5.7), since when.</summary>
public sealed record KbEditor(string Name, DateTime Since);

/// <summary>
/// A page (§6.1), with what PNMT knows about it: its revision (the number a save starts from), the files its content
/// uses, the pages that link to it and those it links to, and who else is editing. <see cref="AiTags"/>: AI tags are on
/// (AI usable and <c>KnowledgeBase:AiTags</c>); the button and the question need the page not to be sensitive or
/// archived as well.
/// </summary>
public sealed record KbPageDetail(
    Guid Id,
    int Number,
    string Title,
    string? Summary,
    string Content,
    KbPageStatus Status,
    IReadOnlyList<KbPageTag> Tags,
    IReadOnlyList<KbLinkItem> Links,
    bool Sensitive,
    int Revision,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime UpdatedAt,
    string? UpdatedByName,
    DateTime? ArchivedAt,
    string? ArchivedByName,
    int Views,
    IReadOnlyList<FileItem> Files,
    IReadOnlyList<KbPageRef> LinkedFrom,
    IReadOnlyList<KbPageRef> LinksTo,
    IReadOnlyList<KbEditor> Editing,
    bool AiTags,
    bool WrittenWithAi);

/// <summary>A link to set: the kind and the target's id.</summary>
public sealed class KbLinkRequest
{
    public KbLinkKind Kind { get; set; }

    public Guid Id { get; set; }
}

/// <summary>
/// <c>POST /kb/pages</c> and <c>PUT /kb/pages/{id}</c> (§9): every writable field. A new page starts as <i>Published</i>.
/// </summary>
public sealed class KbPageRequest
{
    /// <summary>3–160 characters.</summary>
    public string Title { get; set; } = "";

    /// <summary>At most 300 characters.</summary>
    public string? Summary { get; set; }

    /// <summary>Markdown, at most <c>KnowledgeBase:MaxPageBytes</c> in UTF-8.</summary>
    public string Content { get; set; } = "";

    /// <summary>At most 30, normalised (§6.4): <i>Backup</i> becomes <i>backup</i>.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// The page's AI tags that are still unconfirmed; any other tag is the person's own (§6.4). Null keeps every AI tag
    /// as it is. A new tag is always manual.
    /// </summary>
    public List<string>? AiTags { get; set; }

    public KbPageStatus Status { get; set; } = KbPageStatus.Published;

    /// <summary>At most 50.</summary>
    public List<KbLinkRequest> Links { get; set; } = [];

    /// <summary>The revision the edit started from: needed to update, 409 <c>edit_conflict</c> when it isn't the current one.</summary>
    public int? Revision { get; set; }

    /// <summary>For the history, like a commit message: at most 200 characters.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// The credentials warning was answered (§5.7): without it, a save whose content has new lines that look like
    /// credentials is answered with 422 <c>looks_like_credential</c> and their line numbers.
    /// </summary>
    public bool AcknowledgeCredentials { get; set; }

    /// <summary><i>Mark as sensitive and save</i> (§5.7, §7.1): in the same revision. Never unmarks.</summary>
    public bool MarkSensitive { get; set; }

    /// <summary>
    /// The sensitive mark with the save (docs/knowledge-base-extensions-plan.md §4). Null leaves it as it is; true marks the page
    /// in the save's revision, as <see cref="MarkSensitive"/> does; false unmarks it, which takes both <see cref="Statements"/>,
    /// recorded as the declaration of the revision the save makes.
    /// </summary>
    public bool? Sensitive { get; set; }

    /// <summary>For unmarking with <see cref="Sensitive"/> false: both statements, about the text being saved.</summary>
    public KbStatements? Statements { get; set; }

    /// <summary>Written mostly with AI (§5): null leaves it as it is, and is false for a new page.</summary>
    public bool? WrittenWithAi { get; set; }
}

/// <summary>
/// A save's answer: the page, whether the AI question follows (§7.2: always after a new page's first save, after about
/// one in ten edits), and on creation the other pages with the same title (§6.1).
/// </summary>
public sealed record KbSaveResult(KbPageDetail Page, bool AskAiTags, IReadOnlyList<KbPageRef> SameTitle);

/// <summary>
/// <c>GET /kb/pages</c> (§6.6, §6.7). <see cref="Q"/> searches the content (<c>"…"</c> a phrase, <c>-word</c> excludes,
/// <c>or</c> alternatives, <c>#tag</c> a tag); <see cref="Tags"/> are comma-separated and all must match;
/// <see cref="Location"/> includes its sub-locations. Sorts: <c>newest</c> (the default), <c>updated</c>,
/// <c>views</c>, <c>edits</c>, <c>title</c>, and <c>relevance</c> (the default while searching). <see cref="Period"/>
/// for views and edits: <c>30d</c> (the default) or <c>all</c>.
/// </summary>
public sealed class KbPageQuery
{
    public string? Q { get; set; }

    public string? Tags { get; set; }

    public KbPageStatus? Status { get; set; }

    public Guid? Location { get; set; }

    public Guid? Host { get; set; }

    public Guid? Sensor { get; set; }

    /// <summary>Archived pages too.</summary>
    public bool Archived { get; set; }

    /// <summary>Only sensitive pages (true), or only the others (false) (docs/knowledge-base-extensions-plan.md §4).</summary>
    public bool? Sensitive { get; set; }

    /// <summary>Only pages written mostly with AI (true), or only the others (false) (§5).</summary>
    public bool? Ai { get; set; }

    public string? Sort { get; set; }

    public string? Period { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;
}

/// <summary>
/// A page in a list (§6.6). <see cref="Snippet"/> only while searching: the content around the matches, which are
/// enclosed in U+E000 and U+E001 (the UI makes them <c>&lt;mark&gt;</c>), so no HTML comes from the server.
/// <see cref="Views"/> and <see cref="Edits"/> are of all time, <see cref="RecentViews"/> and <see cref="RecentEdits"/>
/// of the last 30 days.
/// </summary>
public sealed record KbPageListItem(
    Guid Id,
    int Number,
    string Title,
    KbPageStatus Status,
    string? Summary,
    string? Snippet,
    IReadOnlyList<KbPageTag> Tags,
    bool HasAttachments,
    bool Sensitive,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime UpdatedAt,
    string? UpdatedByName,
    DateTime? ArchivedAt,
    int Views,
    int RecentViews,
    int Edits,
    int RecentEdits,
    bool WrittenWithAi);

/// <summary>
/// A page in the recent changes (docs/knowledge-base-extensions-plan.md §6), with its newest change: <see cref="Kind"/> is
/// <c>created</c>, <c>edited</c>, <c>restored</c> or <c>status</c>; <see cref="Detail"/> the new status (<i>Draft → Published</i>)
/// or the revision restored from (<i>from 4</i>). <see cref="Changes"/> and <see cref="People"/> count the page's changes in the
/// period and who made them.
/// </summary>
public sealed record KbRecentChange(
    Guid PageId,
    int Number,
    string Title,
    KbPageStatus Status,
    bool Sensitive,
    bool WrittenWithAi,
    int Revision,
    string Kind,
    string? Detail,
    DateTime At,
    string ByName,
    int Added,
    int Removed,
    string? Note,
    int Changes,
    int People);

/// <summary><c>GET /kb/changes</c> (§6): the period in days, and its changes.</summary>
public sealed record KbRecentChanges(int Days, IReadOnlyList<KbRecentChange> Items);

/// <summary>Pages linked to an object the panel's own links (§6.5): <i>via Zurich DC</i>, <i>via its hosts</i>.</summary>
public sealed record KbRelatedGroup(KbLinkKind Kind, Guid? Id, string Name, IReadOnlyList<KbPageListItem> Pages);

/// <summary>
/// <c>GET /kb/related</c> (§6.5): the pages linked to the object itself, then the groups it inherits, nearest first. A
/// page is shown once, where it is nearest. Archived pages are left out.
/// </summary>
public sealed record KbRelated(IReadOnlyList<KbPageListItem> Pages, IReadOnlyList<KbRelatedGroup> Via);

/// <summary>A tag with its number of pages (not archived), for the tag browser and the completion (§6.4, §6.6).</summary>
public sealed record KbTagItem(Guid Id, string Name, int Pages);

/// <summary><c>PUT /kb/tags/{id}</c>: a new name; an existing tag's name merges the two (§6.4).</summary>
public sealed class KbTagRequest
{
    public string Name { get; set; } = "";
}

/// <summary>What a rename did: the tag as it is now, whether it was merged into another, and the pages changed.</summary>
public sealed record KbTagChange(KbTagItem Tag, bool Merged, int Pages);

/// <summary>One item of a revision's list of what changed (§6.3), such as <c>content</c> with <i>+12 −3</i>.</summary>
public sealed record KbChange(string Kind, string Text);

/// <summary>
/// Who confirmed which statements, when, and for which revision (§7.2): for AI scans and unmarking sensitive.
/// The statements are <c>noCredentials</c> and <c>noPersonalData</c>.
/// </summary>
public sealed record KbDeclaration(string ByName, DateTime At, IReadOnlyList<string> Statements, int Revision);

/// <summary>A revision in the history, newest first (§6.3). <see cref="IsEdit"/>: it changed the title, summary or content.</summary>
public sealed record KbRevisionItem(
    int Number,
    DateTime At,
    string ByName,
    string? Note,
    int Added,
    int Removed,
    IReadOnlyList<KbChange> Changes,
    bool IsEdit,
    KbDeclaration? Declaration,
    string Title,
    KbPageStatus Status,
    string? Via = null,
    bool LinksOnly = false);

/// <summary>A revision as it was: the snapshot, and the files its content uses.</summary>
public sealed record KbRevisionDetail(
    int Number,
    DateTime At,
    string ByName,
    string? Note,
    IReadOnlyList<KbChange> Changes,
    KbDeclaration? Declaration,
    string Title,
    string? Summary,
    string Content,
    IReadOnlyList<string> Tags,
    KbPageStatus Status,
    bool Sensitive,
    bool Archived,
    IReadOnlyList<KbLinkItem> Links,
    IReadOnlyList<FileItem> Files,
    bool WrittenWithAi);

public sealed record KbDiffLine(Lib.Helper.DiffLineKind Kind, string Text, int? OldNumber, int? NewNumber);

public sealed record KbDiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<KbDiffLine> Lines);

/// <summary>
/// <c>GET /kb/pages/{id}/diff?from&amp;to</c> (§6.3): the meta changes between two revisions, and their content, both texts
/// for Monaco's diff editor and the hunks for the HTML table where it doesn't run.
/// </summary>
public sealed record KbDiff(
    int From,
    int To,
    IReadOnlyList<KbChange> Changes,
    int Added,
    int Removed,
    string FromContent,
    string ToContent,
    IReadOnlyList<KbDiffHunk> Hunks);

/// <summary><c>POST /kb/pages/{id}/redact</c> (§6.3): the exact text, at least 4 characters.</summary>
public sealed class KbRedactRequest
{
    public string Text { get; set; } = "";
}

/// <summary>
/// How many revisions held the text, how often it was replaced in the page and its history, and how many requests of the
/// AI request log (the page's tag scans) held it.
/// </summary>
public sealed record KbRedactResult(int Revisions, int Occurrences, int AiRequests, KbPageDetail Page);

/// <summary>The two statements of §7.2. Never remembered: each scan and each unmarking asks again.</summary>
public sealed class KbStatements
{
    /// <summary>No credentials: no passwords, keys, tokens or codes.</summary>
    public bool NoCredentials { get; set; }

    /// <summary>No personal data, and no confidential or IP-protected information.</summary>
    public bool NoPersonalData { get; set; }
}

/// <summary>
/// <c>PUT /kb/pages/{id}/sensitive</c> (§7.1): marking takes effect at once; unmarking needs both statements, for the
/// current <see cref="Revision"/>.
/// </summary>
public sealed class KbSensitiveRequest
{
    public bool Sensitive { get; set; }

    public int? Revision { get; set; }

    public KbStatements? Statements { get; set; }
}

/// <summary><c>POST /kb/pages/{id}/ai-tags</c> (§7.3): the revision the person saw, and both statements.</summary>
public sealed class KbAiTagsRequest
{
    public int Revision { get; set; }

    public KbStatements Statements { get; set; } = new();
}

/// <summary>
/// An AI tag scan (§7.3), as the UI polls it: its work item's state; once done, the tags it added (as AI tags, in one
/// revision) or why it added none. <see cref="BlockedBy"/>: <c>scan</c>, <c>injection</c>, <c>credentials</c> or
/// <c>personalData</c>, when the pipeline stopped before the main model.
/// </summary>
public sealed record KbAiTagsStatus(
    Guid WorkId,
    AiWorkState State,
    IReadOnlyList<string> Added,
    int? Revision,
    string? BlockedBy,
    string? Message,
    DateTime CreatedAt,
    DateTime? FinishedAt);

/// <summary><c>POST /kb/pages/{id}/editing</c>: an open editor's report every 30 seconds; <see cref="Stopped"/> when it closes.</summary>
public sealed class KbEditingRequest
{
    public bool Stopped { get; set; }
}
