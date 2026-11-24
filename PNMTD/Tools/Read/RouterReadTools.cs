using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>
/// MikroTik routers beyond <c>router_get</c> (docs/mcp-extensions-plan.md §3.2, §4): the direct-SSH join script, the update jobs,
/// a release's changelog, and one's RouterOS commands. Through the same services as the router's page.
/// </summary>
public static class RouterReadTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("router_join_script", "A router's join script",
            "The join script of a MIKROTIK router in direct SSH mode, to paste into the router's terminal or import as a .rsc file. It holds PNMT's " +
            "public key, and the router makes its own random password for the user pnmt, so nothing in it is secret. A router in a WireGuard mode " +
            "has its WireGuard private key in its script: that one is on the sensor's page in PNMT only. Every view is in the audit log.",
            Schema(Id("sensorId", "The MIKROTIK sensor"),
                Bool("firewall", "PNMT's firewall rules in the script; left out: as the router's script was last shown"),
                Bool("removals", "Remove what an earlier script created and this one doesn't need; left out: as last shown")),
            Area.Monitoring, Level.Edit, ToolKind.Read, JoinScriptAsync),
        new("router_jobs", "A router's update jobs",
            "A MikroTik router's RouterOS update jobs, newest first; with jobId, one job with its log. Commands are mikrotik_commands_list's.",
            Schema([Id("sensorId", "The MIKROTIK sensor"), Id("jobId", "One job, with its log", required: false),
                Choice("state", "Only jobs in this state", Enum.GetNames<MikrotikJobState>()), .. PageArgs]),
            Area.Monitoring, Level.View, ToolKind.Read, JobsAsync),
        new("router_changelog", "A RouterOS release's changelog",
            "What a RouterOS release brings, from MikroTik's update server: by default the newest release of the router's channel, the one router_get " +
            "says is available.",
            Schema(Id("sensorId", "The MIKROTIK sensor"), Text("version", "A RouterOS version, as 7.25.1; left out: the newest of the router's channel")),
            Area.Monitoring, Level.View, ToolKind.Read, ChangelogAsync) { OpenWorld = true },
        new("mikrotik_commands_list", "RouterOS commands",
            "The RouterOS commands run with mikrotik_command_run, newest first, by router, batch and state. An admin's runs stay hidden, as in PNMT. " +
            "mikrotik_command_get gives one's output.",
            Schema([Id("sensorId", "Only this router's", required: false), Id("batchId", "Only this batch", required: false),
                Choice("state", "Only commands in this state", Enum.GetNames<MikrotikJobState>()), .. PageArgs]),
            Area.Commands, Level.Run, ToolKind.Read, CommandsAsync),
    ];

    private static async Task<object> JoinScriptAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var mode = await db.MikrotikRouters.AsNoTracking().Where(r => r.SensorId == id).Select(r => (MikrotikMode?)r.Mode).SingleOrDefaultAsync(ct)
            ?? throw ToolException.NotFound("MikroTik router");
        if (mode.UsesWireGuard())
        {
            throw new ToolException("not_available",
                "This router's join script holds its WireGuard private key: it is on the sensor's page in PNMT, never over MCP.");
        }
        var script = await ctx.Services.GetRequiredService<MikrotikRouterService>()
            .ScriptAsync(id, removal: false, OptionalBool(args, "firewall"), OptionalBool(args, "removals"), ct);
        if (script.Script.Contains("private-key", StringComparison.OrdinalIgnoreCase))
        {
            // A direct-SSH script has no tunnel; should one ever carry a key, it stays inside all the same.
            throw new ToolException("not_available", "This join script holds a private key: it is on the sensor's page in PNMT.");
        }
        ctx.Target("Sensor", id, null);
        ctx.Summary = "the join script of a router (direct SSH)";
        return new
        {
            SensorId = id, script.FileName, Mode = script.Mode.ToString(), script.GeneratedAt, script.Notes, script.Options, script.Script,
            Next = "Paste it into the router's terminal, or import it as a .rsc file; the router answers PNMT's next poll.",
            Url = ctx.Link($"sensors/{id}"),
        };
    }

    private static async Task<object> JobsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var jobs = ctx.Services.GetRequiredService<MikrotikJobService>();
        await ctx.Services.GetRequiredService<MikrotikRouterService>().EnsureRouterAsync(id, ct);
        ctx.Target("Sensor", id, null);
        if (OptionalId(args, "jobId") is { } jobId)
        {
            var job = await jobs.GetAsync(jobId, id, ct);
            ctx.Summary = $"update job of {job.Job.RouterName}";
            // The log's messages and output come from the router.
            return ToolSafety.Scanned(job)!;
        }
        var paging = Paging.From(args);
        var page = await jobs.ListAsync(paging.Query(new MikrotikJobQuery
        {
            SensorId = id, Kind = MikrotikJobKind.Update, State = OptionalEnum<MikrotikJobState>(args, "state"),
        }), ct);
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} update jobs";
        return ToolSafety.Scanned(Paging.Of(page, j => j))!;
    }

    private static async Task<object> ChangelogAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var version = OptionalText(args, "version", 32);
        if (version is null)
        {
            var router = await ctx.Services.GetRequiredService<MikrotikRouterService>().DetailAsync(id, ct) ?? throw ToolException.NotFound("MikroTik router");
            version = router.LatestVersion ?? throw new ToolException("not_available",
                "PNMT doesn't know the newest release of this router's channel yet: give a version.");
        }
        else
        {
            await ctx.Services.GetRequiredService<MikrotikRouterService>().EnsureRouterAsync(id, ct);
        }
        var changelog = await ctx.Services.GetRequiredService<MikrotikJobService>().ChangelogAsync(version, ct);
        ctx.Target("Sensor", id, null);
        ctx.Summary = $"RouterOS {changelog.Version}'s changelog";
        return new { changelog.Version, Changelog = ctx.Untrusted("RouterOS changelog", changelog.Text, 20_000) };
    }

    private static async Task<object> CommandsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var page = await ctx.Services.GetRequiredService<MikrotikCommandService>().ListAsync(paging.Query(new MikrotikCommandQuery
        {
            SensorId = OptionalId(args, "sensorId"), BatchId = OptionalId(args, "batchId"), State = OptionalEnum<MikrotikJobState>(args, "state"),
        }), ct);
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} RouterOS commands";
        return ToolSafety.Scanned(Paging.Of(page, c => c))!;
    }
}
