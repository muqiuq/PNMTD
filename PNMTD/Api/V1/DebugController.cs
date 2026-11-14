using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>Admin → Debug (docs/ai-operations-plan.md §8): test sends, 10 a minute, audited.</summary>
[Route("api/v1/debug")]
[RequireRight(Area.System, Level.Manage)]
public sealed class DebugController(DebugService debug) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DebugStatus>> Status(CancellationToken ct) => await debug.StatusAsync(ct);

    [HttpPost("pushover")]
    [EnableRateLimiting(AuthSetup.DebugRateLimit)]
    public async Task<ActionResult<DebugResult>> Pushover(DebugPushoverRequest request, CancellationToken ct) => await debug.PushoverAsync(request, ct);

    [HttpPost("mail")]
    [EnableRateLimiting(AuthSetup.DebugRateLimit)]
    public async Task<ActionResult<DebugResult>> Mail(DebugMailRequest request, CancellationToken ct) => await debug.MailAsync(request, ct);
}
