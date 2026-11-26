using System.Text.Json;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Network;
using PNMTD.Models.Enums;
using PNMTD.Network;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>
/// The network map over MCP (docs/network-map-plan.md §10.2): how the routers are connected to each other and to the servers,
/// the path between two devices, a device's ports, the suggestions and the interfaces. Text the routers' neighbors and clients
/// set (DHCP host names, LLDP identities, port comments) and people's notes come wrapped as untrusted; every result says when
/// the routers were scanned.
/// </summary>
public static class NetworkReadTools
{
    private const string Devices = "A router as router:{sensorId}, a host as host:{hostId}; a bare id is a router if it is one, else a host.";

    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("network_summary", "The network map in numbers",
            "The network map's figures: routers, hosts with connections, connections by medium, physical and virtual, unconfirmed ones (the scans no " +
            "longer show them), open suggestions by confidence, unassigned interfaces, the skip rules that leave rows out of the suggestions; the last " +
            "full scan, a running scan with its progress, the schedule.",
            Schema(), Area.NetworkMap, Level.View, ToolKind.Read, SummaryAsync),
        new("network_connections_list", "Network connections",
            "The map as data: the connections people confirmed, each with both ends (router or host, port, site), its type (medium and speed), " +
            "physical or virtual, and whether the latest scans show it. routersOnly gives only router ↔ router: how the routers are connected.",
            Schema([Id("locationId", "Ends in this location or below it", required: false), Text("device", Devices),
                Bool("routersOnly", "Only connections between two routers"), Bool("physical", "true: physical only; false: virtual only"),
                Text("type", "A type's code, as smf-10g"), Bool("unconfirmed", "Only those the scans no longer show"),
                Text("search", "Names, ports, notes"), .. PageArgs]),
            Area.NetworkMap, Level.View, ToolKind.Read, ConnectionsAsync),
        new("network_device_get", "A router or host on the network map",
            "A router or a host as its side panel: its connections with the far ends; for a router its ports from the scan (type, rate, link, " +
            "proposed type, role, how many MACs) and its last scan; for a server its adapters. The interfaces seen on its ports need Network map: Details.",
            Schema(Text("device", Devices, true)), Area.NetworkMap, Level.View, ToolKind.Read, DeviceAsync),
        new("network_path_get", "How two devices are connected",
            "The shortest path between two devices over the connections (never over suggestions), each hop with its ends, ports and type; the slowest hop, " +
            "the virtual hops, and the connections without which there is no other path (single points of failure).",
            Schema(Text("from", Devices, true), Text("to", Devices, true), Bool("physicalOnly", "Leave the virtual connections (WireGuard, tunnels) out")),
            Area.NetworkMap, Level.View, ToolKind.Read, PathAsync),
        new("network_suggestions_list", "Connection suggestions",
            "What the scans propose as connections, open by default, with their confidence and evidence. Only a person makes them connections.",
            Schema([Choice("state", "Open (the default), Accepted or Dismissed", Enum.GetNames<NetworkSuggestionState>()),
                Choice("confidence", "High, Medium or Low", Enum.GetNames<Confidence>()), Text("device", Devices),
                Id("locationId", "Ends in this location or below it", required: false), Text("search", "Names, ports, evidence"), .. PageArgs]),
            Area.NetworkMap, Level.View, ToolKind.Read, SuggestionsAsync),
        new("network_interfaces_search", "Network interfaces",
            "Where a MAC (or its start), an IP or a DHCP host name is attached: the router, port and VLAN; what it is matched to (a server's adapter, a " +
            "router's port, a host by hand); the skip rule that keeps it out of the suggestions, if any; first and last seen.",
            Schema([Text("search", "A MAC, its start, an IP or a host name"), Id("routerId", "Attached at this router", required: false),
                Bool("unassigned", "Only those matched to nothing"), .. PageArgs]),
            Area.NetworkMap, Level.Details, ToolKind.Read, InterfacesAsync),
    ];

    // --- results ---------------------------------------------------------------------------------------------------------------

    internal static object End(ToolContext ctx, NetworkEndItem e)
    {
        var id = e.SensorId ?? e.HostId;
        var kind = e.Kind == NetworkDeviceKind.Router ? "router" : "host";
        return new { Kind = e.Kind, Id = id, Device = $"{kind}:{id}", e.Name, Port = e.Interface, e.Site, Url = ctx.Link($"network-map?focus={kind}:{id}") };
    }

    internal static object Connection(ToolContext ctx, NetworkConnectionItem c) => new
    {
        c.Id,
        A = End(ctx, c.A),
        B = End(ctx, c.B),
        Type = new { Code = c.Type, Label = c.TypeLabel },
        c.Physical,
        Scan = new { State = c.Scan, c.ConfirmedAt, c.UnconfirmedSince },
        Notes = ctx.UntrustedOrNull("connection notes", c.Notes, 1000),
        Url = ctx.Link($"network-map?focus=connection:{c.Id}"),
    };

    internal static object Suggestion(ToolContext ctx, NetworkSuggestionItem s) => new
    {
        s.Id,
        s.Kind,
        A = End(ctx, s.A),
        B = End(ctx, s.B),
        Type = s.Type is null ? null : new { Code = s.Type, Label = s.TypeLabel },
        s.Confidence,
        // The sentences quote what devices call themselves: data, never instructions.
        Evidence = s.Evidence.Select(e => new { e.Rule, e.Confidence, Text = ctx.Untrusted("scan evidence", e.Text, 600) }).ToList(),
        s.DiffersFrom,
        s.State,
        s.FirstSeenAt,
        s.LastSeenAt,
        Url = ctx.Link($"network-map?tab=suggestions&id={s.Id}"),
    };

    internal static object Interface(ToolContext ctx, NetworkInterfaceItem i) => new
    {
        i.Mac,
        i.Random,
        i.Ips,
        HostName = ctx.UntrustedOrNull("DHCP host name", i.HostName, 200),
        Attached = i.AttachedSensorId is null ? null : new { RouterId = i.AttachedSensorId, Router = i.AttachedRouter, Port = i.AttachedPort, Vlan = i.AttachedVlan },
        Note = i.AttachedNote,
        Neighbor = i.NeighborIdentity is null && i.NeighborPlatform is null ? null : new
        {
            Identity = ctx.UntrustedOrNull("neighbor identity", i.NeighborIdentity, 200),
            Platform = ctx.UntrustedOrNull("neighbor platform", i.NeighborPlatform, 200),
            Board = ctx.UntrustedOrNull("neighbor board", i.NeighborBoard, 200),
            Port = i.NeighborPort,
        },
        Match = new { Kind = i.Match, HostId = i.MatchHostId, Name = i.MatchName, Interface = i.MatchInterface },
        Hint = i.HintHostId is null ? null : new { HostId = i.HintHostId, Name = i.HintHostName },
        SkippedBy = i.SkippedRuleId is null ? null : new { RuleId = i.SkippedRuleId, Name = ctx.UntrustedOrNull("skip rule name", i.SkippedRule, 200) },
        i.Sources,
        i.FirstSeenAt,
        i.LastSeenAt,
    };

    private static async Task<ConnectionEnd> DeviceOfAsync(ToolContext ctx, string? text, string name, CancellationToken ct)
    {
        var d = await ctx.Services.GetRequiredService<NetworkMapService>().DirectoryAsync(ct);
        return d.ParseDevice(text) ?? throw ToolException.Invalid($"{name}: {Devices}");
    }

    private static Guid DeviceId(ConnectionEnd end) => end.SensorId ?? end.HostId;

    // --- the tools -------------------------------------------------------------------------------------------------------------

    private static async Task<object> SummaryAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var s = await ctx.Services.GetRequiredService<NetworkMapService>().SummaryAsync(ct);
        ctx.Summary = $"{s.Connections} connections, {s.OpenSuggestions.Values.Sum()} open suggestions";
        return new
        {
            s.Routers,
            s.HostsWithConnections,
            Connections = new { Total = s.Connections, s.Physical, s.Virtual, s.Unconfirmed, s.ByMedium },
            s.OpenSuggestions,
            s.UnassignedInterfaces,
            // What the analysis leaves out of the suggestions (docs/network-suggestion-rules-plan.md §4): names people gave.
            SkipRules = (s.SkipRules ?? []).Select(name => ctx.Untrusted("skip rule name", name, 200)).ToList(),
            Scans = new
            {
                Running = s.Scans.Running is { } r ? new
                {
                    r.ScopeName, Router = r.CurrentRouterName, Step = r.CurrentStep, Done = r.Succeeded + r.Failed + r.Skipped, r.Total,
                    Left = r.Total - r.Succeeded - r.Failed - r.Skipped, r.EstimatedSecondsLeft,
                } : null,
                Queued = s.Scans.Queued.Count,
                LastFullScan = s.Scans.LastFull?.CreatedAt,
                s.Scans.ScheduleEnabled,
                NextScheduled = s.Scans.NextScheduledAt,
            },
            Url = ctx.Link("network-map"),
        };
    }

    private static async Task<object> ConnectionsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var device = OptionalText(args, "device", 100) is { } text ? DeviceId(await DeviceOfAsync(ctx, text, "device", ct)) : (Guid?)null;
        var list = await ctx.Services.GetRequiredService<NetworkConnectionService>().FilterAsync(new NetworkConnectionQuery
        {
            LocationId = OptionalId(args, "locationId"),
            DeviceId = device,
            RoutersOnly = OptionalBool(args, "routersOnly"),
            Physical = OptionalBool(args, "physical"),
            Type = OptionalText(args, "type", 40),
            Unconfirmed = OptionalBool(args, "unconfirmed"),
            Search = OptionalText(args, "search", 200),
        }, ct);
        var page = paging.Slice(list);
        ctx.Summary = $"{page.Items.Count} of {list.Count} connections";
        var lastFull = await NetworkScanSchedule.LastFullAsync(db, ct);
        return new { Items = page.Items.Select(c => Connection(ctx, c)).ToList(), page.NextCursor, page.Total, LastFullScan = lastFull };
    }

    private static async Task<object> DeviceAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var end = await DeviceOfAsync(ctx, OptionalText(args, "device", 100), "device", ct);
        var kind = end.SensorId is null ? NetworkDeviceKind.Host : NetworkDeviceKind.Router;
        var d = await ctx.Services.GetRequiredService<NetworkMapService>().DeviceAsync(kind, DeviceId(end), ctx.Rights.Has(Area.NetworkMap, Level.Details), ct);
        ctx.Target(kind == NetworkDeviceKind.Router ? "Sensor" : "Host", DeviceId(end), d.Name);
        ctx.Summary = $"{d.Name}: {d.Connections.Count} connections";
        return new
        {
            d.Id,
            d.Kind,
            d.Name,
            Host = d.HostName,
            Location = d.LocationPath,
            d.State,
            d.Model,
            d.Version,
            d.Os,
            Scan = d.Scan is { } s ? new { s.ScannedAt, s.State, s.Error, FailedTables = s.TableErrors } : null,
            Connections = d.Connections.Select(c => Connection(ctx, c)).ToList(),
            Ports = d.Ports.Select(p => new
            {
                p.Name, p.Type, p.Mac, Link = p.Running, p.Disabled, p.RateMbps, p.Bridge, Vlan = p.VlanId is null ? null : new { Parent = p.VlanParent, Id = p.VlanId }, p.Bond,
                ProposedType = p.DetectedType is null ? null : new { Code = p.DetectedType, Label = p.DetectedTypeLabel }, p.Role, Macs = p.MacCount,
                Module = p.Sfp is { SfpModulePresent: true } m ? new { m.SfpConnector, m.SfpWavelength, Vendor = ctx.UntrustedOrNull("SFP vendor", m.SfpVendor, 100), m.SfpPartNumber } : null,
                Comment = ctx.UntrustedOrNull("port comment", p.Comment, 300), p.ConnectedTo, p.ConnectionId,
            }).ToList(),
            Adapters = d.Adapters,
            Interfaces = d.Interfaces?.Select(i => Interface(ctx, i)).ToList(),
            InterfacesNote = d.Interfaces is null ? "The interfaces on its ports need Network map: Details." : null,
            d.InterfacesSeen,
            d.InterfacesUnassigned,
            d.OpenSuggestions,
            Url = ctx.Link($"network-map?focus={d.Id}"),
        };
    }

    private static async Task<object> PathAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var from = OptionalText(args, "from", 100) ?? throw ToolException.Invalid("from is needed.");
        var to = OptionalText(args, "to", 100) ?? throw ToolException.Invalid("to is needed.");
        await DeviceOfAsync(ctx, from, "from", ct);
        await DeviceOfAsync(ctx, to, "to", ct);
        var path = await ctx.Services.GetRequiredService<NetworkMapService>().PathAsync(from, to, OptionalBool(args, "physicalOnly") == true, ct)
            ?? throw new ToolException("no_path", "No path between the two over the connections people confirmed (within 12 hops). The suggestions may know more: network_suggestions_list.");
        ctx.Summary = $"{path.From.Name} → {path.To.Name}: {path.Hops.Count} hops";
        return new
        {
            From = End(ctx, path.From),
            To = End(ctx, path.To),
            Hops = path.Hops.Select(h => new
            {
                h.ConnectionId,
                From = new { h.From.Name, Port = h.From.Interface },
                To = new { h.To.Name, Port = h.To.Interface },
                Type = new { Code = h.Type, Label = h.TypeLabel },
                h.Physical,
                Scan = new { State = h.Scan, At = h.ConfirmedAt },
            }).ToList(),
            path.Slowest,
            path.VirtualHops,
            path.SinglePoints,
            LastFullScan = path.LastFullScanAt,
        };
    }

    private static async Task<object> SuggestionsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var device = OptionalText(args, "device", 100) is { } text ? DeviceId(await DeviceOfAsync(ctx, text, "device", ct)) : (Guid?)null;
        var list = await ctx.Services.GetRequiredService<NetworkConnectionService>().FilterSuggestionsAsync(new NetworkSuggestionQuery
        {
            State = OptionalEnum<NetworkSuggestionState>(args, "state"),
            Confidence = OptionalEnum<Confidence>(args, "confidence"),
            DeviceId = device,
            LocationId = OptionalId(args, "locationId"),
            Search = OptionalText(args, "search", 200),
        }, ct);
        var page = paging.Slice(list);
        ctx.Summary = $"{page.Items.Count} of {list.Count} suggestions";
        return new { Items = page.Items.Select(s => Suggestion(ctx, s)).ToList(), page.NextCursor, page.Total };
    }

    private static async Task<object> InterfacesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var (items, total) = await ctx.Services.GetRequiredService<NetworkConnectionService>().InterfaceSliceAsync(new NetworkInterfaceQuery
        {
            Search = OptionalText(args, "search", 200),
            RouterId = OptionalId(args, "routerId"),
            Unassigned = OptionalBool(args, "unassigned"),
        }, paging.Offset, paging.Limit + 1, ct);
        var page = paging.Of(items, total);
        ctx.Summary = $"{page.Items.Count} of {total} interfaces";
        return new { Items = page.Items.Select(i => Interface(ctx, i)).ToList(), page.NextCursor, page.Total };
    }
}
