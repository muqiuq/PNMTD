using System.Text.Json;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Services.Domain;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Write;

/// <summary>
/// A router's join-script differences acknowledged as intended (docs/mcp-extensions-plan.md §4; docs/mikrotik-join-options-plan.md §3).
/// The admin's differences (the policies, the management rule, retired keys) are refused by the service: a token is never an admin.
/// </summary>
public static class RouterWriteTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("router_difference_acknowledge", "Acknowledge a router's difference",
            "Acknowledges differences between a MikroTik router and its join script as intended, with a reason, so their warning goes until what " +
            "they cover changes (acknowledge); or ends one acknowledgement, so the warning is back (end). The ids are router_get's. Differences " +
            "marked adminOnly are an admin's, in PNMT.",
            Schema(Id("sensorId", "The MIKROTIK sensor"), Choice("action", "acknowledge or end", ["acknowledge", "end"], true),
                Texts("ids", "acknowledge: the differences' ids"),
                Text("reason", "acknowledge: why the difference is intended, required"), Text("id", "end: the difference's id")),
            Area.Monitoring, Level.Edit, ToolKind.Write, AcknowledgeAsync)
        {
            AuditEvents = [AuditEvents.MikrotikDifferenceAcknowledged, AuditEvents.MikrotikDifferenceAcknowledgementEnded], Idempotent = true,
        },
    ];

    private static async Task<object> AcknowledgeAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var routers = ctx.Services.GetRequiredService<MikrotikRouterService>();
        MikrotikRouterDetail detail;
        switch (OptionalText(args, "action", 16))
        {
            case "acknowledge":
                var ids = OptionalTexts(args, "ids", 500, 200) is { Count: > 0 } given ? given
                    : throw ToolException.Invalid("ids must be a list of the differences' ids.");
                detail = await routers.AcknowledgeAsync(id, new MikrotikDifferenceAcknowledgeRequest(ids, TextOf(args, "reason", 1, 500)), ct);
                ctx.Summary = $"acknowledged {ids.Count} difference{(ids.Count == 1 ? "" : "s")} of {detail.Identity ?? "a router"}";
                break;
            case "end":
                AuditScope.Current?.UseEvent(AuditEvents.MikrotikDifferenceAcknowledgementEnded);
                detail = await routers.UnacknowledgeAsync(id, TextOf(args, "id", 1, 200), ct);
                ctx.Summary = $"ended an acknowledgement of {detail.Identity ?? "a router"}";
                break;
            default:
                throw ToolException.Invalid("action is acknowledge or end.");
        }
        ctx.Target("Sensor", id, detail.Identity);
        return Actions.CommandTools.Router(ctx, id, detail);
    }
}
