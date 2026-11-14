using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>Diagnostics (docs/windows-sensor-plan.md §5, §9.2): sets, assignments and runs. Changing them is Admin; Run now CanWrite.</summary>
[Route("api/v1/diagnostics")]
[Authorize]
public sealed class DiagnosticsController(DiagnosticsSetService diagnostics) : V1ControllerBase
{
    [HttpGet("sets")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<DiagnosticSetItem>>> Sets([FromQuery] PageQuery query, CancellationToken ct) => await diagnostics.SetsAsync(query, ct);

    [HttpGet("sets/{id:guid}")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<DiagnosticSetDetail>> Set(Guid id, CancellationToken ct) => await diagnostics.SetAsync(id, ct);

    /// <summary>A set with its checks (§5.1): valid names and addresses, at most 10 pings of at most 5 s. Audit-logged.</summary>
    [HttpPost("sets")]
    [RequireRight(Area.Diagnostics, Level.Manage)]
    public async Task<ActionResult<DiagnosticSetDetail>> CreateSet(DiagnosticSetRequest request, CancellationToken ct) => await diagnostics.CreateSetAsync(request, ct);

    [HttpPut("sets/{id:guid}")]
    [RequireRight(Area.Diagnostics, Level.Manage)]
    public async Task<ActionResult<DiagnosticSetDetail>> UpdateSet(Guid id, DiagnosticSetRequest request, CancellationToken ct) =>
        await diagnostics.UpdateSetAsync(id, request, ct);

    /// <summary>Refused while the set is assigned.</summary>
    [HttpDelete("sets/{id:guid}")]
    [RequireRight(Area.Diagnostics, Level.Manage)]
    public async Task<IActionResult> DeleteSet(Guid id, CancellationToken ct)
    {
        await diagnostics.DeleteSetAsync(id, ct);
        return NoContent();
    }

    [HttpGet("assignments")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<DiagnosticAssignmentItem>>> Assignments([FromQuery] DiagnosticAssignmentQuery query, CancellationToken ct) =>
        await diagnostics.AssignmentsAsync(query, ct);

    /// <summary>Assigns a set to servers: each assignment creates its DIAGNOSTICS sensor (§5.2). Audit-logged.</summary>
    [HttpPost("assignments")]
    [RequireRight(Area.Diagnostics, Level.Manage)]
    public async Task<ActionResult<IReadOnlyList<DiagnosticAssignmentItem>>> Assign(DiagnosticAssignmentRequest request, CancellationToken ct) =>
        Ok(await diagnostics.AssignAsync(request, ct));

    [HttpPut("assignments/{id:guid}")]
    [RequireRight(Area.Diagnostics, Level.Manage)]
    public async Task<ActionResult<DiagnosticAssignmentItem>> UpdateAssignment(Guid id, DiagnosticAssignmentUpdate request, CancellationToken ct) =>
        await diagnostics.UpdateAssignmentAsync(id, request, ct);

    /// <summary>Deletes the assignment, its sensor and that sensor's history.</summary>
    [HttpDelete("assignments/{id:guid}")]
    [RequireRight(Area.Diagnostics, Level.Manage)]
    public async Task<IActionResult> DeleteAssignment(Guid id, CancellationToken ct)
    {
        await diagnostics.DeleteAssignmentAsync(id, ct);
        return NoContent();
    }

    /// <summary>Run now (§5.3): the result arrives within seconds; 409 when the agent is offline.</summary>
    [HttpPost("assignments/{id:guid}/run")]
    [RequireRight(Area.Commands, Level.Run)]
    public async Task<ActionResult<DiagnosticRunItem>> Run(Guid id, CancellationToken ct) => await diagnostics.RunNowAsync(id, ct);

    [HttpGet("runs")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<DiagnosticRunItem>>> Runs([FromQuery] DiagnosticRunQuery query, CancellationToken ct) => await diagnostics.RunsAsync(query, ct);

    [HttpGet("runs/{id:guid}")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<DiagnosticRunDetail>> RunDetail(Guid id, CancellationToken ct) => await diagnostics.RunAsync(id, ct);
}
