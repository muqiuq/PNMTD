using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Responsibilities;

namespace PNMTD.Api.V1;

/// <summary>
/// The responsibilities' overview (docs/responsibilities-extensions-plan.md §7.3): the map, and the places saved for everyone by hand.
/// Reading needs <i>Responsibilities: View</i>, saving <i>Edit</i>.
/// </summary>
[Route("api/v1/responsibilities/map")]
[RequireRight(Area.Responsibilities, Level.View)]
public sealed class ResponsibilityMapController(ResponsibilityMapService map) : V1ControllerBase
{
    /// <summary>The responsibilities, their people, the objects of the layers asked for, the global people; saved places on the nodes.</summary>
    [HttpGet]
    public async Task<ResponsibilityMap> Map([FromQuery] ResponsibilityMapQuery query, CancellationToken ct) => await map.MapAsync(query, ct);

    /// <summary>Saves places, adding or updating; responsibilities deleted meanwhile are skipped.</summary>
    [HttpPut("positions")]
    [RequireRight(Area.Responsibilities, Level.Edit)]
    [Audit(AuditEvents.ResponsibilityMapSaved)]
    public async Task<ResponsibilityMapSaveResult> SavePositions(ResponsibilityMapPositionsRequest request, CancellationToken ct) =>
        await map.SaveAsync(request, ct);

    /// <summary>Every responsibility back to automatic; the notes keep their places.</summary>
    [HttpDelete("positions")]
    [RequireRight(Area.Responsibilities, Level.Edit)]
    [Audit(AuditEvents.ResponsibilityMapReset)]
    public async Task<IActionResult> ResetPositions(CancellationToken ct)
    {
        await map.ResetAsync(ct);
        return NoContent();
    }

    /// <summary>A note at the given place: 409 <c>too_many_notes</c>, 422 <c>looks_like_credential</c> unless acknowledged.</summary>
    [HttpPost("notes")]
    [RequireRight(Area.Responsibilities, Level.Edit)]
    [Audit(AuditEvents.ResponsibilityNoteCreated)]
    public async Task<ActionResult<ResponsibilityMapNote>> CreateNote(ResponsibilityNoteRequest request, CancellationToken ct) =>
        await map.CreateNoteAsync(request, ct);

    /// <summary>Its text, colour and width; its place is saved with the layout.</summary>
    [HttpPut("notes/{id:guid}")]
    [RequireRight(Area.Responsibilities, Level.Edit)]
    [Audit(AuditEvents.ResponsibilityNoteChanged)]
    public async Task<ResponsibilityMapNote> UpdateNote(Guid id, ResponsibilityNoteRequest request, CancellationToken ct) =>
        await map.UpdateNoteAsync(id, request, ct);

    [HttpDelete("notes/{id:guid}")]
    [RequireRight(Area.Responsibilities, Level.Edit)]
    [Audit(AuditEvents.ResponsibilityNoteDeleted)]
    public async Task<IActionResult> DeleteNote(Guid id, CancellationToken ct)
    {
        await map.DeleteNoteAsync(id, ct);
        return NoContent();
    }

    /// <summary>One responsibility back to automatic.</summary>
    [HttpDelete("positions/{id:guid}")]
    [RequireRight(Area.Responsibilities, Level.Edit)]
    [Audit(AuditEvents.ResponsibilityMapReset)]
    public async Task<IActionResult> ClearPosition(Guid id, CancellationToken ct)
    {
        await map.ClearAsync(id, ct);
        return NoContent();
    }
}
