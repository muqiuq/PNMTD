using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Files;
using PNMTD.Knowledge;
using PNMTD.Licenses;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1;

/// <summary>
/// Licences &amp; services (docs/licences-plan.md §10.1): the items, their hosts, links, renewals, reviews, charges, documents and keys; the
/// money views; the panels. Reading is <i>View</i>, keys and documents <i>Details</i>, changes <i>Edit</i>, deleting <i>Manage</i>. Every
/// endpoint answers 404 <c>licenses_disabled</c> while <c>Licenses__Enabled</c> is off, for everyone (§13.2).
/// </summary>
[Route("api/v1/licenses")]
[RequiresLicenses]
[RequireRight(Area.Licenses, Level.View)]
public sealed class LicensesController(LicenseQueries queries, LicenseService licenses) : V1ControllerBase
{
    /// <summary>The rows of the lists: <c>type</c>, and <c>status</c> (active and cancelled unless asked).</summary>
    [HttpGet]
    public async Task<IReadOnlyList<LicenseRow>> List([FromQuery] LicenseType? type, [FromQuery] List<LicenseStatus>? status, CancellationToken ct) =>
        await queries.ListAsync(type, status, null, ct);

    /// <summary>The list as CSV, with the same filters, or the selection's <c>ids</c>: never the keys, the note or the documents.</summary>
    [HttpGet("export.csv")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "text/csv")]
    public async Task<IActionResult> Export([FromQuery] LicenseType? type, [FromQuery] List<LicenseStatus>? status, [FromQuery] List<Guid>? ids,
        CancellationToken ct)
    {
        var rows = await queries.ListAsync(type, status, ids is { Count: > 0 } ? ids : null, ct);
        return Csv(LicenseQueries.RowsCsv(rows), "licences");
    }

    [HttpGet("overview")]
    public async Task<LicenseOverview> Overview([FromQuery] LicenseMoneyFilter filter, CancellationToken ct) => await queries.OverviewAsync(filter, ct);

    [HttpGet("costs")]
    public async Task<LicenseCosts> Costs([FromQuery] LicenseCostsQuery query, CancellationToken ct) => await queries.CostsAsync(query, ct);

    [HttpGet("costs/export.csv")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "text/csv")]
    public async Task<IActionResult> CostsExport([FromQuery] LicenseCostsQuery query, CancellationToken ct) =>
        Csv(LicenseQueries.CostsCsv(await queries.CostsAsync(query, ct)), "licence-costs");

    [HttpGet("charges")]
    public async Task<IReadOnlyList<LicenseChargeItem>> Charges([FromQuery] LicenseChargesQuery query, CancellationToken ct) =>
        await queries.ChargesAsync(query, ct);

    [HttpGet("charges/export.csv")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "text/csv")]
    public async Task<IActionResult> ChargesExport([FromQuery] LicenseChargesQuery query, CancellationToken ct) =>
        Csv(LicenseQueries.ChargesCsv(await queries.ChargesAsync(query, ct)), "licence-charges");

    /// <summary>What comes up in the next <c>months</c> (1–36).</summary>
    [HttpGet("upcoming")]
    public async Task<LicenseUpcoming> Upcoming([FromQuery] int? months, [FromQuery] LicenseType? type, [FromQuery] Guid? owner, CancellationToken ct) =>
        await queries.UpcomingAsync(months ?? 6, type, owner, ct);

    /// <summary>Manufacturers, vendors, cost centres and seat units in use, with counts: the suggestions and the Names view.</summary>
    [HttpGet("names")]
    public async Task<LicenseNames> Names(CancellationToken ct) => await queries.NamesAsync(ct);

    /// <summary>The active users an item can name as its owner, never their contact details.</summary>
    [HttpGet("people")]
    public async Task<IReadOnlyList<LicensePerson>> People(CancellationToken ct) => await queries.PeopleAsync(ct);

    /// <summary>The items of a host or a location (its own and its hosts', below it too), for their panels.</summary>
    [HttpGet("linked")]
    public async Task<IReadOnlyList<LicenseLinkedItem>> Linked([FromQuery] Guid? host, [FromQuery] Guid? location, CancellationToken ct) =>
        await queries.LinkedAsync(host, location, ct);

    /// <summary>
    /// Windows activation, matched by a key's last five characters (§8.11): an item's hosts that use its key (<c>license</c>), or a host's
    /// activation and the items it matches (<c>host</c>).
    /// </summary>
    [HttpGet("activations")]
    public async Task<IReadOnlyList<LicenseActivation>> Activations([FromQuery] Guid? license, [FromQuery] Guid? host, CancellationToken ct) =>
        await queries.ActivationsAsync(license, host, ct);

    /// <summary>Hosts to assign (<c>kind=host</c>) or sensors to link (<c>kind=sensor</c>), by name, without Monitoring: View.</summary>
    [HttpGet("targets")]
    [RequireRight(Area.Licenses, Level.Edit)]
    public async Task<IReadOnlyList<LicenseTarget>> Targets([FromQuery] string? kind, [FromQuery] string? q, [FromQuery] Guid? license, CancellationToken ct) =>
        await queries.TargetsAsync(kind, q, license, ct);

    [HttpGet("{id:guid}")]
    public async Task<LicenseDetail> Get(Guid id, CancellationToken ct) => await queries.GetAsync(id, ct);

    [HttpGet("number/{number:int}")]
    public async Task<LicenseDetail> ByNumber(int number, CancellationToken ct) => await queries.GetByNumberAsync(number, ct);

    /// <summary>Both keys, shown: audited (warn). 409 <c>key_lost</c> when PNMT's key ring was replaced.</summary>
    [HttpGet("{id:guid}/keys")]
    [RequireRight(Area.Licenses, Level.Details)]
    [Audit(AuditEvents.LicenseKeysShown)]
    public async Task<LicenseKeys> Keys(Guid id, CancellationToken ct) => await licenses.KeysAsync(id, ct);

    [HttpGet("{id:guid}/history")]
    public async Task<Paged<AuditLogItem>> History(Guid id, [FromQuery] PageQuery query, CancellationToken ct) => await queries.HistoryAsync(id, query, ct);

    /// <summary>A new item. 422 <c>looks_like_credential</c> for a note or support contact that looks like it holds a key, unless acknowledged.</summary>
    [HttpPost]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseCreated)]
    public async Task<ActionResult<LicenseDetail>> Create(LicenseRequest request, CancellationToken ct)
    {
        var created = await licenses.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPost("{id:guid}/duplicate")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseCreated)]
    public async Task<LicenseDetail> Duplicate(Guid id, CancellationToken ct) => await licenses.DuplicateAsync(id, ct);

    /// <summary>The whole form. Keys: null keeps, "" clears.</summary>
    [HttpPut("{id:guid}")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseChanged)]
    public async Task<LicenseDetail> Update(Guid id, LicenseRequest request, CancellationToken ct) => await licenses.UpdateAsync(id, request, ct);

    [HttpPost("{id:guid}/renew")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseRenewed)]
    public async Task<LicenseRenewalItem> Renew(Guid id, LicenseRenewRequest request, CancellationToken ct) => await licenses.RenewAsync(id, request, ct);

    [HttpPost("{id:guid}/review")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseReviewed)]
    public async Task<LicenseDetail> Review(Guid id, LicenseReviewRequest request, CancellationToken ct)
    {
        await licenses.ReviewAsync([id], request, ct);
        return await queries.GetAsync(id, ct);
    }

    /// <summary>Mark reviewed… for many rows at once (<c>ids</c>).</summary>
    [HttpPost("review")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseReviewed)]
    public async Task<LicenseBulkResult> ReviewMany(LicenseReviewRequest request, CancellationToken ct) =>
        await licenses.ReviewAsync(request.Ids ?? [], request, ct);

    /// <summary>It runs to its expiry and isn't renewed; <c>endsOn</c> is required without an expiry.</summary>
    [HttpPost("{id:guid}/cancel")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseCancelled)]
    public async Task<LicenseDetail> Cancel(Guid id, LicenseCancelRequest request, CancellationToken ct) => await licenses.CancelAsync(id, request, ct);

    [HttpPost("{id:guid}/reactivate")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseReactivated)]
    public async Task<LicenseDetail> Reactivate(Guid id, CancellationToken ct) => await licenses.ReactivateAsync(id, ct);

    /// <summary>Its hosts and their sensors go (<c>confirmed</c> when there are any, 409 <c>confirm_needed</c>); the item stays for the record.</summary>
    [HttpPost("{id:guid}/retire")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseRetired)]
    public async Task<LicenseDetail> Retire(Guid id, LicenseRetireRequest request, CancellationToken ct) => await licenses.RetireAsync(id, request, ct);

    /// <summary>With everything of it; <c>number</c> must match, with or without its prefix.</summary>
    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Licenses, Level.Manage)]
    [Audit(AuditEvents.LicenseDeleted)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] string? number, CancellationToken ct)
    {
        await licenses.DeleteAsync(id, number, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/assignments")]
    public async Task<IReadOnlyList<LicenseAssignmentItem>> Assignments(Guid id, CancellationToken ct) => await queries.AssignmentsAsync(id, ct);

    /// <summary>Hosts, at most 500 at once; each gets the item's sensor. 409 <c>license_retired</c>, 409 <c>too_many_assignments</c>.</summary>
    [HttpPost("{id:guid}/assignments")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseAssigned)]
    public async Task<IReadOnlyList<LicenseAssignmentItem>> Assign(Guid id, LicenseAssignRequest request, CancellationToken ct) =>
        await licenses.AssignAsync(id, request, ct);

    [HttpPut("{id:guid}/assignments/{hostId:guid}")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseAssignmentChanged)]
    public async Task<IReadOnlyList<LicenseAssignmentItem>> UpdateAssignment(Guid id, Guid hostId, LicenseAssignmentRequest request, CancellationToken ct) =>
        await licenses.UpdateAssignmentAsync(id, hostId, request, ct);

    [HttpPost("{id:guid}/assignments/remove")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseUnassigned)]
    public async Task<IReadOnlyList<LicenseAssignmentItem>> Unassign(Guid id, LicenseUnassignRequest request, CancellationToken ct) =>
        await licenses.UnassignAsync(id, request, ct);

    [HttpGet("{id:guid}/sensor-links")]
    public async Task<IReadOnlyList<LicenseSensorLinkItem>> SensorLinks(Guid id, CancellationToken ct) => await queries.SensorLinksAsync(id, ct);

    [HttpPost("{id:guid}/sensor-links")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseSensorLinked)]
    public async Task<IReadOnlyList<LicenseSensorLinkItem>> Link(Guid id, LicenseLinkRequest request, CancellationToken ct) =>
        await licenses.LinkAsync(id, request, ct);

    [HttpDelete("{id:guid}/sensor-links/{sensorId:guid}")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseSensorUnlinked)]
    public async Task<IReadOnlyList<LicenseSensorLinkItem>> Unlink(Guid id, Guid sensorId, CancellationToken ct) => await licenses.UnlinkAsync(id, sensorId, ct);

    [HttpGet("{id:guid}/renewals")]
    public async Task<IReadOnlyList<LicenseRenewalItem>> Renewals(Guid id, CancellationToken ct) => await queries.RenewalsAsync(id, ct);

    [HttpPut("{id:guid}/renewals/{renewalId:guid}")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseRenewalChanged)]
    public async Task<IReadOnlyList<LicenseRenewalItem>> UpdateRenewal(Guid id, Guid renewalId, LicenseRenewalChangeRequest request, CancellationToken ct) =>
        await licenses.UpdateRenewalAsync(id, renewalId, request, ct);

    [HttpDelete("{id:guid}/renewals/{renewalId:guid}")]
    [RequireRight(Area.Licenses, Level.Manage)]
    [Audit(AuditEvents.LicenseRenewalDeleted)]
    public async Task<IReadOnlyList<LicenseRenewalItem>> DeleteRenewal(Guid id, Guid renewalId, CancellationToken ct) =>
        await licenses.DeleteRenewalAsync(id, renewalId, ct);

    [HttpGet("{id:guid}/charges")]
    public async Task<IReadOnlyList<LicenseChargeItem>> ItemCharges(Guid id, CancellationToken ct) => await queries.ItemChargesAsync(id, ct);

    [HttpPost("{id:guid}/charges")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseChargeRecorded)]
    public async Task<LicenseChargeItem> RecordCharge(Guid id, LicenseChargeRequest request, CancellationToken ct) =>
        await licenses.RecordChargeAsync(id, request, ct);

    [HttpPut("{id:guid}/charges/{chargeId:guid}")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseChargeChanged)]
    public async Task<LicenseChargeItem> UpdateCharge(Guid id, Guid chargeId, LicenseChargeRequest request, CancellationToken ct) =>
        await licenses.UpdateChargeAsync(id, chargeId, request, ct);

    /// <summary>A person's charge; 409 <c>automatic_charge</c> for PNMT's own, which would be recorded again.</summary>
    [HttpDelete("{id:guid}/charges/{chargeId:guid}")]
    [RequireRight(Area.Licenses, Level.Manage)]
    [Audit(AuditEvents.LicenseChargeDeleted)]
    public async Task<IActionResult> DeleteCharge(Guid id, Guid chargeId, CancellationToken ct)
    {
        await licenses.DeleteChargeAsync(id, chargeId, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/documents")]
    public async Task<IReadOnlyList<LicenseDocumentItem>> Documents(Guid id, CancellationToken ct) => await queries.DocumentsAsync(id, ct);

    /// <summary>
    /// <c>?name=…&amp;kind=…&amp;chargeId=…&amp;renewalId=…</c> with the file as the body, as any attachment: at most
    /// <c>KnowledgeBase:MaxAttachmentBytes</c>, executables refused. 413 <c>too_large</c>, 422 <c>blocked_type</c>, 429 <c>too_many_uploads</c>.
    /// </summary>
    [HttpPost("{id:guid}/documents")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [FileUploadLimit]
    [ProducesResponseType<LicenseDocumentItem>(StatusCodes.Status201Created)]
    [Audit(AuditEvents.LicenseDocumentAdded)]
    public async Task<ActionResult<LicenseDocumentItem>> AddDocument(Guid id, [FromQuery] string? name, [FromQuery] string? kind, [FromQuery] Guid? chargeId,
        [FromQuery] Guid? renewalId, [FromServices] FileStore store, [FromServices] UploadGuard uploads,
        [FromServices] IOptionsMonitor<KnowledgeBaseOptions> files, CancellationToken ct)
    {
        var perHour = files.CurrentValue.UploadsPerHour;
        if (User.UserId() is { } user && !uploads.TryEnter(user, perHour, out var retryAfter))
        {
            Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            throw new ApiException(StatusCodes.Status429TooManyRequests, FileErrors.TooManyUploads,
                $"At most {perHour} uploads per hour (KnowledgeBase:UploadsPerHour).");
        }
        var actor = new FileActor(User.UserId(), User.Identity?.Name,
            User.FindFirst(SessionClaims.DisplayName)?.Value is { Length: > 0 } display ? display : null, AuditContext.From(HttpContext));
        var item = await licenses.AddDocumentAsync(id, Request.Body, name, kind, chargeId, renewalId, actor, store, ct);
        return Created($"/api/v1/licenses/{id}/documents/{item.FileId}/{Uri.EscapeDataString(item.Name)}", item);
    }

    [HttpPut("{id:guid}/documents/{fileId:guid}")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseDocumentChanged)]
    public async Task<IReadOnlyList<LicenseDocumentItem>> UpdateDocument(Guid id, Guid fileId, LicenseDocumentChangeRequest request, CancellationToken ct) =>
        await licenses.UpdateDocumentAsync(id, fileId, request, ct);

    /// <summary>
    /// A document, only through its item, always as a download: <c>application/octet-stream</c>, <c>attachment</c>, <c>nosniff</c> and
    /// <c>no-store</c>, so a key file doesn't stay in a browser's cache. Audited (warn).
    /// </summary>
    [HttpGet("{id:guid}/documents/{fileId:guid}/{name}")]
    [RequireRight(Area.Licenses, Level.Details)]
    [Audit(AuditEvents.LicenseDocumentDownloaded)]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "application/octet-stream")]
    public async Task<IActionResult> Document(Guid id, Guid fileId, string name, [FromServices] FileStore store, CancellationToken ct)
    {
        var file = await licenses.DocumentAsync(id, fileId, ct);
        var path = store.Locate(file.Path) ?? throw ApiException.NotFound("File");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(stream, "application/octet-stream", FileNames.NameOf(file));
    }

    [HttpDelete("{id:guid}/documents/{fileId:guid}")]
    [RequireRight(Area.Licenses, Level.Edit)]
    [Audit(AuditEvents.LicenseDocumentRemoved)]
    public async Task<IActionResult> RemoveDocument(Guid id, Guid fileId, CancellationToken ct)
    {
        await licenses.RemoveDocumentAsync(id, fileId, ct);
        return NoContent();
    }

    /// <summary>A manufacturer, vendor, cost centre or seat unit renamed everywhere; to a name in use, the two merge.</summary>
    [HttpPost("names/rename")]
    [RequireRight(Area.Licenses, Level.Manage)]
    [Audit(AuditEvents.LicenseNamesRenamed)]
    public async Task<LicenseRenameResult> Rename(LicenseRenameRequest request, CancellationToken ct) => await licenses.RenameAsync(request, ct);

    private FileContentResult Csv(string text, string name) =>
        File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(), "text/csv; charset=utf-8",
            $"{name}-{Clock.Today:yyyy-MM-dd}.csv");
}

