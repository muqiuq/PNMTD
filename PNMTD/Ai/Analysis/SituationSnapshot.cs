using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;
using PNMTD.Services.Events;

namespace PNMTD.Ai.Analysis;

/// <summary>
/// What the situation analysis sends (docs/ai-operations-plan.md §9.1): anonymised. Hosts and sensors are <c>H1…</c> and
/// <c>S1…</c>, uplink endpoints <c>E1…</c>; no names, addresses, notes, parameters or users. Fields starting with
/// "untrusted" are devices' messages, scrubbed.
/// </summary>
public sealed record SituationInput(
    string LocalTime,
    string Weekday,
    IReadOnlyList<SnapshotTotal> Totals,
    IReadOnlyList<SnapshotHost> Hosts,
    IReadOnlyList<SnapshotSensor> Sensors,
    int OmittedSensors,
    int WithheldMessages,
    SnapshotUplink Uplink,
    IReadOnlyList<SnapshotTask> LateTasks,
    string WireGuardGateway,
    SnapshotQueue AiQueue);

public sealed record SnapshotTotal(string Type, int Ok, int Failing, int NoData, int Off);

public sealed record SnapshotHost(string Ref, string Importance, bool Enabled, int Sensors);

/// <summary><c>StateForMinutes</c>: how long the state has held; null when longer than 24 hours or unknown.</summary>
public sealed record SnapshotSensor(
    string Ref,
    string Host,
    string Type,
    string Importance,
    string State,
    int? StateForMinutes,
    int Failures1h,
    int Failures24h,
    int StateChanges24h,
    IReadOnlyList<SnapshotEvent>? RecentEvents);

public sealed record SnapshotEvent(int Code, int AgeMinutes, string UntrustedMessage);

public sealed record SnapshotUplink(
    string State,
    int Outages24h,
    int Outages7d,
    int? LongestOutage7dMinutes,
    int? MedianLatency24hMs,
    int? MedianLatency7dMs,
    IReadOnlyList<SnapshotEndpoint> Endpoints);

public sealed record SnapshotEndpoint(string Ref, string? LastOutcome, double? SuccessRate24h, double? SuccessRate7d, int? MedianLatency24hMs);

public sealed record SnapshotTask(string Name, string State);

public sealed record SnapshotQueue(int Queued, int WaitingRetry, int WaitingBudget, int Failed24h);

/// <summary>
/// A snapshot with what stays in PNMT: the pseudonyms' and placeholders' real values (<see cref="Mapping"/>), the untrusted
/// messages as they are sent, and the structural hash.
/// </summary>
public sealed record SituationSnapshot(
    SituationInput Input,
    Dictionary<string, string> Mapping,
    IReadOnlyList<string> Messages,
    string Hash,
    int Chars)
{
    public const string Withheld = "<message withheld>";

    /// <summary>The same snapshot with every message withheld: after the guard flagged the batch (§9.1).</summary>
    public SituationSnapshot WithoutMessages()
    {
        var count = 0;
        var sensors = Input.Sensors.Select(s => s.RecentEvents is null ? s : s with
        {
            RecentEvents = s.RecentEvents.Select(e =>
            {
                count += e.UntrustedMessage == Withheld ? 0 : 1;
                return e with { UntrustedMessage = Withheld };
            }).ToList(),
        }).ToList();
        return this with { Input = Input with { Sensors = sensors, WithheldMessages = Input.WithheldMessages + count }, Messages = [] };
    }
}

/// <summary>Builds the snapshot (§9.1) from the monitoring state, the uplink, the tasks, the gateway and the AI queue.</summary>
public sealed class SituationSnapshots(IServiceScopeFactory scopes)
{
    /// <summary>About 30,000 characters (§9.2): the most important and longest-failing sensors come first.</summary>
    public const int MaxChars = 30_000;
    public const int EventsPerSensor = 5;
    public const int SensorsWithEvents = 40;
    public const int MessageChars = 300;

    private static readonly string[] AnalysisSensors =
        [SystemSensorKeys.AnalysisCritical, SystemSensorKeys.AnalysisWarning, SystemSensorKeys.AnalysisInfo];

    public sealed class CountRow
    {
        public Guid SensorId { get; set; }
        public int Hour { get; set; }
        public int Day { get; set; }
    }

