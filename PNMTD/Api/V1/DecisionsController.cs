using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>Monitoring → Decisions (docs/ai-operations-plan.md §5.3): the User role, as the mail log.</summary>
[Route("api/v1/decisions")]
[RequireRight(Area.Decisions, Level.Decide)]
public sealed class DecisionsController(DecisionService decisions) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<DecisionItem>>> List([FromQuery] DecisionQuery query, CancellationToken ct) => await decisions.ListAsync(query, ct);

    [HttpGet("count")]
    public async Task<ActionResult<DecisionCount>> Count(CancellationToken ct) => await decisions.CountAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DecisionDetail>> Get(Guid id, CancellationToken ct) => await decisions.GetAsync(id, ct);

    [HttpPost("{id:guid}/decide")]
    [Audit(AuditEvents.DecisionDecided)]
    public async Task<ActionResult<DecisionDetail>> Decide(Guid id, DecideRequest request, CancellationToken ct) =>
        await decisions.DecideAsync(id, request, ct);
}
