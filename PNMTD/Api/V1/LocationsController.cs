using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Locations;

namespace PNMTD.Api.V1;

/// <summary>
/// Locations (docs/locations-knowledge-plan.md §3, §9): every role reads; Users create, change, move and fill them and
/// assign hosts; only Admins delete (§8.1). Hosts' locations many at once are <c>PUT /hosts/location</c>.
/// </summary>
[Route("api/v1/locations")]
[RequireRight(Area.Locations, Level.View)]
public sealed class LocationsController(LocationService locations, LocationValues values) : V1ControllerBase
{
    /// <summary>The whole tree, each with its parent, path, state and counts (§3.6); with <c>search</c>, the matches flat.</summary>
    [HttpGet]
    public async Task<ActionResult<LocationList>> List([FromQuery] LocationQuery query, CancellationToken ct) => await locations.ListAsync(query, ct);

    /// <summary>The page (§3.4): path, state, sub-locations, hosts (with sub-locations' unless <c>own</c>), values with where they are inherited from.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LocationDetail>> Get(Guid id, [FromQuery] bool own, CancellationToken ct) => await locations.GetAsync(id, own, ct);

    [HttpPost]
    [RequireRight(Area.Locations, Level.Edit)]
    [Audit(AuditEvents.LocationCreated)]
    public async Task<ActionResult<LocationDetail>> Create(LocationRequest request, CancellationToken ct)
    {
        var location = await locations.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = location.Id }, location);
    }

    /// <summary>Name, description and parent; a new parent is a move (409 <c>cycle</c>, <c>too_deep</c>).</summary>
    [HttpPut("{id:guid}")]
    [RequireRight(Area.Locations, Level.Edit)]
    [Audit(AuditEvents.LocationUpdated)]
    public async Task<ActionResult<LocationDetail>> Update(Guid id, LocationRequest request, CancellationToken ct) =>
        await locations.UpdateAsync(id, request, ct);

    /// <summary>Under another parent, or to the top with null: 409 <c>cycle</c> or <c>too_deep</c> (§3.3).</summary>
    [HttpPost("{id:guid}/move")]
    [RequireRight(Area.Locations, Level.Edit)]
    [Audit(AuditEvents.LocationMoved)]
    public async Task<ActionResult<LocationDetail>> Move(Guid id, LocationMoveRequest request, CancellationToken ct) =>
        await locations.MoveAsync(id, request, ct);

    /// <summary>Admins only; 409 <c>has_sub_locations</c> while it has any. Its hosts keep their texts. Audited.</summary>
    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Locations, Level.Manage)]
    [Audit(AuditEvents.LocationDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await locations.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>A field's value (§4.1): null clears it, back to the inherited value; 409 <c>edit_conflict</c> after someone else's save.</summary>
    [HttpPut("{id:guid}/values/{fieldId:guid}")]
    [RequireRight(Area.Locations, Level.Edit)]
    [Audit(AuditEvents.LocationValueChanged)]
    public async Task<ActionResult<LocationValueItem>> SetValue(Guid id, Guid fieldId, LocationValueRequest request, CancellationToken ct) =>
        await values.SetAsync(id, fieldId, request, ct);

    /// <summary>Every change, newest first (§3.5).</summary>
    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<Paged<LocationHistoryItem>>> History(Guid id, [FromQuery] PageQuery page, CancellationToken ct) =>
        await locations.HistoryAsync(id, page, ct);

    /// <summary><i>From the hosts' texts…</i> (§3.2): the texts of hosts without a location, with their counts.</summary>
    [HttpGet("suggestions")]
    [RequireRight(Area.Locations, Level.Edit)]
    public async Task<ActionResult<IReadOnlyList<LocationSuggestion>>> Suggestions(CancellationToken ct) => Ok(await locations.SuggestionsAsync(ct));

    /// <summary>Creates or picks a location per text and assigns those hosts; optionally clears their texts.</summary>
    [HttpPost("from-text")]
    [RequireRight(Area.Locations, Level.Edit)]
    [Audit(AuditEvents.LocationsCreatedFromText)]
    public async Task<ActionResult<LocationFromTextResult>> FromText(LocationFromTextRequest request, CancellationToken ct) =>
        await locations.FromTextAsync(request, ct);
}

/// <summary>
/// The location fields (docs/locations-knowledge-plan.md §4, §9, §10.5): every role reads them, to render; Admins define,
/// order, archive, restore and delete them (§8.1).
/// </summary>
[Route("api/v1/location-fields")]
[RequireRight(Area.Locations, Level.View)]
public sealed class LocationFieldsController(LocationFieldService fields) : V1ControllerBase
{
    /// <summary>
    /// Every field in order, archived ones included, with how many locations filled it; the typical fields (§4.4); for
    /// Users and Admins, the users a User field can name.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<LocationFieldList>> List(CancellationToken ct) => await fields.ListAsync(CanSeeSecrets, ct);

    [HttpPost]
    [RequireRight(Area.Locations, Level.Manage)]
    [Audit(AuditEvents.LocationFieldCreated)]
    public async Task<ActionResult<LocationFieldItem>> Create(LocationFieldRequest request, CancellationToken ct) => await fields.CreateAsync(request, ct);

    /// <summary>409 <c>type_change</c> for a type change that would lose values; 409 <c>choice_in_use</c> for a choice still used.</summary>
    [HttpPut("{id:guid}")]
    [RequireRight(Area.Locations, Level.Manage)]
    [Audit(AuditEvents.LocationFieldChanged)]
    public async Task<ActionResult<LocationFieldItem>> Update(Guid id, LocationFieldRequest request, CancellationToken ct) =>
        await fields.UpdateAsync(id, request, ct);

    [HttpPut("order")]
    [RequireRight(Area.Locations, Level.Manage)]
    [Audit(AuditEvents.LocationFieldsReordered)]
    public async Task<ActionResult<LocationFieldList>> Order(LocationFieldOrderRequest request, CancellationToken ct) => await fields.OrderAsync(request, ct);

    [HttpPost("{id:guid}/archive")]
    [RequireRight(Area.Locations, Level.Manage)]
    [Audit(AuditEvents.LocationFieldArchived)]
    public async Task<ActionResult<LocationFieldItem>> Archive(Guid id, CancellationToken ct) => await fields.ArchiveAsync(id, ct);

    [HttpPost("{id:guid}/restore")]
    [RequireRight(Area.Locations, Level.Manage)]
    [Audit(AuditEvents.LocationFieldRestored)]
    public async Task<ActionResult<LocationFieldItem>> Restore(Guid id, CancellationToken ct) => await fields.RestoreAsync(id, ct);

    /// <summary>An archived field, with its values and their history (409 <c>not_archived</c>). Audited.</summary>
    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Locations, Level.Manage)]
    [Audit(AuditEvents.LocationFieldDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await fields.DeleteAsync(id, ct);
        return NoContent();
    }
}
