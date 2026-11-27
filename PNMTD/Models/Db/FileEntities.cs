using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace PNMTD.Models.Db;

// Uploaded files (docs/locations-knowledge-plan.md §5.5, §8.2): what each file is, and which pages and locations use it.
// No content: the bytes lie in the files folder on the Docker host (KnowledgeBase:FilesPath, §8.5), written once and
// never changed. Migration AddFiles (K1).

/// <summary>How a file is shown and served (§5.5). Stored as its number.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FileKind>))]
public enum FileKind
{
    /// <summary>PNG, JPEG, GIF or WebP, checked by its bytes and stripped of metadata: shown inline.</summary>
    Image = 0,

    /// <summary>Any other file: always served as a download.</summary>
    Attachment = 1,
}

/// <summary>
/// A stored file (§5.5): one per content among the files not deleted (<see cref="Sha256"/>). Deleting marks it
/// (<see cref="DeletedAt"/>); FileCleanupTask removes it from the disk 7 days later, an Admin's delete at once.
/// </summary>
[Table("files")]
[Audited]
public class FileEntity
{
    [Key]
    public Guid Id { get; set; }

    public FileKind Kind { get; set; }

    /// <summary>The original name, as uploaded; only ever shown and sent in Content-Disposition.</summary>
    [MaxLength(255)]
    public string Name { get; set; } = "";

    /// <summary>An image's type as detected from its bytes; an attachment's as uploaded (it is always served as octet-stream).</summary>
    [MaxLength(255)]
    public string ContentType { get; set; } = "";

    public long Size { get; set; }

    /// <summary>An image's, from its headers.</summary>
    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Lower-case hex of the stored bytes (after stripping), for deduplication and the weekly check.</summary>
    [MaxLength(64)]
    public string Sha256 { get; set; } = "";

    /// <summary>Relative to the folder, from the id and a slug only: <c>2026/11/{id}-cage-b.png</c>.</summary>
    [MaxLength(512)]
    [AuditIgnore]
    public string Path { get; set; } = "";

    [AuditIgnore]

    public DateTime CreatedAt { get; set; }

    [AuditIgnore]

    public Guid? CreatedBy { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? CreatedByName { get; set; }

    public DateTime? DeletedAt { get; set; }
}

/// <summary>
/// A file referenced by a page's, a location's or a responsibility's Markdown, or shown by a board's image element (exactly one of
/// them), recorded on each save (§5.5, docs/responsibilities-plan.md §10.4, docs/board-messages-plan.md §9.4). A page's, location's or
/// responsibility's use lasts as long as its owner, since their revisions and history keep the reference; a board's matches its
/// layout on each save.
/// </summary>
[Table("file_uses")]
[NotAudited("which page or field uses a file: derived from their saves")]
public class FileUseEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid FileId { get; set; }

    public Guid? PageId { get; set; }

    public Guid? LocationId { get; set; }

    public Guid? ResponsibilityId { get; set; }

    /// <summary>A board that shows the file in an image element (docs/board-messages-plan.md §9.4). Migration BoardImages.</summary>
    public Guid? BoardId { get; set; }

    /// <summary>A licence's or service's document (docs/licences-plan.md §3.9): served only through the item. Migration Licenses.</summary>
    public Guid? LicenseId { get; set; }

    public DateTime FirstUsedAt { get; set; }
}
