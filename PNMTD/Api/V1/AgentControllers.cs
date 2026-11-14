using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Lib.Agent;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>
/// Servers with an agent, Windows and Linux (docs/windows-sensor-plan.md §9.2; docs/linux-sensor-plan.md §10): the fleet
/// and a server's agent. Under /api/v1/windows/ before L1; a Windows server's typed sections and WMI stay there.
/// </summary>
[Route("api/v1/agents/servers")]
[Authorize]
public sealed class AgentServersController(AgentServerService servers, SshKeyService keys, AgentScriptService scripts) : V1ControllerBase
{
    /// <summary>The fleet of both platforms: state, host, OS, agent, connection, remote access, audit gaps, diagnostics.</summary>
    [HttpGet]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<AgentServerItem>>> List([FromQuery] AgentServerQuery query, CancellationToken ct) =>
        await servers.ListAsync(query, ct);

    /// <summary>A server's agent: its connection, key, last heartbeat, switches and recent problems.</summary>
    [HttpGet("{id:guid}")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<AgentServerDetail>> Get(Guid id, CancellationToken ct) => await servers.DetailAsync(id, ct);

    /// <summary>Logins and connections (§4.7): every admin login, failures aggregated. The User role: names and addresses.</summary>
    [HttpGet("{id:guid}/logins")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<Paged<AgentLoginItem>>> Logins(Guid id, [FromQuery] AgentLoginQuery query, CancellationToken ct) =>
        await servers.LoginsAsync(id, query, ct);

    /// <summary>
    /// A Linux server's SSH keys (docs/linux-sensor-plan.md §6.3, §10): sshd's key settings and CA keys, each file and key with its
    /// registration, last use and whether PNMT may remove it here. The User role: key comments and accounts.
    /// </summary>
    [HttpGet("{id:guid}/ssh-keys")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    public async Task<ActionResult<SshKeyServerView>> SshKeys(Guid id, CancellationToken ct) => await keys.ServerKeysAsync(id, ct);

    /// <summary>A Linux server's accounts for the run dialog (docs/linux-sensor-plan.md §8.4), with what its config.yaml allows. Admin, as scripts are.</summary>
    [HttpGet("{id:guid}/run-as-accounts")]
    [RequireRight(Area.Servers, Level.Manage)]
    public async Task<ActionResult<LinuxRunAsAccounts>> RunAsAccounts(Guid id, CancellationToken ct) => await scripts.RunAsAccountsAsync(id, ct);

    /// <summary>The readable changes of the sections (§4.9).</summary>
    [HttpGet("{id:guid}/changes")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<AgentChangeItem>>> Changes(Guid id, [FromQuery] AgentChangeQuery query, CancellationToken ct) =>
        await servers.ChangesAsync(id, query, ct);

    /// <summary>The agent log of this server, with its problems (§3.8). Admin.</summary>
    [HttpGet("{id:guid}/log")]
    [RequireRight(Area.Servers, Level.Manage)]
    public async Task<ActionResult<Paged<AgentLogItem>>> Log(Guid id, [FromQuery] AgentLogQuery query, CancellationToken ct) =>
        await servers.LogAsync(id, query, ct);

    /// <summary>Asks the agent to collect sections now (§6); 409 when it is offline.</summary>
    [HttpPost("{id:guid}/collect")]
    [RequireRight(Area.Commands, Level.Run)]
    [Audit(AuditEvents.AgentCollectRequested)]
    public async Task<ActionResult<AgentRequestResult>> Collect(Guid id, AgentCollectRequest request, CancellationToken ct) =>
        await servers.CollectAsync(id, request, ct);

    /// <summary>The last 500 lines of the agent's log file (§3.8). Admin.</summary>
    [HttpPost("{id:guid}/send-log")]
    [RequireRight(Area.Servers, Level.Manage)]
    [Audit(AuditEvents.AgentLogRequested)]
    public async Task<ActionResult<AgentLogFile>> SendLog(Guid id, CancellationToken ct) => await servers.SendLogAsync(id, ct);

    /// <summary>The server's event switches (§4.7, §4.8). Audit-logged.</summary>
    [HttpPut("{id:guid}/settings")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AgentSettingsChanged)]
    public async Task<ActionResult<AgentServerDetail>> Settings(Guid id, AgentServerSettings request, CancellationToken ct) =>
        await servers.SettingsAsync(id, request, ct);

    /// <summary>Revokes the agent's key and closes its connection (§3.4); the sensor and its history stay. Audit-logged.</summary>
    [HttpPost("{id:guid}/revoke")]
    [RequireRight(Area.Servers, Level.Manage)]
    [Audit(AuditEvents.AgentRevoked)]
    public async Task<ActionResult<AgentServerDetail>> Revoke(Guid id, AgentRevokeRequest request, CancellationToken ct) =>
        await servers.RevokeAsync(id, request, ct);

    /// <summary>A token bound to this server, of its platform: joining with it replaces the key and keeps the history (§3.3). Shown once.</summary>
    [HttpPost("{id:guid}/rejoin-token")]
    [RequireRight(Area.Servers, Level.Manage)]
    [Audit(AuditEvents.AgentJoinTokenCreated)]
    public async Task<ActionResult<AgentJoinTokenCreated>> RejoinToken(Guid id, AgentRejoinTokenRequest request, CancellationToken ct) =>
        await servers.RejoinTokenAsync(id, request, ct);
}

/// <summary>Join tokens, the agents' log and the agents' downloads (§9.2). Admin.</summary>
[Route("api/v1/agents")]
[RequireRight(Area.Servers, Level.Manage)]
public sealed class AgentsController(AgentServerService servers) : V1ControllerBase
{
    [HttpGet("join-tokens")]
    public async Task<ActionResult<Paged<AgentJoinTokenItem>>> Tokens([FromQuery] AgentJoinTokenQuery query, CancellationToken ct) =>
        await servers.TokensAsync(query, ct);

    /// <summary>
    /// A join token for a server of a platform on an existing host (§3.3): answered once, only its hash is kept. An agent of the
    /// other platform is refused at the join. Audit-logged.
    /// </summary>
    [HttpPost("join-tokens")]
    [Audit(AuditEvents.AgentJoinTokenCreated)]
    public async Task<ActionResult<AgentJoinTokenCreated>> CreateToken(AgentJoinTokenRequest request, CancellationToken ct) =>
        await servers.CreateTokenAsync(request, ct);

    /// <summary>Revokes a token that was not used yet. Audit-logged.</summary>
    [HttpDelete("join-tokens/{id:guid}")]
    [Audit(AuditEvents.AgentJoinTokenRevoked)]
    public async Task<IActionResult> RevokeToken(Guid id, CancellationToken ct)
    {
        await servers.RevokeTokenAsync(id, ct);
        return NoContent();
    }

    /// <summary>Every agent's log: joins, connections, problems, crashes, failures, revocations.</summary>
    [HttpGet("agent-log")]
    public async Task<ActionResult<Paged<AgentLogItem>>> AgentLog([FromQuery] AgentLogQuery query, CancellationToken ct) =>
        await servers.LogAsync(null, query, ct);

    /// <summary>
    /// The agents of PNMTD's version (§10.1; docs/linux-sensor-plan.md §4.4, §10): each file's PNMT and GitHub links, SHA-256, size
    /// and state in PNMT's download folder, and the Linux install and update lines. Before L1 <c>GET /api/v1/windows/agent</c>.
    /// </summary>
    [HttpGet("downloads")]
    public async Task<ActionResult<AgentDownloadLinks>> Downloads(CancellationToken ct) => await servers.DownloadsAsync(ct);

    /// <summary><i>Fetch again</i>: the files from the release into PNMT's download folder; answers at once. Audited.</summary>
    [HttpPost("downloads/fetch")]
    [Audit(AuditEvents.AgentDownloadsFetched)]
    public async Task<ActionResult<AgentDownloadLinks>> FetchDownloads(AgentDownloadFetchRequest request, CancellationToken ct) =>
        await servers.FetchDownloadsAsync(request, ct);
}

/// <summary>Scripts (docs/windows-sensor-plan.md §7, W6; docs/linux-sensor-plan.md §8): the library, runs and their history. Admin, audited.</summary>
[Route("api/v1/agents")]
[RequireRight(Area.Servers, Level.Manage)]
public sealed class AgentScriptsController(AgentScriptService scripts) : V1ControllerBase
{
    /// <summary>The library, PowerShell and bash (docs/linux-sensor-plan.md §8.1), of one language with <c>language</c>.</summary>
    [HttpGet("scripts")]
    public async Task<ActionResult<Paged<AgentScriptItem>>> Scripts([FromQuery] AgentScriptQuery query, CancellationToken ct) => await scripts.ScriptsAsync(query, ct);

    /// <summary>What a new script of a language starts from: bash from the header template of docs/linux-sensor-plan.md §8.2.</summary>
    [HttpGet("scripts/template")]
    public ActionResult<AgentScriptTemplate> Template([FromQuery] ScriptLanguage language = ScriptLanguage.PowerShell) => AgentScriptService.Template(language);

    [HttpGet("scripts/{id:guid}")]
    public async Task<ActionResult<AgentScriptDetail>> Script(Guid id, CancellationToken ct) => await scripts.ScriptAsync(id, ct);

    /// <summary>
    /// A new script of a language (PowerShell when missing): a connected Windows agent parses PowerShell's parameters (§7.1); a bash
    /// script's header is read at once, its syntax checked by a Linux agent (docs/linux-sensor-plan.md §8.3). Without a text, bash starts
    /// from the template.
    /// </summary>
    [HttpPost("scripts")]
    [Audit(AuditEvents.AgentScriptChanged)]
    public async Task<ActionResult<AgentScriptDetail>> Create(AgentScriptRequest request, CancellationToken ct) => await scripts.CreateAsync(request, ct);

    /// <summary>A new version when the text changed; parsed again.</summary>
    [HttpPut("scripts/{id:guid}")]
    [Audit(AuditEvents.AgentScriptChanged)]
    public async Task<ActionResult<AgentScriptDetail>> Update(Guid id, AgentScriptRequest request, CancellationToken ct) =>
        await scripts.UpdateAsync(id, request, ct);

    [HttpPost("scripts/{id:guid}/parse")]
    [NotAudited("parses a script, saves nothing")]
    public async Task<ActionResult<AgentScriptDetail>> Parse(Guid id, CancellationToken ct) => await scripts.ReparseAsync(id, ct);

    /// <summary>Deletes the script and its versions; its runs stay in the history.</summary>
    [HttpDelete("scripts/{id:guid}")]
    [Audit(AuditEvents.AgentScriptChanged)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await scripts.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Runs the current version on servers (§7.3); each server answers with its own run.</summary>
    [HttpPost("scripts/{id:guid}/run")]
    [Audit(AuditEvents.AgentScriptRun)]
    public async Task<ActionResult<IReadOnlyList<AgentScriptRunItem>>> Run(Guid id, AgentScriptRunRequest request, CancellationToken ct) =>
        Ok(await scripts.RunAsync(id, request, ct));

    [HttpGet("script-runs")]
    public async Task<ActionResult<Paged<AgentScriptRunItem>>> Runs([FromQuery] AgentScriptRunQuery query, CancellationToken ct) => await scripts.RunsAsync(query, ct);

    [HttpGet("script-runs/{id:guid}")]
    public async Task<ActionResult<AgentScriptRunDetail>> RunDetail(Guid id, CancellationToken ct) => await scripts.RunDetailAsync(id, ct);

    [HttpPost("script-runs/{id:guid}/redact")]
    [Audit(AuditEvents.AgentScriptRunRedacted)]
    public async Task<ActionResult<AgentScriptRunDetail>> Redact(Guid id, CancellationToken ct) => await scripts.RedactAsync(id, ct);
}
