using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>
/// What is Linux's own (docs/linux-sensor-plan.md §10): a Linux server's typed sections. The fleet and the rest of a server's agent
/// are under /api/v1/agents/ (AgentControllers.cs); a Windows server's sections under /api/v1/windows/ (WindowsControllers.cs).
/// </summary>
[Route("api/v1/linux/servers")]
[Authorize(Policy = Policies.CanRead)]
public sealed class LinuxServersController(AgentServerService servers) : V1ControllerBase
{
    /// <summary>A section, typed by Linux's records (§5). <c>access</c>, <c>journal</c> and <c>ssh-keys</c> need the User role. 404 for a Windows server.</summary>
    [HttpGet("{id:guid}/sections/{section}")]
    public async Task<ActionResult<LinuxSectionView>> Section(Guid id, string section, CancellationToken ct) =>
        await servers.LinuxSectionAsync(id, section, CanSeeSecrets, ct);
}
