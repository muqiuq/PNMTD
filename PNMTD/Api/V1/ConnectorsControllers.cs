using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Connectors;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1;

/// <summary>
/// 3rd Party Connectors' calls (docs/connectors-plan.md §11, §13.2): every connector's calls, active ones first, the figures on top,
/// one call with its review and its run; redacting and cancelling.
/// </summary>
[Route("api/v1/connector-calls")]
public sealed class ConnectorCallsController(ConnectorService connectors) : V1ControllerBase
{
    [HttpGet]
    [RequireRight(Area.Connectors, Level.View)]
    public async Task<ActionResult<Paged<ConnectorCallItem>>> List([FromQuery] ConnectorCallQuery query, CancellationToken ct) => await connectors.CallsAsync(query, ct);

    [HttpGet("figures")]
    [RequireRight(Area.Connectors, Level.View)]
    public async Task<ActionResult<ConnectorCallFigures>> Figures(CancellationToken ct) => await connectors.FiguresAsync(ct);

    [HttpGet("{id:guid}")]
    [RequireRight(Area.Connectors, Level.View)]
    public async Task<ActionResult<ConnectorCallDetail>> Get(Guid id, CancellationToken ct) => await connectors.CallAsync(id, ct);

    /// <summary>Removes a final call's arguments and note, and its run's arguments.</summary>
    [HttpPost("{id:guid}/redact")]
    [RequireRight(Area.Connectors, Level.Manage)]
    [Audit(AuditEvents.ConnectorCallRedacted)]
    public async Task<ActionResult<ConnectorCallDetail>> Redact(Guid id, CancellationToken ct) => await connectors.RedactAsync(id, ct);

    /// <summary>Cancels a queued call; a running one is 409 <c>call_running</c>.</summary>
    [HttpPost("{id:guid}/cancel")]
    [RequireRight(Area.Connectors, Level.Manage)]
    [Audit(AuditEvents.ConnectorCallCancelled)]
    public async Task<ActionResult<ConnectorCallDetail>> Cancel(Guid id, CancellationToken ct) => await connectors.CancelAsync(id, ct);
}

/// <summary>
/// The connectors (§11): the list, a connector's detail with its target, arguments, warnings and description, its comparison with the
/// library; creating, changing and deleting; checking a call and parsing a text before a save; testing a pattern; the selectors.
/// </summary>
[Route("api/v1/connectors")]
public sealed class ConnectorsController(ConnectorService connectors) : V1ControllerBase
{
    [HttpGet]
    [RequireRight(Area.Connectors, Level.View)]
    public async Task<ActionResult<Paged<ConnectorItem>>> List([FromQuery] ConnectorQuery query, CancellationToken ct) => await connectors.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    [RequireRight(Area.Connectors, Level.View)]
    public async Task<ActionResult<ConnectorDetail>> Get(Guid id, CancellationToken ct) => await connectors.DetailAsync(id, ct);

    /// <summary>docs/connectors-api.md, the callers' reference, for the Documentation tab (§13.1).</summary>
    [HttpGet("docs")]
    [RequireRight(Area.Connectors, Level.View)]
    public ActionResult<ConnectorDocs> Docs() => ConnectorDocs.Load();

    [HttpGet("{id:guid}/compare")]
    [RequireRight(Area.Connectors, Level.View)]
    public async Task<ActionResult<ConnectorCompare>> Compare(Guid id, CancellationToken ct) => await connectors.CompareAsync(id, ct);

    /// <summary>Created disabled, with its arguments' rows from the parse.</summary>
    [HttpPost]
    [RequireRight(Area.Connectors, Level.Manage)]
    [Audit(AuditEvents.ConnectorChanged)]
    public async Task<ActionResult<ConnectorDetail>> Create(ConnectorRequest request, CancellationToken ct) => await connectors.CreateAsync(request, ct);

    [HttpPut("{id:guid}")]
    [RequireRight(Area.Connectors, Level.Manage)]
    [Audit(AuditEvents.ConnectorChanged)]
    public async Task<ActionResult<ConnectorDetail>> Update(Guid id, ConnectorRequest request, CancellationToken ct) => await connectors.UpdateAsync(id, request, ct);

