using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace PNMTD.Models.Db;

// The knowledge base (docs/locations-knowledge-plan.md §6, §7, §8.2): pages with a number, their revisions (full
// snapshots), tags, links to locations, hosts and sensors, the page links behind "Linked from", views per day, and when
// someone was last asked about AI tags. Everything of a page goes with it. Authors are a user id plus their name at the
// time, with no foreign key (§8.2). Migration AddKnowledgeBase (K4).

/// <summary>How far to trust a page (§6.2). New pages start as <see cref="Published"/>. Stored as its number.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KbPageStatus>))]
public enum KbPageStatus
{
    Draft = 0,
    Published = 1,
    NeedsReview = 2,
}

/// <summary>
/// A page (§6.1): <c>KB-42</c> by its <see cref="Number"/>. The Markdown is stored exactly as written; <see cref="PlainText"/>
/// and <see cref="TagsText"/> are what the server derived on the last save, for <see cref="SearchVector"/> (§6.7).
/// </summary>
[Table("kb_pages")]
[Audited]
public class KbPageEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>From an identity column, never reused: the <c>42</c> of <c>KB-42</c> and of <c>/kb/42</c>.</summary>
    public int Number { get; set; }

    /// <summary>3–160 characters; a duplicate is allowed.</summary>
    [MaxLength(160)]
    public string Title { get; set; } = "";

    [MaxLength(300)]
    public string? Summary { get; set; }

    /// <summary>Markdown, at most KnowledgeBase:MaxPageBytes.</summary>
    [AuditText]
    public string Content { get; set; } = "";

    /// <summary>The content's plain text, which MarkdownText writes on every save (§6.7).</summary>
    [AuditIgnore]
    public string PlainText { get; set; } = "";

    /// <summary>The page's tag names, separated by spaces, for the search's weight A.</summary>
    [AuditIgnore]
    public string TagsText { get; set; } = "";

    public KbPageStatus Status { get; set; } = KbPageStatus.Published;

    /// <summary>Never sent to an AI, never shown to MCP (§7.1, §7.4).</summary>
    public bool Sensitive { get; set; }

    /// <summary>
    /// A person said most of the text came from an AI (docs/knowledge-base-extensions-plan.md §5). Readers see it; it changes
    /// nothing else.
    /// </summary>
    public bool WrittenWithAi { get; set; }

    /// <summary>The current revision's number; a save carries the revision it started from (409 <c>edit_conflict</c>).</summary>
    [AuditIgnore]
    public int Revision { get; set; }

    [AuditIgnore]

    public DateTime CreatedAt { get; set; }

    [AuditIgnore]

    public Guid? CreatedBy { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? CreatedByName { get; set; }

    [AuditIgnore]

    public DateTime UpdatedAt { get; set; }

    [AuditIgnore]

    public Guid? UpdatedBy { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? UpdatedByName { get; set; }

    /// <summary>Archived: read-only, left out of lists and the search (§6.2). Only an archived page can be deleted.</summary>
    public DateTime? ArchivedAt { get; set; }

    [AuditIgnore]

    public Guid? ArchivedBy { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? ArchivedByName { get; set; }

    /// <summary>All views; per day in kb_page_views (§6.6).</summary>
    [AuditIgnore]
    public int Views { get; set; }

    /// <summary>
    /// Generated and stored by PostgreSQL with the <c>simple</c> configuration: A the title and tags, B the summary, C the
    /// plain text (§6.7). Never written by PNMTD.
    /// </summary>
    [AuditIgnore]
    public NpgsqlTsVector SearchVector { get; set; } = null!;

    public List<KbPageTagEntity> Tags { get; set; } = [];

    public List<KbLinkEntity> Links { get; set; } = [];
}

/// <summary>
/// A revision (§6.3): a full snapshot of the page after a save, with who, the note, the lines added and removed, the list
/// of what changed, and for AI scans and unmarking sensitive the declaration. Machines never read revisions (§7.4).
/// </summary>
[Table("kb_revisions")]
[NotAudited("a page's revisions are its history, with the full diff")]
public class KbRevisionEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid PageId { get; set; }

    /// <summary>1, 2, … per page.</summary>
    public int Number { get; set; }

    public DateTime At { get; set; }

    public Guid? ByUserId { get; set; }

    [MaxLength(200)]
    public string ByName { get; set; } = "";

    /// <summary>How it was made: null in the UI, <c>mcp</c> over MCP (docs/mcp-plan.md §7.3).</summary>
    [MaxLength(16)]
    public string? Via { get; set; }

    [MaxLength(160)]
    public string Title { get; set; } = "";

    [MaxLength(300)]
    public string? Summary { get; set; }

    public string Content { get; set; } = "";

    /// <summary>The tag names at this revision.</summary>
    public List<string> Tags { get; set; } = [];

    public KbPageStatus Status { get; set; }

    public bool Sensitive { get; set; }

    /// <summary>Written mostly with AI, at this revision (docs/knowledge-base-extensions-plan.md §5).</summary>
    public bool WrittenWithAi { get; set; }

    public bool Archived { get; set; }

    /// <summary>The links at this revision, as JSON.</summary>
    [Column(TypeName = "jsonb")]
    public string? Links { get; set; }

    /// <summary>Like a commit message, at most 200 characters.</summary>
    [MaxLength(200)]
    public string? Note { get; set; }

    /// <summary>Content lines added and removed against the revision before (LineDiff).</summary>
    public int Added { get; set; }

    public int Removed { get; set; }

    /// <summary>What changed, as a JSON list: content, title, tags, status, links, files, sensitive, archived, restored, AI tags, redacted.</summary>
    [Column(TypeName = "jsonb")]
    public string Changes { get; set; } = "[]";

    /// <summary>Who confirmed which statements, for AI scans and unmarking sensitive (§7.2), as JSON.</summary>
    [Column(TypeName = "jsonb")]
    public string? Declaration { get; set; }

    /// <summary>The save changed the title, summary or content: an edit for <i>most edited</i> (§6.6).</summary>
    public bool IsEdit { get; set; }

    /// <summary>
    /// Made on this page because a page link to it was made or removed on the other page (docs/knowledge-base-extensions-plan.md
    /// §7.3): it changes nothing but the page links, so no revision check counts it.
    /// </summary>
    public bool LinksOnly { get; set; }
}

/// <summary>A tag (§6.4): 2–40 characters, lower case, unique.</summary>
[Table("kb_tags")]
[Audited]
public class KbTagEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(40)]
    public string Name { get; set; } = "";

    [MaxLength(40)]
    public string NormalizedName { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}

