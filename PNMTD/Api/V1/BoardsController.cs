using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Boards;
using PNMTD.Files;
using PNMTD.Knowledge;

namespace PNMTD.Api.V1;

/// <summary>
/// Boards (docs/boards-plan.md §12.1): monitoring dashboards for a wall, shown at /pnmt-board/&lt;token&gt;. Reading needs <i>Boards: View</i>,
/// every change and the URL <i>Manage</i>; adding an element or a rule also needs its area's right (§7.2).
/// </summary>
[Route("api/v1/boards")]
[RequireRight(Area.Boards, Level.View)]
public sealed class BoardsController(BoardService boards, BoardData data) : V1ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<BoardItem>> List(CancellationToken ct) => await boards.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<BoardDetail> Get(Guid id, CancellationToken ct) => await boards.GetAsync(id, ct);

    /// <summary>Empty, the starting layout, or a copy of a board; a URL of its own, opened from anywhere until networks are set.</summary>
    [HttpPost]
    [RequireRight(Area.Boards, Level.Manage)]
    [Audit(AuditEvents.BoardCreated)]
    public async Task<ActionResult<BoardDetail>> Create(BoardCreateRequest request, CancellationToken ct)
    {
        var created = await boards.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>The whole page at once: 409 <c>edit_conflict</c> from an older revision, 403 <c>element_right</c> for an element beyond the editor's rights.</summary>
    [HttpPut("{id:guid}")]
    [RequireRight(Area.Boards, Level.Manage)]
    [Audit(AuditEvents.BoardChanged)]
    public async Task<BoardDetail> Update(Guid id, BoardRequest request, CancellationToken ct) => await boards.UpdateAsync(id, request, ct);

    [HttpPost("{id:guid}/duplicate")]
    [RequireRight(Area.Boards, Level.Manage)]
    [Audit(AuditEvents.BoardCreated)]
    public async Task<ActionResult<BoardDetail>> Duplicate(Guid id, CancellationToken ct)
    {
        var created = await boards.DuplicateAsync(id, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>At once: its URL stops working.</summary>
    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Boards, Level.Manage)]
    [Audit(AuditEvents.BoardDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await boards.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>The URL, shown: audited.</summary>
    [HttpGet("{id:guid}/url")]
    [RequireRight(Area.Boards, Level.Manage)]
    [Audit(AuditEvents.BoardUrlShown)]
    public async Task<BoardUrl> Url(Guid id, CancellationToken ct) => await boards.UrlAsync(id, ct);

    /// <summary>A new URL; the old one stops at once.</summary>
    [HttpPost("{id:guid}/url/renew")]
    [RequireRight(Area.Boards, Level.Manage)]
    [Audit(AuditEvents.BoardUrlRenewed)]
    public async Task<BoardUrl> Renew(Guid id, CancellationToken ct) => await boards.RenewAsync(id, ct);

    /// <summary>A draft's view, as the board would show it; what the editor may not see is withheld (§10.4).</summary>
    [HttpPost("preview")]
    [NotAudited("a preview: computed, nothing saved")]
    public async Task<BoardView> Preview(BoardPreviewRequest request, CancellationToken ct)
    {
        var v = new ApiValidator();
        var layout = BoardLayouts.Validate(request.Layout, v);
        v.ThrowIfInvalid();
        return await data.BuildAsync(request.Name ?? "New board", layout, User.Rights(), null, ct);
    }

    /// <summary>The element types and measures (each with whether this editor may add it), the themes, and the pickers' lists.</summary>
    [HttpGet("catalog")]
    public async Task<BoardCatalog> Catalog(CancellationToken ct) => await boards.CatalogAsync(ct);

    /// <summary>Hosts and sensors for status tiles, by name: at most 20.</summary>
    [HttpGet("catalog/targets")]
    [RequireRight(Area.Boards, Level.Manage)]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<IReadOnlyList<BoardTargetOption>> Targets([FromQuery] string? q, CancellationToken ct) => await boards.TargetsAsync(q, ct);

    /// <summary>The names of hosts and sensors by id, for a layout's tiles.</summary>
    [HttpGet("catalog/names")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<IReadOnlyList<BoardTargetOption>> Names([FromQuery] Guid[] id, CancellationToken ct) => await boards.TargetNamesAsync(id, ct);

    /// <summary>The address PNMT sees for this browser, for the networks.</summary>
    [HttpGet("address")]
    public BoardAddress Address() => boards.Address();

    // --- images (docs/board-messages-plan.md §9.4) -----------------------------------------------------------------------------

    /// <summary>
    /// An image for the board's image elements, the file as the body (<c>?name=…</c>): PNG, JPEG, GIF or WebP by its bytes, animations
    /// kept, metadata removed, at most <c>Boards:MaxImageBytes</c>. Its use is recorded at once. 413 <c>too_large</c>, 415
    /// <c>not_an_image</c>, 429 <c>too_many_uploads</c>, 503 <c>file_storage_unavailable</c>.
    /// </summary>
    [HttpPost("{id:guid}/images")]
    [RequireRight(Area.Boards, Level.Manage)]
    [BoardImageUploadLimit]
    [ProducesResponseType<BoardImageItem>(StatusCodes.Status201Created)]
    [Audit(AuditEvents.FileUploaded)]
    public async Task<ActionResult<BoardImageItem>> UploadImage(Guid id, [FromQuery] string? name, [FromServices] FileStore store,
        [FromServices] UploadGuard uploads, [FromServices] IOptionsMonitor<KnowledgeBaseOptions> files, CancellationToken ct)
    {
        var perHour = files.CurrentValue.UploadsPerHour;
        if (User.UserId() is { } user && !uploads.TryEnter(user, perHour, out var retryAfter))
        {
            Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            throw new ApiException(StatusCodes.Status429TooManyRequests, FileErrors.TooManyUploads,
                $"At most {perHour} uploads per hour (KnowledgeBase:UploadsPerHour).");
        }
        var actor = new FileActor(User.UserId(), User.Identity?.Name,
            User.FindFirst(SessionClaims.DisplayName)?.Value is { Length: > 0 } display ? display : null, AuditContext.From(HttpContext));
        var item = await boards.UploadImageAsync(id, Request.Body, name, actor, store, ct);
        return Created($"/api/v1/boards/{id}/images/{item.Id}", item);
    }

    /// <summary>The images the board uses, newest first.</summary>
    [HttpGet("{id:guid}/images")]
    public async Task<IReadOnlyList<BoardImageItem>> Images(Guid id, CancellationToken ct) => await boards.ImagesAsync(id, ct);

    /// <summary>One of the board's images, for the editor's preview: 404 for a file the board doesn't use.</summary>
    [HttpGet("{id:guid}/images/{fileId:guid}")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "application/octet-stream")]
    public async Task<IResult> Image(Guid id, Guid fileId, [FromServices] FileStore store, CancellationToken ct) =>
        BoardImages.Serve(HttpContext, await boards.ImageAsync(id, fileId, ct), store) ?? throw ApiException.NotFound("Image");
}

/// <summary>
/// Raises the request body limit for a board's image upload to <c>Boards:MaxImageBytes</c> plus 4 KB (docs/board-messages-plan.md §9.4);
/// the store checks the limit again while the body streams in.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BoardImageUploadLimitAttribute : Attribute, IResourceFilter, IOrderedFilter
{
    public int Order => -2000;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var max = context.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<BoardOptions>>().CurrentValue.MaxImageBytes;
        if (context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = max + 4 * 1024;
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
