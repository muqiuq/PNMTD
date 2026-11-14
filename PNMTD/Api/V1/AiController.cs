using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>Admin → AI (docs/ai-operations-plan.md §14, §15): Admin only, because requests hold mail contents.</summary>
[Route("api/v1/ai")]
[RequireRight(Area.Ai, Level.Manage)]
public sealed class AiController(AiService ai, PNMTD.Decisions.DecisionBook decisions, PNMTD.Ai.AiWorkQueue queue) : V1ControllerBase
{
    /// <summary>The unmatched mails of the last days (§6.2): <c>dryRun</c> (the default) counts them first.</summary>
    [HttpPost("mail/backfill")]
    [Audit(AuditEvents.AiBackfillStarted)]
    public async Task<ActionResult<MailBackfillResult>> Backfill(MailBackfillRequest request, CancellationToken ct) =>
        await ai.BackfillAsync(request, decisions, queue, ct);

    [HttpGet("status")]
    public async Task<ActionResult<AiStatus>> Status(CancellationToken ct) => await ai.StatusAsync(ct);

    [HttpGet("requests")]
    public async Task<ActionResult<Paged<AiRequestItem>>> Requests([FromQuery] AiRequestQuery query, CancellationToken ct) => await ai.RequestsAsync(query, ct);

    [HttpGet("requests/{id:long}")]
    public async Task<ActionResult<AiRequestDetail>> Request(long id, CancellationToken ct) => await ai.RequestAsync(id, ct);

    [HttpGet("work")]
    public async Task<ActionResult<Paged<AiWorkItem>>> Work([FromQuery] AiWorkQuery query, CancellationToken ct) => await ai.WorkAsync(query, ct);

    [HttpPost("work/{id:guid}/retry")]
    [Audit(AuditEvents.AiWorkRetried)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        await ai.RetryAsync(id, ct);
        return NoContent();
    }

    [HttpPost("work/{id:guid}/cancel")]
    [Audit(AuditEvents.AiWorkCancelled)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await ai.CancelAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Admin → Debug → Test the AI (§8.4).</summary>
[Route("api/v1/debug/ai")]
[RequireRight(Area.System, Level.Manage)]
public sealed class DebugAiController(AiService ai) : V1ControllerBase
{
    [HttpPost]
    [EnableRateLimiting(AuthSetup.DebugRateLimit)]
    [Audit(AuditEvents.DebugAiTested)]
    public async Task<ActionResult<IReadOnlyList<DebugAiResult>>> Test(CancellationToken ct) => Ok(await ai.TestAsync(ct));
}
