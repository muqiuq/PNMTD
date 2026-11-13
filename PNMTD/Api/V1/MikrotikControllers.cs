using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>A router's own endpoints (docs/mikrotik-plan.md §10): its scripts, the host key, poll now.</summary>
[Route("api/v1/sensors/{id:guid}/mikrotik")]
[Authorize(Policy = Policies.CanWrite)]
public sealed class SensorMikrotikController(MikrotikRouterService routers) : V1ControllerBase
{
    /// <summary>
    /// The join script (§4.3): pasted into the router's terminal, or imported as a .rsc file. In the
    /// WireGuard modes it holds the router's private key; every view is audit-logged.
    /// </summary>
    [HttpGet("join-script")]
    public async Task<ActionResult<MikrotikScript>> JoinScript(Guid id, CancellationToken ct) =>
        await routers.ScriptAsync(id, removal: false, ct);

    /// <summary>The script that removes everything PNMT created on the router (§4.6).</summary>
    [HttpGet("removal-script")]
    public async Task<ActionResult<MikrotikScript>> RemovalScript(Guid id, CancellationToken ct) =>
        await routers.ScriptAsync(id, removal: true, ct);

    /// <summary>Polls the router now, and answers when the poll is done.</summary>
    [HttpPost("poll")]
    public async Task<ActionResult<MikrotikRouterDetail>> Poll(Guid id, CancellationToken ct) =>
        await routers.PollNowAsync(id, ct);

    /// <summary>Accepts the host key the router presented instead of the pinned one (§4.4). Audit-logged.</summary>
    [HttpPost("host-key/accept")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<MikrotikRouterDetail>> AcceptHostKey(Guid id, CancellationToken ct) =>
        await routers.AcceptHostKeyAsync(id, ct);
}

/// <summary>The admin page's endpoints (§11.2): the overview and the fleet.</summary>
[Route("api/v1/mikrotik")]
[Authorize(Policy = Policies.Admin)]
public sealed class MikrotikController(MikrotikRouterService routers) : V1ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<MikrotikStatus>> Status(CancellationToken ct) => await routers.StatusAsync(ct);

    [HttpGet("routers")]
    public async Task<ActionResult<Paged<MikrotikFleetItem>>> Routers([FromQuery] MikrotikFleetQuery query, CancellationToken ct) =>
        await routers.FleetAsync(query, ct);
}
