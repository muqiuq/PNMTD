using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Services.Domain;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Delete;

/// <summary>
/// Deleting (docs/mcp-plan.md §7.5): only in the window a person opens on Account → Security, one object per call, counted
/// before it runs (<see cref="Mcp.McpCalls"/>). The object is read first and goes into the audit as it was, field by
/// field, because the services' bulk deletes leave the change tracker out. Never what needs <i>Manage</i>.
/// </summary>
public static class DeleteTools
{
    private const string Window = " Only while the person allows deleting on Account → Security in PNMT (10 minutes, at most 20 objects); otherwise delete_not_allowed.";

    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("sensor_delete", "Delete a sensor", "Deletes a sensor with its events and rule links. System and diagnostics sensors can't be deleted." + Window,
            Schema(Id("id", "The sensor's id")), Area.Monitoring, Level.Edit, ToolKind.Delete, SensorAsync) { AuditEvents = [AuditEvents.SensorDeleted] },
        new("host_delete", "Delete a host", "Deletes a host that has no sensors left (delete them first, each counts)." + Window,
            Schema(Id("id", "The host's id")), Area.Monitoring, Level.Edit, ToolKind.Delete, HostAsync) { AuditEvents = [AuditEvents.HostDeleted] },
        new("mail_rule_delete", "Delete a mail input rule", "Deletes a mail input rule." + Window,
            Schema(Id("id", "The rule's id")), Area.Monitoring, Level.Edit, ToolKind.Delete, MailRuleAsync) { AuditEvents = [AuditEvents.MailInputRuleDeleted] },
        // docs/network-map-plan.md §10.2: one connection of the network map.
        new("network_connection_delete", "Delete a network connection", "Deletes one connection of the network map, as it was recorded." + Window,
            Schema(Id("id", "The connection's id")), Area.NetworkMap, Level.Edit, ToolKind.Delete, NetworkConnectionAsync)
        { AuditEvents = [AuditEvents.NetworkConnectionDeleted] },
    ];

    /// <summary>The object as it was, field by field, into the audit's changes (field: value → deleted).</summary>
    private static void AsItWas(object snapshot)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, ToolResults.Json)!.AsObject();
        foreach (var (name, value) in node)
        {
            if (value is not null && name != "url")
            {
                AuditLog.Change(name, value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString(), null);
            }
        }
    }

    private static async Task<object> SensorAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var was = await db.Sensors.AsNoTracking().Where(s => s.Id == id).Select(s => new
        {
            s.Id, s.Name, Type = s.Type.ToString(), HostId = s.ParentId, Host = s.Parent.Name, s.Enabled, s.Ignore, s.Interval, s.GracePeriod, s.Parameters, s.Notes,
            s.Importance, s.Created,
        }).SingleOrDefaultAsync(ct) ?? throw ToolException.NotFound("sensor");
        var snapshot = was with { Parameters = ToolSafety.WithoutCredentials(was.Parameters) };
        AsItWas(snapshot);
        await ctx.Services.GetRequiredService<SensorService>().DeleteAsync(id, ct);
        ctx.Target("Sensor", id, $"{was.Host} / {was.Name}");
        ctx.Summary = $"deleted sensor {was.Host} / {was.Name} ({was.Type})";
        return new { Deleted = snapshot };
    }

    private static async Task<object> NetworkConnectionAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var service = ctx.Services.GetRequiredService<Network.NetworkConnectionService>();
        Api.V1.Contracts.NetworkConnectionItem was;
        try
        {
            was = await service.GetAsync(id, ct);
        }
        catch (Api.ApiException e) when (e.Status == 404)
        {
            throw ToolException.NotFound("connection");
        }
        var snapshot = new
        {
            was.Id, A = $"{was.A.Name} {was.A.Interface}".Trim(), B = $"{was.B.Name} {was.B.Interface}".Trim(), was.Type, was.Notes, was.Source, was.CreatedAt,
            was.CreatedByName,
        };
        AsItWas(snapshot);
        await service.DeleteAsync(id, ct);
        ctx.Target("NetworkConnection", id, $"{snapshot.A} ↔ {snapshot.B}");
        ctx.Summary = $"deleted connection {snapshot.A} ↔ {snapshot.B} ({was.TypeLabel})";
        return new { Deleted = snapshot };
    }

    private static async Task<object> HostAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var was = await db.Hosts.AsNoTracking().Where(h => h.Id == id).Select(h => new
        {
            h.Id, h.Name, h.Location, h.LocationId, h.Notes, h.Enabled, h.Importance, h.Created,
        }).SingleOrDefaultAsync(ct) ?? throw ToolException.NotFound("host");
        var sensors = await db.Sensors.AsNoTracking().Where(s => s.ParentId == id).OrderBy(s => s.Name).Select(s => new { s.Id, s.Name, Type = s.Type.ToString() })
            .ToListAsync(ct);
        if (sensors.Count > 0)
        {
            throw new ToolException("host_has_sensors", $"{was.Name} still has {sensors.Count} sensors. Delete them first (sensor_delete), or the person deletes the host in PNMT.",
                new Dictionary<string, object?> { ["sensors"] = sensors });
        }
        AsItWas(was);
        await ctx.Services.GetRequiredService<HostService>().DeleteAsync(id, ct);
        ctx.Target("Host", id, was.Name);
        ctx.Summary = $"deleted host {was.Name}";
        return new { Deleted = was };
    }

    private static async Task<object> MailRuleAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var mail = ctx.Services.GetRequiredService<MailService>();
        var was = await mail.GetRuleAsync(id, ct);
        var snapshot = new
        {
            was.Id, was.Name, was.Enabled, was.FromTest, was.SubjectTest, was.BodyTest, was.OkTest, was.OkCode, was.FailTest, was.FailCode, was.DefaultCode,
            was.ExtractMessageRegex, SensorId = was.Sensor?.Id, was.Action, was.AskAiWhenUnsure, was.VerifyAi,
        };
        AsItWas(snapshot);
        await mail.DeleteRuleAsync(id, ct);
        ctx.Target("MailInputRule", id, was.Name);
        ctx.Summary = $"deleted mail input rule {was.Name}";
        return new { Deleted = snapshot };
    }
}