    public sealed class EventRow
    {
        public Guid SensorId { get; set; }
        public DateTime Created { get; set; }
        public int Code { get; set; }
        public string? Message { get; set; }
    }

    public async Task<SituationSnapshot> BuildAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<PnmtdDbContext>();
        var reader = services.GetRequiredService<MonitoringReader>();
        var queries = services.GetRequiredService<MonitoringQueries>();
        var now = Clock.UtcNow;

        var snapshot = await reader.SnapshotAsync(s => !(s.Type == SensorType.SYSTEM && AnalysisSensors.Contains(s.Parameters)), ct);
        var sensors = snapshot.Sensors.Values.Where(s => !s.IsContainer).ToList();
        var ids = sensors.Select(s => s.Info.Id).ToArray();
        var hosts = await db.Hosts.AsNoTracking().Select(h => new { h.Id, h.Name, h.Enabled, h.Importance }).ToListAsync(ct);

        var hour = now.AddHours(-1);
        var day = now.AddHours(-24);
        var counts = ids.Length == 0 ? [] : await db.Database.SqlQuery<CountRow>($"""
            SELECT e."SensorId" AS "SensorId", count(*) FILTER (WHERE e."Created" >= {hour})::int AS "Hour", count(*)::int AS "Day"
            FROM events e
            WHERE e."SensorId" = ANY({ids}) AND e."Created" >= {day} AND e."Code" > 399
            GROUP BY e."SensorId"
            """).ToListAsync(ct);
        var failures = counts.ToDictionary(c => c.SensorId);
        var changes = (await queries.StateChangesAsync(ids, day, now, 20_000, ct))
            .GroupBy(c => c.SensorId).ToDictionary(g => g.Key, g => (Count: g.Count(), Last: g.Max(c => c.At)));

