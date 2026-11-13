using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Decisions;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.Ai.Analysis;

/// <summary>The situation analysis's answer (docs/ai-operations-plan.md §9.3).</summary>
public sealed record Situation(
    [property: Description("normal, degraded or incident")] SituationStatus Status,
    [property: Description("What stands out, at most 400 characters")] string Summary,
    [property: Description("At most 10 findings, the most important first; none when nothing stands out")] IReadOnlyList<SituationFinding> Findings);

public sealed record SituationFinding(
    [property: Description("incident: broken now; pattern: recurring behaviour; risk: likely to break; noise: a sensor that alerts without meaning")]
    FindingKind Kind,
    [property: Description("critical: needs action now; warning: needs attention soon; info: worth knowing")] FindingSeverity Severity,
    [property: Description("At most 120 characters")] string Title,
    [property: Description("At most 800 characters")] string Explanation,
    [property: Description("Sensor references from the input only, such as S3")] IReadOnlyList<string> Sensors,
    [property: Description("Host references from the input only, such as H2")] IReadOnlyList<string> Hosts,
    [property: Description("At most 5 facts it rests on, each with a reference, a code or a count, and an age")] IReadOnlyList<string> Evidence,
    [property: Description("What to do, at most 400 characters")] string Recommendation,
    [property: Description("0 to 1")] double Confidence,
    [property: Description("A short kebab-case slug, the same for the same finding in later runs, such as uplink-flapping-evenings")] string Key);

/// <summary><c>situation.analyse</c> (§9): the anonymised snapshot in, the situation and its findings out.</summary>
public sealed partial class SituationTask : AiTask<SituationInput, Situation>
{
    public const string Name = "situation.analyse";

    public override string Purpose => Name;

    public override string PromptVersion => "situation-1";

    public override int MaxTokens => 4000;

    public const string CriticalQuestion =
        "Is this finding critical, meaning something needs action now, and is it supported by the snapshot alone?";

    protected override string Instructions =>
        "PNMT monitors hosts and their sensors. The data is an anonymised snapshot of its state: hosts (H1…) and sensors (S1…) with their type, " +
        "importance, state, how long the state has held, failures in the last hour and 24 hours, and state changes in 24 hours; the recent events " +
        "of failing and flapping sensors; totals by type; the internet uplink with its endpoints (E1…); background tasks that are late; the " +
        "WireGuard gateway; the AI queue; and the local time. Look for what a person running it should know: incidents (broken now, especially " +
        "on High importance), patterns (flapping, failures at certain times or weekdays, several sensors of one host or type failing together), " +
        "risks (something likely to break), and noise (sensors that alert without meaning). Status: normal when nothing needs attention, " +
        "degraded when something does, incident when important things are broken. Report few findings, the important ones; none when all is " +
        "well. Use only references that occur in the data, never invent any, and refer to hosts and sensors by them. Critical is for what " +
        "needs action now. Give each finding a key that stays the same while the finding persists. Placeholders such as <ip:1> stand for " +
        "values that were removed; keep them as they are.";

    public override string? Validate(Situation output, SituationInput input)
    {
        if (output.Summary.Length > 600 || output.Findings.Count > 10)
        {
            return "The summary is too long or there are too many findings.";
        }
        var sensors = input.Sensors.Select(s => s.Ref).ToHashSet();
        var hosts = input.Hosts.Select(h => h.Ref).ToHashSet();
        foreach (var f in output.Findings)
        {
            if (f.Title.Length > 200 || f.Explanation.Length > 1200 || f.Recommendation.Length > 600 || f.Evidence.Count > 8)
            {
                return $"The finding {f.Key} is too long.";
            }
            if (f.Confidence is < 0 or > 1)
            {
                return $"The finding {f.Key} has a confidence outside 0 to 1.";
            }
            if (!KeyPattern().IsMatch(f.Key))
            {
                return $"The key {f.Key} is not a short kebab-case slug.";
            }
            if (f.Sensors.FirstOrDefault(s => !sensors.Contains(s)) is { } unknownSensor)
            {
                return $"The finding {f.Key} names a sensor that is not in the input: {unknownSensor}.";
            }
            if (f.Hosts.FirstOrDefault(h => !hosts.Contains(h)) is { } unknownHost)
            {
                return $"The finding {f.Key} names a host that is not in the input: {unknownHost}.";
            }
        }
        return null;
    }

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+){0,9}$")]
    private static partial Regex KeyPattern();
}

