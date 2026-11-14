using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// Uploaded files (docs/locations-knowledge-plan.md §5.5, §9): POST /api/v1/files (CanWrite), GET /api/v1/files/{id}/{name}
// (CanRead), and the Admin's overview and delete.

/// <summary>
/// A stored file, as an upload's reply and in the responses of the pages and locations that use it (§5.5, §5.6):
/// <see cref="Url"/> is what Markdown points to. An image has its <see cref="Width"/> and <see cref="Height"/>; an
/// attachment's <see cref="ContentType"/> names it for the chip's icon, but it is always served as a download.
/// </summary>
public sealed record FileItem(Guid Id, FileKind Kind, string Name, string ContentType, long Size, int? Width, int? Height, string Url);
