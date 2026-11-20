using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Network;

namespace PNMTD.Api.V1;

/// <summary>
/// The network map (docs/network-map-plan.md §10.1): the map, connections, suggestions, network interfaces, scans and the
/// settings. View reads; Details adds the interfaces; Edit writes; Manage scans many routers and sets the schedule. A scan of
/// one router needs Commands: Run, as a poll does.
/// </summary>
[Route("api/v1/network-map")]
[Authorize]
public sealed class NetworkMapController(NetworkMapService map, NetworkConnectionService connections, NetworkScanService scans,
    NetworkPositionService positions) : V1ControllerBase
{
    // --- the map ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The graph: nodes (routers, and the hosts of the chosen locations), the top-level locations as groups, and the connections.
    /// A router's saved position is on its node; everything else the client lays out.
    /// </summary>
    [HttpGet("graph")]
    [RequireRight(Area.NetworkMap, Level.View)]
    public async Task<ActionResult<NetworkGraph>> Graph([FromQuery] NetworkGraphQuery query, CancellationToken ct) => await map.GraphAsync(query, ct);

    [HttpGet("connection-types")]
    [RequireRight(Area.NetworkMap, Level.View)]
    public ActionResult<IReadOnlyList<NetworkConnectionTypeItem>> ConnectionTypes() => Ok(NetworkMapService.ConnectionTypeList());

    /// <summary>A router or host as its side panel; the interfaces with Details.</summary>
    [HttpGet("devices/{kind}/{id:guid}")]
    [RequireRight(Area.NetworkMap, Level.View)]
    public async Task<ActionResult<NetworkDeviceDetail>> Device(NetworkDeviceKind kind, Guid id, CancellationToken ct) =>
        await map.DeviceAsync(kind, id, User.Has(Area.NetworkMap, Level.Details), ct);

    [HttpGet("summary")]
    [RequireRight(Area.NetworkMap, Level.View)]
    public async Task<ActionResult<NetworkMapSummary>> Summary(CancellationToken ct) => await map.SummaryAsync(ct);

    /// <summary>How two devices are connected (router:{id} or host:{id}): the shortest path, its slowest hop, its single points. 404 without one.</summary>
    [HttpGet("path")]
    [RequireRight(Area.NetworkMap, Level.View)]
    public async Task<ActionResult<NetworkPath>> Path([FromQuery] string from, [FromQuery] string to, [FromQuery] bool physicalOnly, CancellationToken ct) =>
        await map.PathAsync(from, to, physicalOnly, ct) ?? throw ApiException.NotFound("A path between the two");

    // --- saved positions (docs/network-map-positions-plan.md §5) ---------------------------------------------------------

    /// <summary>Saves routers' places on the map, adding or updating; routers deleted meanwhile are skipped.</summary>
    [HttpPut("positions")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkPositionsSaved)]
    public async Task<ActionResult<NetworkPositionsResult>> SavePositions(NetworkPositionsRequest request, CancellationToken ct) =>
        await positions.SaveAsync(request, ct);

    /// <summary>One router back to automatic.</summary>
    [HttpDelete("positions/{sensorId:guid}")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkPositionCleared)]
    public async Task<IActionResult> ClearPosition(Guid sensorId, CancellationToken ct)
    {
        await positions.ClearAsync(sensorId, ct);
        return NoContent();
    }

    /// <summary>Every router back to automatic.</summary>
    [HttpDelete("positions")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkPositionsReset)]
    public async Task<IActionResult> ResetPositions(CancellationToken ct)
    {
        await positions.ResetAsync(ct);
        return NoContent();
    }

    // --- connections -----------------------------------------------------------------------------------------------------

    [HttpGet("connections")]
    [RequireRight(Area.NetworkMap, Level.View)]
    public async Task<ActionResult<Paged<NetworkConnectionItem>>> Connections([FromQuery] NetworkConnectionQuery query, CancellationToken ct) =>
        await connections.ListAsync(query, ct);

    [HttpGet("connections/{id:guid}")]
    [RequireRight(Area.NetworkMap, Level.View)]
    public async Task<ActionResult<NetworkConnectionItem>> Connection(Guid id, CancellationToken ct) => await connections.GetAsync(id, ct);

    [HttpPost("connections")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkConnectionCreated)]
    public async Task<ActionResult<NetworkConnectionItem>> CreateConnection(NetworkConnectionRequest request, CancellationToken ct) =>
        await connections.CreateAsync(request, ct);

    [HttpPut("connections/{id:guid}")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkConnectionUpdated)]
    public async Task<ActionResult<NetworkConnectionItem>> UpdateConnection(Guid id, NetworkConnectionUpdate request, CancellationToken ct) =>
        await connections.UpdateAsync(id, request, ct);

    [HttpDelete("connections/{id:guid}")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkConnectionDeleted)]
    public async Task<IActionResult> DeleteConnection(Guid id, CancellationToken ct)
    {
        await connections.DeleteAsync(id, ct);
        return NoContent();
    }

    // --- suggestions -----------------------------------------------------------------------------------------------------

    [HttpGet("suggestions")]
    [RequireRight(Area.NetworkMap, Level.View)]
    public async Task<ActionResult<Paged<NetworkSuggestionItem>>> Suggestions([FromQuery] NetworkSuggestionQuery query, CancellationToken ct) =>
        await connections.SuggestionsAsync(query, ct);

    /// <summary>Makes the suggestion a connection: its proposal, or with another type, ports or notes.</summary>
    [HttpPost("suggestions/{id:guid}/accept")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkSuggestionAccepted)]
    public async Task<ActionResult<NetworkConnectionItem>> Accept(Guid id, NetworkSuggestionAccept request, CancellationToken ct) =>
        await connections.AcceptAsync(id, request, ct);

    /// <summary>Many suggestions, each with its proposed type.</summary>
    [HttpPost("suggestions/accept")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkSuggestionAccepted)]
    public async Task<ActionResult<NetworkSuggestionsDecided>> AcceptMany(NetworkSuggestionIds request, CancellationToken ct) =>
        await connections.AcceptManyAsync(request.Ids, ct);

    [HttpPost("suggestions/{id:guid}/dismiss")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkSuggestionDismissed)]
    public async Task<ActionResult<NetworkSuggestionsDecided>> Dismiss(Guid id, CancellationToken ct) => await connections.DismissAsync([id], ct);

    [HttpPost("suggestions/dismiss")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkSuggestionDismissed)]
    public async Task<ActionResult<NetworkSuggestionsDecided>> DismissMany(NetworkSuggestionIds request, CancellationToken ct) =>
        await connections.DismissAsync(request.Ids, ct);

    [HttpPost("suggestions/{id:guid}/restore")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkSuggestionRestored)]
    public async Task<ActionResult<NetworkSuggestionsDecided>> Restore(Guid id, CancellationToken ct) => await connections.RestoreAsync([id], ct);

    // --- network interfaces ----------------------------------------------------------------------------------------------

    [HttpGet("interfaces")]
    [RequireRight(Area.NetworkMap, Level.Details)]
    public async Task<ActionResult<Paged<NetworkInterfaceItem>>> Interfaces([FromQuery] NetworkInterfaceQuery query, CancellationToken ct) =>
        await connections.InterfacesAsync(query, ct);

    [HttpPut("interfaces/{mac}/assignment")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkInterfaceAssigned)]
    public async Task<ActionResult<NetworkInterfaceItem>> Assign(string mac, NetworkInterfaceAssignment request, CancellationToken ct) =>
        await connections.AssignAsync(mac, request, ct);

    [HttpDelete("interfaces/{mac}/assignment")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkInterfaceCleared)]
    public async Task<ActionResult<NetworkInterfaceItem>> ClearAssignment(string mac, CancellationToken ct) => await connections.ClearAsync(mac, ct);

    [HttpPost("interfaces/{mac}/ignore")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkInterfaceIgnored)]
    public async Task<ActionResult<NetworkInterfaceItem>> Ignore(string mac, CancellationToken ct) => await connections.IgnoreAsync(mac, true, ct);

    [HttpPost("interfaces/{mac}/unignore")]
    [RequireRight(Area.NetworkMap, Level.Edit)]
    [Audit(AuditEvents.NetworkInterfaceUnignored)]
    public async Task<ActionResult<NetworkInterfaceItem>> Unignore(string mac, CancellationToken ct) => await connections.IgnoreAsync(mac, false, ct);

    // --- scans -----------------------------------------------------------------------------------------------------------

    [HttpGet("scans/status")]
    [RequireAnyRight(Area.NetworkMap, Level.View, Area.Commands, Level.Run)]
    public async Task<ActionResult<NetworkScanStatus>> ScanStatus(CancellationToken ct) => await scans.StatusAsync(ct);

    [HttpGet("scans")]
    [RequireRight(Area.NetworkMap, Level.View)]
    public async Task<ActionResult<Paged<NetworkScanRow>>> Scans([FromQuery] NetworkScanQuery query, CancellationToken ct) => await scans.ListAsync(query, ct);

    [HttpGet("scans/{id:guid}")]
    [RequireAnyRight(Area.NetworkMap, Level.View, Area.Commands, Level.Run)]
    public async Task<ActionResult<NetworkScanDetail>> Scan(Guid id, CancellationToken ct) => await scans.GetAsync(id, ct);

    /// <summary>Starts a scan: of all routers or a location (Manage), or of one router (Commands: Run, or Manage). 409 when the same scope is queued or running.</summary>
    [HttpPost("scans")]
    [RequireAnyRight(Area.NetworkMap, Level.Manage, Area.Commands, Level.Run)]
    [Audit(AuditEvents.NetworkScanStarted)]
    public async Task<ActionResult<NetworkScanRow>> StartScan(NetworkScanRequest request, CancellationToken ct) => await scans.StartAsync(request, ct);

    [HttpPost("scans/{id:guid}/cancel")]
    [RequireRight(Area.NetworkMap, Level.Manage)]
    [Audit(AuditEvents.NetworkScanCancelled)]
    public async Task<ActionResult<NetworkScanRow>> CancelScan(Guid id, CancellationToken ct) => await scans.CancelAsync(id, ct);

    [HttpGet("settings")]
    [RequireRight(Area.NetworkMap, Level.View)]
    public async Task<ActionResult<NetworkMapSettings>> Settings(CancellationToken ct) => await scans.SettingsAsync(ct);

    [HttpPut("settings")]
    [RequireRight(Area.NetworkMap, Level.Manage)]
    [Audit(AuditEvents.NetworkMapSettingsChanged)]
    public async Task<ActionResult<NetworkMapSettings>> SaveSettings(NetworkMapSettingsRequest request, CancellationToken ct) =>
        await scans.SaveSettingsAsync(request, ct);
}