/// <summary>
/// Runs an analysis (§9.3): the snapshot through the pipeline (when the guard flags the messages, again with every
/// message withheld), critical findings verified one by one, the pseudonyms mapped back, new findings to their severity's
/// system sensor, and warnings and critical findings to <c>analysis.finding</c> decisions. Muted keys are skipped.
/// </summary>
public sealed partial class SituationHandler(IDbContextFactory<PnmtdDbContext> dbs, SituationSnapshots snapshots, AiPipeline pipeline, DecisionBook decisions,
    SystemSensors sensors, ILogger<SituationHandler> logger) : IAiWorkHandler
{
    /// <summary>At most this many critical findings go to the verification model; the rest count as disputed.</summary>
    public const int MaxVerified = 3;

    public static readonly TimeSpan ReportedWithin = TimeSpan.FromHours(24);

    private static readonly SituationTask Task = new();

    public string Purpose => SituationTask.Name;

    public async Task<string?> RunAsync(AiWorkEntity work, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var run = await db.AiAnalysisRuns.SingleOrDefaultAsync(r => r.Id == work.SubjectId, ct) ?? throw new AiWorkStaleException("The run is gone.");
        if (run.State != AnalysisRunState.Queued)
        {
            throw new AiWorkStaleException("The run is no longer queued.");
        }
        var snapshot = await snapshots.BuildAsync(ct);
        var call = new AiCall(Purpose, work.Id, work.Attempts + 1);
        var result = await pipeline.RunAsync(Task, snapshot.Input, snapshot.Messages, call, ct);
        var flagged = false;
        AiPipelineResult<Situation>? first = null;
        if (result.Blocked)
        {
            logger.LogWarning("The guard flagged the analysis's messages ({By}); running it with every message withheld", result.BlockedBy);
            flagged = true;
            first = result;
            snapshot = snapshot.WithoutMessages();
            result = await pipeline.RunAsync(Task, snapshot.Input, [], call, ct);
        }
        var output = result.Output!;
        var now = Clock.UtcNow;
        var names = await NamesAsync(db, snapshot.Mapping, ct);
        string Unmask(string text) => Unmasked(text, snapshot.Mapping, names);

        run.SnapshotHash = snapshot.Hash;
        run.SnapshotChars = snapshot.Chars;
        run.Mapping = JsonSerializer.Serialize(snapshot.Mapping);
        run.Withheld = snapshot.Input.WithheldMessages;
        run.GuardFlagged = flagged;
        run.Status = output.Status;
        run.Summary = Cut(Unmask(output.Summary), 1000);

        var verified = 0;
        var findings = new List<(AiFindingEntity Entity, Verdict? Verdict)>();
        foreach (var f in output.Findings)
        {
            var sensorIds = f.Sensors.Distinct().Select(s => Guid.Parse(snapshot.Mapping[s])).ToList();
            var hostIds = f.Hosts.Distinct().Select(h => Guid.Parse(snapshot.Mapping[h])).ToList();
            if (findings.Any(x => x.Entity.DedupeKey == DedupeKey(f.Key, sensorIds)))
            {
                // The same finding twice in one answer counts once.
                continue;
            }
            var entity = new AiFindingEntity
            {
                Id = Guid.NewGuid(), RunId = run.Id, Key = f.Key, DedupeKey = DedupeKey(f.Key, sensorIds), Kind = f.Kind, Severity = f.Severity,
                Title = Cut(Unmask(f.Title), 200), Explanation = Cut(Unmask(f.Explanation), 1200), Recommendation = Cut(Unmask(f.Recommendation), 600),
                Confidence = f.Confidence, Sensors = sensorIds, Hosts = hostIds, Evidence = f.Evidence.Take(8).Select(e => Cut(Unmask(e), 300)).ToList(),
            };
            Verdict? verdict = null;
            if (f.Severity == FindingSeverity.Critical)
            {
                // §9.3: critical only with the verification model's agreement.
                if (verified < MaxVerified)
                {
                    verified++;
                    (verdict, _) = await pipeline.VerifyAsync(Purpose, SituationTask.CriticalQuestion, snapshot.Input, f, call, ct);
                }
                entity.Verified = verdict?.Answer == VerdictKind.Agree;
                if (!entity.Verified)
                {
                    entity.Severity = FindingSeverity.Warning;
                    entity.Disputed = true;
                }
            }
            entity.Muted = await AnalysisFindingDecision.MutedAsync(db, entity.DedupeKey, ct);
            var since = now - ReportedWithin;
            entity.New = !entity.Muted && !await db.AiFindings.AnyAsync(x => x.DedupeKey == entity.DedupeKey && x.New && x.Run.At >= since, ct);
            findings.Add((entity, verdict));
        }
        db.AiFindings.AddRange(findings.Select(f => f.Entity));
        run.State = AnalysisRunState.Done;
        run.CompletedAt = now;
        await db.SaveChangesAsync(ct);

        await WriteEventsAsync(findings.Select(f => f.Entity).ToList(), ct);
        var requests = await db.AiRequests.Where(r => r.WorkId == work.Id).OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(ct);
        foreach (var (f, verdict) in findings.Where(f => f.Entity.Severity >= FindingSeverity.Warning && !f.Entity.Muted))
        {
            f.DecisionId = f.New
                ? await decisions.RaiseAsync(new DecisionDraft(AnalysisFindingDecision.KindName, $"{f.Severity}: {f.Title}", f.Explanation,
                    new FindingPayload(Evidence(f, names), new AiEvidence(first?.Injection ?? result.Injection, flagged ? first!.BlockedBy : null,
                        first?.Scan.Flags ?? result.Scan.Flags, verdict?.Answer.ToString().ToLowerInvariant(), verdict?.Issues ?? [], requests)),
                    f.DedupeKey, f.Id, "analysis", run.Id, Disputed: f.Disputed, Priority: f.Severity == FindingSeverity.Critical ? 2 : 1,
                    AiWorkId: work.Id), ct)
                : await decisions.AttachAsync(f.DedupeKey, f.Id, ct);
        }
        await db.SaveChangesAsync(ct);
        return JsonSerializer.Serialize(new
        {
            run = run.Id, status = output.Status.ToString(), findings = findings.Count, fresh = findings.Count(f => f.Entity.New),
            muted = findings.Count(f => f.Entity.Muted), withheld = run.Withheld, guardFlagged = flagged,
        });
    }

    /// <summary>
    /// Per severity (§9.3): new findings write their code (423, 424, 221) to the severity's sensor; a run without any of that
    /// severity writes 220; findings that persist write nothing, so the sensor keeps its state.
    /// </summary>
    private async Task WriteEventsAsync(IReadOnlyList<AiFindingEntity> findings, CancellationToken ct)
    {
        (FindingSeverity Severity, string Key, int Code, string One, string Many, string None)[] levels =
        [
            (FindingSeverity.Critical, SystemSensorKeys.AnalysisCritical, PNMTStatusCodes.AI_FINDING_CRITICAL, "Critical", "critical", "No critical finding."),
            (FindingSeverity.Warning, SystemSensorKeys.AnalysisWarning, PNMTStatusCodes.AI_FINDING_WARNING, "Warning", "warnings", "No warning."),
            (FindingSeverity.Info, SystemSensorKeys.AnalysisInfo, PNMTStatusCodes.AI_FINDING_INFO, "Info", "infos", "No info."),
        ];
        foreach (var level in levels)
        {
            var all = findings.Where(f => f.Severity == level.Severity && !f.Muted).ToList();
            var fresh = all.Where(f => f.New).ToList();
            if (fresh.Count > 0)
            {
                var message = fresh.Count == 1 ? $"{level.One}: {fresh[0].Title}" : $"{fresh.Count} {level.Many}: {string.Join("; ", fresh.Select(f => f.Title))}";
                await sensors.WriteAsync(level.Key, level.Code, Cut(message, 1900), ct);
            }
            else if (all.Count == 0)
            {
                await sensors.WriteAsync(level.Key, PNMTStatusCodes.SYSTEM_OK, level.None, ct);
            }
        }
    }

    /// <summary>The key with the finding's sensors (§9.3: deduplicated by key and sensors).</summary>
    public static string DedupeKey(string key, IEnumerable<Guid> sensorIds)
    {
        var ids = sensorIds.Order().ToList();
        if (ids.Count == 0)
        {
            return $"{AnalysisFindingDecision.KindName}:{key}";
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", ids))))[..12];
        return $"{AnalysisFindingDecision.KindName}:{key}:{hash}";
    }

    private static FindingEvidence Evidence(AiFindingEntity f, IReadOnlyDictionary<Guid, string> names) =>
        new(f.Key, f.Kind.ToString(), f.Severity.ToString(), f.Explanation, f.Recommendation, f.Confidence,
            f.Sensors.Select(id => new Ref(id, names.GetValueOrDefault(id, "?"))).ToList(), f.Hosts.Select(id => new Ref(id, names.GetValueOrDefault(id, "?"))).ToList(),
            f.Verified);

    /// <summary>The current names of the hosts and sensors in a mapping.</summary>
    public static Task<Dictionary<Guid, string>> NamesAsync(PnmtdDbContext db, IReadOnlyDictionary<string, string> mapping, CancellationToken ct) =>
        NamesAsync(db, mapping.Where(m => m.Key is ['H' or 'S', ..] && Guid.TryParse(m.Value, out _)).Select(m => Guid.Parse(m.Value)).ToList(), ct);

    /// <summary>Hosts by their name, sensors as "host · sensor".</summary>
    public static async Task<Dictionary<Guid, string>> NamesAsync(PnmtdDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var hosts = await db.Hosts.AsNoTracking().Where(h => ids.Contains(h.Id)).Select(h => new { h.Id, h.Name }).ToListAsync(ct);
        var sensors = await db.Sensors.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.Name, Host = s.Parent.Name }).ToListAsync(ct);
        return hosts.Select(h => (h.Id, h.Name)).Concat(sensors.Select(s => (s.Id, $"{s.Host} · {s.Name}"))).ToDictionary(x => x.Item1, x => x.Item2);
    }

    /// <summary>Pseudonyms and placeholders back to the real names and values, for display in PNMT only.</summary>
    public static string Unmasked(string text, IReadOnlyDictionary<string, string> mapping, IReadOnlyDictionary<Guid, string> names) =>
        Reference().Replace(text, m => mapping.TryGetValue(m.Value, out var value)
            ? Guid.TryParse(value, out var id) ? names.GetValueOrDefault(id, m.Value) : value
            : m.Value);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [GeneratedRegex(@"<[a-z]+:\d+>|\b[HSE]\d+\b")]
    private static partial Regex Reference();
}

