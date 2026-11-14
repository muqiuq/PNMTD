using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Zabbix;

namespace PNMTD.Api.V1;

/// <summary>
/// Zabbix (docs/audit-redmine-zabbix-plan.md §7, §9): the overview, problems and hosts for Zabbix's View, inventory and
/// acknowledgement messages for its Details; the connection's test for System's Manage. Zabbix's failures are 502; not
/// configured is 409, as for Redmine and AI.
/// </summary>
[Route("api/v1/zabbix")]
[Authorize]
public sealed class ZabbixController(ZabbixService zabbix, ZabbixSensorService sensors) : V1ControllerBase
{
    /// <summary>Whether Zabbix is on, its version, where browsers open it: every signed-in user, for the drawer.</summary>
    [HttpGet("status")]
    public async Task<ZabbixStatus> Status(CancellationToken ct) => await zabbix.StatusAsync(ct);

    /// <summary>The overview (§7.4): <c>category=group</c> (default) or <c>tag:&lt;name&gt;</c>; suppressed problems with <c>suppressed=true</c>.</summary>
    [HttpGet("summary")]
    [RequireRight(Area.Zabbix, Level.View)]
    public async Task<ZabbixSummary> Summary([FromQuery] string? category, [FromQuery] bool suppressed, CancellationToken ct) =>
        await Call(() => zabbix.SummaryAsync(category, suppressed, ct));

    [HttpGet("problems")]
    [RequireRight(Area.Zabbix, Level.View)]
    public async Task<Paged<ZabbixProblemItem>> Problems([FromQuery] ZabbixProblemQuery query, CancellationToken ct) =>
        await Call(() => zabbix.ProblemsAsync(query, ct));

    /// <summary>A problem's row opened: the trigger's description; the updates and messages with Zabbix's Details.</summary>
    [HttpGet("problems/{eventId}")]
    [RequireRight(Area.Zabbix, Level.View)]
    public async Task<ZabbixProblemDetail> Problem(string eventId, CancellationToken ct) =>
        await Call(() => zabbix.ProblemAsync(eventId, User.Has(Area.Zabbix, Level.Details), ct));

    [HttpGet("hosts")]
    [RequireRight(Area.Zabbix, Level.View)]
    public async Task<Paged<ZabbixHostItem>> Hosts([FromQuery] ZabbixHostQuery query, CancellationToken ct) =>
        await Call(() => zabbix.HostsAsync(query, ct));

    /// <summary>A host's page (§7.6); its inventory with Zabbix's Details.</summary>
    [HttpGet("hosts/{hostId:long}")]
    [RequireRight(Area.Zabbix, Level.View)]
    public async Task<ZabbixHostDetail> Host(long hostId, CancellationToken ct) =>
        await Call(() => zabbix.HostAsync(hostId, User.Has(Area.Zabbix, Level.Details), ct));

    /// <summary>A host's latest data: 200 items at most, <c>search</c> in their names.</summary>
    [HttpGet("hosts/{hostId:long}/items")]
    [RequireRight(Area.Zabbix, Level.View)]
    public async Task<IReadOnlyList<ZabbixItemItem>> Items(long hostId, [FromQuery] string? search, CancellationToken ct) =>
        await Call(() => zabbix.ItemsOfHostAsync(hostId, search, ct));

    /// <summary>Unattached Zabbix hosts with the PNMT hosts they match (§7.8).</summary>
    [HttpGet("suggestions")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [RequireRight(Area.Zabbix, Level.View)]
    public async Task<IReadOnlyList<ZabbixSuggestion>> Suggestions(CancellationToken ct) => await Call(() => sensors.SuggestionsAsync(ct));

    /// <summary>Zabbix hosts attached as sensors (§7.8), all or none: one is a SensorCreated, several a ZabbixHostsAttached.</summary>
    [HttpPost("attach")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [RequireRight(Area.Zabbix, Level.View)]
    [Audit(AuditEvents.SensorCreated, AuditEvents.ZabbixHostsAttached)]
    public async Task<IReadOnlyList<ZabbixAttached>> Attach(ZabbixAttachRequest request, CancellationToken ct) =>
        await Call(() => sensors.AttachAsync(request, ct));

    /// <summary>The connection's test (§8.5): the version, a poll and a host sync now, the host groups found and not found.</summary>
    [HttpPost("test")]
    [RequireRight(Area.System, Level.Manage)]
    [Audit(AuditEvents.ZabbixTested)]
    public async Task<ZabbixTestResult> Test(CancellationToken ct) => await Call(async () =>
    {
        var result = await zabbix.TestAsync(ct);
        AuditLog.Summary($"Zabbix {result.Version}; {result.Hosts} hosts, {result.Problems} problems"
            + (result.HostGroups.Count > 0 ? $"; groups {string.Join(", ", result.HostGroups)}" : "")
            + (result.NotFound.Count > 0 ? $"; not found: {string.Join(", ", result.NotFound)}" : ""));
        return result;
    });

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ZabbixException e)
        {
            throw e.Failure == ZabbixFailure.NotConfigured
                ? ApiException.Conflict("zabbix_not_configured", e.Message)
                : new ApiException(StatusCodes.Status502BadGateway, e.Failure == ZabbixFailure.Unauthorized ? "zabbix_refused" : "zabbix_unreachable", e.Message);
        }
    }
}