    /// <summary>Its calls stay in the history, under its name; its queued calls are cancelled.</summary>
    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Connectors, Level.Manage)]
    [Audit(AuditEvents.ConnectorChanged)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await connectors.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/check")]
    [RequireRight(Area.Connectors, Level.Manage)]
    [NotAudited("a check, nothing runs")]
    public async Task<ActionResult<ConnectorCheckResult>> Check(Guid id, ConnectorCheckRequest request, CancellationToken ct) =>
        await connectors.CheckAsync(id, request, ct);

    [HttpPost("parse")]
    [RequireRight(Area.Connectors, Level.Manage)]
    [NotAudited("a parse, nothing runs")]
    public async Task<ActionResult<ConnectorParseResult>> Parse(ConnectorParseRequest request, CancellationToken ct) => await connectors.ParseAsync(request, ct);

    [HttpPost("patterns/test")]
    [RequireRight(Area.Connectors, Level.View)]
    [NotAudited("a pattern tested, nothing stored")]
    public ActionResult<ConnectorPatternResult> TestPattern(ConnectorPatternRequest request) => connectors.TestPattern(request);

    [HttpGet("lookups/servers")]
    [RequireRight(Area.Connectors, Level.Manage)]
    public async Task<ActionResult<IReadOnlyList<ConnectorServerOption>>> Servers([FromQuery] ConnectorType type, CancellationToken ct) =>
        Ok(await connectors.ServerOptionsAsync(type, ct));

    [HttpGet("lookups/library")]
    [RequireRight(Area.Connectors, Level.Manage)]
    public async Task<ActionResult<IReadOnlyList<ConnectorLibraryScript>>> Library([FromQuery] ConnectorType type, CancellationToken ct) =>
        Ok(await connectors.LibraryAsync(type, ct));

    [HttpGet("lookups/library/{scriptId:guid}/versions/{number:int}")]
    [RequireRight(Area.Connectors, Level.Manage)]
    public async Task<ActionResult<ConnectorLibraryVersion>> LibraryVersion(Guid scriptId, int number, CancellationToken ct) =>
        await connectors.LibraryVersionAsync(scriptId, number, ct);

    [HttpGet("lookups/registered")]
    [RequireRight(Area.Connectors, Level.Manage)]
    public async Task<ActionResult<IReadOnlyList<ConnectorRegisteredScript>>> Registered([FromQuery] Guid server, CancellationToken ct) =>
        Ok(await connectors.RegisteredAsync(server, ct));

    [HttpGet("lookups/accounts")]
    [RequireRight(Area.Connectors, Level.Manage)]
    public async Task<ActionResult<IReadOnlyList<ConnectorAccountOption>>> Accounts([FromQuery] ConnectorType type, [FromQuery] Guid? server, CancellationToken ct) =>
        Ok(await connectors.AccountsAsync(type, server, ct));
}

/// <summary>The callers (§6, §11): their keys (shown again, each display audited; rotated), networks and grants.</summary>
[Route("api/v1/connector-callers")]
public sealed class ConnectorCallersController(ConnectorService connectors) : V1ControllerBase
{
    [HttpGet]
    [RequireRight(Area.Connectors, Level.View)]
    public async Task<ActionResult<Paged<ConnectorCallerItem>>> List([FromQuery] PageQuery query, CancellationToken ct) => await connectors.CallersAsync(query, ct);

    [HttpGet("{id:guid}")]
    [RequireRight(Area.Connectors, Level.View)]
    public async Task<ActionResult<ConnectorCallerDetail>> Get(Guid id, CancellationToken ct) => await connectors.CallerAsync(id, ct);

    /// <summary>Created disabled unless enabled with its networks, and with its key.</summary>
    [HttpPost]
    [RequireRight(Area.Connectors, Level.Manage)]
    [Audit(AuditEvents.ConnectorCallerChanged)]
    public async Task<ActionResult<ConnectorCallerCreated>> Create(ConnectorCallerRequest request, CancellationToken ct) => await connectors.CreateCallerAsync(request, ct);

    [HttpPut("{id:guid}")]
    [RequireRight(Area.Connectors, Level.Manage)]
    [Audit(AuditEvents.ConnectorCallerChanged)]
    public async Task<ActionResult<ConnectorCallerDetail>> Update(Guid id, ConnectorCallerRequest request, CancellationToken ct) =>
        await connectors.UpdateCallerAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Connectors, Level.Manage)]
    [Audit(AuditEvents.ConnectorCallerChanged)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await connectors.DeleteCallerAsync(id, ct);
        return NoContent();
    }

    /// <summary>The key, shown again: each display is audited. 409 <c>key_lost</c> without the key ring.</summary>
    [HttpGet("{id:guid}/key")]
    [RequireRight(Area.Connectors, Level.Manage)]
    public async Task<ActionResult<ConnectorCallerKey>> Key(Guid id, CancellationToken ct) => await connectors.KeyAsync(id, ct);

    [HttpPost("{id:guid}/key/rotate")]
    [RequireRight(Area.Connectors, Level.Manage)]
    [Audit(AuditEvents.ConnectorCallerKeyRotated)]
    public async Task<ActionResult<ConnectorCallerKey>> Rotate(Guid id, CancellationToken ct) => await connectors.RotateKeyAsync(id, ct);
}
