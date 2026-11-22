using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Knowledge;

namespace PNMTD.Api.V1;

/// <summary>
/// The knowledge base (docs/locations-knowledge-plan.md §6, §7, §9), with the roles of §8.1: every role reads, the
/// sensitive pages included, and counts a view; Users write, mark sensitive and run AI tags; Admins delete archived
/// pages, redact, and look after the tags. A page is read by its number (<c>KB-42</c>), changed by its id.
/// </summary>
[Route("api/v1/kb")]
[RequireRight(Area.KnowledgeBase, Level.View)]
public sealed class KnowledgeBaseController(KbPageService pages, KbPageList lists, KbTags tags, KbAiTags aiTags, KbChanges changes) : V1ControllerBase
{
    /// <summary>The list and the content search (§6.6, §6.7).</summary>
    [HttpGet("pages")]
    public async Task<ActionResult<Paged<KbPageListItem>>> List([FromQuery] KbPageQuery query, CancellationToken ct) =>
        await lists.ListAsync(query, ct);

    /// <summary>The recent changes on top of the list (docs/knowledge-base-extensions-plan.md §6): one per page, newest first.</summary>
    [HttpGet("changes")]
    public async Task<ActionResult<KbRecentChanges>> Changes(CancellationToken ct) => await changes.RecentAsync(ct);

