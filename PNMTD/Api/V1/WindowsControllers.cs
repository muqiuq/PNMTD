using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Agent;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>Windows servers (docs/windows-sensor-plan.md §9.2): the fleet and a server's agent.</summary>
[Route("api/v1/windows/servers")]
[Authorize(Policy = Policies.CanRead)]
public sealed class WindowsServersController(WindowsServerService servers) : V1ControllerBase
{
    /// <summary>The fleet: state, host, OS, agent, connection, remote access, audit gaps, diagnostics.</summary>
    [HttpGet]
    public async Task<ActionResult<Paged<WindowsServerItem>>> List([FromQuery] WindowsServerQuery query, CancellationToken ct) =>
        await servers.ListAsync(query, ct);

    /// <summary>A server's agent: its connection, key, last heartbeat, switches and recent problems.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WindowsServerDetail>> Get(Guid id, CancellationToken ct) => await servers.DetailAsync(id, ct);

    /// <summary>A section, typed (§4). <c>access</c> and <c>event-log</c> need the User role.</summary>
    [HttpGet("{id:guid}/sections/{section}")]
    public async Task<ActionResult<WindowsSectionView>> Section(Guid id, string section, CancellationToken ct) =>
        await servers.SectionAsync(id, section, CanSeeSecrets, ct);

    /// <summary>Logins and connections (§4.7): every admin login, failures aggregated. The User role: names and addresses.</summary>
    [HttpGet("{id:guid}/logins")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<Paged<WindowsLoginItem>>> Logins(Guid id, [FromQuery] WindowsLoginQuery query, CancellationToken ct) =>
        await servers.LoginsAsync(id, query, ct);

    /// <summary>The readable changes of the sections (§4.9).</summary>
    [HttpGet("{id:guid}/changes")]
    public async Task<ActionResult<Paged<WindowsChangeItem>>> Changes(Guid id, [FromQuery] WindowsChangeQuery query, CancellationToken ct) =>
        await servers.ChangesAsync(id, query, ct);

    /// <summary>The agent log of this server, with its problems (§3.8). Admin.</summary>
    [HttpGet("{id:guid}/log")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<Paged<WindowsAgentLogItem>>> Log(Guid id, [FromQuery] WindowsAgentLogQuery query, CancellationToken ct) =>
        await servers.LogAsync(id, query, ct);

    /// <summary>Asks the agent to collect sections now (§6); 409 when it is offline.</summary>
    [HttpPost("{id:guid}/collect")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<WindowsRequestResult>> Collect(Guid id, WindowsCollectRequest request, CancellationToken ct) =>
        await servers.CollectAsync(id, request, ct);

    /// <summary>The last 500 lines of the agent's log file (§3.8). Admin.</summary>
    [HttpPost("{id:guid}/send-log")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<WindowsAgentLogFile>> SendLog(Guid id, CancellationToken ct) => await servers.SendLogAsync(id, ct);

    /// <summary>WMI (§16): PNMT's settings for the server, and its queries, their state and the own queries' rows. Admin.</summary>
    [HttpGet("{id:guid}/wmi")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<WindowsWmiView>> Wmi(Guid id, CancellationToken ct) => await servers.WmiAsync(id, ct);

    /// <summary>Saves the WMI settings and sends the agent its new configuration. Admin, audited.</summary>
    [HttpPut("{id:guid}/wmi")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<WindowsWmiView>> SaveWmi(Guid id, WmiConfig request, CancellationToken ct) => await servers.SaveWmiAsync(id, request, ct);

    /// <summary>The server's event switches (§4.7, §4.8). Audit-logged.</summary>
    [HttpPut("{id:guid}/settings")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<WindowsServerDetail>> Settings(Guid id, WindowsServerSettings request, CancellationToken ct) =>
        await servers.SettingsAsync(id, request, ct);

    /// <summary>Revokes the agent's key and closes its connection (§3.4); the sensor and its history stay. Audit-logged.</summary>
    [HttpPost("{id:guid}/revoke")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<WindowsServerDetail>> Revoke(Guid id, WindowsRevokeRequest request, CancellationToken ct) =>
        await servers.RevokeAsync(id, request, ct);

    /// <summary>A token bound to this server: joining with it replaces the key and keeps the history (§3.3). Shown once.</summary>
    [HttpPost("{id:guid}/rejoin-token")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<WindowsJoinTokenCreated>> RejoinToken(Guid id, WindowsRejoinTokenRequest request, CancellationToken ct) =>
        await servers.RejoinTokenAsync(id, request, ct);
}

/// <summary>Join tokens, the agents' log and the agent's downloads (§9.2). Admin.</summary>
[Route("api/v1/windows")]
[Authorize(Policy = Policies.Admin)]
public sealed class WindowsController(WindowsServerService servers) : V1ControllerBase
{
    [HttpGet("join-tokens")]
    public async Task<ActionResult<Paged<WindowsJoinTokenItem>>> Tokens([FromQuery] WindowsJoinTokenQuery query, CancellationToken ct) =>
        await servers.TokensAsync(query, ct);

    /// <summary>A join token for a server on an existing host (§3.3): answered once, only its hash is kept. Audit-logged.</summary>
    [HttpPost("join-tokens")]
    public async Task<ActionResult<WindowsJoinTokenCreated>> CreateToken(WindowsJoinTokenRequest request, CancellationToken ct) =>
        await servers.CreateTokenAsync(request, ct);

    /// <summary>Revokes a token that was not used yet. Audit-logged.</summary>
    [HttpDelete("join-tokens/{id:guid}")]
    public async Task<IActionResult> RevokeToken(Guid id, CancellationToken ct)
    {
        await servers.RevokeTokenAsync(id, ct);
        return NoContent();
    }

    /// <summary>Every agent's log: joins, connections, problems, crashes, failures, revocations.</summary>
    [HttpGet("agent-log")]
    public async Task<ActionResult<Paged<WindowsAgentLogItem>>> AgentLog([FromQuery] WindowsAgentLogQuery query, CancellationToken ct) =>
        await servers.LogAsync(null, query, ct);

    /// <summary>The agent of PNMTD's version: its release links and SHA-256 (§10.1).</summary>
    [HttpGet("agent")]
    public async Task<ActionResult<WindowsAgentDownloads>> Agent(CancellationToken ct) => await servers.DownloadsAsync(ct);
}
