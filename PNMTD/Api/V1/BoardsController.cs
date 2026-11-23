using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Boards;

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
}
