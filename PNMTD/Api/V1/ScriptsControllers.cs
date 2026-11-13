using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>PowerShell scripts (docs/windows-sensor-plan.md §7, W6): the library, run-as accounts, runs and their history. Admin, audited.</summary>
[Route("api/v1/windows")]
[Authorize(Policy = Policies.Admin)]
public sealed class WindowsScriptsController(WindowsScriptService scripts) : V1ControllerBase
{
    [HttpGet("scripts")]
    public async Task<ActionResult<Paged<WindowsScriptItem>>> Scripts([FromQuery] PageQuery query, CancellationToken ct) => await scripts.ScriptsAsync(query, ct);

    [HttpGet("scripts/{id:guid}")]
    public async Task<ActionResult<WindowsScriptDetail>> Script(Guid id, CancellationToken ct) => await scripts.ScriptAsync(id, ct);

    /// <summary>A new script; a connected agent parses its parameters (§7.1).</summary>
    [HttpPost("scripts")]
    public async Task<ActionResult<WindowsScriptDetail>> Create(WindowsScriptRequest request, CancellationToken ct) => await scripts.CreateAsync(request, ct);

    /// <summary>A new version when the text changed; parsed again.</summary>
    [HttpPut("scripts/{id:guid}")]
    public async Task<ActionResult<WindowsScriptDetail>> Update(Guid id, WindowsScriptRequest request, CancellationToken ct) =>
        await scripts.UpdateAsync(id, request, ct);

    [HttpPost("scripts/{id:guid}/parse")]
    public async Task<ActionResult<WindowsScriptDetail>> Parse(Guid id, CancellationToken ct) => await scripts.ReparseAsync(id, ct);

    /// <summary>Deletes the script and its versions; its runs stay in the history.</summary>
    [HttpDelete("scripts/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await scripts.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Runs the current version on servers (§7.3); each server answers with its own run.</summary>
    [HttpPost("scripts/{id:guid}/run")]
    public async Task<ActionResult<IReadOnlyList<WindowsScriptRunItem>>> Run(Guid id, WindowsScriptRunRequest request, CancellationToken ct) =>
        Ok(await scripts.RunAsync(id, request, ct));

    [HttpGet("run-as")]
    public async Task<ActionResult<IReadOnlyList<WindowsRunAsItem>>> RunAs(CancellationToken ct) => Ok(await scripts.RunAsListAsync(ct));

    [HttpPost("run-as")]
    public async Task<ActionResult<WindowsRunAsItem>> CreateRunAs(WindowsRunAsRequest request, CancellationToken ct) => await scripts.SaveRunAsAsync(null, request, ct);

    [HttpPut("run-as/{id:guid}")]
    public async Task<ActionResult<WindowsRunAsItem>> UpdateRunAs(Guid id, WindowsRunAsRequest request, CancellationToken ct) =>
        await scripts.SaveRunAsAsync(id, request, ct);

    [HttpDelete("run-as/{id:guid}")]
    public async Task<IActionResult> DeleteRunAs(Guid id, CancellationToken ct)
    {
        await scripts.DeleteRunAsAsync(id, ct);
        return NoContent();
    }

    [HttpGet("script-runs")]
    public async Task<ActionResult<Paged<WindowsScriptRunItem>>> Runs([FromQuery] WindowsScriptRunQuery query, CancellationToken ct) => await scripts.RunsAsync(query, ct);

    [HttpGet("script-runs/{id:guid}")]
    public async Task<ActionResult<WindowsScriptRunDetail>> RunDetail(Guid id, CancellationToken ct) => await scripts.RunDetailAsync(id, ct);

    [HttpPost("script-runs/{id:guid}/redact")]
    public async Task<ActionResult<WindowsScriptRunDetail>> Redact(Guid id, CancellationToken ct) => await scripts.RedactAsync(id, ct);
}