/// <summary>Where a page's tag came from (§6.4): AI tags show a spark until a person confirms them. Stored as its number.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KbTagSource>))]
public enum KbTagSource
{
    Manual = 0,
    Ai = 1,
}

/// <summary>A tag on a page; goes with either.</summary>
[Table("kb_page_tags")]
[Audited]
public class KbPageTagEntity
{
    public Guid PageId { get; set; }

    public Guid TagId { get; set; }

    public KbTagEntity? Tag { get; set; }

    public KbTagSource Source { get; set; }

    public DateTime AddedAt { get; set; }

    public Guid? AddedBy { get; set; }
}

/// <summary>
/// A page's link to exactly one location, host, sensor or other page (§6.5). The database cleans up when the target goes,
/// through ExecuteDeleteAsync as well. A link between two pages is mutual (docs/knowledge-base-extensions-plan.md §7.2): two
/// mirrored rows, one on each page, written and deleted together by KbPageService.
/// </summary>
[Table("kb_links")]
[Audited]
public class KbLinkEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid PageId { get; set; }

    public Guid? LocationId { get; set; }

    public Guid? HostId { get; set; }

    public Guid? SensorId { get; set; }

    /// <summary>The other page of a mutual page link; its own row points back here.</summary>
    public Guid? TargetPageId { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}

/// <summary>A link in a page's Markdown to another page, by number, recorded on each save: <i>Linked from</i> (§6.5).</summary>
[Table("kb_page_refs")]
[NotAudited("a page's links to others: derived from its text")]
public class KbPageRefEntity
{
    public Guid FromPageId { get; set; }

    /// <summary>A number, not a reference: a link to a deleted page shows as <i>missing page</i>.</summary>
    public int ToNumber { get; set; }
}

/// <summary>A page's views on one day (§6.6), so the last 30 days can be summed.</summary>
[Table("kb_page_views")]
[NotAudited("page views: reads are not operations")]
public class KbPageViewEntity
{
    public Guid PageId { get; set; }

    public DateOnly Day { get; set; }

    public int Views { get; set; }
}

/// <summary>When a person was last asked about AI tags for a page: not again within 24 hours (§7.2).</summary>
[Table("kb_ai_questions")]
[NotAudited("when the AI question was asked")]
public class KbAiQuestionEntity
{
    public Guid PageId { get; set; }

    public Guid UserId { get; set; }

    public DateTime AskedAt { get; set; }
}
