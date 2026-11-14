using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Knowledge;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1;

/// <summary>
/// Uploaded files (docs/locations-knowledge-plan.md §5.5, §9): images and attachments for pages and locations, stored in the
/// files folder on the Docker host. Uploading needs CanWrite, reading CanRead; the overview and deleting are the Admin's.
/// </summary>
[Route("api/v1/files")]
[Authorize(Policy = Policies.CanRead)]
public sealed class FilesController(FileStore store, FileAdmin admin, UploadGuard uploads, IDbContextFactory<PnmtdDbContext> dbs,
    IOptionsMonitor<KnowledgeBaseOptions> options) : V1ControllerBase
{
    /// <summary>
    /// <c>?kind=image|attachment&amp;name=…</c> with the file as the body (any content type, no form). An image's type comes
    /// from its bytes and its metadata is removed; an attachment's name must not end in a blocked extension. The same bytes
    /// twice give the same file. 413 <c>too_large</c>, 415 <c>not_an_image</c>, 422 <c>blocked_type</c>, 429
    /// <c>too_many_uploads</c>. No Consumes constraint: it would decide at routing, before authorization.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = Policies.CanWrite)]
    [FileUploadLimit]
    [ProducesResponseType<FileItem>(StatusCodes.Status201Created)]
    public async Task<ActionResult<FileItem>> Upload([FromQuery] string? kind, [FromQuery] string? name, CancellationToken ct)
    {
        var fileKind = kind?.Trim().ToLowerInvariant() switch
        {
            "image" => FileKind.Image,
            "attachment" => FileKind.Attachment,
            _ => throw ApiException.Invalid("kind", "image or attachment."),
        };
        var actor = Actor();
        var perHour = options.CurrentValue.UploadsPerHour;
        if (actor.UserId is { } user && !uploads.TryEnter(user, perHour, out var retryAfter))
        {
            Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            throw new ApiException(StatusCodes.Status429TooManyRequests, FileErrors.TooManyUploads,
                $"At most {perHour} uploads per hour (KnowledgeBase:UploadsPerHour).");
        }
        var item = await store.StoreAsync(Request.Body, fileKind, name, Request.ContentType, actor, ct);
        return Created(item.Url, item);
    }

    /// <summary>
    /// A file, streamed from the folder with range requests: an image inline as its detected type, anything else as
    /// <c>application/octet-stream</c> and <c>attachment</c>. Cached for a year (files never change), the hash as ETag.
    /// The name is only there to be read; the id finds the file. 404 once it left the disk (<i>File removed</i>).
    /// </summary>
    [HttpGet("{id:guid}/{name}")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "application/octet-stream")]
    public async Task<IActionResult> Download(Guid id, string name, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var file = await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw ApiException.NotFound("File");
        var path = store.Locate(file.Path) ?? throw ApiException.NotFound("File");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        var etag = new EntityTagHeaderValue($"\"{file.Sha256}\"");
        var modified = new DateTimeOffset(DateTime.SpecifyKind(file.CreatedAt, DateTimeKind.Utc));
        if (file.Kind == FileKind.Image)
        {
            var inline = new ContentDispositionHeaderValue("inline");
            inline.SetHttpFileName(file.Name);
            Response.Headers.ContentDisposition = inline.ToString();
            return File(stream, file.ContentType, modified, etag, enableRangeProcessing: true);
        }
        // Never inline, whatever was uploaded (§8.6): a download, with the name in RFC 5987 form for non-ASCII.
        return File(stream, "application/octet-stream", file.Name, modified, etag, enableRangeProcessing: true);
    }

    /// <summary>The Admin's overview (§10.6): totals, the folder, the files by size, unused ones, unknown ones on disk.</summary>
    [HttpGet]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<FilesOverview>> Overview([FromQuery] FileQuery query, CancellationToken ct) =>
        await admin.OverviewAsync(query, ct);

    /// <summary>Deletes a file at once, from the disk too (an image that shows what it shouldn't): pages show <i>File removed</i>.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!await store.DeleteNowAsync(id, Actor(), ct))
        {
            throw ApiException.NotFound("File");
        }
        return NoContent();
    }

    private FileActor Actor() => new(User.UserId(), User.Identity?.Name,
        User.FindFirst(SessionClaims.DisplayName)?.Value is { Length: > 0 } display ? display : null, AuditContext.From(HttpContext));
}

/// <summary>
/// Raises the request body limit for the upload alone, to KnowledgeBase:MaxAttachmentBytes plus 4 KB (§5.5). Each kind's own
/// limit is checked while the body streams in, so an oversized upload is cut off early.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FileUploadLimitAttribute : Attribute, IResourceFilter, IOrderedFilter
{
    public int Order => -2000;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var max = context.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<KnowledgeBaseOptions>>().CurrentValue.MaxAttachmentBytes;
        if (context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = max + 4 * 1024;
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