    [HttpPost("pages")]
    [RequireRight(Area.KnowledgeBase, Level.Edit)]
    [Audit(AuditEvents.KbPageCreated)]
    public async Task<ActionResult<KbSaveResult>> Create(KbPageRequest request, CancellationToken ct)
    {
        var saved = await pages.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { number = saved.Page.Number }, saved);
    }

    /// <summary>A page by its number, with the files it uses (§6.1).</summary>
    [HttpGet("pages/{number:int}")]
    public async Task<ActionResult<KbPageDetail>> Get(int number, CancellationToken ct) => await pages.GetAsync(number, ct);

    /// <summary>A save, from the revision it started from: 409 <c>edit_conflict</c>; the answer says whether the AI question follows.</summary>
    [HttpPut("pages/{id:guid}")]
    [RequireRight(Area.KnowledgeBase, Level.Edit)]
    [Audit(AuditEvents.KbPageSaved)]
    public async Task<ActionResult<KbSaveResult>> Update(Guid id, KbPageRequest request, CancellationToken ct) =>
        await pages.UpdateAsync(id, request, ct);

    [HttpPost("pages/{id:guid}/archive")]
    [RequireRight(Area.KnowledgeBase, Level.Edit)]
    [Audit(AuditEvents.KbPageArchived)]
    public async Task<ActionResult<KbPageDetail>> Archive(Guid id, CancellationToken ct) => await pages.ArchiveAsync(id, true, ct);

    [HttpPost("pages/{id:guid}/restore")]
    [RequireRight(Area.KnowledgeBase, Level.Edit)]
    [Audit(AuditEvents.KbPageRestored)]
    public async Task<ActionResult<KbPageDetail>> Unarchive(Guid id, CancellationToken ct) => await pages.ArchiveAsync(id, false, ct);

    /// <summary>Archived pages only (409 <c>not_archived</c>), by an Admin (§6.2).</summary>
    [HttpDelete("pages/{id:guid}")]
    [RequireRight(Area.KnowledgeBase, Level.Manage)]
    [Audit(AuditEvents.KbPageDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await pages.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>The page was opened in the UI (§6.6): every role's views count, once per 30 minutes.</summary>
    [HttpPost("pages/{id:guid}/views")]
    [NotAudited("the view counter: a page's reads are not operations")]
    public async Task<IActionResult> View(Guid id, CancellationToken ct)
    {
        await pages.ViewAsync(id, ct);
        return NoContent();
    }

    /// <summary>An open editor's report every 30 seconds: who else is editing (§5.7).</summary>
    [HttpPost("pages/{id:guid}/editing")]
    [RequireRight(Area.KnowledgeBase, Level.Edit)]
    [NotAudited("who is editing: a heartbeat, changes nothing")]
    public async Task<ActionResult<IReadOnlyList<KbEditor>>> Editing(Guid id, KbEditingRequest? request, CancellationToken ct) =>
        Ok(await pages.EditingAsync(id, request, ct));

    [HttpGet("pages/{id:guid}/revisions")]
    public async Task<ActionResult<IReadOnlyList<KbRevisionItem>>> Revisions(Guid id, CancellationToken ct) => Ok(await pages.RevisionsAsync(id, ct));

    [HttpGet("pages/{id:guid}/revisions/{number:int}")]
    public async Task<ActionResult<KbRevisionDetail>> Revision(Guid id, int number, CancellationToken ct) => await pages.RevisionAsync(id, number, ct);

    /// <summary>Two revisions compared; by default the current one against the one before (§6.3).</summary>
    [HttpGet("pages/{id:guid}/diff")]
    public async Task<ActionResult<KbDiff>> Diff(Guid id, [FromQuery] int? from, [FromQuery] int? to, CancellationToken ct) =>
        await pages.DiffAsync(id, from, to, ct);

    [HttpPost("pages/{id:guid}/revisions/{number:int}/restore")]
    [RequireRight(Area.KnowledgeBase, Level.Edit)]
    [Audit(AuditEvents.KbRevisionRestored)]
    public async Task<ActionResult<KbPageDetail>> RestoreRevision(Guid id, int number, CancellationToken ct) =>
        await pages.RestoreRevisionAsync(id, number, ct);

    /// <summary>A leaked text out of every revision (§6.3): Admins.</summary>
    [HttpPost("pages/{id:guid}/redact")]
    [RequireRight(Area.KnowledgeBase, Level.Manage)]
    [Audit(AuditEvents.KbRevisionRedacted)]
    public async Task<ActionResult<KbRedactResult>> Redact(Guid id, KbRedactRequest request, CancellationToken ct) =>
        await pages.RedactAsync(id, request, ct);

    /// <summary>Marking takes effect at once; unmarking needs both statements (§7.1).</summary>
    [HttpPut("pages/{id:guid}/sensitive")]
    [RequireRight(Area.KnowledgeBase, Level.Edit)]
    [Audit(AuditEvents.KbPageSensitivityChanged)]
    public async Task<ActionResult<KbPageDetail>> Sensitive(Guid id, KbSensitiveRequest request, CancellationToken ct) =>
        await pages.SetSensitiveAsync(id, request, ct);

    /// <summary>
    /// An AI tag scan (§7.3): 202 with the work item to poll; 409 <c>sensitive</c>, <c>archived</c>, <c>edit_conflict</c>,
    /// <c>ai_unavailable</c> or <c>ai_budget_spent</c>; 422 <c>looks_like_credential</c> with the line numbers.
    /// </summary>
    [HttpPost("pages/{id:guid}/ai-tags")]
    [RequireRight(Area.KnowledgeBase, Level.Edit)]
    [Audit(AuditEvents.KbAiTagsRequested)]
    public async Task<ActionResult<KbAiTagsStatus>> StartAiTags(Guid id, KbAiTagsRequest request, CancellationToken ct) =>
        Accepted(await aiTags.StartAsync(id, request, ct));

    [HttpGet("pages/{id:guid}/ai-tags/{workId:guid}")]
    [RequireRight(Area.KnowledgeBase, Level.Edit)]
    public async Task<ActionResult<KbAiTagsStatus>> AiTagsStatus(Guid id, Guid workId, CancellationToken ct) => await aiTags.StatusAsync(id, workId, ct);

    /// <summary>The Knowledge base panels of a location, a host or a sensor (§6.5): exactly one of the three.</summary>
    [HttpGet("related")]
    public async Task<ActionResult<KbRelated>> Related([FromQuery] Guid? locationId, [FromQuery] Guid? hostId, [FromQuery] Guid? sensorId,
        CancellationToken ct) => await lists.RelatedAsync(locationId, hostId, sensorId, ct);

    /// <summary>Every tag with its pages, for the tag browser and the completion (§6.4, §6.6).</summary>
    [HttpGet("tags")]
    public async Task<ActionResult<IReadOnlyList<KbTagItem>>> Tags([FromQuery] string? search, CancellationToken ct) => Ok(await tags.ListAsync(search, ct));

    /// <summary>Renames a tag; an existing tag's name merges the two (§6.4).</summary>
    [HttpPut("tags/{id:guid}")]
    [RequireRight(Area.KnowledgeBase, Level.Manage)]
    [Audit(AuditEvents.KbTagRenamed)]
    public async Task<ActionResult<KbTagChange>> RenameTag(Guid id, KbTagRequest request, CancellationToken ct) => await tags.RenameAsync(id, request, ct);

    /// <summary>Removes the tag from every page, as a revision of each (§6.4).</summary>
    [HttpDelete("tags/{id:guid}")]
    [RequireRight(Area.KnowledgeBase, Level.Manage)]
    [Audit(AuditEvents.KbTagDeleted)]
    public async Task<IActionResult> DeleteTag(Guid id, CancellationToken ct)
    {
        await tags.DeleteAsync(id, ct);
        return NoContent();
    }
}
