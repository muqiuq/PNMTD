using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>The tunnel network on the admin page (docs/mikrotik-plan.md §10, §11.2). Admin only.</summary>
[Route("api/v1/wireguard")]
[Authorize(Policy = Policies.Admin)]
public sealed class WireGuardController(WireGuardService wireGuard) : V1ControllerBase
{
    /// <summary>The gateway, the endpoint in effect with the source of each part, peers up out of all, and warnings.</summary>
    [HttpGet("status")]
    public async Task<ActionResult<WireGuardStatus>> Status(CancellationToken ct) => await wireGuard.StatusAsync(ct);

    [HttpGet("network")]
    public async Task<ActionResult<WireGuardNetworkView>> Network(CancellationToken ct) => await wireGuard.NetworkAsync(ct);

    /// <summary>Changes the settings; one the environment sets is refused. A new prefix needs <c>confirmPrefix</c>. Audit-logged.</summary>
    [HttpPut("network")]
    public async Task<ActionResult<WireGuardNetworkSaved>> SaveNetwork(WireGuardNetworkRequest request, CancellationToken ct) =>
        await wireGuard.UpdateNetworkAsync(request, ct);

    /// <summary>Every peer, router or custom, with its tunnel as the gateway sees it.</summary>
    [HttpGet("peers")]
    public async Task<ActionResult<IReadOnlyList<WireGuardPeerItem>>> Peers(CancellationToken ct) => Ok(await wireGuard.PeersAsync(ct));
}
