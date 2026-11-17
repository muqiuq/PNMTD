using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Actions;

/// <summary>
/// Commands to devices (docs/mcp-plan.md §7.4): UDP nodes, MikroTik routers, agents, diagnostics. <i>Commands: Run</i>, the
/// read-write token only, through the same services as the endpoints. Long work returns an id to ask about later.
/// MikroTik commands run as PNMT's own user only: a token never has MikroTik Manage.
/// </summary>
public static class CommandTools
{
    public const int MaxRouters = 10;

    public static readonly IReadOnlyList<ToolSpec> All =
    [
        // --- reading, for what the commands did --------------------------------------------------------------------------
        new("udp_node_get", "A UDP node", "A UDP sensor's node: its device id, last packets, refused packets, and its commands with their states and results.",
            Schema(Id("sensorId", "The UDP sensor"), Int("limit", "Commands, 1 to 50; 10 when left out")), Area.Monitoring, Level.View, ToolKind.Read, UdpNodeAsync),
        new("udp_command_get", "A UDP command", "One UDP command: its state, attempts, the result's status and bytes (hex), and its log.",
            Schema(Id("sensorId", "The UDP sensor"), Id("commandId", "The command")), Area.Monitoring, Level.View, ToolKind.Read, UdpCommandAsync),
        new("router_get", "A MikroTik router",
            "A MikroTik router: its state, RouterOS and firmware versions, resources, last poll and error, its latest backup, and whether an update is there.",
            Schema(Id("sensorId", "The MIKROTIK sensor")), Area.Monitoring, Level.View, ToolKind.Read, RouterAsync),
        new("certificate_get", "An SSL certificate",
            "An SSL certificate sensor (docs/ssl-certificate-plan.md §9): its endpoint, trust and rules, its state, the current certificate's main " +
            "facts (subject, names, issuer, validity, key, fingerprint, validation), a change waiting to be accepted, and the last error. Accepting a " +
            "change is a person's decision, on the sensor's page in PNMT.",
            Schema(Id("sensorId", "The SSL_CERTIFICATE sensor")), Area.Certificates, Level.View, ToolKind.Read, CertificateAsync),
        new("mikrotik_command_get", "A MikroTik command's result",
            "A command run with mikrotik_command_run: one router's output and log (id), or a batch's results grouped by outcome (batchId).",
            Schema(Id("id", "One router's command", required: false), Id("batchId", "The batch", required: false)),
            Area.Commands, Level.Run, ToolKind.Read, MikrotikCommandAsync),

        // --- actions ------------------------------------------------------------------------------------------------------
        new("udp_command_queue", "Send a UDP command",
            "Queues a command for a UDP node. The node gets it as the answer to its next uplink, so the result comes later: ask udp_command_get. The " +
            "payload's bytes are the device's firmware's to define.",
            Schema(Id("sensorId", "The UDP sensor"), Text("payloadHex", "The payload as hex, as 0103ff; or payloadBase64"), Text("payloadBase64", "The payload as base64"),
                Choice("delivery", "ResultRequired (when left out) or FireAndForget", Enum.GetNames<UdpCommandDelivery>()),
                Int("expiresInHours", "Given up after this many hours; PNMT's default when left out"), Int("maxAttempts", "Sent at most this often, 1 to 100")),
            Area.Commands, Level.Run, ToolKind.Action, UdpQueueAsync) { AuditEvents = [AuditEvents.UdpCommandQueued], Destructive = true },
        new("udp_command_cancel", "Cancel a UDP command", "Cancels a command that is still queued.",
            Schema(Id("sensorId", "The UDP sensor"), Id("commandId", "The command")), Area.Commands, Level.Run, ToolKind.Action, UdpCancelAsync)
        { AuditEvents = [AuditEvents.UdpCommandCancelled], Idempotent = true },
        new("router_poll", "Poll a MikroTik router now", "Asks the router for its state now, and returns it.",
            Schema(Id("sensorId", "The MIKROTIK sensor")), Area.Commands, Level.Run, ToolKind.Action, RouterPollAsync) { AuditEvents = [AuditEvents.MikrotikPollRequested], Idempotent = true },
        new("router_backup_now", "Back a MikroTik router up now", "Takes a backup of the router's configuration now; a new version is kept only if it changed.",
            Schema(Id("sensorId", "The MIKROTIK sensor")), Area.Commands, Level.Run, ToolKind.Action, RouterBackupAsync) { AuditEvents = [AuditEvents.MikrotikBackupRequested], Idempotent = true },
        new("certificate_check_now", "Check an SSL certificate now", "Reads the sensor's certificate now, and returns it as certificate_get does.",
            Schema(Id("sensorId", "The SSL_CERTIFICATE sensor")), Area.Certificates, Level.Edit, ToolKind.Action, CertificateCheckAsync)
        { AuditEvents = [AuditEvents.CertificateCheckRequested], Idempotent = true },
        new("mikrotik_command_run", "Run a RouterOS command",
            $"Runs RouterOS commands on 1 to {MaxRouters} routers, as PNMT's own user on them (never as their admin), with an optional backup before and " +
            "after. The commands run as jobs: ask mikrotik_command_get with the batch id.",
            Schema(Ids("sensorIds", $"The MIKROTIK sensors, 1 to {MaxRouters}"), Text("command", "The RouterOS commands, one per line", true),
                Int("timeoutSeconds", "Per router"), Bool("backupBeforeAndAfter", "A backup before and after, to see what changed"),
                Bool("oneAtATime", "Router after router, stopping at the first failure")),
            Area.Commands, Level.Run, ToolKind.Action, MikrotikRunAsync) { AuditEvents = [AuditEvents.MikrotikCommandStarted], Destructive = true },
        new("server_collect_now", "Collect a server's data now", "Asks a Windows or Linux agent to send its sections now (all, or the ones named).",
            Schema(Id("sensorId", "The WINDOWS or LINUX sensor"),
                new Field("sections", "array", "The sections, as services, disks; all when left out") { Extra = new System.Text.Json.Nodes.JsonObject { ["items"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" } } }),
            Area.Commands, Level.Run, ToolKind.Action, CollectAsync) { AuditEvents = [AuditEvents.AgentCollectRequested], Idempotent = true },
        new("diagnostics_run", "Run a diagnostics set now", "Runs a diagnostics assignment on its server now; the result is in diagnostics_runs.",
            Schema(Id("assignmentId", "The diagnostics assignment")), Area.Commands, Level.Run, ToolKind.Action, DiagnosticsAsync) { AuditEvents = [AuditEvents.DiagnosticsRun] },
    ];

    private static object Command(ToolContext ctx, UdpCommandItem c) => new
    {
        c.Id, c.SensorId, c.DeviceId, c.NodeName, c.CmdId, PayloadHex = Convert.ToHexString(c.Payload), c.State, c.Delivery, c.Attempts, c.MaxAttempts, c.Created,
        c.CreatedBy, c.ExpiresAt, c.LastSentAt, c.CompletedAt, c.ResultStatus, c.ResultStatusText, ResultHex = c.Result is null ? null : Convert.ToHexString(c.Result),
        Note = ctx.UntrustedOrNull("command note", c.Note, 500),
    };

    private static async Task<object> UdpNodeAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var node = await db.UdpNodes.AsNoTracking().Where(n => n.SensorId == id).Select(n => new { n.DeviceId, n.Sensor.Name, Host = n.Sensor.Parent.Name })
            .SingleOrDefaultAsync(ct) ?? throw ToolException.NotFound("UDP node");
        var commands = await ctx.Services.GetRequiredService<UdpCommandService>().ListAsync(id,
            new UdpCommandQuery { PageSize = Math.Clamp(IntOf(args, "limit", 10), 1, 50) }, ct);
        var packets = (await db.UdpLastPackets.AsNoTracking().Where(p => p.SensorId == id).OrderByDescending(p => p.ReceivedAt).Take(10)
                .Select(p => new { p.ReceivedAt, p.Type, p.Endpoint, p.Suite, p.Fcnt, p.Payload }).ToListAsync(ct))
            .Select(p => new { p.ReceivedAt, Type = p.Type.ToString(), p.Endpoint, Suite = p.Suite.ToString(), p.Fcnt, PayloadHex = p.Payload is null ? null : Convert.ToHexString(p.Payload) })
            .ToList();
        ctx.Target("Sensor", id, $"{node.Host} / {node.Name}");
        return new
        {
            SensorId = id, node.Name, node.Host, node.DeviceId, LastPackets = packets, Commands = commands.Items.Select(c => Command(ctx, c)),
            Url = ctx.Link($"sensors/{id}/commands"),
        };
    }

    private static async Task<object> UdpCommandAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var sensor = IdOf(args, "sensorId");
        var commandId = IdOf(args, "commandId");
        var service = ctx.Services.GetRequiredService<UdpCommandService>();
        var command = (await service.ListAsync(sensor, new UdpCommandQuery { PageSize = PageQuery.MaxPageSize }, ct)).Items.FirstOrDefault(c => c.Id == commandId)
            ?? throw ToolException.NotFound("command");
        var log = await service.CommandLogAsync(sensor, commandId, ct);
        ctx.Target("UdpCommand", commandId, null);
        return new { Command = Command(ctx, command), Log = log.Select(l => new { l.Created, l.Kind, l.Direction }) };
    }

    private static object Router(ToolContext ctx, Guid sensorId, MikrotikRouterDetail r) => new
    {
        SensorId = sensorId, r.Mode, r.SetupState, r.Address, r.Identity, r.Model, r.BoardName, r.Version, r.Channel, r.LatestVersion, r.UpdateAvailable,
        r.SecurityFix, r.FirmwareCurrent, r.FirmwareUpgrade, r.BootedAt, r.Resources, r.LastPollAt, r.LastSuccessAt, r.FailingSince,
        LastError = ctx.UntrustedOrNull("router error", r.LastError, 500), r.Warnings, r.LastExportAt, r.BackupCount, r.BackupFailures, r.LowStorage,
        LatestBackup = r.LatestBackup is { } b ? new { b.Id, b.CreatedAt, b.Kind, b.IncludesSecrets } : null,
        Tunnel = r.Tunnel is { } t ? new { t.Up, t.LatestHandshake, t.GatewayUp } : null,
        Url = ctx.Link($"sensors/{sensorId}"),
    };

    private static async Task<object> RouterAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var detail = await ctx.Services.GetRequiredService<MikrotikRouterService>().DetailAsync(id, ct) ?? throw ToolException.NotFound("MikroTik router");
        ctx.Target("Sensor", id, detail.Identity);
        return Router(ctx, id, detail);
    }

    private static async Task<object> CertificateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var detail = await ctx.Services.GetRequiredService<Certificates.CertificateSensorService>().DetailAsync(id, ct);
        ctx.Target("Sensor", id, $"{detail.Summary.Host}:{detail.Summary.Port}");
        return Certificate(ctx, id, detail);
    }

