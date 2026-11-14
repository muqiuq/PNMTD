using System.Text.Json.Serialization;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// Uploaded files (docs/locations-knowledge-plan.md §5.5, §9, §10.6): POST /api/v1/files?kind&name (CanWrite, the file as the
// body), GET /api/v1/files/{id}/{name} (CanRead), and the Admin's GET /api/v1/files and DELETE /api/v1/files/{id}.

/// <summary>
/// A stored file, as an upload's reply and in the responses of the pages and locations that use it (§5.5, §5.6):
/// <see cref="Url"/> is what Markdown points to. An image has its <see cref="Width"/> and <see cref="Height"/>; an
/// attachment's <see cref="ContentType"/> names it for the chip's icon, but it is always served as a download.
/// </summary>
public sealed record FileItem(Guid Id, FileKind Kind, string Name, string ContentType, long Size, int? Width, int? Height, string Url);

/// <summary>
/// The Admin's overview (§10.6): <c>?kind=Image|Attachment</c>; <c>unused=true</c> for files no page or location uses (unused
/// uploads, and files waiting to leave the disk); <c>unknown=true</c> adds the files on disk that the database doesn't know.
/// Sorted by <c>-size</c> (the default), <c>size</c>, <c>-created</c>, <c>created</c> or <c>name</c>; <c>search</c> in names.
/// </summary>
public sealed class FileQuery : PageQuery
{
    public FileKind? Kind { get; set; }

    public bool Unused { get; set; }

    public bool Unknown { get; set; }
}

/// <summary>The files of a kind that aren't deleted: how many, and their bytes.</summary>
public sealed record FileKindTotal(FileKind Kind, int Count, long Bytes);

/// <summary>
/// The folder (§8.5): its path in the container, whether PNMTD can write into it (<see cref="Problem"/> says why not), and
/// its disk's free and total space (null where the system doesn't say). Below <see cref="MinFreeBytes"/> the
/// <i>File storage</i> sensor writes 428.
/// </summary>
public sealed record FileStorageInfo(string Path, bool Writable, string? Problem, long? FreeBytes, long? TotalBytes, long MinFreeBytes);

[JsonConverter(typeof(JsonStringEnumConverter<FileUserKind>))]
public enum FileUserKind { Page, Location }

/// <summary>A page (<see cref="Number"/>, its title as <see cref="Name"/>) or a location that uses a file.</summary>
public sealed record FileUseRef(FileUserKind Kind, Guid Id, string Name, int? Number);

/// <summary>
/// A file in the overview: when and by whom it was uploaded, where it is used, and for a file marked deleted, when it
/// leaves the disk (KnowledgeBase:DeletedFileDays after).
/// </summary>
public sealed record FileAdminItem(FileItem File, DateTime CreatedAt, string? CreatedByName, DateTime? DeletedAt, DateTime? LeavesDiskAt,
    IReadOnlyList<FileUseRef> UsedBy);

/// <summary>A file in the folder that no row names (§5.5): listed, never deleted by PNMTD. <see cref="Path"/> is relative to the folder.</summary>
public sealed record UnknownFileItem(string Path, long Size, DateTime ModifiedAt);

/// <summary>A file the check found missing from the folder or changed in it: <see cref="Detail"/> says how.</summary>
public sealed record FileCheckFinding(Guid Id, string Name, string Path, string Detail);

/// <summary>
/// The last check of the folder (§5.5), the <i>File storage</i> sensor's findings: whether it compared contents (weekly) and
/// found a problem (428), the files missing or changed, and how many files on disk no row names. Null until the first
/// check after a start.
/// </summary>
public sealed record FileCheckReport(DateTime CheckedAt, bool ContentsChecked, bool Problem, IReadOnlyList<FileCheckFinding> Missing,
    IReadOnlyList<FileCheckFinding> Changed, int UnknownCount);

/// <summary>
/// GET /api/v1/files: the totals, the folder, the last check, a page of files, and with <c>unknown=true</c> the unknown files
/// (at most 500).
/// </summary>
public sealed record FilesOverview(IReadOnlyList<FileKindTotal> Totals, FileStorageInfo Storage, FileCheckReport? Check, Paged<FileAdminItem> Files,
    IReadOnlyList<UnknownFileItem>? Unknown);
