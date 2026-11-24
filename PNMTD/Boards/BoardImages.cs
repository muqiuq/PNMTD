using Microsoft.Net.Http.Headers;
using PNMTD.Files;
using PNMTD.Models.Db;

namespace PNMTD.Boards;

/// <summary>
/// Serving a board's image (docs/board-messages-plan.md §9.4), to the board app by its token and to the editor's preview by the
/// admin's session: streamed from the files folder as its detected type, cached for a year (files never change), the hash as ETag.
/// </summary>
public static class BoardImages
{
    /// <summary>The image, or null when there is none or it left the disk.</summary>
    public static IResult? Serve(HttpContext http, FileEntity? file, FileStore store)
    {
        if (file is null || store.Locate(file.Path) is not { } path)
        {
            return null;
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.File(stream, file.ContentType, lastModified: new DateTimeOffset(DateTime.SpecifyKind(file.CreatedAt, DateTimeKind.Utc)),
            entityTag: new EntityTagHeaderValue($"\"{file.Sha256}\""), enableRangeProcessing: true);
    }
}
