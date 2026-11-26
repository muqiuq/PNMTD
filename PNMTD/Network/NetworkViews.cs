using System.Text.Json;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Network;
using PNMTD.Models.Db;

namespace PNMTD.Network;

/// <summary>The network map's entities as the API and MCP show them (docs/network-map-plan.md §10).</summary>
public static class NetworkViews
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static NetworkScanEvidence Evidence(NetworkConnectionEntity c) =>
        c.UnconfirmedSince is not null ? NetworkScanEvidence.Unconfirmed : c.ConfirmedAt is not null ? NetworkScanEvidence.Confirmed : NetworkScanEvidence.Manual;

    public static NetworkConnectionItem Connection(NetworkConnectionEntity c, NetworkDirectory d) => new(
        c.Id, d.End(c.A), d.End(c.B), c.Type, ConnectionTypes.Label(c.Type), ConnectionTypes.IsPhysical(c.Type), ConnectionTypes.Find(c.Type)?.SpeedMbps,
        Evidence(c), c.ConfirmedAt, c.UnconfirmedSince, c.Notes, c.Source, c.CreatedAt, c.CreatedByName, c.UpdatedAt, c.UpdatedByName);

    public static IReadOnlyList<Evidence> EvidenceOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<Evidence>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static NetworkSuggestionItem Suggestion(NetworkSuggestionEntity s, NetworkDirectory d) => new(
        s.Id, s.Kind, d.End(s.A), d.End(s.B), s.Type, s.Type is null ? null : ConnectionTypes.Label(s.Type), s.Confidence, EvidenceOf(s.Evidence),
        s.DiffersFrom, s.State, s.ConnectionId, s.DecidedByName, s.DecidedAt, s.FirstSeenAt, s.LastSeenAt);

    public static IReadOnlyList<string> Sources(InterfaceSources sources) =>
        Enum.GetValues<InterfaceSources>().Where(s => s != InterfaceSources.None && sources.HasFlag(s)).Select(s => s.ToString()).ToList();

    public static NetworkInterfaceItem Interface(NetworkInterfaceEntity i, NetworkDirectory d)
    {
        string? matchName = null;
        if (i.MatchKind == MatchKind.RouterPort && i.MatchSensorId is { } r && d.Routers.TryGetValue(r, out var router))
        {
            matchName = router.Name;
        }
        else if (i.MatchHostId is { } h && d.Hosts.TryGetValue(h, out var host))
        {
            matchName = host.Name;
        }
        return new NetworkInterfaceItem(
            i.Mac, i.Random, i.Ignored, i.Ips, i.HostName, Sources(i.Sources),
            i.AttachedSensorId, i.AttachedSensorId is { } a && d.Routers.TryGetValue(a, out var at) ? at.Name : null, i.AttachedPort, i.AttachedVlan, i.AttachedNote,
            i.NeighborIdentity, i.NeighborPlatform, i.NeighborBoard, i.NeighborPort,
            i.MatchKind, i.MatchHostId, i.MatchSensorId, matchName, i.MatchInterface,
            i.HintHostId, i.HintHostId is { } hint && d.Hosts.TryGetValue(hint, out var hinted) ? hinted.Name : null,
            i.AssignedByName, i.AssignedAt, i.FirstSeenAt, i.LastSeenAt,
            i.SkippedRuleId, i.SkippedRuleId is { } rule && d.SkipRules.TryGetValue(rule, out var skipped) ? skipped : null);
    }

    public static PortFacts? Sfp(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<PortFacts>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyDictionary<string, T>? Map<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, T>>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static NetworkScanRow Scan(NetworkScanEntity s, int? estimatedSecondsLeft = null) => new(
        s.Id, s.Scope, s.ScopeName, s.LocationId, s.SensorId, s.Trigger, s.RequestedByName, s.State, s.Total, s.Succeeded, s.Failed, s.Skipped,
        s.CurrentRouterName, s.CurrentStep, s.CurrentStepNumber, NetworkScanScript.Order.Count, s.CreatedAt, s.StartedAt, s.FinishedAt, estimatedSecondsLeft);

    public static NetworkScanItemRow ScanItem(NetworkScanItemEntity i) => new(
        i.Id, i.SensorId, i.RouterName, i.Position, i.State, i.Step, i.StartedAt, i.FinishedAt, i.Error, Map<string>(i.TableErrors), Map<int>(i.Counts),
        i.DurationMs);
}
