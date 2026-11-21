using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>
/// What is Linux's own (docs/linux-sensor-plan.md §10): a Linux server's typed sections. The fleet and the rest of a server's agent
/// are under /api/v1/agents/ (AgentControllers.cs); a Windows server's sections under /api/v1/windows/ (WindowsControllers.cs).
/// </summary>
[Route("api/v1/linux/servers")]
[RequireRight(Area.Monitoring, Level.View)]
public sealed class LinuxServersController(AgentServerService servers) : V1ControllerBase
{
    /// <summary>A section, typed by Linux's records (§5). <c>access</c>, <c>journal</c> and <c>ssh-keys</c> need Monitoring's Details. 404 for a Windows server.</summary>
    [HttpGet("{id:guid}/sections/{section}")]
    public async Task<ActionResult<LinuxSectionView>> Section(Guid id, string section, CancellationToken ct) =>
        await servers.LinuxSectionAsync(id, section, CanSeeDetails, ct);
}

/// <summary>
/// A Linux server's OS updates (docs/linux-os-updates-plan.md §9): its state, runs and logs for Monitoring's View (names of people with its
/// Details); refreshing the lists, installing updates, marking a failure handled and acknowledging findings for Servers: Run; its switches
/// for Servers: Manage.
/// </summary>
[Route("api/v1/linux/servers/{id:guid}/os-updates")]
[Authorize]
public sealed class LinuxOsUpdatesController(OsUpdatesService updates) : V1ControllerBase
{
    /// <summary>The state, the pending packages with their CVEs, the findings, both modes' plans, the run in progress and the last runs.</summary>
    [HttpGet]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<OsUpdatesView>> Get(Guid id, CancellationToken ct) => await updates.ViewAsync(id, CanSeeDetails, ct);

