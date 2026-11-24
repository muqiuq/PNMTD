using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Boards;

namespace PNMTD.Api.V1;

/// <summary>
/// Board messages (docs/board-messages-plan.md §7, §11): a message box sent to boards from Admin → Debug. Reading the open and the recent
/// ones needs <i>Boards: View</i>; sending and withdrawing <i>Boards: Message</i>, which <i>Manage</i> includes. Sending shares the Debug
/// page's limit of 10 a minute per user; both are audited.
/// </summary>
[Route("api/v1/boards/messages")]
[RequireRight(Area.Boards, Level.View)]
public sealed class BoardMessagesController(BoardMessageService messages) : V1ControllerBase
{
    /// <summary><c>?open=true</c>: the open messages; otherwise the last <c>limit</c> (20) sent in the days kept, newest first.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<BoardMessageItem>> List([FromQuery] bool open = false, [FromQuery] int limit = 20, CancellationToken ct = default) =>
        await messages.ListAsync(open, open ? 200 : limit, ct);

    /// <summary>To the boards named, or all that show messages: 409 <c>board_disabled</c>, <c>board_no_messages</c>, <c>too_many_open</c>.</summary>
    [HttpPost]
    [RequireRight(Area.Boards, Level.Message)]
    [EnableRateLimiting(AuthSetup.DebugRateLimit)]
    [Audit(AuditEvents.BoardMessageSent)]
    [ProducesResponseType<BoardMessageItem>(StatusCodes.Status201Created)]
    public async Task<ActionResult<BoardMessageItem>> Send(BoardMessageRequest request, CancellationToken ct)
    {
        var sent = await messages.SendAsync(request, new BoardMessageSender(BoardMessageSource.Person, User.UserId(), Name(), AuditWho.From(HttpContext)), ct);
        return Created($"/api/v1/boards/messages/{sent.Id}", sent);
    }

    /// <summary>Ends it on every board it went to: 409 <c>not_open</c> when it ended already.</summary>
    [HttpPost("{id:guid}/withdraw")]
    [RequireRight(Area.Boards, Level.Message)]
    [Audit(AuditEvents.BoardMessageWithdrawn)]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct)
    {
        await messages.WithdrawAsync(id, User.UserId(), Name(), AuditWho.From(HttpContext), ct);
        return NoContent();
    }

    private string Name() => User.FindFirst(SessionClaims.DisplayName)?.Value is { Length: > 0 } display ? display : User.Identity?.Name ?? "someone";
}