    private static async Task<object> CertificateCheckAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var detail = await ctx.Services.GetRequiredService<Certificates.CertificateSensorService>().CheckNowAsync(id, ct);
        ctx.Target("Sensor", id, $"{detail.Summary.Host}:{detail.Summary.Port}");
        ctx.Summary = $"checked the certificate of {detail.Summary.Host}:{detail.Summary.Port}";
        return Certificate(ctx, id, detail);
    }

    /// <summary>
    /// What the tools say of a certificate (docs/ssl-certificate-plan.md §9): its texts came from the server, so they are
    /// untrusted; an e-mail address in the subject is left out.
    /// </summary>
    private static object Certificate(ToolContext ctx, Guid id, CertificateSensorDetail d)
    {
        var s = d.Summary;
        var f = d.Current?.Facts;
        static string Dn(Lib.Certificates.CertificateName n) => string.Join(", ", new[]
        {
            n.CommonName is { } cn ? $"CN={cn}" : null, n.Organization is { } o ? $"O={o}" : null, n.OrganizationalUnit is { } ou ? $"OU={ou}" : null,
            n.Locality is { } l ? $"L={l}" : null, n.State is { } st ? $"ST={st}" : null, n.Country is { } c ? $"C={c}" : null,
        }.Where(p => p is not null));
        return new
        {
            SensorId = id,
            Endpoint = $"{s.Host}:{s.Port}",
            s.ServerName,
            s.ConnectTo,
            Trust = s.Trust.ToString(),
            PrivateCas = s.Authorities.Select(a => a.Name).ToList(),
            Rules = new
            {
                s.WarnDays, s.CheckExpiry, s.CheckName, s.IncompleteChainFails, FailOnNewCertificate = s.AlertOnCertificateChange,
                FailOnNewKey = s.AlertOnKeyChange, FailOnNewIssuer = s.AlertOnIssuerChange, IssuerMatch = s.IssuerMatch.ToString(),
            },
            s.LastCheckAt,
            s.NextCheckAt,
            s.ExpiresAt,
            s.DaysLeft,
            s.ConsecutiveFailures,
            LastError = ctx.UntrustedOrNull("certificate check", s.LastError, 500),
            Current = f is null ? null : new
            {
                Subject = ctx.UntrustedOrNull("certificate subject", Dn(f.Subject), 1000),
                Names = ctx.UntrustedOrNull("certificate names", string.Join(", ", f.Names.Take(50)) + (f.Names.Count > 50 ? $" and {f.Names.Count - 50} more" : ""), 3000),
                Issuer = ctx.UntrustedOrNull("certificate issuer", Dn(f.Issuer), 1000),
                f.NotBefore, f.NotAfter, Key = f.Key.Label, f.SignatureAlgorithm, f.Sha256, f.SelfSigned, f.ValidationLevel,
            },
            Validation = d.Validation is { } v ? new
            {
                v.Trusted, TrustedBy = ctx.UntrustedOrNull("certificate issuer", v.TrustedBy, 300), v.NameMatches, v.Complete,
                Problems = ctx.UntrustedOrNull("certificate validation", string.Join("; ", v.Problems), 1000),
            } : null,
            Change = d.Change is { } c ? new { c.Alarm, c.Since, Kinds = ctx.UntrustedOrNull("certificate change", string.Join(", ", c.Kinds), 1000) } : null,
            Note = "Accepting a changed certificate is a person's decision, on the sensor's page in PNMT.",
            Url = ctx.Link($"sensors/{id}/certificate"),
        };
    }

    private static async Task<object> MikrotikCommandAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var commands = ctx.Services.GetRequiredService<MikrotikCommandService>();
        if (OptionalId(args, "id") is { } id)
        {
            var d = await commands.GetAsync(id, ct);
            ctx.Target("MikrotikCommand", id, d.Command.RouterName);
            return new
            {
                d.Command.Id, d.Command.BatchId, d.Command.SensorId, d.Command.RouterName, d.Command.State, d.Command.Step, d.Command.ExitStatus,
                Error = ctx.UntrustedOrNull("router error", d.Command.Error, 1000), d.Command.Command, d.Command.RunAs, d.Command.StartedAt, d.Command.FinishedAt,
                Output = ctx.UntrustedOrNull("router output", d.Output, 20_000), d.Command.OutputTruncated, d.Added, d.Removed,
            };
        }
        if (OptionalId(args, "batchId") is { } batch)
        {
            var b = await commands.BatchAsync(batch, ct);
            ctx.Target("MikrotikCommand", batch, null);
            var node = JsonSerializer.SerializeToNode(b, ToolResults.Json)!;
            return node;
        }
        throw ToolException.Invalid("Give id (one router's command) or batchId.");
    }

    private static byte[] PayloadOf(JsonElement args)
    {
        var hex = OptionalText(args, "payloadHex", 2000);
        var b64 = OptionalText(args, "payloadBase64", 2000);
        try
        {
            return hex is not null ? Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal))
                : b64 is not null ? Convert.FromBase64String(b64)
                : throw ToolException.Invalid("Give payloadHex or payloadBase64.");
        }
        catch (FormatException)
        {
            throw ToolException.Invalid(hex is not null ? "payloadHex isn't hex." : "payloadBase64 isn't base64.");
        }
    }

    private static async Task<object> UdpQueueAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var sensor = IdOf(args, "sensorId");
        var created = await ctx.Services.GetRequiredService<UdpCommandService>().CreateAsync(sensor, new UdpCommandRequest
        {
            Payload = PayloadOf(args),
            Delivery = OptionalEnum<UdpCommandDelivery>(args, "delivery") ?? UdpCommandDelivery.ResultRequired,
            ExpiresInHours = Has(args, "expiresInHours") ? IntOf(args, "expiresInHours") : null,
            MaxAttempts = Has(args, "maxAttempts") ? IntOf(args, "maxAttempts") : null,
        }, ct);
        ctx.Target("Sensor", sensor, created.Command.NodeName);
        ctx.Summary = $"queued UDP command {created.Command.CmdId} to {created.Command.NodeName}";
        return new { Command = Command(ctx, created.Command), created.Warnings, Next = "Ask udp_command_get for the result after the node's next uplink." };
    }

    private static async Task<object> UdpCancelAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var sensor = IdOf(args, "sensorId");
        var command = IdOf(args, "commandId");
        await ctx.Services.GetRequiredService<UdpCommandService>().CancelAsync(sensor, command, ct);
        ctx.Target("UdpCommand", command, null);
        ctx.Summary = "cancelled a UDP command";
        return new { Cancelled = command };
    }

    private static async Task<object> RouterPollAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var detail = await ctx.Services.GetRequiredService<MikrotikRouterService>().PollNowAsync(id, ct);
        ctx.Target("Sensor", id, detail.Identity);
        ctx.Summary = $"polled {detail.Identity ?? "a router"}";
        return Router(ctx, id, detail);
    }

    private static async Task<object> RouterBackupAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var result = await ctx.Services.GetRequiredService<MikrotikBackupService>().BackupNowAsync(id, ct);
        ctx.Target("Sensor", id, null);
        ctx.Summary = result.Changed ? "a new backup" : "backed up: no change";
        return new { result.Changed, result.Message, Backup = result.Backup is { } b ? new { b.Id, b.CreatedAt, b.Kind, b.IncludesSecrets } : null };
    }

    private static async Task<object> MikrotikRunAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var routers = IdsOf(args, "sensorIds", MaxRouters);
        var created = await ctx.Services.GetRequiredService<MikrotikCommandService>().CreateAsync(new MikrotikCommandRequest
        {
            SensorIds = routers,
            Command = TextOf(args, "command", 1, 20_000),
            RunAs = MikrotikRunAs.Pnmt,
            TimeoutSeconds = Has(args, "timeoutSeconds") ? IntOf(args, "timeoutSeconds") : null,
            BackupBeforeAndAfter = OptionalBool(args, "backupBeforeAndAfter"),
            OneAtATime = OptionalBool(args, "oneAtATime"),
        }, ct);
        foreach (var job in created.Jobs)
        {
            ctx.Target("Sensor", job.SensorId ?? Guid.Empty, job.RouterName);
        }
        ctx.Summary = $"RouterOS command on {created.Jobs.Count} routers ({created.Refused.Count} refused)";
        return new
        {
            created.BatchId, Jobs = created.Jobs.Select(j => new { j.Id, j.SensorId, j.RouterName, j.State }), created.Refused,
            Next = "Ask mikrotik_command_get with batchId for the results.",
        };
    }

    private static async Task<object> CollectAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        List<string>? sections = null;
        if (Has(args, "sections"))
        {
            var s = args.GetProperty("sections");
            sections = s.ValueKind == JsonValueKind.Array && s.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
                ? [.. s.EnumerateArray().Select(x => x.GetString()!)] : throw ToolException.Invalid("sections must be a list of texts.");
        }
        var result = await ctx.Services.GetRequiredService<AgentServerService>().CollectAsync(id, new AgentCollectRequest(sections), ct);
        ctx.Target("Sensor", id, null);
        ctx.Summary = result.Ok ? "asked the agent to collect" : $"the agent didn't collect: {result.Error}";
        return result;
    }

    private static async Task<object> DiagnosticsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "assignmentId");
        var run = await ctx.Services.GetRequiredService<DiagnosticsSetService>().RunNowAsync(id, ct);
        ctx.Target("DiagnosticsAssignment", id, run.SetName);
        ctx.Summary = $"ran {run.SetName}";
        return new { run.Id, run.AssignmentId, run.SetName, run.Target, run.Trigger };
    }
}
