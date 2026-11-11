using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

[Route("api/v1/events")]
[Authorize(Policy = Policies.CanRead)]
public sealed class EventsController(EventService events) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<EventItem>>> List([FromQuery] EventQuery query, CancellationToken ct) =>
        await events.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EventItem>> Get(Guid id, CancellationToken ct) => await events.GetAsync(id, ct);

    [HttpGet("state-changes")]
    public async Task<ActionResult<IReadOnlyList<StateChange>>> StateChanges([FromQuery] StateChangeQuery query, CancellationToken ct) =>
        Ok(await events.StateChangesAsync(query, ct));
}
