using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace PNMTD.Models.Db;

// Responsibilities (docs/responsibilities-plan.md §10): who owns an area of work, with a deputy; its links to pages, locations
// and hosts; the global responsible and deputy; and each responsibility's history. Information only: nothing is sent to the
// people they name (decision 5). Not the availability plan's responsible of the day (§2.2), which is why no type here is
// called Responsibility (PNMTD.Availability.Responsibility is that one).

/// <summary>An area of work (§4.1): a name, a short and a long description, a responsible and a deputy.</summary>
[Table("responsibilities")]
[Audited]
public class ResponsibilityEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>1–100 characters, as entered.</summary>
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>The name compared, regardless of case: unique.</summary>
    [MaxLength(100)]
    [AuditIgnore]
    public string NormalizedName { get; set; } = "";

    /// <summary>The short description: one line.</summary>
    [MaxLength(300)]
    public string? Summary { get; set; }

    /// <summary>The long description, Markdown as a knowledge base page's text (§4.1); its old texts are in the history.</summary>
    [AuditText]
    public string Description { get; set; } = "";

    /// <summary>The description without its Markdown, for the search.</summary>
    [AuditIgnore]
    public string PlainText { get; set; } = "";

    /// <summary>Raised by each save of the description: a save from an older one is an edit conflict.</summary>
    [AuditIgnore]
    public int Revision { get; set; }

    /// <summary>A deleted user's place becomes empty (SET NULL).</summary>
    public Guid? ResponsibleId { get; set; }

    /// <summary>Never the responsible (CK_responsibilities_two_people).</summary>
    public Guid? DeputyId { get; set; }

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
}

/// <summary>
/// A responsibility's link to exactly one page, location or host (§10.2). The database removes it when either side goes.
/// Only a linked object shows the responsibility: nothing is inherited (decision 6).
/// </summary>
[Table("responsibility_links")]
[NotAudited("each link and unlink is ResponsibilityLinked or ResponsibilityUnlinked, naming the object")]
public class ResponsibilityLinkEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid ResponsibilityId { get; set; }

    public Guid? PageId { get; set; }

    public Guid? LocationId { get; set; }

    public Guid? HostId { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    [MaxLength(200)]
    public string? CreatedByName { get; set; }
}

/// <summary>
/// The global responsible and deputy (§5): one row, inserted by the migration. They own what no responsibility covers, and
/// stand in for a responsibility without an active responsible or deputy. Not the availability plan's global fallback.
/// </summary>
[Table("responsibility_settings")]
[NotAudited("each save is GlobalResponsibleChanged, naming both people")]
public class ResponsibilitySettingsEntity
{
    public const int SingletonId = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; }

    public Guid? GlobalResponsibleId { get; set; }

    public Guid? GlobalDeputyId { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [MaxLength(200)]
    public string? UpdatedByName { get; set; }
}

/// <summary>What a responsibility's history row records (§10.5). Stored as its number: append, never reorder.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResponsibilityHistoryKind>))]
public enum ResponsibilityHistoryKind
{
    Created = 0,
    Renamed = 1,
    SummaryChanged = 2,
    PeopleChanged = 3,
    /// <summary>The old and the new description; restoring an old one is one too.</summary>
    DescriptionChanged = 4,
    LinkAdded = 5,
    LinkRemoved = 6,
    RedmineAdded = 7,
    RedmineChanged = 8,
    RedmineRemoved = 9,
}

/// <summary>One change to a responsibility (§10.5), newest first, with the description's full texts.</summary>
[Table("responsibility_history")]
[NotAudited("a responsibility's history, with the full descriptions; each change is also in the audit log")]
public class ResponsibilityHistoryEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid ResponsibilityId { get; set; }

    public DateTime At { get; set; }

    public Guid? ByUserId { get; set; }

    [MaxLength(200)]
    public string ByName { get; set; } = "";

    public ResponsibilityHistoryKind Kind { get; set; }

    /// <summary>For <see cref="ResponsibilityHistoryKind.DescriptionChanged"/>: the text before.</summary>
    public string? OldValue { get; set; }

    /// <summary>For <see cref="ResponsibilityHistoryKind.DescriptionChanged"/>: the text after.</summary>
    public string? NewValue { get; set; }

    /// <summary>What the row says, for people: "Anna Muster → Carla Rossi", "host sw-core-01".</summary>
    [MaxLength(2000)]
    public string? Detail { get; set; }
}