/// <summary>The templates (§3.2): reading is View, changing Manage.</summary>
[Route("api/v1/license-templates")]
[RequiresLicenses]
[RequireRight(Area.Licenses, Level.View)]
public sealed class LicenseTemplatesController(LicenseService licenses) : V1ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<LicenseTemplateItem>> List(CancellationToken ct) => await licenses.TemplatesAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<LicenseTemplateItem> Get(Guid id, CancellationToken ct) => await licenses.TemplateAsync(id, ct);

    [HttpPost]
    [RequireRight(Area.Licenses, Level.Manage)]
    [Audit(AuditEvents.LicenseTemplateCreated)]
    public async Task<LicenseTemplateItem> Create(LicenseTemplateRequest request, CancellationToken ct) => await licenses.CreateTemplateAsync(request, ct);

    /// <summary>Changes no item's own fields; the items that follow its end of life are evaluated again.</summary>
    [HttpPut("{id:guid}")]
    [RequireRight(Area.Licenses, Level.Manage)]
    [Audit(AuditEvents.LicenseTemplateChanged)]
    public async Task<LicenseTemplateItem> Update(Guid id, LicenseTemplateRequest request, CancellationToken ct) =>
        await licenses.UpdateTemplateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Licenses, Level.Manage)]
    [Audit(AuditEvents.LicenseTemplateDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await licenses.DeleteTemplateAsync(id, ct);
        return NoContent();
    }
}

