using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>
/// The rest of what people read in PNMT's daily operation (docs/mcp-plan.md §7.2): history, notification rules, the uplink,
/// DNS zones, mail, servers with an agent, router backups, diagnostics, the knowledge base's revisions and tags, and Cookie's
/// decisions and trace. Recipients masked, mails and logs as untrusted text, nothing from a sensitive page or revision.
/// </summary>
public static partial class OperationsReadTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("history_get", "An object's history",
            "Who changed what on a host, a sensor or a location, newest first, as its History tab shows it (without addresses). Paged.",
            Schema([Id("id", "The host's, sensor's or location's id"), .. PageArgs]), Area.Monitoring, Level.View, ToolKind.Read, HistoryAsync),
        new("notification_rules_list", "Notification rules",
            "The notification rules: name, type, whether on, how many sensors; or the rules of one sensor. Recipients are masked.",
            Schema([Id("sensorId", "Only this sensor's rules", required: false), .. PageArgs]), Area.Monitoring, Level.View, ToolKind.Read, RulesAsync),
        new("uplink_get", "The uplink", "PNMT's own internet uplink: up or down, since when, the endpoints, and the outages of the last days.",
            Schema(Int("days", "Outages of the last this many days, 1 to 90; 7 when left out")), Area.Monitoring, Level.View, ToolKind.Read, UplinkAsync),
        new("dns_zones_list", "DNS zones", "The DNS zones PNMT checks: whether their records match, mismatches, when last checked. Paged.",
            Schema(PageArgs), Area.Monitoring, Level.View, ToolKind.Read, DnsZonesAsync),
        new("mail_rules_list", "Mail input rules", "The rules that turn incoming mails into sensors' events, without mail contents. Paged.",
            Schema(PageArgs), Area.Monitoring, Level.View, ToolKind.Read, MailRulesAsync),
        new("mails_search", "The mail log", "Incoming mails: sender, recipient, subject, whether a rule processed them and which. Paged.",
            Schema([Text("query", "Words in the sender, recipient or subject"), Bool("processed", "Only processed (true) or unprocessed (false) mails"), .. PageArgs]),
            Area.Mail, Level.Read, ToolKind.Read, MailsAsync),
        new("mail_get", "A mail", "One incoming mail with its content and how it was processed. The content is outside text.",
            Schema(Id("id", "The mail's id")), Area.Mail, Level.Read, ToolKind.Read, MailAsync),
        new("servers_list", "Servers with an agent",
            "Windows and Linux servers with PNMT's agent: online, their state, OS, agent version and whether it is outdated. Paged.",
            Schema([Choice("platform", "Only Windows or Linux", Enum.GetNames<Lib.Agent.AgentPlatform>()), Bool("connected", "Only connected (true) or not (false)"),
                Bool("outdated", "Only outdated agents"), Text("query", "Part of the name"), .. PageArgs]),
            Area.Monitoring, Level.View, ToolKind.Read, ServersAsync),
        new("server_get", "A server with an agent",
            "A server: its agent, OS, heartbeat, recent problems and changes; with section, one section the agent reports (as services, disks, " +
            "updates). Sections with logins, the event log, the journal or SSH keys need Monitoring Edit.",
            Schema(Id("sensorId", "The WINDOWS or LINUX sensor"), Text("section", "One section, by its key")),
            Area.Monitoring, Level.View, ToolKind.Read, ServerAsync),
        new("router_backup_diff", "A router backup's changes",
            "What changed in a MikroTik router's configuration between two backups (the latest against the one before when left out). Never a backup " +
            "with secrets.",
            Schema(Id("sensorId", "The MIKROTIK sensor"), Id("backupId", "The newer backup; the latest when left out", required: false),
                Id("against", "The older backup; the one before when left out", required: false)),
            Area.Monitoring, Level.Edit, ToolKind.Read, BackupDiffAsync),
        new("diagnostics_runs", "Diagnostics runs", "Diagnostics runs, newest first, with their outcome; with id, one run's checks and results.",
            Schema([Id("id", "One run", required: false), Id("sensorId", "Only this server's runs", required: false), .. PageArgs]),
            Area.Monitoring, Level.View, ToolKind.Read, DiagnosticsAsync),
        new("kb_revisions", "A page's revisions",
            "A knowledge base page's revisions: who, when, what changed; with from and to, the difference between two. Never a page or a revision " +
            "marked sensitive.",
            Schema(Int("number", "The page's number", true), Int("from", "The older revision"), Int("to", "The newer revision")),
            Area.KnowledgeBase, Level.View, ToolKind.Read, RevisionsAsync),
        new("kb_tags", "The knowledge base's tags", "The tags of the pages machines may read, with how many pages each has.",
            Schema(Text("query", "Part of the tag")), Area.KnowledgeBase, Level.View, ToolKind.Read, TagsAsync),
        new("cookie_decisions", "Cookie's decisions",
            "What Cookie proposed and what came of it, with Seraph's verdict and people's ratings, by your rights. Paged.",
            Schema([Id("caseId", "Only this case's", required: false), .. PageArgs]), Area.Cookie, Level.Use, ToolKind.Read, DecisionsAsync),
        new("cookie_trace", "Cookie's trace", "What Cookie did in its rounds (triage, tools, notes, replies), by your rights: the full trace only for those who may see it.",
            Schema(Id("caseId", "Only this case's", required: false), Int("limit", "1 to 200; 50 when left out")), Area.Cookie, Level.Use, ToolKind.Read, TraceAsync),
    ];

    private static System.Security.Claims.ClaimsPrincipal User(ToolContext ctx) =>
        ctx.Services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User ?? throw new ToolException("mcp_only", "Only over MCP.");

    private static PageQuery PageOf(Paging paging) => new() { Page = paging.Offset / paging.Limit + 1, PageSize = paging.Limit };

    private static ToolPage<TOut> Paged<TIn, TOut>(Paged<TIn> page, Func<TIn, TOut> map)
    {
        var more = page.Page * page.PageSize < page.TotalCount;
        return new ToolPage<TOut>([.. page.Items.Select(map)], more ? Paging.Cursor(page.Page * page.PageSize) : null, page.TotalCount);
    }

    private static async Task<object> HistoryAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var paging = Paging.From(args);
        var rows = await db.AuditLog.AsNoTracking().Where(l => l.Subjects.Contains(id)).OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id)
            .Skip(paging.Offset).Take(paging.Limit + 1).ToListAsync(ct);
        ctx.Summary = $"{Math.Min(rows.Count, paging.Limit)} history entries";
        return paging.Of(rows, null).Select(l => new
        {
            l.CreatedAt, l.EventType, Who = l.Actor == Audit.AuditActor.User ? l.Username : $"{l.Username} ({l.Actor})", l.Outcome, l.TargetType, l.TargetName,
            Detail = ctx.UntrustedOrNull("history", l.Detail, 1000), Changes = ToolSafety.Scanned(l.Changes is null ? null : JsonDocument.Parse(l.Changes).RootElement),
        });
    }

    /// <summary>"a…@example.com": who gets a notification is people's contact data (§6.4).</summary>
    public static string MaskRecipient(string recipient) =>
        MailAddress().Replace(recipient, m => $"{m.Groups[1].Value[..1]}…@{m.Groups[2].Value}");

    [GeneratedRegex(@"([A-Za-z0-9._%+-]+)@([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)")]
    private static partial Regex MailAddress();

    private static async Task<object> RulesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var rules = ctx.Services.GetRequiredService<NotificationRuleService>();
        object Rule(NotificationRuleItem r) => new
        {
            r.Id, r.Name, Recipient = MaskRecipient(r.Recipient), r.Provider, Type = r.Type.ToString(), r.Enabled, r.SensorCount, r.ScopeCount, r.Via,
        };
        if (OptionalId(args, "sensorId") is { } sensor)
        {
            var own = await rules.ForSensorAsync(sensor, withSecrets: false, ct);
            ctx.Summary = $"{own.Count} rules of a sensor";
            return own.Select(Rule).ToList();
        }
        var page = await rules.ListAsync(PageOf(Paging.From(args)), withSecrets: false, ct);
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} rules";
        return Paged(page, Rule);
    }

    private static async Task<object> UplinkAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var uplink = ctx.Services.GetRequiredService<UplinkService>();
        var o = await uplink.OverviewAsync(admin: false, ct);
        var outages = await uplink.OutagesAsync(Math.Clamp(IntOf(args, "days", 7), 1, 90), ct);
        ctx.Summary = o.Up ? "the uplink is up" : "the uplink is down";
        return new
        {
            o.Configured, o.Up, o.DownSince, o.LastCheck, o.LastSuccess, o.FailuresInARow, o.FailuresForDown, o.Endpoints, o.PausedTasks, o.SensorId,
            Outages = outages, Url = ctx.Link("uplink"),
        };
    }

    private static async Task<object> DnsZonesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var page = await ctx.Services.GetRequiredService<DnsZoneService>().ListAsync(PageOf(Paging.From(args)), ct);
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} DNS zones";
        return Paged(page, z => (object)z);
    }

    private static async Task<object> MailRulesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var page = await ctx.Services.GetRequiredService<MailService>().ListRulesAsync(PageOf(Paging.From(args)), ct);
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} mail input rules";
        return Paged(page, r => (object)new
        {
            r.Id, r.Name, r.Enabled, r.FromTest, r.SubjectTest, r.BodyTest, r.OkTest, r.OkCode, r.FailTest, r.FailCode, r.DefaultCode, r.ExtractMessageRegex,
            Sensor = r.Sensor is { } s ? new { s.Id, Name = $"{s.HostName} / {s.Name}" } : null, r.Action, r.AskAiWhenUnsure, r.VerifyAi,
        });
    }

    private static async Task<object> MailsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var page = await ctx.Services.GetRequiredService<MailService>().ListLogAsync(new MailLogQuery
        {
            Search = OptionalText(args, "query", 200), Processed = OptionalBool(args, "processed"), Page = paging.Offset / paging.Limit + 1, PageSize = paging.Limit,
        }, ct);
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} mails";
        return Paged(page, m => (object)new
        {
            m.Id, m.Created, m.MessageDate, m.From, m.To, Subject = ctx.Untrusted("mail subject", m.Subject, 300), m.Processed, m.ProcessedBy, m.Disposition, m.AiState,
        });
    }

    private static async Task<object> MailAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var m = await ctx.Services.GetRequiredService<MailService>().GetLogAsync(IdOf(args), ct);
        ctx.Target("Mail", m.Id, m.Subject);
        return new
        {
            m.Id, m.Created, m.MessageDate, m.From, m.To, Subject = ctx.Untrusted("mail subject", m.Subject, 300), m.Processed, m.ProcessedBy, m.Disposition, m.AiState,
            Content = ctx.Untrusted("mail", ToolSafety.WithoutCredentials(m.Content), 12_000), ProcessLog = ctx.UntrustedOrNull("mail processing", m.ProcessLog, 3000),
        };
    }

    private static object Server(ToolContext ctx, AgentServerItem s) => new
    {
        s.SensorId, s.Host, s.Name, Platform = s.Platform.ToString(), s.State, s.LastCode, LastMessage = ctx.UntrustedOrNull("sensor message", s.LastMessage, 300),
        s.Hostname, s.Domain, s.OsCaption, s.OsBuild, s.OsSupported, s.AgentVersion, s.Outdated, s.Connected, s.LastSeenAt, s.BootTime, s.Revoked, s.Uninstalled,
        s.DiagnosticsFailing, s.Distribution, s.KernelRelease, Url = ctx.Link($"sensors/{s.SensorId}"),
    };

    private static async Task<object> ServersAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var page = await ctx.Services.GetRequiredService<AgentServerService>().ListAsync(new AgentServerQuery
        {
            Platform = OptionalEnum<Lib.Agent.AgentPlatform>(args, "platform"), Connected = OptionalBool(args, "connected"),
            Outdated = OptionalBool(args, "outdated") == true ? true : null, Search = OptionalText(args, "query", 200),
            Page = paging.Offset / paging.Limit + 1, PageSize = paging.Limit,
        }, ct);
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} servers";
        return Paged(page, s => Server(ctx, s));
    }

    private static async Task<object> ServerAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var servers = ctx.Services.GetRequiredService<AgentServerService>();
        var canEdit = ctx.Rights.Has(Area.Monitoring, Level.Edit);
        if (OptionalText(args, "section", 64) is { } section)
        {
            var platform = await db.Sensors.AsNoTracking().Where(s => s.Id == id).Select(s => (SensorType?)s.Type).SingleOrDefaultAsync(ct)
                ?? throw ToolException.NotFound("server");
            object view = platform == SensorType.LINUX ? await servers.LinuxSectionAsync(id, section, canEdit, ct) : await servers.SectionAsync(id, section, canEdit, ct);
            ctx.Target("Sensor", id, section);
            ctx.Summary = $"section {section}";
            return ToolSafety.Scanned(view)!;
        }
        var d = await servers.DetailAsync(id, ct);
        var changes = await servers.ChangesAsync(id, new AgentChangeQuery { PageSize = 20 }, ct);
        ctx.Target("Sensor", id, d.Server.Name);
        return new
        {
            Server = Server(ctx, d.Server), d.JoinedAt, d.ConnectedAt, d.DisconnectedAt, d.RemoteAddress, d.Fqdn, d.OsEdition, d.OsDisplayVersion, d.Capabilities,
            d.Heartbeat, RecentProblems = ToolSafety.Scanned(d.RecentProblems), Sections = d.Sections,
            Changes = changes.Items.Select(c => new { c.At, c.Section, Summary = ctx.Untrusted("agent change", c.Summary, 300), c.Notable }),
        };
    }

    private static async Task<object> BackupDiffAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var sensor = IdOf(args, "sensorId");
        var backups = ctx.Services.GetRequiredService<MikrotikBackupService>();
        var backupId = OptionalId(args, "backupId") ?? (await backups.ListAsync(sensor, new MikrotikBackupQuery { PageSize = 1 }, ct)).Items.FirstOrDefault()?.Id
            ?? throw ToolException.NotFound("backup");
        var ids = new[] { backupId, OptionalId(args, "against") ?? Guid.Empty };
        if (await db.MikrotikBackups.AsNoTracking().AnyAsync(b => ids.Contains(b.Id) && (b.IncludesSecrets || b.Kind == MikrotikBackupKind.Binary), ct))
        {
            throw new ToolException("not_available", "That backup holds secrets or is binary: it is compared in PNMT only.");
        }
        var diff = await backups.DiffAsync(sensor, backupId, OptionalId(args, "against"), ct);
        ctx.Target("Sensor", sensor, null);
        ctx.Summary = $"+{diff.Added} −{diff.Removed} lines";
        return new
        {
            From = diff.From is { } f ? new { f.Id, f.CreatedAt } : null, To = new { diff.To.Id, diff.To.CreatedAt }, diff.Added, diff.Removed,
            Diff = ctx.Untrusted("router configuration", ToolSafety.WithoutCredentials(diff.Unified), 30_000),
        };
    }

    private static async Task<object> DiagnosticsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var diagnostics = ctx.Services.GetRequiredService<DiagnosticsSetService>();
        if (OptionalId(args, "id") is { } id)
        {
            var run = await diagnostics.RunAsync(id, ct);
            ctx.Target("DiagnosticsRun", id, run.Run.SetName);
            return ToolSafety.Scanned(run)!;
        }
        var paging = Paging.From(args);
        var page = await diagnostics.RunsAsync(new DiagnosticRunQuery
        {
            TargetSensorId = OptionalId(args, "sensorId"), Page = paging.Offset / paging.Limit + 1, PageSize = paging.Limit,
        }, ct);
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} runs";
        return Paged(page, r => (object)r);
    }

    private static async Task<object> RevisionsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var number = IntOf(args, "number");
        var page = await MachineAccess.Pages(db).Where(p => p.Number == number).Select(p => new { p.Id, p.Number }).SingleOrDefaultAsync(ct)
            ?? throw ToolException.NotFound("page");
        // A revision kept while the page was marked sensitive stays out, as the page did then.
        var sensitive = (await db.KbRevisions.AsNoTracking().Where(r => r.PageId == page.Id && r.Sensitive).Select(r => r.Number).ToListAsync(ct)).ToHashSet();
        var service = ctx.Services.GetRequiredService<KbPageService>();
        ctx.Target("KbPage", page.Id, $"KB-{page.Number}");
        if (Has(args, "from") || Has(args, "to"))
        {
            var from = Has(args, "from") ? IntOf(args, "from") : (int?)null;
            var to = Has(args, "to") ? IntOf(args, "to") : (int?)null;
            if ((from is { } f && sensitive.Contains(f)) || (to is { } t && sensitive.Contains(t)))
            {
                throw ToolException.NotFound("revision");
            }
            var diff = await service.DiffAsync(page.Id, from, to, ct);
            if (sensitive.Contains(diff.From) || sensitive.Contains(diff.To))
            {
                throw ToolException.NotFound("revision");
            }
            ctx.Summary = $"KB-{number}: revision {diff.From} against {diff.To}";
            return new
            {
                diff.From, diff.To, diff.Added, diff.Removed, diff.Changes,
                FromContent = ctx.Untrusted("page", diff.FromContent, 8000), ToContent = ctx.Untrusted("page", diff.ToContent, 8000),
            };
        }
        var revisions = await service.RevisionsAsync(page.Id, ct);
        ctx.Summary = $"{revisions.Count} revisions of KB-{number}";
        return revisions.Where(r => !sensitive.Contains(r.Number)).Select(r => new
        {
            r.Number, r.At, r.ByName, Note = ctx.UntrustedOrNull("revision note", r.Note, 500), r.Added, r.Removed, r.Changes, r.IsEdit,
            Title = ctx.Untrusted("page title", r.Title, 200), r.Status,
        }).ToList();
    }

    private static async Task<object> TagsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var query = OptionalText(args, "query", 100)?.ToLowerInvariant();
        // The tags of readable pages only: a sensitive page's tags can say what it is about.
        var tags = await MachineAccess.Pages(db).Where(p => p.ArchivedAt == null).SelectMany(p => p.Tags.Select(t => t.Tag!.Name))
            .GroupBy(n => n).Select(g => new { Name = g.Key, Pages = g.Count() }).ToListAsync(ct);
        var result = tags.Where(t => query is null || t.Name.Contains(query, StringComparison.Ordinal)).OrderByDescending(t => t.Pages).ThenBy(t => t.Name).Take(200).ToList();
        ctx.Summary = $"{result.Count} tags";
        return result;
    }

    private static async Task<object> DecisionsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var page = await ctx.Services.GetRequiredService<CookieService>().DecisionsAsync(new CookieDecisionQuery
        {
            CaseId = OptionalId(args, "caseId"), Page = paging.Offset / paging.Limit + 1, PageSize = paging.Limit,
        }, User(ctx), ct);
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} of Cookie's decisions";
        return Paged(page, d => (object)new
        {
            d.Id, d.At, d.CaseId, d.CaseTitle, d.Kind, d.Title, d.Text, d.Reason, d.Confidence, d.Verdict, d.VerdictReason, d.Outcome, d.Detail, d.Rating, d.RatingComment,
        });
    }

    private static async Task<object> TraceAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var trace = await ctx.Services.GetRequiredService<CookieService>().TraceAsync(
            new CookieTraceQuery(OptionalId(args, "caseId"), Limit: Math.Clamp(IntOf(args, "limit", 50), 1, 200)), User(ctx), ct);
        ctx.Summary = $"{trace.Items.Count} trace entries";
        return new { trace.Full, Items = trace.Items.Select(i => new { i.Id, i.RoundId, i.CaseId, i.CaseTitle, i.At, i.Kind, i.Title, Body = ToolSafety.Scanned(i.Body) }) };
    }
}
