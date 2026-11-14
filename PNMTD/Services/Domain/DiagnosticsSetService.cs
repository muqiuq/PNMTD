using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Diagnostics;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

/// <summary>
/// Diagnostics in the API (docs/windows-sensor-plan.md §5, §9.2): sets and their checks, assignments with their DIAGNOSTICS
/// sensors, runs, and Run now. Every change raises the configuration's version and sends it to the agents concerned.
/// </summary>
public sealed class DiagnosticsSetService(PnmtdDbContext db, MonitoringReader reader, AgentHub hub, AgentKeyStore keys, IHttpContextAccessor accessor)
{
    // --- sets (§5.1) ----------------------------------------------------------------------------------------

    public async Task<Paged<DiagnosticSetItem>> SetsAsync(PageQuery query, CancellationToken ct)
    {
        var q = db.DiagnosticSets.AsNoTracking();
        if (Text.Pattern(query.Search) is { } pattern)
        {
            q = q.Where(s => EF.Functions.ILike(s.Name, pattern) || EF.Functions.ILike(s.Description ?? "", pattern));
        }
        return await Paging.PageAsync(q.OrderBy(s => s.Name).Select(s => new DiagnosticSetItem(s.Id, s.Name, s.Description, s.Checks.Count,
            db.DiagnosticAssignments.Count(a => a.SetId == s.Id), s.UpdatedAt)), query, ct);
    }

    public async Task<DiagnosticSetDetail> SetAsync(Guid id, CancellationToken ct)
    {
        var set = await db.DiagnosticSets.AsNoTracking().Include(s => s.Checks).SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Diagnostics set");
        var assignments = await db.DiagnosticAssignments.CountAsync(a => a.SetId == id, ct);
        return new DiagnosticSetDetail(set.Id, set.Name, set.Description, set.Checks.OrderBy(c => c.Order).Select(ToItem).ToList(), assignments, set.CreatedAt,
            set.UpdatedAt);
    }

    private static DiagnosticCheckItem ToItem(DiagnosticCheckEntity c)
    {
        var check = DiagnosticsConfig.ToCheck(c);
        return new DiagnosticCheckItem(c.Id, c.Order, c.Name, c.Type, check.Ping, check.Dns);
    }

