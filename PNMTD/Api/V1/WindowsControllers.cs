using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Lib.Agent;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>
/// What stays Windows' own after L1 (docs/linux-sensor-plan.md §3.3, §10): a Windows server's typed sections, its WMI and the
/// scripts registered on it. The fleet and the rest of a server's agent are under /api/v1/agents/ (AgentControllers.cs).
/// </summary>
[Route("api/v1/windows/servers")]
[Authorize]
public sealed class WindowsServersController(AgentServerService servers, AgentScriptService scripts) : V1ControllerBase
{
    /// <summary>A section, typed (§4). <c>access</c> and <c>event-log</c> need the User role.</summary>
    [HttpGet("{id:guid}/sections/{section}")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<WindowsSectionView>> Section(Guid id, string section, CancellationToken ct) =>
        await servers.SectionAsync(id, section, CanSeeSecrets, ct);

    /// <summary>WMI (§16): PNMT's settings for the server, and its queries, their state and the own queries' rows. Admin.</summary>
    [HttpGet("{id:guid}/wmi")]
    [RequireRight(Area.Servers, Level.Manage)]
    public async Task<ActionResult<WindowsWmiView>> Wmi(Guid id, CancellationToken ct) => await servers.WmiAsync(id, ct);

    /// <summary>Saves the WMI settings and sends the agent its new configuration. Admin, audited.</summary>
    [HttpPut("{id:guid}/wmi")]
    [RequireRight(Area.Servers, Level.Manage)]
    public async Task<ActionResult<WindowsWmiView>> SaveWmi(Guid id, WmiConfig request, CancellationToken ct) => await servers.SaveWmiAsync(id, request, ct);

    /// <summary>The scripts registered on a server in the agent's window (§7.2), as it last reported them. Admin.</summary>
    [HttpGet("{id:guid}/local-scripts")]
    [RequireRight(Area.Servers, Level.Manage)]
    public async Task<ActionResult<WindowsLocalScripts>> LocalScripts(Guid id, CancellationToken ct) => await scripts.LocalScriptsAsync(id, ct);

    /// <summary>Runs a script registered on the server, as the account it is registered with. Admin, audited.</summary>
    [HttpPost("{id:guid}/local-scripts/run")]
    [RequireRight(Area.Servers, Level.Manage)]
    public async Task<ActionResult<AgentScriptRunItem>> RunLocal(Guid id, WindowsLocalScriptRunRequest request, CancellationToken ct) =>
        await scripts.RunLocalAsync(id, request, ct);
}

/// <summary>Windows' run-as accounts (docs/windows-sensor-plan.md §7.4): names only, never a password. Admin, audited.</summary>
[Route("api/v1/windows/run-as")]
[RequireRight(Area.Servers, Level.Manage)]
public sealed class WindowsRunAsController(AgentScriptService scripts) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WindowsRunAsItem>>> RunAs(CancellationToken ct) => Ok(await scripts.RunAsListAsync(ct));

    [HttpPost]
    public async Task<ActionResult<WindowsRunAsItem>> CreateRunAs(WindowsRunAsRequest request, CancellationToken ct) => await scripts.SaveRunAsAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WindowsRunAsItem>> UpdateRunAs(Guid id, WindowsRunAsRequest request, CancellationToken ct) =>
        await scripts.SaveRunAsAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteRunAs(Guid id, CancellationToken ct)
    {
        await scripts.DeleteRunAsAsync(id, ct);
        return NoContent();
    }
}