        // Relevance: failing first (most important, then longest failing), then flapping, then the rest by importance.
        int Rank(SensorStatus s) => s.State switch
        {
            SensorState.Failing => 0,
            _ when changes.GetValueOrDefault(s.Info.Id).Count > 3 => 1,
            SensorState.NoData => 2,
            SensorState.Ok when failures.ContainsKey(s.Info.Id) => 3,
            SensorState.Ok => 4,
            _ => 5,
        };
        var ordered = sensors
            .OrderBy(Rank)
            .ThenByDescending(s => s.Info.EffectiveImportance)
            .ThenBy(s => s.FailingSince ?? DateTime.MaxValue)
            .ThenBy(s => s.Info.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Pseudonyms in that order: the first host listed is H1.
        var mapping = new Dictionary<string, string>();
        var hostRefs = new Dictionary<Guid, string>();
        var sensorRefs = new Dictionary<Guid, string>();
        var scrubber = new Scrubber();
        foreach (var hostId in ordered.Select(s => s.Info.HostId).Concat(hosts.Select(h => h.Id)).Distinct())
        {
            var reference = $"H{hostRefs.Count + 1}";
            hostRefs[hostId] = reference;
            mapping[reference] = hostId.ToString();
            if (hosts.FirstOrDefault(h => h.Id == hostId) is { } host)
            {
                scrubber.Name(host.Name, reference);
            }
        }
        foreach (var s in ordered)
        {
            var reference = $"S{sensorRefs.Count + 1}";
            sensorRefs[s.Info.Id] = reference;
            mapping[reference] = s.Info.Id.ToString();
            if (IsDistinctive(s.Info.Name))
            {
                scrubber.Name(s.Info.Name, reference);
            }
        }

        // The last events of the failing and flapping sensors, scrubbed and scanned (layers 1 and 2, per message).
        var withEvents = ordered.Where(s => Rank(s) <= 1).Take(SensorsWithEvents).Select(s => s.Info.Id).ToArray();
        var rows = withEvents.Length == 0 ? [] : await db.Database.SqlQuery<EventRow>($"""
            SELECT e."SensorId" AS "SensorId", e."Created" AS "Created", e."Code" AS "Code", e."Message" AS "Message"
            FROM unnest({withEvents}) AS s(id)
            CROSS JOIN LATERAL (
                SELECT x."SensorId", x."Created", x."Code", x."Message" FROM events x
                WHERE x."SensorId" = s.id ORDER BY x."Created" DESC LIMIT {EventsPerSensor}) e
            """).ToListAsync(ct);
        var withheld = 0;
        var messages = new List<string>();
        var events = rows.GroupBy(r => r.SensorId).ToDictionary(g => g.Key, g => (IReadOnlyList<SnapshotEvent>)g.OrderByDescending(r => r.Created).Select(r =>
        {
            var cleaned = InputSanitizer.Clean(r.Message, MessageChars);
            var text = scrubber.Scrub(cleaned.Text);
            if (InjectionScanner.Check(text, cleaned.InvisibleRemoved).Score >= InjectionScanner.BlockScore)
            {
                withheld++;
                text = SituationSnapshot.Withheld;
            }
            else if (text.Length > 0 && !messages.Contains(text))
            {
                messages.Add(text);
            }
            return new SnapshotEvent(r.Code, Minutes(now - Clock.AsUtc(r.Created)), text);
        }).ToList());

        SnapshotSensor Sensor(SensorStatus s)
        {
            var id = s.Info.Id;
            var (changeCount, lastChange) = changes.GetValueOrDefault(id);
            int? stateFor = s.State == SensorState.Failing && s.FailingSince is { } since ? Minutes(now - Clock.AsUtc(since))
                : changeCount > 0 ? Minutes(now - Clock.AsUtc(lastChange)) : null;
            var f = failures.GetValueOrDefault(id);
            return new SnapshotSensor(sensorRefs[id], hostRefs[s.Info.HostId], s.Info.Type.ToString(), s.Info.EffectiveImportance.ToString(),
                StateName(s.State), stateFor is > 24 * 60 ? null : stateFor, f?.Hour ?? 0, f?.Day ?? 0, changeCount, events.GetValueOrDefault(id));
        }

        var totals = sensors.GroupBy(s => s.Info.Type).OrderBy(g => g.Key.ToString()).Select(g => new SnapshotTotal(g.Key.ToString(),
            g.Count(s => s.State == SensorState.Ok), g.Count(s => s.State == SensorState.Failing), g.Count(s => s.State == SensorState.NoData),
            g.Count(s => s.State is SensorState.Disabled or SensorState.Ignored))).ToList();
        var hostItems = hostRefs.Select(h =>
        {
            var host = hosts.FirstOrDefault(x => x.Id == h.Key);
            return new SnapshotHost(h.Value, (host?.Importance ?? Importance.Normal).ToString(), host?.Enabled ?? true, sensors.Count(s => s.Info.HostId == h.Key));
        }).ToList();
        var (uplink, uplinkHash) = await UplinkAsync(services, mapping, ct);
        var tasks = (await services.GetRequiredService<SystemService>().TasksAsync(ct))
            .Where(t => t.State is "late" or "notRun").Select(t => new SnapshotTask(t.Name, t.State)).ToList();
        var gateway = await GatewayAsync(services, ct);
        var openSince = now.AddHours(-24);
        var queue = await db.AiWork.AsNoTracking().GroupBy(w => 1).Select(g => new SnapshotQueue(
            g.Count(w => w.State == AiWorkState.Queued || w.State == AiWorkState.Running), g.Count(w => w.State == AiWorkState.WaitingRetry),
            g.Count(w => w.State == AiWorkState.WaitingBudget), g.Count(w => w.State == AiWorkState.Failed && w.FinishedAt >= openSince)))
            .FirstOrDefaultAsync(ct) ?? new SnapshotQueue(0, 0, 0, 0);

        var local = Clock.ToLocal(now);
        var input = new SituationInput(local.ToString("yyyy-MM-dd HH:mm"), local.DayOfWeek.ToString(), totals, hostItems, [], 0, withheld, uplink, tasks,
            gateway, queue);

        // The size cap: sensors in order of relevance while they fit.
        var budget = MaxChars - Serialize(input).Length;
        var included = new List<SnapshotSensor>();
        foreach (var s in ordered)
        {
            var item = Sensor(s);
            var size = Serialize(item).Length + 1;
            if (size > budget)
            {
                break;
            }
            budget -= size;
            included.Add(item);
        }
        input = input with { Sensors = included, OmittedSensors = ordered.Count - included.Count };
        var sent = included.Where(s => s.RecentEvents is not null).SelectMany(s => s.RecentEvents!).Select(e => e.UntrustedMessage).ToHashSet();
        foreach (var (placeholder, value) in scrubber.Values)
        {
            mapping[placeholder] = value;
        }
        var hash = Hash(ordered.Select(s =>
        {
            var f = failures.GetValueOrDefault(s.Info.Id);
            return $"{s.Info.Id}|{s.State}|{Bucket(f?.Hour ?? 0)}|{Bucket(f?.Day ?? 0)}|{Bucket(changes.GetValueOrDefault(s.Info.Id).Count)}";
        }).Order(StringComparer.Ordinal).Append(uplinkHash).Append($"gateway|{gateway}").Concat(tasks.Select(t => $"task|{t.Name}|{t.State}")));
        return new SituationSnapshot(input, mapping, messages.Where(sent.Contains).ToList(), hash, Serialize(input).Length);
    }