/// <summary>
/// When the analysis runs (§9.2): every <c>Ai:Analysis:IntervalMinutes</c> if the snapshot's structural hash changed, and
/// early, at most every 10 minutes, on a storm (5 sensors turned to failure in 10 minutes), when the uplink came back,
/// or when the WireGuard gateway went down. One run at a time; runs are kept 90 days.
/// </summary>
public sealed class SituationScheduler(IDbContextFactory<PnmtdDbContext> dbs, IServiceScopeFactory scopes, SituationSnapshots snapshots, AiWorkQueue queue,
    IOptionsMonitor<AiOptions> options, ILogger<SituationScheduler> logger) : BackgroundService
{
    public const int StormSensors = 5;
    public static readonly TimeSpan EarlyEvery = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan KeepRuns = TimeSpan.FromDays(90);

    private bool? uplinkUp;
    private bool? gatewayUp;
    private string? pending;
    private DateTime nextIntervalCheck = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The situation analysis's schedule failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One look: the run it queued, or null.</summary>
    public async Task<Guid?> TickAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Usable || !o.Analysis.Enabled)
        {
            return null;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        await CloseAsync(db, ct);
        var now = Clock.UtcNow;
        var (up, gateway, storm) = await SignalsAsync(ct);
        if (uplinkUp == false && up)
        {
            pending = "uplink-back";
        }
        if (gatewayUp == true && gateway == false)
        {
            pending = "gateway-down";
        }
        (uplinkUp, gatewayUp) = (up, gateway);
        if (await db.AiAnalysisRuns.AnyAsync(r => r.State == AnalysisRunState.Queued, ct))
        {
            return null;
        }
        var last = await db.AiAnalysisRuns.OrderByDescending(r => r.At).Select(r => (DateTime?)r.At).FirstOrDefaultAsync(ct);
        // A storm counts while it lasts; the uplink's return and the gateway's loss are kept until a run is allowed.
        if ((pending ?? (storm >= StormSensors ? "storm" : null)) is { } early && (last is null || now - last.Value >= EarlyEvery))
        {
            pending = null;
            return await QueueAsync(early, null, ct);
        }
        var interval = TimeSpan.FromMinutes(Math.Max(5, o.Analysis.IntervalMinutes));
        if (now < nextIntervalCheck || (last is not null && now - last.Value < interval))
        {
            return null;
        }
        var snapshot = await snapshots.BuildAsync(ct);
        var lastHash = await db.AiAnalysisRuns.Where(r => r.State == AnalysisRunState.Done).OrderByDescending(r => r.At)
            .Select(r => r.SnapshotHash).FirstOrDefaultAsync(ct);
        if (snapshot.Hash == lastHash)
        {
            // §9.2: without a change, no request, and no cost.
            nextIntervalCheck = now + interval;
            return null;
        }
        return await QueueAsync("interval", snapshot.Hash, ct);
    }

    /// <summary>A run and its work item; an open run is returned instead of a second one.</summary>
    public async Task<Guid> QueueAsync(string trigger, string? hash, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        if (await db.AiAnalysisRuns.Where(r => r.State == AnalysisRunState.Queued).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct) is { } open)
        {
            return open;
        }
        var run = new AiAnalysisRunEntity { Id = Guid.NewGuid(), At = Clock.UtcNow, Trigger = trigger, SnapshotHash = hash ?? "", State = AnalysisRunState.Queued };
        db.AiAnalysisRuns.Add(run);
        await db.SaveChangesAsync(ct);
        run.WorkId = await queue.EnqueueAsync(SituationTask.Name, "analysis", run.Id, null, ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Situation analysis queued ({Trigger})", trigger);
        return run.Id;
    }

    /// <summary>Runs whose work ended without them (failed for good, cancelled), and runs older than 90 days.</summary>
    private static async Task CloseAsync(PnmtdDbContext db, CancellationToken ct)
    {
        var open = await db.AiAnalysisRuns.Where(r => r.State == AnalysisRunState.Queued && r.WorkId != null)
            .Join(db.AiWork, r => r.WorkId, w => w.Id, (r, w) => new { r.Id, w.State, w.LastError }).ToListAsync(ct);
        foreach (var r in open.Where(r => r.State is AiWorkState.Failed or AiWorkState.Cancelled or AiWorkState.Succeeded))
        {
            var state = r.State == AiWorkState.Failed ? AnalysisRunState.Failed : AnalysisRunState.Cancelled;
            await db.AiAnalysisRuns.Where(x => x.Id == r.Id && x.State == AnalysisRunState.Queued).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.State, state).SetProperty(x => x.CompletedAt, Clock.UtcNow).SetProperty(x => x.Error, r.LastError), ct);
        }
        var cutoff = Clock.UtcNow - KeepRuns;
        await db.AiAnalysisRuns.Where(r => r.At < cutoff && r.State != AnalysisRunState.Queued).ExecuteDeleteAsync(ct);
    }

    /// <summary>The cheap signals, every minute: the uplink, the gateway, and how many sensors turned to failure in 10 minutes.</summary>
    private async Task<(bool Uplink, bool? Gateway, int Storm)> SignalsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var uplink = await services.GetRequiredService<Services.Domain.SystemService>().UplinkAsync(ct);
        var network = services.GetRequiredService<WireGuard.WireGuardNetwork>();
        bool? gateway = network.Enabled ? (await services.GetRequiredService<Services.Domain.WireGuardService>().StatusAsync(ct)).GatewayUp : null;
        var snapshot = await services.GetRequiredService<Services.Domain.MonitoringReader>().SnapshotAsync(s => s.Type != Lib.Models.Enum.SensorType.SYSTEM, ct);
        var now = Clock.UtcNow;
        var storm = snapshot.Sensors.Values.Count(s => s.State == SensorState.Failing && s.FailingSince is { } since && now - Clock.AsUtc(since) <= EarlyEvery);
        return (uplink.IsUp, gateway, storm);
    }
}