    public async Task<DiagnosticSetDetail> CreateSetAsync(DiagnosticSetRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, null, ct);
        var now = Clock.UtcNow;
        var set = new DiagnosticSetEntity { Id = Guid.NewGuid(), CreatedAt = now };
        Apply(set, request, now);
        db.DiagnosticSets.Add(set);
        UdpViews.Audit(db, accessor, AuthEvents.DiagnosticsSetChanged, $"set {set.Name} created ({set.Checks.Count} checks)");
        await db.SaveChangesAsync(ct);
        return await SetAsync(set.Id, ct);
    }

    public async Task<DiagnosticSetDetail> UpdateSetAsync(Guid id, DiagnosticSetRequest request, CancellationToken ct)
    {
        var set = await db.DiagnosticSets.Include(s => s.Checks).SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Diagnostics set");
        await ValidateAsync(request, id, ct);
        db.DiagnosticChecks.RemoveRange(set.Checks);
        set.Checks.Clear();
        Apply(set, request, Clock.UtcNow);
        UdpViews.Audit(db, accessor, AuthEvents.DiagnosticsSetChanged, $"set {set.Name} changed ({set.Checks.Count} checks)");
        // The assignments' sensors carry the set's name.
        var sensorIds = await db.DiagnosticAssignments.Where(a => a.SetId == id).Select(a => a.SensorId).ToListAsync(ct);
        await db.Sensors.Where(s => sensorIds.Contains(s.Id)).ExecuteUpdateAsync(u => u.SetProperty(s => s.Name, SensorName(set.Name)), ct);
        await db.SaveChangesAsync(ct);
        await PushAsync(await db.DiagnosticAssignments.Where(a => a.SetId == id).Select(a => a.TargetSensorId).Distinct().ToListAsync(ct), ct);
        return await SetAsync(id, ct);
    }

    /// <summary>Refused while assigned: an assignment's sensor and history would go with it.</summary>
    public async Task DeleteSetAsync(Guid id, CancellationToken ct)
    {
        var set = await db.DiagnosticSets.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Diagnostics set");
        if (await db.DiagnosticAssignments.AnyAsync(a => a.SetId == id, ct))
        {
            throw ApiException.Conflict("set_assigned", "The set is assigned: delete its assignments first (their sensors and history go with them).");
        }
        db.DiagnosticSets.Remove(set);
        UdpViews.Audit(db, accessor, AuthEvents.DiagnosticsSetChanged, $"set {set.Name} deleted");
        await db.SaveChangesAsync(ct);
    }

    private static void Apply(DiagnosticSetEntity set, DiagnosticSetRequest request, DateTime now)
    {
        set.Name = request.Name.Trim();
        set.Description = Text.Trimmed(request.Description);
        set.UpdatedAt = now;
        var order = 0;
        foreach (var check in request.Checks)
        {
            set.Checks.Add(new DiagnosticCheckEntity
            {
                Id = Guid.NewGuid(),
                SetId = set.Id,
                Order = order++,
                Name = check.Name.Trim(),
                Type = check.Type,
                Parameters = check.Type == DiagnosticCheckType.Ping
                    ? JsonSerializer.Serialize(check.Ping! with { Target = check.Ping.Target.Trim() }, AgentJson.Options)
                    : JsonSerializer.Serialize(check.Dns! with { Name = check.Dns.Name.Trim(), Server = Text.Trimmed(check.Dns.Server), Expect = Text.Trimmed(check.Dns.Expect) },
                        AgentJson.Options),
            });
        }
    }

    private async Task ValidateAsync(DiagnosticSetRequest request, Guid? id, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var v = new ApiValidator()
            .Check(name is { Length: >= 1 and <= 200 }, "name", "The name must have 1 to 200 characters.")
            .Check(request.Description is null || request.Description.Length <= 2000, "description", "At most 2000 characters.")
            .Check(request.Checks is { Count: >= 1 and <= DiagnosticsRules.MaxChecks }, "checks", $"Between 1 and {DiagnosticsRules.MaxChecks} checks.");
        for (var i = 0; i < (request.Checks?.Count ?? 0); i++)
        {
            var check = request.Checks![i];
            v.Check(check.Name?.Trim() is { Length: >= 1 and <= 200 }, $"checks[{i}].name", "A name of 1 to 200 characters.");
            var problem = check.Type switch
            {
                DiagnosticCheckType.Ping => DiagnosticsRules.Ping(check.Ping),
                DiagnosticCheckType.Dns => DiagnosticsRules.Dns(check.Dns),
                _ => "Ping or DNS.",
            };
            v.Check(problem is null, $"checks[{i}]", problem ?? "");
        }
        if (name is not null)
        {
            v.Check(!await db.DiagnosticSets.AnyAsync(s => s.Name == name && s.Id != id, ct), "name", "Another set has this name.");
        }
        v.ThrowIfInvalid();
    }

    // --- assignments (§5.2) ----------------------------------------------------------------------------------------

    public static string SensorName(string set) => Cuts.Cut($"Diagnostics: {set}", 200);

    public async Task<Paged<DiagnosticAssignmentItem>> AssignmentsAsync(DiagnosticAssignmentQuery query, CancellationToken ct)
    {
        var q = db.DiagnosticAssignments.AsNoTracking();
        if (query.TargetSensorId is { } target)
        {
            q = q.Where(a => a.TargetSensorId == target);
        }
        if (query.SetId is { } set)
        {
            q = q.Where(a => a.SetId == set);
        }
        var assignments = await q.ToListAsync(ct);
        var items = await ItemsAsync(assignments, ct);
        var filtered = items.Where(i => Text.Contains(i.Set.Name, query.Search) || Text.Contains(i.Target.Name, query.Search))
            .OrderBy(i => i.Target.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Set.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return Paging.Page(filtered, query);
    }

    private async Task<List<DiagnosticAssignmentItem>> ItemsAsync(IReadOnlyList<DiagnosticAssignmentEntity> assignments, CancellationToken ct)
    {
        var setIds = assignments.Select(a => a.SetId).Distinct().ToList();
        var sets = await db.DiagnosticSets.AsNoTracking().Where(s => setIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var sensorIds = assignments.SelectMany(a => new[] { a.SensorId, a.TargetSensorId }).Distinct().ToList();
        var snapshot = await reader.SnapshotAsync(s => sensorIds.Contains(s.Id), ct);
        var ids = assignments.Select(a => a.Id).ToList();
        var lastRuns = await db.DiagnosticRuns.AsNoTracking().Where(r => ids.Contains(r.AssignmentId))
            .GroupBy(r => r.AssignmentId).Select(g => g.OrderByDescending(r => r.StartedAt).First()).ToListAsync(ct);
        string Name(Guid sensor) => snapshot.Sensors.TryGetValue(sensor, out var s) ? $"{s.Info.HostName} / {s.Info.Name}" : sensor.ToString();
        return assignments.Select(a =>
        {
            var setName = sets.GetValueOrDefault(a.SetId, "?");
            var last = lastRuns.FirstOrDefault(r => r.AssignmentId == a.Id);
            return new DiagnosticAssignmentItem(a.Id, new Ref(a.SetId, setName), new Ref(a.TargetSensorId, Name(a.TargetSensorId)), new Ref(a.SensorId, Name(a.SensorId)),
                a.Mode, a.IntervalMinutes, a.Enabled, snapshot.Sensors.GetValueOrDefault(a.SensorId)?.State ?? SensorState.NoData, hub.IsConnected(a.TargetSensorId),
                last is null ? null : RunItem(last, setName, new Ref(a.TargetSensorId, Name(a.TargetSensorId))));
        }).ToList();
    }

    public async Task<IReadOnlyList<DiagnosticAssignmentItem>> AssignAsync(DiagnosticAssignmentRequest request, CancellationToken ct)
    {
        var set = await db.DiagnosticSets.AsNoTracking().Include(s => s.Checks).SingleOrDefaultAsync(s => s.Id == request.SetId, ct);
        var targets = (request.TargetSensorIds ?? []).Distinct().ToList();
        var v = new ApiValidator()
            .Check(set is not null, "setId", "No such set.")
            .Check(targets.Count is >= 1 and <= 500, "targetSensorIds", "Between 1 and 500 servers.")
            .Check(Enum.IsDefined(request.Mode), "mode", "Scheduled or Manual.")
            .Check(request.Mode == DiagnosticMode.Manual || request.IntervalMinutes is >= DiagnosticsRules.MinIntervalMinutes and <= DiagnosticsRules.MaxIntervalMinutes,
                "intervalMinutes", $"Between {DiagnosticsRules.MinIntervalMinutes} and {DiagnosticsRules.MaxIntervalMinutes} minutes.");
        // Any server with an agent, of either platform (docs/linux-sensor-plan.md §7): the check types decide below.
        var servers = await db.Agents.AsNoTracking().Where(a => targets.Contains(a.SensorId))
            .Select(a => new { a.SensorId, a.Capabilities, HostId = a.Sensor.ParentId, Name = a.Sensor.Parent.Name + " / " + a.Sensor.Name }).ToListAsync(ct);
        v.Check(servers.Count == targets.Count, "targetSensorIds", "Every target must be a server with an agent.");
        var existing = await db.DiagnosticAssignments.Where(a => a.SetId == request.SetId && targets.Contains(a.TargetSensorId)).Select(a => a.TargetSensorId).ToListAsync(ct);
        v.Check(existing.Count == 0, "targetSensorIds", "The set is already assigned to " + string.Join(", ", servers.Where(s => existing.Contains(s.SensorId)).Select(s => s.Name)) + ".");
        if (set is not null)
        {
            // §5.5: the target's agent must have announced the check types (when it ever connected).
            var needs = set.Checks.Select(c => c.Type == DiagnosticCheckType.Ping ? AgentCapabilities.DiagPing : AgentCapabilities.DiagDns).Distinct().ToList();
            var unable = servers.Where(s => s.Capabilities.Count > 0 && needs.Except(s.Capabilities).Any()).Select(s => s.Name).ToList();
            v.Check(unable.Count == 0, "targetSensorIds", $"These agents can't run the set's checks: {string.Join(", ", unable)}.");
        }
        v.ThrowIfInvalid();

        var now = Clock.UtcNow;
        var created = new List<DiagnosticAssignmentEntity>();
        foreach (var server in servers)
        {
            var sensor = new SensorEntity
            {
                Id = Guid.NewGuid(),
                ParentId = server.HostId,
                Type = SensorType.DIAGNOSTICS,
                Name = SensorName(set!.Name),
                Enabled = true,
                Interval = request.Mode == DiagnosticMode.Scheduled ? request.IntervalMinutes * 60 : 0,
                GracePeriod = 120,
                Created = now,
            };
            sensor.SetNewSecretToken();
            db.Sensors.Add(sensor);
            var assignment = new DiagnosticAssignmentEntity
            {
                Id = Guid.NewGuid(),
                SetId = set.Id,
                TargetSensorId = server.SensorId,
                SensorId = sensor.Id,
                Mode = request.Mode,
                IntervalMinutes = request.IntervalMinutes,
                Enabled = request.Enabled,
                CreatedAt = now,
            };
            db.DiagnosticAssignments.Add(assignment);
            created.Add(assignment);
        }
        UdpViews.Audit(db, accessor, AuthEvents.DiagnosticsAssignmentChanged,
            $"set {set!.Name} assigned to {string.Join(", ", servers.Select(s => s.Name))} ({Schedule(request.Mode, request.IntervalMinutes)})");
        await db.SaveChangesAsync(ct);
        await PushAsync(targets, ct);
        return await ItemsAsync(created, ct);
    }

    public async Task<DiagnosticAssignmentItem> UpdateAssignmentAsync(Guid id, DiagnosticAssignmentUpdate request, CancellationToken ct)
    {
        var assignment = await db.DiagnosticAssignments.SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("Assignment");
        new ApiValidator()
            .Check(Enum.IsDefined(request.Mode), "mode", "Scheduled or Manual.")
            .Check(request.Mode == DiagnosticMode.Manual || request.IntervalMinutes is >= DiagnosticsRules.MinIntervalMinutes and <= DiagnosticsRules.MaxIntervalMinutes,
                "intervalMinutes", $"Between {DiagnosticsRules.MinIntervalMinutes} and {DiagnosticsRules.MaxIntervalMinutes} minutes.")
            .ThrowIfInvalid();
        assignment.Mode = request.Mode;
        assignment.IntervalMinutes = request.IntervalMinutes;
        assignment.Enabled = request.Enabled;
        var interval = request.Mode == DiagnosticMode.Scheduled && request.Enabled ? request.IntervalMinutes * 60 : 0;
        await db.Sensors.Where(s => s.Id == assignment.SensorId).ExecuteUpdateAsync(u => u.SetProperty(s => s.Interval, interval), ct);
        UdpViews.Audit(db, accessor, AuthEvents.DiagnosticsAssignmentChanged,
            $"assignment {id}: {Schedule(request.Mode, request.IntervalMinutes)}, {(request.Enabled ? "on" : "off")}");
        await db.SaveChangesAsync(ct);
        await PushAsync([assignment.TargetSensorId], ct);
        return (await ItemsAsync([assignment], ct))[0];
    }

    /// <summary>Deletes the assignment with its sensor and that sensor's history (§5.2).</summary>
    public async Task DeleteAssignmentAsync(Guid id, CancellationToken ct)
    {
        var assignment = await db.DiagnosticAssignments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("Assignment");
        await DeleteAssignmentsAsync(db, [assignment], ct);
        UdpViews.Audit(db, accessor, AuthEvents.DiagnosticsAssignmentChanged, $"assignment {id} deleted with its sensor");
        await db.SaveChangesAsync(ct);
        await PushAsync([assignment.TargetSensorId], ct);
    }

    /// <summary>The assignments' sensors (and with them the assignments, their runs and events): for an assignment, and for a deleted server.</summary>
    public static async Task DeleteAssignmentsAsync(PnmtdDbContext db, IReadOnlyCollection<DiagnosticAssignmentEntity> assignments, CancellationToken ct)
    {
        var sensorIds = assignments.Select(a => a.SensorId).ToList();
        await db.MailInputs.Where(m => m.SensorOutputId != null && sensorIds.Contains(m.SensorOutputId.Value))
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.SensorOutputId, (Guid?)null), ct);
        await db.Sensors.Where(s => sensorIds.Contains(s.Id)).ExecuteDeleteAsync(ct);
    }

    private static string Schedule(DiagnosticMode mode, int minutes) => mode == DiagnosticMode.Scheduled ? $"every {minutes} min" : "by hand";

    /// <summary>A new configuration version, sent to the agents concerned.</summary>
    private async Task PushAsync(IReadOnlyCollection<Guid> targets, CancellationToken ct)
    {
        var settings = await keys.SettingsAsync(db, ct);
        settings.ConfigVersion++;
        await db.SaveChangesAsync(ct);
        await hub.PushConfigAsync(targets, ct);
    }

    // --- runs (§5.3, §5.4) ---------------------------------------------------------------------------------------

    public async Task<DiagnosticRunItem> RunNowAsync(Guid id, CancellationToken ct)
    {
        var assignment = await db.DiagnosticAssignments.SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("Assignment");
        var connection = hub.Connection(assignment.TargetSensorId)
            ?? throw ApiException.Conflict("agent_offline", "The server's agent is not connected: try again when it is.");
        var (_, user) = UdpViews.User(accessor);
        var run = new DiagnosticRunEntity
        {
            Id = Guid.NewGuid(),
            AssignmentId = assignment.Id,
            SensorId = assignment.SensorId,
            Trigger = "manual",
            RequestedBy = user,
            StartedAt = Clock.UtcNow,
        };
        db.DiagnosticRuns.Add(run);
        await db.SaveChangesAsync(ct);
        try
        {
            var answer = await connection.RequestAsync(keys, AgentMessageTypes.RunDiagnostics, new RunDiagnosticsRequest(assignment.Id, run.Id), TimeSpan.FromSeconds(30), ct);
            if (AgentJson.Deserialize<Ack>(answer) is { Ok: false } refused)
            {
                run.Outcome = DiagnosticOutcome.Error;
                run.FinishedAt = Clock.UtcNow;
                run.Summary = Cuts.Cut($"The agent refused: {refused.Error}", 1000);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw ApiException.Conflict("agent_timeout", "The agent did not answer within 30 s.");
        }
        return (await RunAsync(run.Id, ct)).Run;
    }

    /// <summary>A run with what the lists show; member-initialised, so EF can still filter on it.</summary>
    private sealed class RunRow
    {
        public DiagnosticRunEntity Run { get; init; } = null!;

        public Guid SetId { get; init; }

        public Guid TargetSensorId { get; init; }

        public string SetName { get; init; } = "";

        public string Target { get; init; } = "";
    }

    private IQueryable<RunRow> RunRows() =>
        from r in db.DiagnosticRuns.AsNoTracking()
        join a in db.DiagnosticAssignments on r.AssignmentId equals a.Id
        join s in db.DiagnosticSets on a.SetId equals s.Id
        join t in db.Sensors on a.TargetSensorId equals t.Id
        select new RunRow { Run = r, SetId = a.SetId, TargetSensorId = a.TargetSensorId, SetName = s.Name, Target = t.Parent.Name + " / " + t.Name };

    public async Task<Paged<DiagnosticRunItem>> RunsAsync(DiagnosticRunQuery query, CancellationToken ct)
    {
        var q = RunRows();
        if (query.AssignmentId is { } assignment)
        {
            q = q.Where(x => x.Run.AssignmentId == assignment);
        }
        if (query.SetId is { } set)
        {
            q = q.Where(x => x.SetId == set);
        }
        if (query.TargetSensorId is { } target)
        {
            q = q.Where(x => x.TargetSensorId == target);
        }
        if (query.Outcome is { } outcome)
        {
            q = q.Where(x => x.Run.Outcome == outcome);
        }
        var page = await Paging.PageAsync(q.OrderByDescending(x => x.Run.StartedAt), query, ct);
        return new Paged<DiagnosticRunItem>(page.Items.Select(x => RunItem(x.Run, x.SetName, new Ref(x.TargetSensorId, x.Target))).ToList(), page.Page, page.PageSize,
            page.TotalCount);
    }

    public async Task<DiagnosticRunDetail> RunAsync(Guid id, CancellationToken ct)
    {
        var row = await RunRows().SingleOrDefaultAsync(x => x.Run.Id == id, ct) ?? throw ApiException.NotFound("Run");
        var results = row.Run.Results is null ? [] : JsonSerializer.Deserialize<List<DiagnosticCheckResult>>(row.Run.Results, AgentJson.Options) ?? [];
        return new DiagnosticRunDetail(RunItem(row.Run, row.SetName, new Ref(row.TargetSensorId, row.Target)), results);
    }

    private static DiagnosticRunItem RunItem(DiagnosticRunEntity r, string setName, Ref target) =>
        new(r.Id, r.AssignmentId, setName, target, r.Trigger, r.RequestedBy, r.StartedAt, r.FinishedAt, r.Outcome, r.Summary);
}