    /// <summary>
    /// Sensor names are replaced in messages when they are distinctive: with a digit, a dot, a hyphen or an underscore, or 8
    /// characters and more. A sensor called "Backup" would otherwise turn every "backup" in every message into a pseudonym.
    /// </summary>
    public static bool IsDistinctive(string name) =>
        name.Trim().Length >= 3 && (name.Trim().Length >= 8 || name.Any(c => char.IsDigit(c) || c is '.' or '-' or '_'));

    private static async Task<(SnapshotUplink Uplink, string Hash)> UplinkAsync(IServiceProvider services, Dictionary<string, string> mapping, CancellationToken ct)
    {
        var service = services.GetRequiredService<UplinkService>();
        var overview = await service.OverviewAsync(false, ct);
        if (!overview.Configured)
        {
            return (new SnapshotUplink("not configured", 0, 0, null, null, null, []), "uplink|off");
        }
        var outages = await service.OutagesAsync(7, ct);
        var now = Clock.UtcNow;
        var day = outages.Count(o => (o.End ?? now) >= now.AddHours(-24));
        var endpoints = overview.Endpoints.Select(e =>
        {
            var reference = $"E{e.Number}";
            mapping[reference] = e.Address;
            return new SnapshotEndpoint(reference, e.LastOutcome?.ToString(), Round(e.SuccessRate24h), Round(e.SuccessRate7d), e.MedianLatency24h);
        }).ToList();
        var week = overview.Week.Where(b => b.MedianLatencyMs is not null).Select(b => b.MedianLatencyMs!.Value).Order().ToList();
        var lastDay = overview.Day.Where(b => b.MedianLatencyMs is not null).Select(b => b.MedianLatencyMs!.Value).Order().ToList();
        var uplink = new SnapshotUplink(overview.Up ? "up" : "down", day, outages.Count,
            outages.Count == 0 ? null : outages.Max(o => o.DurationSeconds) / 60, Median(lastDay), Median(week), endpoints);
        return (uplink, $"uplink|{uplink.State}|{day}|{outages.Count}|{string.Join(",", endpoints.Select(e => $"{e.Ref}:{e.LastOutcome}"))}");
    }

    private static async Task<string> GatewayAsync(IServiceProvider services, CancellationToken ct)
    {
        var network = services.GetRequiredService<WireGuard.WireGuardNetwork>();
        if (!network.Enabled)
        {
            return "off";
        }
        var status = await services.GetRequiredService<WireGuardService>().StatusAsync(ct);
        return status.GatewayUp ? "up" : "down";
    }

    private static string StateName(SensorState state) => state switch
    {
        SensorState.Ok => "ok",
        SensorState.Failing => "failing",
        SensorState.NoData => "no-data",
        SensorState.Ignored => "ignored",
        _ => "disabled",
    };

    private static int Minutes(TimeSpan span) => Math.Max(0, (int)span.TotalMinutes);

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 3) : null;

    private static int? Median(IReadOnlyList<int> sorted) => sorted.Count == 0 ? null : sorted[sorted.Count / 2];

    /// <summary>Counts in coarse steps, so that one more failure of a sensor that keeps failing is no change.</summary>
    private static int Bucket(int count) => count switch { 0 => 0, < 3 => 1, < 10 => 2, < 100 => 3, _ => 4 };

    private static string Hash(IEnumerable<string> lines) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, AiJson.Options);
}