/// <summary>The categories (§3.1): reading is View, changing Manage; a category in use is deleted with its items moved (<c>moveTo</c>).</summary>
[Route("api/v1/license-categories")]
[RequiresLicenses]
[RequireRight(Area.Licenses, Level.View)]
public sealed class LicenseCategoriesController(LicenseService licenses) : V1ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<LicenseCategoryItem>> List(CancellationToken ct) => await licenses.CategoriesAsync(ct);

    [HttpPost]
    [RequireRight(Area.Licenses, Level.Manage)]
    [Audit(AuditEvents.LicenseCategoryCreated)]
    public async Task<IReadOnlyList<LicenseCategoryItem>> Create(LicenseCategoryRequest request, CancellationToken ct) =>
        await licenses.CreateCategoryAsync(request, ct);

    [HttpPut("{id:guid}")]
    [RequireRight(Area.Licenses, Level.Manage)]
    [Audit(AuditEvents.LicenseCategoryChanged)]
    public async Task<IReadOnlyList<LicenseCategoryItem>> Update(Guid id, LicenseCategoryRequest request, CancellationToken ct) =>
        await licenses.UpdateCategoryAsync(id, request, ct);

    [HttpPut("order")]
    [RequireRight(Area.Licenses, Level.Manage)]
    [Audit(AuditEvents.LicenseCategoryChanged)]
    public async Task<IReadOnlyList<LicenseCategoryItem>> Order(LicenseCategoryOrderRequest request, CancellationToken ct) =>
        await licenses.OrderCategoriesAsync(request, ct);

    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Licenses, Level.Manage)]
    [Audit(AuditEvents.LicenseCategoryDeleted)]
    public async Task<IReadOnlyList<LicenseCategoryItem>> Delete(Guid id, [FromQuery] Guid? moveTo, CancellationToken ct) =>
        await licenses.DeleteCategoryAsync(id, moveTo, ct);
}
