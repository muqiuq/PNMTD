using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;
using PNMTD.Cookie.Access;

namespace PNMTD.Api.V1;

/// <summary>
/// The access watch in Cookie's window (docs/cookie-access-plan.md §10.3): an access case's logins and its answer, a ticket when
/// asked, and <i>This wasn't expected</i> from a target's <i>Access</i> tab.
/// </summary>
[Route("api/v1/cookie/access")]
[Authorize]
public sealed class CookieAccessController(AccessService access) : V1ControllerBase
{
    [HttpGet("cases/{id:guid}")]
    [RequireRight(Area.Cookie, Level.Use)]
    public async Task<AccessCaseDetail> Case(Guid id, CancellationToken ct) => await access.CaseAsync(id, User, ct);

    /// <summary><i>Expected</i> (with <i>Remember this</i>) or <i>Not expected</i> (§7): a contact for the target, or <i>Cookie: Instruct</i>.</summary>
    [HttpPost("cases/{id:guid}/answer")]
    [RequireRight(Area.Cookie, Level.Use)]
    [Audit(AuditEvents.AccessAnswered)]
    public async Task<IActionResult> Answer(Guid id, AccessAnswerRequest request, CancellationToken ct)
    {
        await access.AnswerAsync(id, request, User, ct);
        return NoContent();
    }

    /// <summary><i>Ask Cookie for a ticket</i> (§7): the only way an access case gets one.</summary>
    [HttpPost("cases/{id:guid}/ticket")]
    [RequireRight(Area.Cookie, Level.Instruct)]
    [RequireRight(Area.Redmine, Level.Link)]
    [Audit(AuditEvents.AccessTicketAsked)]
    public async Task<IActionResult> Ticket(Guid id, CancellationToken ct)
    {
        await access.AskTicketAsync(id, User, ct);
        return NoContent();
    }

    /// <summary><i>This wasn't expected</i> on a server's or router's login (§7): its case, answered <i>Not expected</i>.</summary>
    [HttpPost("logins/{id:guid}/not-expected")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AccessAnswered)]
    public async Task<CookieCaseRef> NotExpected(Guid id, AccessNotExpectedRequest request, CancellationToken ct) =>
        new(await access.NotExpectedAsync(id, request, User, ct), "");
}

/// <summary>Admin → Cookie → Access (§8): the warnings, known access and the routers' switches. <i>Cookie: Manage</i>.</summary>
[Route("api/v1/cookie/admin/access")]
[RequireRight(Area.Cookie, Level.Manage)]
public sealed class CookieAccessAdminController(AccessService access) : V1ControllerBase
{
    [HttpGet("state")]
    public async Task<AccessAdminState> State(CancellationToken ct) => await access.StateAsync(ct);

    [HttpGet("known")]
    public async Task<IReadOnlyList<AccessKnownItem>> Known(CancellationToken ct) => await access.KnownAsync(ct);

    [HttpPost("known")]
    [Audit(AuditEvents.AccessKnownAdded)]
    public async Task<AccessKnownItem> AddKnown(AccessKnownRequest request, CancellationToken ct) => await access.AddKnownAsync(request, User, ct);

    [HttpPost("known/{id:guid}/end")]
    [Audit(AuditEvents.AccessKnownEnded)]
    public async Task<AccessKnownItem> EndKnown(Guid id, CancellationToken ct) => await access.EndKnownAsync(id, User, ct);

    [HttpGet("routers")]
    public async Task<IReadOnlyList<AccessRouterItem>> Routers(CancellationToken ct) => await access.RoutersAsync(ct);

    /// <summary>Many routers' switches at once (§4.2): all of a location, all on, all off.</summary>
    [HttpPut("routers")]
    [Audit(AuditEvents.AccessWatchChanged)]
    public async Task<IActionResult> SwitchRouters(AccessRoutersRequest request, CancellationToken ct)
    {
        await access.SwitchRoutersAsync(request.SensorIds, request.Watch, ct);
        return NoContent();
    }
}

/// <summary>A router's <i>Access</i> tab (§10.4): its logins for <i>Monitoring: Edit</i>, its switch for <i>Cookie: Manage</i>.</summary>
[Route("api/v1/sensors/{id:guid}/mikrotik")]
[Authorize]
public sealed class SensorMikrotikAccessController(AccessService access) : V1ControllerBase
{
    [HttpGet("logins")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<RouterAccessView> Logins(Guid id, [FromQuery] AgentLoginQuery query, CancellationToken ct) => await access.RouterAsync(id, query, User, ct);

    [HttpPut("access-watch")]
    [RequireRight(Area.Cookie, Level.Manage)]
    [Audit(AuditEvents.AccessWatchChanged)]
    public async Task<IActionResult> Watch(Guid id, AccessWatchRequest request, CancellationToken ct)
    {
        await access.SwitchRoutersAsync([id], request.Watch, ct);
        return NoContent();
    }
}

/// <summary>
/// <i>Was this you?</i> (docs/cookie-access-plan.md §6.6, §9): the access watch's only page without sign-in. The token in the link
/// answers one question once; opening it answers nothing, only the page's buttons do.
/// </summary>
[Route("api/v1/access/was-this-you")]
[AllowAnonymous]
[EnableRateLimiting(AuthSetup.LoginRateLimit)]
public sealed class WasThisYouController(WasThisYou ask) : V1ControllerBase
{
    [HttpGet("{token}")]
    public async Task<WasThisYouView> View(string token, CancellationToken ct) => await ask.ViewAsync(token, ct);

    [HttpPost("{token}")]
    [Audit(AuditEvents.AccessPersonAnswered)]
    public async Task<WasThisYouResult> Answer(string token, WasThisYouRequest request, CancellationToken ct) =>
        new(await ask.AnswerAsync(token, request.ItWasMe, AuditContext.From(HttpContext).IpAddress, ct)
            ?? throw ApiException.Conflict("access_link_used", "This link no longer works."));
}
