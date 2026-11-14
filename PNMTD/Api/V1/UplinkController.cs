using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

/// <summary>The uplink page (docs/ai-operations-plan.md §7.3): every role reads it, an Admin checks now.</summary>
[Route("api/v1/uplink")]
[Authorize]
public sealed class UplinkController(UplinkService uplink) : V1ControllerBase
{
    [HttpGet]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<UplinkOverview>> Get(CancellationToken ct) => await uplink.OverviewAsync(User.Has(Area.System, Level.Manage), ct);

    [HttpGet("checks")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<UplinkCheckItem>>> Checks([FromQuery] UplinkCheckQuery query, CancellationToken ct) =>
        await uplink.ChecksAsync(query, ct);

    [HttpGet("outages")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<IReadOnlyList<UplinkOutage>>> Outages([FromQuery] int days = 7, CancellationToken ct = default) =>
        Ok(await uplink.OutagesAsync(days, ct));

    [HttpPost("check")]
    [RequireRight(Area.System, Level.Manage)]
    public async Task<ActionResult<UplinkCheckItem>> Check(CancellationToken ct) => await uplink.CheckNowAsync(ct);
}
