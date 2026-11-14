using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Availability;

namespace PNMTD.Api.V1;

/// <summary>
/// The availability plan (docs/cookie-plan.md §8.2, §9.4). The table and one's own column are for everyone in the plan and
/// for <i>Availability: View</i>; everything else needs <i>Edit</i>; the people and the settings need <i>Manage</i>.
/// </summary>
[Route("api/v1/availability")]
[Authorize]
public sealed class AvailabilityController(AvailabilityService availability) : V1ControllerBase
{
    /// <summary>The days from <c>from</c> to <c>to</c> (at most 400), by default a week back and two months ahead.</summary>
    [HttpGet("plan")]
    public async Task<AvailabilityPlanItem> Plan([FromQuery] AvailabilityPlanQuery query, CancellationToken ct) =>
        await availability.PlanAsync(query, User, ct);

    /// <summary>Cells of several people and days: one's own column from today on, everything with <i>Edit</i>.</summary>
    [HttpPut("entries")]
    [Audit(AuditEvents.AvailabilityChanged)]
    public async Task<IActionResult> Entries(AvailabilityCellsRequest request, CancellationToken ct)
    {
        await availability.SaveCellsAsync(request, User, ct);
        return NoContent();
    }

    [HttpPut("days")]
    [RequireRight(Area.Availability, Level.Edit)]
    [Audit(AuditEvents.AvailabilityChanged)]
    public async Task<IActionResult> Days(AvailabilityDaysRequest request, CancellationToken ct)
    {
        await availability.SaveDaysAsync(request, User, ct);
        return NoContent();
    }

    /// <summary><i>Fill</i> or <i>Fill again</i> for the next 365 days: everyone's columns.</summary>
    [HttpPost("fill")]
    [RequireRight(Area.Availability, Level.Edit)]
    [Audit(AuditEvents.AvailabilityFilled)]
    public async Task<AvailabilityFillResult> Fill(AvailabilityFillRequest request, CancellationToken ct) =>
        await availability.FillAsync(request, onlyMine: false, User, ct);

    /// <summary>The same for one's own column, for everyone in the plan (409 for someone who isn't).</summary>
    [HttpPost("fill/mine")]
    [Audit(AuditEvents.AvailabilityFilled)]
    public async Task<AvailabilityFillResult> FillMine(AvailabilityFillRequest request, CancellationToken ct) =>
        await availability.FillAsync(request, onlyMine: true, User, ct);

    [HttpGet("people")]
    [RequireRight(Area.Availability, Level.Manage)]
    public async Task<IReadOnlyList<AvailabilityPeopleItem>> People(CancellationToken ct) => await availability.PeopleAsync(ct);

    [HttpPut("people/{id:guid}")]
    [RequireRight(Area.Availability, Level.Manage)]
    [Audit(AuditEvents.AvailabilityPeopleChanged)]
    public async Task<AvailabilityPeopleItem> Person(Guid id, AvailabilityPersonRequest request, CancellationToken ct) =>
        await availability.SavePersonAsync(id, request, ct);

    [HttpGet("settings")]
    [RequireRight(Area.Availability, Level.Manage)]
    public async Task<AvailabilitySettingsItem> Settings(CancellationToken ct) => await availability.SettingsAsync(ct);

    [HttpPut("settings")]
    [RequireRight(Area.Availability, Level.Manage)]
    [Audit(AuditEvents.AvailabilitySettingsChanged)]
    public async Task<AvailabilitySettingsItem> SaveSettings(AvailabilitySettingsRequest request, CancellationToken ct) =>
        await availability.SaveSettingsAsync(request, User, ct);
}
