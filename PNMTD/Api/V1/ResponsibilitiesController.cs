using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Responsibilities;

namespace PNMTD.Api.V1;

/// <summary>
/// Responsibilities (docs/responsibilities-plan.md §11): who owns what, with a deputy, its links and the global people.
/// Reading needs <i>Responsibilities: View</i>, every change <i>Manage</i>. Information only: nothing is sent to anyone.
/// </summary>
[Route("api/v1/responsibilities")]
[RequireRight(Area.Responsibilities, Level.View)]
public sealed class ResponsibilitiesController(ResponsibilityQueries queries, ResponsibilityService responsibilities) : V1ControllerBase
{
    /// <summary>Every responsibility by name, with the global people; <c>q</c> searches, <c>attention</c> keeps those that need it.</summary>
    [HttpGet]
    public async Task<ResponsibilityList> List([FromQuery] string? q, [FromQuery] bool attention, CancellationToken ct) =>
        await queries.ListAsync(q, attention, ct);

    [HttpGet("{id:guid}")]
    public async Task<ResponsibilityDetail> Get(Guid id, CancellationToken ct) => await queries.GetAsync(id, ct);

    [HttpPost]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    [Audit(AuditEvents.ResponsibilityCreated)]
    public async Task<ActionResult<ResponsibilityDetail>> Create(ResponsibilityRequest request, CancellationToken ct)
    {
        var created = await responsibilities.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>The whole form: name, short description, responsible and deputy (null meaning nobody).</summary>
    [HttpPut("{id:guid}")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    [Audit(AuditEvents.ResponsibilityChanged)]
    public async Task<ResponsibilityDetail> Update(Guid id, ResponsibilityRequest request, CancellationToken ct) =>
        await responsibilities.UpdateAsync(id, request, ct);

    /// <summary>At once, with its links and history; the audit log keeps what it was.</summary>
    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    [Audit(AuditEvents.ResponsibilityDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await responsibilities.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// The long description, saved as a page's text: 409 <c>edit_conflict</c> from an older revision, 422
    /// <c>looks_like_credential</c> unless acknowledged.
    /// </summary>
    [HttpPut("{id:guid}/description")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    [Audit(AuditEvents.ResponsibilityChanged)]
    public async Task<ResponsibilityDetail> Description(Guid id, ResponsibilityDescriptionRequest request, CancellationToken ct) =>
        await responsibilities.SetDescriptionAsync(id, request, ct);

    /// <summary>A link to a page, a location or a host: 409 <c>already_linked</c>, 409 <c>too_many_links</c>.</summary>
    [HttpPost("{id:guid}/links")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    [Audit(AuditEvents.ResponsibilityLinked)]
    public async Task<ResponsibilityDetail> Link(Guid id, ResponsibilityLinkRequest request, CancellationToken ct) =>
        await responsibilities.LinkAsync(id, request, ct);

    [HttpDelete("{id:guid}/links/{linkId:guid}")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    [Audit(AuditEvents.ResponsibilityUnlinked)]
    public async Task<ResponsibilityDetail> Unlink(Guid id, Guid linkId, CancellationToken ct) => await responsibilities.UnlinkAsync(id, linkId, ct);

    /// <summary>
    /// The responsibilities that link one host, location or page (exactly one of the three), for its panel: its own links only,
    /// and an empty list when none does.
    /// </summary>
    [HttpGet("linked")]
    public async Task<IReadOnlyList<ResponsibilityRef>> Linked([FromQuery] Guid? host, [FromQuery] Guid? location, [FromQuery] Guid? page,
        CancellationToken ct)
    {
        new ApiValidator().Check(new[] { host, location, page }.Count(x => x is not null) == 1, "host", "One host, location or page.").ThrowIfInvalid();
        return host is { } h ? await queries.LinkedAsync(KbLinkKind.Host, h, ct)
            : location is { } l ? await queries.LinkedAsync(KbLinkKind.Location, l, ct)
            : await queries.LinkedAsync(KbLinkKind.Page, page!.Value, ct);
    }

    /// <summary>Its history, newest first; a description's texts come with the row.</summary>
    [HttpGet("{id:guid}/history")]
    public async Task<Paged<ResponsibilityHistoryItem>> History(Guid id, [FromQuery] PageQuery query, CancellationToken ct) =>
        await responsibilities.HistoryAsync(id, query, ct);

    [HttpGet("{id:guid}/history/{rowId:guid}")]
    public async Task<ResponsibilityHistoryDetail> HistoryRow(Guid id, Guid rowId, CancellationToken ct) =>
        await responsibilities.HistoryRowAsync(id, rowId, ct);

    /// <summary>The description from before a change in the history: a new save of its own.</summary>
    [HttpPost("{id:guid}/history/{rowId:guid}/restore")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    [Audit(AuditEvents.ResponsibilityChanged)]
    public async Task<ResponsibilityDetail> Restore(Guid id, Guid rowId, CancellationToken ct) => await responsibilities.RestoreAsync(id, rowId, ct);

    /// <summary>The global responsible and deputy, both at once.</summary>
    [HttpPut("global")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    [Audit(AuditEvents.GlobalResponsibleChanged)]
    public async Task<ResponsibilityGlobal> Global(ResponsibilityGlobalRequest request, CancellationToken ct) =>
        await responsibilities.SetGlobalAsync(request, ct);

    /// <summary>The projects PNMT reads and Redmine's trackers, for the combination dialog; <c>error</c> when Redmine can't be read.</summary>
    [HttpGet("redmine")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    public async Task<ResponsibilityRedmineLookups> RedmineLookups(CancellationToken ct) => await responsibilities.RedmineLookupsAsync(ct);

    /// <summary>Every Redmine combination, by project, responsibility or user; 404 <c>redmine_not_configured</c> without Redmine.</summary>
    [HttpGet("redmine/combinations")]
    public async Task<RedmineCombinationList> Combinations([FromQuery] int? project, [FromQuery] Guid? responsibility, [FromQuery] Guid? user,
        CancellationToken ct) => await responsibilities.CombinationsAsync(project, responsibility, user, ct);

    /// <summary>A Redmine project and tracker on this responsibility, with who handles it: 409 <c>duplicate_combination</c>.</summary>
    [HttpPost("{id:guid}/redmine")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    [Audit(AuditEvents.ResponsibilityRedmineSaved)]
    public async Task<ResponsibilityDetail> AddCombination(Guid id, RedmineCombinationRequest request, CancellationToken ct) =>
        await responsibilities.AddCombinationAsync(id, request, ct);

    [HttpPut("{id:guid}/redmine/{combinationId:guid}")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    [Audit(AuditEvents.ResponsibilityRedmineSaved)]
    public async Task<ResponsibilityDetail> UpdateCombination(Guid id, Guid combinationId, RedmineCombinationRequest request, CancellationToken ct) =>
        await responsibilities.UpdateCombinationAsync(id, combinationId, request, ct);

    [HttpDelete("{id:guid}/redmine/{combinationId:guid}")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    [Audit(AuditEvents.ResponsibilityRedmineDeleted)]
    public async Task<ResponsibilityDetail> DeleteCombination(Guid id, Guid combinationId, CancellationToken ct) =>
        await responsibilities.DeleteCombinationAsync(id, combinationId, ct);

    /// <summary>The active users a responsibility can name, never their contact details.</summary>
    [HttpGet("people")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    public async Task<ResponsibilityPeople> People(CancellationToken ct) => await responsibilities.PeopleAsync(ct);

    /// <summary>Pages, locations and hosts by name, for the link picker: names only, as the links show them.</summary>
    [HttpGet("targets")]
    [RequireRight(Area.Responsibilities, Level.Manage)]
    public async Task<SearchResult> Targets([FromQuery] string? q, CancellationToken ct) => await responsibilities.TargetsAsync(q, ct);
}
