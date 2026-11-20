using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Models.Db;
using PNMTD.Redmine;

namespace PNMTD.Api.V1;

/// <summary>
/// Redmine (docs/audit-redmine-zabbix-plan.md §6, §9): its overview, search and tickets for Redmine's View, from the worker's
/// snapshots with their freshness, and the refresh button (docs/redmine-queue-outbound-plan.md §5.3); links for its Link; the
/// connection's test for System's Manage. The buttons' jobs go first in the worker's queue (§3.3). Redmine's failures are 502;
/// not configured is 409, as for AI; a job that takes longer than <c>Redmine__WaitSeconds</c> is 504.
/// </summary>
[Route("api/v1/redmine")]
[Authorize]
public sealed class RedmineController(RedmineService redmine) : V1ControllerBase
{
    /// <summary>Whether Redmine is on, where browsers open it, its projects: every signed-in user, for the drawer.</summary>
    [HttpGet("status")]
    public async Task<RedmineStatus> Status(CancellationToken ct) => await redmine.StatusAsync(ct);

    [HttpGet("lookups")]
    [RequireRight(Area.Redmine, Level.View)]
    public async Task<RedmineLookupsItem> Lookups(CancellationToken ct) => await Call(() => redmine.LookupsItemAsync(ct));

    [HttpGet("overview")]
    [RequireRight(Area.Redmine, Level.View)]
    public async Task<RedmineOverview> Overview([FromQuery] int? project, CancellationToken ct) => await Call(() => redmine.OverviewAsync(project, ct));

    [HttpGet("issues")]
    [RequireRight(Area.Redmine, Level.View)]
    public async Task<RedmineSearchPage> Issues([FromQuery] RedmineQuery query, CancellationToken ct) => await Call(() => redmine.SearchAsync(query, ct));

    [HttpGet("issues/{id:int}")]
    [RequireRight(Area.Redmine, Level.View)]
    public async Task<RedmineIssueDetail> Issue(int id, CancellationToken ct) => await Call(() => redmine.IssueAsync(id, ct));

    /// <summary>
    /// The refresh button (docs/redmine-queue-outbound-plan.md §5.3): <c>?view=overview|search|issue</c> with the view's
    /// <c>project</c>, search filters or <c>id</c>. Waits for the new data up to <c>Redmine__WaitSeconds</c>; 202 if it is
    /// still being read then.
    /// </summary>
    [HttpPost("refresh")]
    [RequireRight(Area.Redmine, Level.View)]
    [NotAudited("a read of Redmine, as the page itself")]
    public async Task<ActionResult<RedmineFreshness>> Refresh([FromQuery] string? view, [FromQuery] int? id, [FromQuery] RedmineQuery query,
        CancellationToken ct)
    {
        var (freshness, done) = await Call(() => redmine.RefreshAsync(view, id, query, ct));
        return done ? freshness : StatusCode(StatusCodes.Status202Accepted, freshness);
    }

    /// <summary>An object's tickets, from PNMT's copy: <c>?pageId|locationId|hostId|sensorId</c>, <c>deep</c> for a location's Tickets tab.</summary>
    [HttpGet("related")]
    [RequireRight(Area.Redmine, Level.View)]
    public async Task<RedmineRelated> Related([FromQuery] Guid? pageId, [FromQuery] Guid? locationId, [FromQuery] Guid? hostId, [FromQuery] Guid? sensorId,
        [FromQuery] bool deep, CancellationToken ct) => await redmine.RelatedAsync(pageId, locationId, hostId, sensorId, deep, ct);

    /// <summary>Failing sensors with an open linked ticket, for the dashboard's chips.</summary>
    [HttpGet("problems")]
    [RequireRight(Area.Redmine, Level.View)]
    public async Task<IReadOnlyList<RedmineProblemTicket>> Problems(CancellationToken ct) => await redmine.ProblemsAsync(ct);

    [HttpPost("links")]
    [RequireRight(Area.Redmine, Level.Link)]
    [Audit(AuditEvents.RedmineLinkAdded)]
    public async Task<ActionResult<RedmineLinkItem>> Link(RedmineLinkRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await Call(() => redmine.AddLinkAsync(request, ct, RedmineLane.Button)));

    [HttpPut("links/{id:guid}")]
    [RequireRight(Area.Redmine, Level.Link)]
    [Audit(AuditEvents.RedmineLinkChanged)]
    public async Task<RedmineLinkItem> Note(Guid id, RedmineLinkNoteRequest request, CancellationToken ct) => await redmine.UpdateLinkAsync(id, request, ct);

    [HttpDelete("links/{id:guid}")]
    [RequireRight(Area.Redmine, Level.Link)]
    [Audit(AuditEvents.RedmineLinkRemoved)]
    public async Task<IActionResult> Unlink(Guid id, CancellationToken ct)
    {
        await redmine.RemoveLinkAsync(id, ct);
        return NoContent();
    }

    /// <summary>The connection's test (§8.5), in the button lane: the key's account, the projects found and those not found.</summary>
    [HttpPost("test")]
    [RequireRight(Area.System, Level.Manage)]
    [Audit(AuditEvents.RedmineTested)]
    public async Task<RedmineTestResult> Test([FromServices] RedmineWorker worker, CancellationToken ct) => await Call(async () =>
    {
        var (account, lookups) = await worker.TestAsync(User.Identity?.Name ?? "PNMT", ct);
        var status = await redmine.StatusAsync(ct);
        AuditLog.Summary($"account {account}; projects {string.Join(", ", status.Projects.Select(p => p.Identifier))}"
            + (lookups.NotFound.Count > 0 ? $"; not found: {string.Join(", ", lookups.NotFound)}" : ""));
        return new RedmineTestResult(account, status.Projects, lookups.NotFound);
    });

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (RedmineException e)
        {
            throw e.Failure == RedmineFailure.NotConfigured
                ? ApiException.Conflict("redmine_not_configured", e.Message)
                : new ApiException(StatusCodes.Status502BadGateway, e.Failure == RedmineFailure.Unauthorized ? "redmine_refused" : "redmine_unreachable", e.Message);
        }
    }
}
