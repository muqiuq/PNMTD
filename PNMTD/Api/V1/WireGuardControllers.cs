using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>The tunnel network on the admin page (docs/mikrotik-plan.md §10, §11.2). Admin only.</summary>
[Route("api/v1/wireguard")]
[RequireRight(Area.Mikrotik, Level.Manage)]
public sealed class WireGuardController(WireGuardService wireGuard, WireGuardTunnelService tunnels) : V1ControllerBase
{
    /// <summary>The gateway, the endpoint in effect with the source of each part, peers up out of all, and warnings.</summary>
    [HttpGet("status")]
    public async Task<ActionResult<WireGuardStatus>> Status(CancellationToken ct) => await wireGuard.StatusAsync(ct);

    [HttpGet("network")]
    public async Task<ActionResult<WireGuardNetworkView>> Network(CancellationToken ct) => await wireGuard.NetworkAsync(ct);

    /// <summary>Changes the settings; one the environment sets is refused. A new prefix needs <c>confirmPrefix</c>. Audit-logged.</summary>
    [HttpPut("network")]
    [Audit(AuditEvents.WireGuardNetworkChanged)]
    public async Task<ActionResult<WireGuardNetworkSaved>> SaveNetwork(WireGuardNetworkRequest request, CancellationToken ct) =>
        await wireGuard.UpdateNetworkAsync(request, ct);

    /// <summary>Every peer, router or custom, with its tunnel as the gateway sees it.</summary>
    [HttpGet("peers")]
    public async Task<ActionResult<IReadOnlyList<WireGuardPeerItem>>> Peers(CancellationToken ct) => Ok(await wireGuard.PeersAsync(ct));

    /// <summary>Custom tunnels (§3.5), each with the routers it actually reaches.</summary>
    [HttpGet("tunnels")]
    public async Task<ActionResult<IReadOnlyList<WireGuardTunnelItem>>> Tunnels(CancellationToken ct) => Ok(await tunnels.ListAsync(null, ct));

    [HttpPost("tunnels")]
    [Audit(AuditEvents.WireGuardTunnelCreated)]
    public async Task<ActionResult<WireGuardTunnelItem>> CreateTunnel(WireGuardTunnelRequest request, CancellationToken ct) =>
        await tunnels.CreateAsync(request, ct);

    [HttpPut("tunnels/{id:guid}")]
    [Audit(AuditEvents.WireGuardTunnelChanged)]
    public async Task<ActionResult<WireGuardTunnelItem>> UpdateTunnel(Guid id, WireGuardTunnelRequest request, CancellationToken ct) =>
        await tunnels.UpdateAsync(id, request, ct);

    [HttpDelete("tunnels/{id:guid}")]
    [Audit(AuditEvents.WireGuardTunnelDeleted)]
    public async Task<IActionResult> DeleteTunnel(Guid id, CancellationToken ct)
    {
        await tunnels.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>New keys: every downloaded file stops working. Audit-logged.</summary>
    [HttpPost("tunnels/{id:guid}/regenerate-keys")]
    [Audit(AuditEvents.WireGuardKeysRegenerated)]
    public async Task<ActionResult<WireGuardTunnelItem>> RegenerateTunnel(Guid id, CancellationToken ct) => await tunnels.RegenerateAsync(id, ct);

    /// <summary>The wg-quick file (the UI also shows it as a QR code). It holds a private key: audit-logged.</summary>
    [HttpGet("tunnels/{id:guid}/config")]
    public async Task<ActionResult<WireGuardTunnelConfig>> TunnelConfig(Guid id, CancellationToken ct) => await tunnels.ConfigAsync(id, null, ct);
}

/// <summary>A user's own custom tunnels (§3.5, §11.3): any role, only the tunnels an Admin gave them.</summary>
[Route("api/v1/account/tunnels")]
[Authorize]
public sealed class AccountTunnelsController(WireGuardTunnelService tunnels) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WireGuardTunnelItem>>> Mine(CancellationToken ct) =>
        Ok(await tunnels.ListAsync(User.UserId() ?? Guid.Empty, ct));

    /// <summary>The user's own tunnel, while it is enabled and not expired. Audit-logged.</summary>
    [HttpGet("{id:guid}/config")]
    public async Task<ActionResult<WireGuardTunnelConfig>> Config(Guid id, CancellationToken ct) =>
        await tunnels.ConfigAsync(id, User.UserId() ?? Guid.Empty, ct);
}
