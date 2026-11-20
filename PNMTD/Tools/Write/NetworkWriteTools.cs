using System.Text.Json;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Network;
using PNMTD.Network;
using PNMTD.Tools.Read;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Write;

/// <summary>
/// Writing the network map over MCP (docs/network-map-plan.md §10.2), with the read-write token: connections, deciding a
/// suggestion, and assigning an interface to a host that exists. No tool creates a host (decision 6): <c>host_create</c> is
/// there for that, when a person asks for it.
/// </summary>
public static class NetworkWriteTools
{
    private const string End = "a router (its sensor's id) or a host (its id), and optionally the port or adapter";

    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("network_connection_create", "Add a network connection",
            $"Adds a connection between two ends, each {End}, with its type (a code from network_summary's media, as baset-1g, dac-10g, mmf-10g, " +
            "smf-10g, wifi, wireguard, tunnel, vm, other-physical, other-virtual) and notes.",
            Schema(Id("aRouterId", "End A: a router", required: false), Id("aHostId", "End A: a host", required: false), Text("aInterface", "End A's port or adapter"),
                Id("bRouterId", "End B: a router", required: false), Id("bHostId", "End B: a host", required: false), Text("bInterface", "End B's port or adapter"),
                Text("type", "The type's code", true), Text("notes", "Notes")),
            Area.NetworkMap, Level.Edit, ToolKind.Write, CreateAsync) { AuditEvents = [AuditEvents.NetworkConnectionCreated] },
        new("network_connection_update", "Change a network connection",
            "Changes a connection's ports, type or notes. Its two devices stay: another pair is another connection.",
            Schema(Id("id", "The connection"), Text("aInterface", "End A's port"), Text("bInterface", "End B's port"), Text("type", "The type's code"),
                Text("notes", "Notes")),
            Area.NetworkMap, Level.Edit, ToolKind.Write, UpdateAsync) { AuditEvents = [AuditEvents.NetworkConnectionUpdated] },
        new("network_suggestion_decide", "Accept or dismiss a suggestion",
            "accept makes a suggestion a connection, as proposed or with another type, ports or notes; dismiss sets it aside, and it stays dismissed while " +
            "the scans keep finding it. One suggestion per call.",
            Schema(Id("id", "The suggestion"), Choice("decision", "accept or dismiss", ["accept", "dismiss"], true), Text("type", "Another type's code"),
                Text("aInterface", "Another port for end A"), Text("bInterface", "Another port for end B"), Text("notes", "Notes for the connection")),
            Area.NetworkMap, Level.Edit, ToolKind.Write, DecideAsync) { AuditEvents = [AuditEvents.NetworkSuggestionAccepted, AuditEvents.NetworkSuggestionDismissed] },
        new("network_interface_assign", "Assign a network interface to a host",
            "Assigns a MAC the routers see to a host that exists, for devices without an agent (printers, APs, cameras); hostId left out clears the " +
            "assignment. The suggestions follow in a few seconds.",
            Schema(Text("mac", "The MAC, as AA:BB:CC:DD:EE:FF", true), Id("hostId", "The host; left out to clear", required: false), Text("interface", "The interface's name on the host")),
            Area.NetworkMap, Level.Edit, ToolKind.Write, AssignAsync) { AuditEvents = [AuditEvents.NetworkInterfaceAssigned, AuditEvents.NetworkInterfaceCleared] },
    ];

    private static NetworkEndRequest EndOf(JsonElement args, string side) => new()
    {
        SensorId = OptionalId(args, $"{side}RouterId"),
        HostId = OptionalId(args, $"{side}HostId"),
        Interface = OptionalText(args, $"{side}Interface", NetworkConnectionService.MaxInterface),
    };

    private static async Task<object> CreateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var made = await ctx.Services.GetRequiredService<NetworkConnectionService>().CreateAsync(new NetworkConnectionRequest
        {
            A = EndOf(args, "a"),
            B = EndOf(args, "b"),
            Type = OptionalText(args, "type", 40) ?? "",
            Notes = OptionalText(args, "notes", NetworkConnectionService.MaxNotes),
        }, ct);
        ctx.Target("NetworkConnection", made.Id, $"{made.A.Name} ↔ {made.B.Name}");
        ctx.Summary = $"added connection {made.A.Name} ↔ {made.B.Name} ({made.TypeLabel})";
        return NetworkReadTools.Connection(ctx, made);
    }

    private static async Task<object> UpdateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "id");
        var changed = await ctx.Services.GetRequiredService<NetworkConnectionService>().UpdateAsync(id, new NetworkConnectionUpdate
        {
            AInterface = OptionalText(args, "aInterface", NetworkConnectionService.MaxInterface),
            BInterface = OptionalText(args, "bInterface", NetworkConnectionService.MaxInterface),
            Type = OptionalText(args, "type", 40),
            Notes = OptionalText(args, "notes", NetworkConnectionService.MaxNotes),
        }, ct);
        ctx.Target("NetworkConnection", id, $"{changed.A.Name} ↔ {changed.B.Name}");
        ctx.Summary = $"changed connection {changed.A.Name} ↔ {changed.B.Name}";
        return NetworkReadTools.Connection(ctx, changed);
    }

    private static async Task<object> DecideAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "id");
        var service = ctx.Services.GetRequiredService<NetworkConnectionService>();
        switch (OptionalText(args, "decision", 10))
        {
            case "accept":
                var connection = await service.AcceptAsync(id, new NetworkSuggestionAccept
                {
                    Type = OptionalText(args, "type", 40),
                    AInterface = OptionalText(args, "aInterface", NetworkConnectionService.MaxInterface),
                    BInterface = OptionalText(args, "bInterface", NetworkConnectionService.MaxInterface),
                    Notes = OptionalText(args, "notes", NetworkConnectionService.MaxNotes),
                }, ct);
                ctx.Target("NetworkSuggestion", id, $"{connection.A.Name} ↔ {connection.B.Name}");
                ctx.Summary = $"accepted a suggestion: {connection.A.Name} ↔ {connection.B.Name} ({connection.TypeLabel})";
                return new { Accepted = id, Connection = NetworkReadTools.Connection(ctx, connection) };
            case "dismiss":
                AuditScope.Current?.UseEvent(AuditEvents.NetworkSuggestionDismissed);
                await service.DismissAsync([id], ct);
                ctx.Target("NetworkSuggestion", id, null);
                ctx.Summary = "dismissed a suggestion";
                return new { Dismissed = id };
            default:
                throw ToolException.Invalid("decision is accept or dismiss.");
        }
    }

    private static async Task<object> AssignAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var mac = OptionalText(args, "mac", 40) ?? throw ToolException.Invalid("mac is needed.");
        var service = ctx.Services.GetRequiredService<NetworkConnectionService>();
        NetworkInterfaceItem item;
        if (OptionalId(args, "hostId") is { } hostId)
        {
            item = await service.AssignAsync(mac, new NetworkInterfaceAssignment { HostId = hostId, Interface = OptionalText(args, "interface", NetworkConnectionService.MaxInterface) }, ct);
            ctx.Summary = $"assigned {item.Mac} to {item.MatchName}";
        }
        else
        {
            AuditScope.Current?.UseEvent(AuditEvents.NetworkInterfaceCleared);
            item = await service.ClearAsync(mac, ct);
            ctx.Summary = $"cleared {item.Mac}'s assignment";
        }
        ctx.Target("NetworkInterface", item.Mac, item.Mac);
        return NetworkReadTools.Interface(ctx, item);
    }
}