    /// <summary>The log of runs, newest first: updates, upgrades, and other runs from history.log.</summary>
    [HttpGet("runs")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<OsUpdateRunItem>>> Runs(Guid id, [FromQuery] OsUpdateRunQuery query, CancellationToken ct) =>
        await updates.RunsAsync(id, query, CanSeeDetails, ct);

    [HttpGet("runs/{runId:guid}")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<OsUpdateRunDetail>> Run(Guid id, Guid runId, CancellationToken ct) => await updates.RunAsync(id, runId, CanSeeDetails, ct);

    /// <summary>A run's log from an offset (bytes), for the live view: the text, and the offset to ask from next.</summary>
    [HttpGet("runs/{runId:guid}/log")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<OsUpdateLogPart>> Log(Guid id, Guid runId, [FromQuery] long from, CancellationToken ct) =>
        await updates.LogAsync(id, runId, from, ct);

    /// <summary>The whole log as a file.</summary>
    [HttpGet("runs/{runId:guid}/log/download")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<IActionResult> Download(Guid id, Guid runId, CancellationToken ct)
    {
        var (name, text) = await updates.LogFileAsync(id, runId, ct);
        return File(System.Text.Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", name);
    }

    /// <summary>A pending package's changelog part (§6.2).</summary>
    [HttpGet("changelogs/{package}")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<OsPackageChangelogView>> Changelog(Guid id, string package, CancellationToken ct) => await updates.ChangelogAsync(id, package, ct);

    /// <summary><i>Refresh lists</i>: apt-get update now.</summary>
    [HttpPost("update")]
    [RequireRight(Area.Servers, Level.Run)]
    [Audit(AuditEvents.OsUpdateRequested)]
    public async Task<ActionResult<OsUpdateStarted>> Update(Guid id, CancellationToken ct) => await updates.UpdateAsync(id, ct);

    /// <summary><i>Install updates…</i>: apt-get upgrade or full-upgrade; a full upgrade with the removals' hash the person saw.</summary>
    [HttpPost("upgrade")]
    [RequireRight(Area.Servers, Level.Run)]
    [Audit(AuditEvents.OsUpdateRequested)]
    public async Task<ActionResult<OsUpdateStarted>> Upgrade(Guid id, OsUpdateUpgradeRequest request, CancellationToken ct) => await updates.UpgradeAsync(id, request, ct);

    /// <summary>A failed upgrade marked handled: its 467 ends.</summary>
    [HttpPost("runs/{runId:guid}/dismiss")]
    [RequireRight(Area.Servers, Level.Run)]
    [Audit(AuditEvents.OsUpdateFailureDismissed)]
    public async Task<ActionResult<OsUpdateRunItem>> Dismiss(Guid id, Guid runId, CancellationToken ct) => await updates.DismissAsync(id, runId, CanSeeDetails, ct);

    /// <summary>The server's switches: OS updates, high-risk alerts (466), update failures (467).</summary>
    [HttpPut("settings")]
    [RequireRight(Area.Servers, Level.Manage)]
    [Audit(AuditEvents.OsUpdateSettingsChanged)]
    public async Task<ActionResult<OsUpdatesView>> Settings(Guid id, OsUpdateServerSettings request, CancellationToken ct) =>
        await updates.SettingsAsync(id, request, CanSeeDetails, ct);

    /// <summary>Acknowledges high-risk findings of this server, with a reason, for 1 to AcknowledgeMaxDays days (§6.5).</summary>
    [HttpPost("findings/acknowledge")]
    [RequireRight(Area.Servers, Level.Run)]
    [Audit(AuditEvents.OsUpdateFindingAcknowledged)]
    public async Task<ActionResult<OsUpdatesView>> Acknowledge(Guid id, OsFindingAcknowledgeRequest request, CancellationToken ct) =>
        await updates.AcknowledgeAsync(id, request, CanSeeDetails, ct);

    /// <summary>Removes a finding's acknowledgement: 466 returns while it is pending.</summary>
    [HttpDelete("findings/{findingId:guid}/acknowledgement")]
    [RequireRight(Area.Servers, Level.Run)]
    [Audit(AuditEvents.OsUpdateFindingAcknowledgementEnded)]
    public async Task<ActionResult<OsUpdatesView>> Unacknowledge(Guid id, Guid findingId, CancellationToken ct) =>
        await updates.UnacknowledgeAsync(id, findingId, CanSeeDetails, ct);
}

/// <summary>
/// The fleet's OS updates (docs/linux-os-updates-plan.md §9): the dashboard's card for Monitoring's View; the high-risk check's sources and
/// advisory texts for Servers: Manage (Admin → Servers → Advisories).
/// </summary>
[Route("api/v1/linux/os-updates")]
[Authorize]
public sealed class OsUpdatesController(OsUpdatesService updates) : V1ControllerBase
{
    /// <summary>The dashboard's card (§10.3): servers with updates, packages, security, high risk, failures and stale lists, by server and by package.</summary>
    [HttpGet("summary")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<OsUpdatesSummary>> Summary(CancellationToken ct) => await updates.SummaryAsync(ct);

    [HttpGet("sources")]
    [RequireRight(Area.Servers, Level.Manage)]
    public async Task<ActionResult<OsAdvisorySourcesView>> Sources(CancellationToken ct) => await updates.SourcesAsync(ct);

    [HttpPost("sources")]
    [RequireRight(Area.Servers, Level.Manage)]
    [Audit(AuditEvents.OsAdvisorySourceCreated)]
    public async Task<ActionResult<OsAdvisorySourceView>> CreateSource(OsAdvisorySourceRequest request, CancellationToken ct) => await updates.CreateSourceAsync(request, ct);

    [HttpPut("sources/{sourceId:guid}")]
    [RequireRight(Area.Servers, Level.Manage)]
    [Audit(AuditEvents.OsAdvisorySourceChanged)]
    public async Task<ActionResult<OsAdvisorySourceView>> UpdateSource(Guid sourceId, OsAdvisorySourceRequest request, CancellationToken ct) =>
        await updates.UpdateSourceAsync(sourceId, request, ct);

    [HttpDelete("sources/{sourceId:guid}")]
    [RequireRight(Area.Servers, Level.Manage)]
    [Audit(AuditEvents.OsAdvisorySourceDeleted)]
    public async Task<IActionResult> DeleteSource(Guid sourceId, CancellationToken ct)
    {
        await updates.DeleteSourceAsync(sourceId, ct);
        return NoContent();
    }

    /// <summary>Fetches a source on the next pass (within a minute).</summary>
    [HttpPost("sources/{sourceId:guid}/fetch")]
    [RequireRight(Area.Servers, Level.Manage)]
    [NotAudited("asks for a fetch the schedule makes anyway; it changes nothing a person did")]
    public async Task<IActionResult> FetchSource(Guid sourceId, CancellationToken ct)
    {
        await updates.FetchSourceAsync(sourceId, ct);
        return Accepted();
    }

    /// <summary>The advisory texts and their assessments (§7).</summary>
    [HttpGet("advisories")]
    [RequireRight(Area.Servers, Level.Manage)]
    public async Task<ActionResult<Paged<OsAdvisoryItemView>>> Advisories([FromQuery] PageQuery query, CancellationToken ct) => await updates.ItemsAsync(query, ct);
}
