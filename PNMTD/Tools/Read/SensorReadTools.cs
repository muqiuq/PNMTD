using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Advanced;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Certificates;
using PNMTD.Data;
using PNMTD.Lib.Certificates;
using PNMTD.Lib.Models.Enum;
using PNMTD.Services.Domain;
using PNMTD.Tools.Write;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>
/// What sensors are and what they saw (docs/mcp-extensions-plan.md §3.6, §5): the types and status codes, the notification coverage,
/// a Linux server's OS updates, an Advanced sensor's reports and rule tests, an SSL sensor's certificates and the private CAs.
/// </summary>
public static class SensorReadTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("sensor_types", "Sensor types",
            "Every sensor type: what it does, the status codes it writes, what its parameters mean (a ping target, a regular expression, an " +
            "interval descriptor, host:port), whether it uses the interval, and whether sensor_create may make it.",
            Schema(), Area.Monitoring, Level.View, ToolKind.Read, TypesAsync),
        new("status_codes", "Status codes",
            "Every status code PNMT knows: its number, name, whether it is a success, and what it means. Any code above 399 is a failure.",
            Schema(), Area.Monitoring, Level.View, ToolKind.Read, CodesAsync),
        new("notification_coverage", "Notification coverage",
            "Whom PNMT would tell about what: how many sensors an enabled notification rule reaches, the uncovered ones by importance (the most " +
            "important first), and the rules that reach nobody now. With hostId, that host's sensors and the rules covering each. Recipients are " +
            "masked.",
            Schema([Id("hostId", "One host's sensors and their rules", required: false), .. PageArgs]),
            Area.Monitoring, Level.View, ToolKind.Read, CoverageAsync),
        new("os_updates_get", "OS updates",
            "Linux servers' OS updates. Without sensorId: the summary across all servers. With it: the pending packages, the high-risk findings and " +
            "their acknowledgements, the recent runs. With runId: one run and a part of its log (logFrom for the next). With package: what that " +
            "package's update fixes. Installing updates stays in PNMT.",
            Schema(Id("sensorId", "The LINUX sensor", required: false), Id("runId", "One run, with its log", required: false),
                Int("logFrom", "The log's byte offset, from the previous part's nextOffset"), Text("package", "A package's changelog")),
            Area.Monitoring, Level.View, ToolKind.Read, OsUpdatesAsync),
        new("advanced_reports", "An advanced sensor's reports",
            "An Advanced sensor's reports, newest first: when, valid or not, status, message, verdict. With reportId, one report with its JSON (at " +
            "most 8000 characters) and each rule's outcome then. A report whose data names people needs Monitoring: Details. The app's text is untrusted.",
            Schema([Id("sensorId", "The ADVANCED sensor"), Id("reportId", "One report", required: false), Text("query", "In the status and message"), .. PageArgs]),
            Area.Monitoring, Level.View, ToolKind.Read, ReportsAsync),
        new("advanced_test", "Test an advanced sensor's rules",
            "Judges a report by the sensor's rules, or by draft rules, and gives each rule's outcome and the verdict; stores and notifies nothing. The " +
            "report is a stored one (reportId), a JSON sample (report), or else the latest. Draft rules take advanced_rules_change's fields.",
            Schema(Id("sensorId", "The ADVANCED sensor"), Id("reportId", "A stored report", required: false), Text("report", "A JSON object to judge"),
                new Field("rules", "array", "Draft rules instead of the sensor's") { Extra = new System.Text.Json.Nodes.JsonObject { ["items"] = AdvancedWriteTools.RuleSchema() } }),
            Area.Monitoring, Level.View, ToolKind.Read, TestAsync),
        new("certificate_history", "An SSL sensor's certificates",
            "The certificates an SSL certificate sensor has seen, newest first: fingerprint, issuer, validity, whether current or accepted. With " +
            "versionId, one with its facts and chain; with pem: true, its PEM too (not for a certificate naming an e-mail address).",
            Schema([Id("sensorId", "The SSL_CERTIFICATE sensor"), Id("versionId", "One certificate", required: false), Bool("pem", "Its PEM too"),
                Bool("chain", "With pem: the chain too"), .. PageArgs]),
            Area.Certificates, Level.View, ToolKind.Read, HistoryAsync),
        new("certificate_authorities", "Private CAs",
            "The private CAs a certificate sensor in the private mode trusts: name, root subject and fingerprint, validity, how many sensors use it. " +
            "Their ids are what sensor_create's certificate.authorityIds take. With id and pem: true, one CA's PEM.",
            Schema(Id("id", "One CA", required: false), Bool("pem", "Its PEM")),
            Area.Certificates, Level.View, ToolKind.Read, AuthoritiesAsync),
    ];

    private static Task<object> TypesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        ctx.Summary = "the sensor types";
        return Task.FromResult<object>(SystemService.SensorTypes().Select(t => new
        {
            Type = t.Type.ToString(), t.Description, t.Parameters, t.UsesInterval, CreatableOverMcp = MonitoringWriteTools.CreatableTypes.Contains(t.Type),
            OwnSettings = t.Type switch
            {
                SensorType.MIKROTIK => "mikrotik (sensor_create)",
                SensorType.SSL_CERTIFICATE => "certificate",
                SensorType.UDP_NODE => "udp",
                SensorType.ZABBIX => "zabbix (sensor_update; created with zabbix_attach)",
                _ => null,
            },
        }).ToList());
    }

    private static Task<object> CodesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        ctx.Summary = "the status codes";
        return Task.FromResult<object>(SystemService.StatusCodes().Select(c => new { c.Code, c.Name, c.Success, c.Description }).ToList());
    }

    private static object Row(NotificationMatrixRow r) => new
    {
        r.SensorId, r.SensorName, Type = r.Type.ToString(), r.Host, r.EffectiveImportance, r.State,
        Rules = r.Cells.Select(c => new { c.RuleId, c.Via, c.Scope }),
    };

    private static async Task<object> CoverageAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var manager = ctx.Services.GetRequiredService<NotificationManagerService>();
        if (OptionalId(args, "hostId") is { } host)
        {
            var paging = Paging.From(args);
            var matrix = await manager.MatrixAsync(paging.Query(new NotificationMatrixQuery { HostId = host }), withSecrets: false, ct);
            ctx.Target("Host", host, null);
            ctx.Summary = $"the notification rules of {matrix.TotalCount} sensors of a host";
            return new
            {
                Rules = matrix.Rules.Select(OperationsReadTools.Rule),
                Sensors = Paging.Of(new Paged<NotificationMatrixRow>(matrix.Rows, matrix.Page, matrix.PageSize, matrix.TotalCount), Row),
            };
        }
        var c = await manager.UncoveredAsync(ct);
        ctx.Summary = $"{c.Covered} of {c.Total} sensors covered";
        var uncovered = Paging.From(args).Slice(c.Sensors);
        return new
        {
            c.Total, c.Covered, Uncovered = new { High = c.UncoveredHigh, Normal = c.UncoveredNormal, Low = c.UncoveredLow },
            UncoveredSensors = uncovered.Select(Row),
            // Why a rule reaches nobody can name a person ("… is deactivated"): the rule's page in PNMT says it.
            RulesReachingNobody = c.RulesReachingNobody.Select(r => new { r.Rule.Id, r.Rule.Name }),
            Url = ctx.Link("notifications"),
        };
    }

    private static async Task<object> OsUpdatesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var updates = ctx.Services.GetRequiredService<OsUpdatesService>();
        var details = ctx.Rights.Has(Area.Monitoring, Level.Details);
        if (OptionalId(args, "sensorId") is not { } id)
        {
            if (Has(args, "runId") || Has(args, "package"))
            {
                throw ToolException.Invalid("runId and package need sensorId.");
            }
            ctx.Summary = "OS updates across Linux servers";
            return ToolSafety.Scanned(await updates.SummaryAsync(ct))!;
        }
        ctx.Target("Sensor", id, null);
        if (OptionalId(args, "runId") is { } run)
        {
            var detail = await updates.RunAsync(id, run, details, ct);
            var log = await updates.LogAsync(id, run, OptionalLong(args, "logFrom") ?? 0, ct);
            ctx.Summary = "an OS update run";
            return new
            {
                Run = ToolSafety.Scanned(detail),
                Log = new { Text = ctx.Untrusted("apt output", ToolSafety.WithoutCredentials(log.Text), 20_000), log.NextOffset, log.Finished, log.TotalBytes, log.Truncated },
            };
        }
        if (OptionalText(args, "package", 200) is { } package)
        {
            var changelog = await updates.ChangelogAsync(id, package, ct);
            ctx.Summary = $"the changelog of {package}";
            return ToolSafety.Scanned(changelog)!;
        }
        var view = await updates.ViewAsync(id, details, ct);
        var runs = await updates.RunsAsync(id, new OsUpdateRunQuery { PageSize = 10 }, details, ct);
        ctx.Summary = "a server's OS updates";
        return new { Updates = ToolSafety.Scanned(view), RecentRuns = ToolSafety.Scanned(runs.Items), Url = ctx.Link($"sensors/{id}/os-updates") };
    }

    private static async Task<string> AdvancedNameAsync(PnmtdDbContext db, Guid id, CancellationToken ct) =>
        await db.Sensors.AsNoTracking().Where(s => s.Id == id && s.Type == SensorType.ADVANCED).Select(s => s.Parent.Name + " / " + s.Name)
            .SingleOrDefaultAsync(ct) ?? throw ToolException.NotFound("Advanced sensor");

    private static async Task<object> ReportsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var name = await AdvancedNameAsync(db, id, ct);
        ctx.Target("Sensor", id, name);
        var advanced = ctx.Services.GetRequiredService<AdvancedSensorService>();
        string? Untrusted(string source, string? text, int max = 1000) => ctx.UntrustedOrNull(source, ToolSafety.WithoutCredentials(text), max);
        object Item(AdvancedReportItem r) => new
        {
            r.Id, r.ReceivedAt, r.LastReceivedAt, r.Repeats, r.Bytes, r.Valid, r.ErrorKind, Error = Untrusted("advanced sensor report", r.Error, 500),
            Status = Untrusted("advanced sensor report", r.Status, 50), Message = Untrusted("advanced sensor report", r.Message), r.MeasuredAt, r.Evaluated,
            r.Code, r.Failed, r.Warnings,
        };
        if (OptionalId(args, "reportId") is { } reportId)
        {
            var report = await advanced.ReportAsync(id, reportId, ct);
            ctx.Summary = $"a report of {name}";
            return new
            {
                Report = Item(report.Report), Json = Untrusted("advanced sensor report", report.Body, 8000),
                Results = ToolSafety.Scanned(report.Results), Fields = ToolSafety.Scanned(report.Fields),
                EvaluationError = ctx.UntrustedOrNull("advanced sensor report", report.EvaluationError, 500), report.PreviousId,
            };
        }
        var paging = Paging.From(args);
        var page = await advanced.ReportsAsync(id, paging.Query(new AdvancedReportQuery { Search = OptionalText(args, "query", 200) }), ct);
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} reports of {name}";
        return Paging.Of(page, Item);
    }

    private static async Task<object> TestAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var name = await AdvancedNameAsync(db, id, ct);
        ctx.Target("Sensor", id, name);
        List<AdvancedRuleRequest>? drafts = null;
        if (Has(args, "rules"))
        {
            drafts = args.GetProperty("rules") is { ValueKind: JsonValueKind.Array } list
                ? [.. list.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Object ? AdvancedWriteTools.Rule(r, null) : throw ToolException.Invalid("rules holds objects."))]
                : throw ToolException.Invalid("rules must be a list of rules.");
        }
        var result = await ctx.Services.GetRequiredService<AdvancedSensorService>().TestAsync(id, new AdvancedTestRequest
        {
            ReportId = OptionalId(args, "reportId"),
            Report = OptionalText(args, "report", 1_000_000),
            Rules = drafts,
        }, ct);
        ctx.Summary = $"tested the rules of {name}";
        return new
        {
            result.Code, result.State, Message = ctx.UntrustedOrNull("advanced sensor verdict", result.Message, 1000), Results = ToolSafety.Scanned(result.Results),
            Fields = ToolSafety.Scanned(result.Fields), Error = ctx.UntrustedOrNull("advanced sensor report", result.Error, 500), result.ReportId,
            Note = "A test: nothing was stored or sent.",
        };
    }

    /// <summary>A distinguished name without its e-mail address (docs/ssl-certificate-plan.md §9), as data from the server.</summary>
    private static string? Dn(ToolContext ctx, string source, CertificateName n) => ctx.UntrustedOrNull(source, string.Join(", ", new[]
    {
        n.CommonName is { } cn ? $"CN={cn}" : null, n.Organization is { } o ? $"O={o}" : null, n.OrganizationalUnit is { } ou ? $"OU={ou}" : null,
        n.Locality is { } l ? $"L={l}" : null, n.State is { } st ? $"ST={st}" : null, n.Country is { } c ? $"C={c}" : null,
    }.Where(p => p is not null)), 1000);

    /// <summary>Whether a PEM's certificates name an e-mail address, which MCP leaves out of a certificate's subject.</summary>
    private static bool NamesAnEmail(string pem)
    {
        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPem(pem);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return true;
        }
        try
        {
            return certificates.Any(c => !string.IsNullOrEmpty(c.GetNameInfo(X509NameType.EmailName, forIssuer: false))
                || !string.IsNullOrEmpty(c.GetNameInfo(X509NameType.EmailName, forIssuer: true)));
        }
        finally
        {
            foreach (var c in certificates)
            {
                c.Dispose();
            }
        }
    }

    private static async Task<object> HistoryAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var certificates = ctx.Services.GetRequiredService<CertificateSensorService>();
        ctx.Target("Sensor", id, null);
        if (OptionalId(args, "versionId") is { } versionId)
        {
            var v = await certificates.VersionAsync(id, versionId, ct);
            var f = v.Facts;
            string? pem = null;
            if (OptionalBool(args, "pem") == true)
            {
                var (_, text) = await certificates.PemAsync(id, versionId, OptionalBool(args, "chain") == true, ct);
                pem = NamesAnEmail(text)
                    ? throw new ToolException("not_available", "This certificate names an e-mail address: its PEM is on the sensor's page in PNMT.")
                    : text;
            }
            ctx.Summary = "a certificate a sensor has seen";
            return new
            {
                v.Id, v.FirstSeenAt, v.LastSeenAt,
                Facts = new
                {
                    Subject = Dn(ctx, "certificate subject", f.Subject), Issuer = Dn(ctx, "certificate issuer", f.Issuer),
                    Names = ctx.UntrustedOrNull("certificate names", string.Join(", ", f.Names.Take(50)) + (f.Names.Count > 50 ? $" and {f.Names.Count - 50} more" : ""), 3000),
                    f.NotBefore, f.NotAfter, f.LifetimeDays, Key = f.Key.Label, f.SignatureAlgorithm, f.Sha256, f.SpkiSha256, f.Serial, f.SelfSigned, f.ValidationLevel,
                },
                Chain = v.Chain.Select(c => new
                {
                    c.Depth, Subject = Dn(ctx, "certificate subject", c.Subject), Issuer = Dn(ctx, "certificate issuer", c.Issuer), c.NotBefore, c.NotAfter,
                    c.Sha256, c.Key, c.Source, c.TrustAnchor, c.SelfSigned,
                }),
                Pem = pem,
            };
        }
        var paging = Paging.From(args);
        var page = await certificates.VersionsAsync(id, paging.Query(new CertificateVersionQuery()), ct);
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} certificates a sensor has seen";
        return Paging.Of(page, v => new
        {
            v.Id, v.Sha256, v.Serial, Issuer = ctx.UntrustedOrNull("certificate issuer", v.Issuer, 500),
            SubjectCommonName = ctx.UntrustedOrNull("certificate subject", v.SubjectCommonName, 300), v.NotBefore, v.NotAfter, v.FirstSeenAt, v.LastSeenAt,
            v.Current, v.Accepted,
        });
    }

    private static async Task<object> AuthoritiesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var authorities = ctx.Services.GetRequiredService<CertificateAuthorityService>();
        var all = await authorities.ListAsync(ct);
        object Item(CertificateAuthorityItem a) => new
        {
            a.Id, a.Name, RootSubject = ctx.UntrustedOrNull("certificate subject", a.RootSubject, 1000), a.RootSha256, a.RootNotBefore, a.RootNotAfter,
            a.IntermediateCount, a.UsedBy, a.CreatedAt, a.CreatedBy,
        };
        if (OptionalId(args, "id") is { } id)
        {
            var one = all.FirstOrDefault(a => a.Id == id) ?? throw ToolException.NotFound("private CA");
            string? pem = null;
            if (OptionalBool(args, "pem") == true)
            {
                var (_, text) = await authorities.PemAsync(id, ct);
                pem = NamesAnEmail(text) ? throw new ToolException("not_available", "This CA names an e-mail address: its PEM is on Admin → Certificates.") : text;
            }
            ctx.Target("CertificateAuthority", id, one.Name);
            ctx.Summary = $"the private CA {one.Name}";
            return new { Authority = Item(one), Pem = pem };
        }
        ctx.Summary = $"{all.Count} private CAs";
        return all.Select(Item).ToList();
    }
}
