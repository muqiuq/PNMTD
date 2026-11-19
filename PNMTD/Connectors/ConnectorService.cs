using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Agents;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;
using PNMTD.Services.Events;
using IPNetwork = System.Net.IPNetwork;

namespace PNMTD.Connectors;

/// <summary>
/// 3rd Party Connectors in the admin API (docs/connectors-plan.md §11): the calls with their figures, redacting and cancelling; the
/// connectors with their target as text, its parse, its arguments, warnings and description; the callers with their keys (shown again,
/// each display audited), networks and grants; the selectors, which copy and never link. Connectors: View reads, Manage changes.
/// </summary>
public sealed partial class ConnectorService(
    PnmtdDbContext db,
    AgentHub hub,
    AgentScriptResults results,
    AgentScriptService scripts,
    ConnectorKeyProtector protector,
    ConnectorReview review,
    ConnectorSecretVault vault,
    ConnectorCallSignals signals,
    SystemSensors systemSensors,
    PublicUrls urls,
    IOptionsMonitor<ConnectorOptions> options,
    IConfiguration configuration,
    IHttpContextAccessor accessor)
{
    public const int MaxName = 200;
    public const int MaxPurpose = 2000;
    public const int MaxDescription = 1000;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]{0,99}$")]
    private static partial Regex FunctionPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]{0,31}\$?$")]
    private static partial Regex LinuxAccountName();

    private static readonly ConnectorCallState[] ActiveStates = [ConnectorCallState.Queued, ConnectorCallState.Running];

    // --- calls (§13.2) ----------------------------------------------------------------------------------------------------------------

    public async Task<Paged<ConnectorCallItem>> CallsAsync(ConnectorCallQuery query, CancellationToken ct)
    {
        var q = db.ConnectorCalls.AsNoTracking();
        if (query.ConnectorId is { } connector)
        {
            q = q.Where(c => c.ConnectorId == connector);
        }
        if (query.CallerId is { } caller)
        {
            q = q.Where(c => c.CallerId == caller);
        }
        if (query.Active is { } active)
        {
            q = active ? q.Where(c => ActiveStates.Contains(c.State)) : q.Where(c => !ActiveStates.Contains(c.State));
        }
        if (query.State is { } state)
        {
            q = q.Where(c => c.State == state);
        }
        if (Text.Trimmed(query.RefusedBy) is { } refusedBy)
        {
            q = q.Where(c => c.RefusedBy == refusedBy);
        }
        if (Text.Trimmed(query.Reference) is { } reference)
        {
            var pattern = Text.Pattern(reference)!;
            q = q.Where(c => c.Reference != null && EF.Functions.ILike(c.Reference, pattern));
        }
        if (query.Since is { } since)
        {
            q = q.Where(c => c.ReceivedAt >= since);
        }
        if (query.Until is { } until)
        {
            q = q.Where(c => c.ReceivedAt < until);
        }
        if (Text.Trimmed(query.From) is { } from)
        {
            if (from.Contains('/') && IPNetwork.TryParse(from, out var network))
            {
                var addresses = (await q.Where(c => c.From != null).Select(c => c.From!).Distinct().Take(10_000).ToListAsync(ct))
                    .Where(a => IPAddress.TryParse(a, out var ip) && network.Contains(ip)).ToList();
                q = q.Where(c => c.From != null && addresses.Contains(c.From));
            }
            else
            {
                q = q.Where(c => c.From == from);
            }
        }
        if (Text.Pattern(query.Search) is { } search)
        {
            q = q.Where(c => EF.Functions.ILike(c.ConnectorName, search) || EF.Functions.ILike(c.CallerName, search)
                || (c.Reference != null && EF.Functions.ILike(c.Reference, search)) || (c.From != null && EF.Functions.ILike(c.From, search)));
        }
        // Active calls first, oldest first as the worker takes them; then the processed ones, newest first.
        var ordered = q.OrderByDescending(c => c.State == ConnectorCallState.Queued || c.State == ConnectorCallState.Running)
            .ThenBy(c => c.State == ConnectorCallState.Queued || c.State == ConnectorCallState.Running ? c.ReceivedAt : DateTime.MaxValue)
            .ThenByDescending(c => c.ReceivedAt).ThenBy(c => c.Id);
        var page = await Paging.PageAsync(ordered, query, ct);
        return new Paged<ConnectorCallItem>(await ItemsAsync(page.Items, ct), page.Page, page.PageSize, page.TotalCount);
    }

    private async Task<IReadOnlyList<ConnectorCallItem>> ItemsAsync(IReadOnlyList<ConnectorCallEntity> calls, CancellationToken ct)
    {
        var runIds = calls.Where(c => c.RunId is not null).Select(c => c.RunId!.Value).ToList();
        var runs = runIds.Count == 0 ? new Dictionary<Guid, (int? ExitCode, double? DurationMs)>()
            : (await db.AgentScriptRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).Select(r => new { r.Id, r.ExitCode, r.DurationMs }).ToListAsync(ct))
                .ToDictionary(r => r.Id, r => (r.ExitCode, r.DurationMs));
        // The place in the line of a queued call, for its script and server.
        var queuedKeys = calls.Where(c => c.State == ConnectorCallState.Queued && c.ServerSensorId is not null && c.ScriptName is not null)
            .Select(c => c.ServerSensorId!.Value).Distinct().ToList();
        var lines = queuedKeys.Count == 0 ? []
            : await db.ConnectorCalls.AsNoTracking().Where(c => c.State == ConnectorCallState.Queued && c.ServerSensorId != null && queuedKeys.Contains(c.ServerSensorId.Value))
                .OrderBy(c => c.ReceivedAt).Select(c => new { c.Id, c.ServerSensorId, c.ScriptName }).ToListAsync(ct);
        return calls.Select(c =>
        {
            var run = c.RunId is { } id && runs.TryGetValue(id, out var r) ? r : ((int?)null, (double?)null);
            int? place = null;
            if (c.State == ConnectorCallState.Queued && c.ServerSensorId is not null)
            {
                var line = lines.Where(l => l.ServerSensorId == c.ServerSensorId && string.Equals(l.ScriptName, c.ScriptName, StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.Id).ToList();
                place = line.IndexOf(c.Id) is >= 0 and var p ? p + 1 : null;
            }
            var reviewState = ConnectorReviewRecord.Read(c.Review)?.State;
            double? queued = c.StartedAt is { } s ? (s - c.ReceivedAt).TotalSeconds
                : c.State == ConnectorCallState.Queued ? (Clock.UtcNow - Clock.AsUtc(c.ReceivedAt)).TotalSeconds : null;
            return new ConnectorCallItem(c.Id, c.ConnectorId is { } connector ? new Ref(connector, c.ConnectorName) : null, c.ConnectorName,
                c.CallerId is { } caller ? new Ref(caller, c.CallerName) : null, c.CallerName, Clock.AsUtc(c.ReceivedAt), c.From, c.State, c.Mode, c.DryRun, c.RefusedBy,
                c.WaitingFor, c.State == ConnectorCallState.Queued ? ConnectorWorker.WaitingText(c.WaitingFor) : null, UdpViews.NullableUtc(c.ExpiresAt),
                UdpViews.NullableUtc(c.StartedAt), UdpViews.NullableUtc(c.FinishedAt), c.ServerSensorId is { } server ? new Ref(server, c.ServerName ?? "") : null,
                c.ServerName, c.ScriptName, c.ScriptRevision, c.Function, c.Reference, reviewState, c.CollectedAt is not null, run.Item1, run.Item2,
                queued is { } q ? Math.Round(q, 1) : null, c.Message, place, c.RunId);
        }).ToList();
    }

    /// <summary>The top of <i>Calls</i> (§13.2): running now against the limit, the last 24 h in figures, the system sensor's state.</summary>
    public async Task<ConnectorCallFigures> FiguresAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var since = Clock.UtcNow.AddHours(-24);
        var running = await db.ConnectorCalls.CountAsync(c => c.State == ConnectorCallState.Running, ct);
        var queued = await db.ConnectorCalls.CountAsync(c => c.State == ConnectorCallState.Queued, ct);
        var day = await db.ConnectorCalls.AsNoTracking().Where(c => c.ReceivedAt >= since).GroupBy(c => c.State).Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int Of(ConnectorCallState s) => day.Where(d => d.Key == s).Sum(d => d.Count);
        var waits = await db.ConnectorCalls.AsNoTracking().Where(c => c.ReceivedAt >= since && c.StartedAt != null)
            .Select(c => new { c.ReceivedAt, c.StartedAt }).ToListAsync(ct);
        var sensor = await db.Sensors.AsNoTracking().Where(s => s.Type == SensorType.SYSTEM && s.Parameters == SystemSensorKeys.Connectors)
            .Select(s => new { s.Id, Name = s.Parent.Name + " / " + s.Name }).FirstOrDefaultAsync(ct);
        SensorState? sensorState = null;
        string? sensorMessage = null;
        if (sensor is not null)
        {
            var last = await db.Events.AsNoTracking().Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).Select(e => new { e.Code, e.Message })
                .FirstOrDefaultAsync(ct);
            sensorState = last is null ? SensorState.NoData : last.Code > 399 ? SensorState.Failing : SensorState.Ok;
            sensorMessage = last?.Code > 399 ? last.Message : null;
        }
        return new ConnectorCallFigures(o.Enabled, running, o.RunningLimit, queued, day.Sum(d => d.Count), Of(ConnectorCallState.Refused),
            Of(ConnectorCallState.Failed) + Of(ConnectorCallState.TimedOut) + Of(ConnectorCallState.Lost), Of(ConnectorCallState.Expired),
            waits.Count == 0 ? null : Math.Round(waits.Max(w => (w.StartedAt!.Value - w.ReceivedAt).TotalSeconds), 1), Math.Max(1, o.KeepCalls),
            sensor is null ? null : new Ref(sensor.Id, sensor.Name), sensorState, sensorMessage);
    }

    public async Task<ConnectorCallDetail> CallAsync(Guid id, CancellationToken ct)
    {
        var call = await db.ConnectorCalls.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Call");
        var item = (await ItemsAsync([call], ct))[0];
        var record = ConnectorReviewRecord.Read(call.Review);
        AgentScriptRunDetail? run = null;
        if (call.RunId is { } runId && await db.AgentScriptRuns.AnyAsync(r => r.Id == runId, ct))
        {
            run = await scripts.RunDetailAsync(runId, ct);
        }
        var steps = new List<ConnectorCallStep> { new("received", Clock.AsUtc(call.ReceivedAt), call.From is { } from ? $"from {from}" : null) };
        if (record is not null)
        {
            steps.Add(new("reviewed", null, record.State switch
            {
                ConnectorReviewRecord.Refused => $"refused: {record.RefusedLabel}",
                ConnectorReviewRecord.Unreviewed => $"ran unreviewed: {record.Reason}",
                ConnectorReviewRecord.Unavailable => $"couldn't run: {record.Reason}",
                _ => "passed",
            }));
        }
        if (call.State == ConnectorCallState.Queued)
        {
            steps.Add(new("waiting", null, ConnectorWorker.WaitingText(call.WaitingFor)));
        }
        if (call.StartedAt is { } started)
        {
            steps.Add(new("started", Clock.AsUtc(started), call.ServerName is { } server ? $"on {server}" : null));
        }
        if (call.FinishedAt is { } finished)
        {
            steps.Add(new("finished", Clock.AsUtc(finished), ConnectorGateway.StateName(call.State)));
        }
        return new ConnectorCallDetail(item, call.Arguments, call.SecureNames, call.Note, call.Redacted, call.IdempotencyKey, steps, ReviewOf(record), run);
    }

    private static ConnectorCallReview? ReviewOf(ConnectorReviewRecord? record) => record is null ? null
        : new ConnectorCallReview(record.State, record.Threshold,
            record.Answers.Select(a => new ConnectorReviewAnswer(a.Key, record.Labels.GetValueOrDefault(a.Key, a.Key), a.Value, a.Value >= record.Threshold)).ToList(),
            record.RefusedLabel, record.Reason, record.AiRequestId);

    /// <summary>Removes a final call's arguments and note from the history, and its run's arguments. Audited.</summary>
    public async Task<ConnectorCallDetail> RedactAsync(Guid id, CancellationToken ct)
    {
        var call = await db.ConnectorCalls.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Call");
        if (ConnectorGateway.IsActive(call.State))
        {
            throw ApiException.Conflict("call_active", "A call is redacted once it is final.");
        }
        call.Arguments = null;
        call.Note = null;
        call.Redacted = true;
        if (call.RunId is { } runId)
        {
            await db.AgentScriptRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Arguments, (string?)null).SetProperty(r => r.Redacted, true), ct);
        }
        UdpViews.Audit(db, accessor, AuditEvents.ConnectorCallRedacted, $"call {id} of {call.ConnectorName} by {call.CallerName}: arguments and note redacted",
            AuditTarget.Of(AuditTargets.ConnectorCall, id, $"{call.ConnectorName} {Clock.ToLocal(call.ReceivedAt):yyyy-MM-dd HH:mm:ss}", call.ConnectorId, call.CallerId));
        await db.SaveChangesAsync(ct);
        return await CallAsync(id, ct);
    }

    /// <summary>Cancels a queued call (§9.1). A running one can't be: the agents can't stop a run. Audited.</summary>
    public async Task<ConnectorCallDetail> CancelAsync(Guid id, CancellationToken ct)
    {
        var call = await db.ConnectorCalls.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Call");
        if (call.State == ConnectorCallState.Running)
        {
            throw ApiException.Conflict("call_running", "The call's run has started, and an agent can't stop a run: it ends with the script, or with its timeout.");
        }
        if (call.State != ConnectorCallState.Queued)
        {
            throw ApiException.Conflict("call_final", "The call is final already.");
        }
        var (_, user) = UdpViews.User(accessor);
        if (await CancelQueuedAsync(c => c.Id == id, $"Cancelled by {user ?? "an admin"} while queued.", ct) == 0)
        {
            throw ApiException.Conflict("call_running", "The call started meanwhile, and an agent can't stop a run.");
        }
        UdpViews.Audit(db, accessor, AuditEvents.ConnectorCallCancelled, $"call {id} of {call.ConnectorName} by {call.CallerName} cancelled while queued",
            AuditTarget.Of(AuditTargets.ConnectorCall, id, $"{call.ConnectorName} {Clock.ToLocal(call.ReceivedAt):yyyy-MM-dd HH:mm:ss}", call.ConnectorId, call.CallerId));
        await db.SaveChangesAsync(ct);
        return await CallAsync(id, ct);
    }

    /// <summary>Cancels the queued calls a filter selects, in one statement that can't race the worker's start; their waiters hear it.</summary>
    private async Task<int> CancelQueuedAsync(System.Linq.Expressions.Expression<Func<ConnectorCallEntity, bool>> which, string message, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var ids = await db.ConnectorCalls.Where(c => c.State == ConnectorCallState.Queued).Where(which).Select(c => c.Id).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return 0;
        }
        var count = await db.ConnectorCalls.Where(c => ids.Contains(c.Id) && c.State == ConnectorCallState.Queued).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.State, ConnectorCallState.Cancelled).SetProperty(c => c.Message, message).SetProperty(c => c.FinishedAt, now)
            .SetProperty(c => c.WaitingFor, (string?)null), ct);
        foreach (var id in ids)
        {
            vault.Remove(id);
            signals.Complete(id);
        }
        return count;
    }

    // --- connectors (§13.3, §13.5) ----------------------------------------------------------------------------------------------------

    public async Task<Paged<ConnectorItem>> ListAsync(ConnectorQuery query, CancellationToken ct)
    {
        var q = db.Connectors.AsNoTracking().Include(c => c.Script).Include(c => c.Grants).ThenInclude(g => g.Caller).AsQueryable();
        if (query.Type is { } type)
        {
            q = q.Where(c => c.Type == type);
        }
        if (query.Enabled is { } enabled)
        {
            q = q.Where(c => c.Enabled == enabled);
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            q = q.Where(c => EF.Functions.ILike(c.Name, pattern) || EF.Functions.ILike(c.Slug, pattern) || EF.Functions.ILike(c.Purpose, pattern));
        }
        var page = await Paging.PageAsync(q.OrderBy(c => c.Name), query, ct);
        var ids = page.Items.Select(c => c.Id).ToList();
        var since = Clock.UtcNow.AddHours(-24);
        var counts = await db.ConnectorCalls.AsNoTracking().Where(c => c.ConnectorId != null && ids.Contains(c.ConnectorId.Value) && c.ReceivedAt >= since)
            .GroupBy(c => c.ConnectorId).Select(g => new { g.Key, Calls = g.Count(), Refused = g.Count(c => c.State == ConnectorCallState.Refused) }).ToListAsync(ct);
        var servers = new Dictionary<AgentPlatform, IReadOnlyList<ConnectorServer>>();
        var items = new List<ConnectorItem>();
        foreach (var c in page.Items)
        {
            var platform = ConnectorTargets.PlatformOf(c.Type);
            if (!servers.TryGetValue(platform, out var known))
            {
                servers[platform] = known = await ConnectorTargets.ServersAsync(db, hub, platform, ct);
            }
            var warnings = c.Script is null ? [] : await ConnectorTargets.WarningsAsync(db, hub, c, c.Script, known, ct);
            var count = counts.FirstOrDefault(x => x.Key == c.Id);
            items.Add(new ConnectorItem(c.Id, c.Name, c.Slug, c.Type, c.Enabled, c.Script?.Server ?? "", c.Script?.Name ?? "", c.Script?.Function, c.Mode,
                c.Grants.Select(g => CallerRef(g.Caller)).OrderBy(r => r.Name).ToList(), c.ReviewEnabled,
                warnings.Where(w => w.Code is not ("allowed_hashes" or "wait_shorter")).ToList(), UdpViews.NullableUtc(c.LastCallAt), count?.Calls ?? 0,
                count?.Refused ?? 0));
        }
        return new Paged<ConnectorItem>(items, page.Page, page.PageSize, page.TotalCount);
    }

    private static ConnectorCallerRef CallerRef(ConnectorCallerEntity c)
    {
        var networks = ConnectorNetworks.Read(c.Networks);
        return new ConnectorCallerRef(c.Id, c.Name, c.Enabled, ConnectorNetworks.AnyAddress(networks), networks.Count, UdpViews.NullableUtc(c.LastCallAt));
    }

    private async Task<ConnectorEntity> LoadAsync(Guid id, CancellationToken ct, bool tracking = false)
    {
        var q = db.Connectors.Include(c => c.Script).Include(c => c.Arguments).Include(c => c.Grants).ThenInclude(g => g.Caller).AsQueryable();
        if (!tracking)
        {
            q = q.AsNoTracking();
        }
        return await q.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Connector");
    }

    public async Task<ConnectorDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var c = await LoadAsync(id, ct);
        var script = c.Script ?? throw ApiException.NotFound("Connector's target");
        var servers = await ConnectorTargets.ServersAsync(db, hub, ConnectorTargets.PlatformOf(c.Type), ct);
        var found = ConnectorTargets.Match(servers, script.Server);
        var warnings = await ConnectorTargets.WarningsAsync(db, hub, c, script, servers, ct);
        var parsed = ConnectorTargets.Parsed(script);
        var parameters = (parsed?.ParametersOf(script.Function) ?? []).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var arguments = c.Arguments.OrderBy(a => a.Order).Select(a =>
        {
            var rule = ConnectorArguments.Rule(a);
            return new ConnectorArgumentDetail(a.Name, a.Mode, a.Required, a.Pattern, rule.FixedValue, a.Description, a.Multiline, a.Order, rule.Parameter, rule.Kind,
                parameters.TryGetValue(a.Name, out var p) ? ConnectorArguments.Misfit(a, p, options.CurrentValue.PatternTimeoutMs) : null);
        }).ToList();
        var language = ConnectorTargets.LanguageOf(c.Type);
        var library = await db.AgentScripts.AsNoTracking().Where(s => s.Language == language && s.Name == script.Name)
            .Select(s => new { s.Id, s.Name, s.CurrentVersion }).FirstOrDefaultAsync(ct);
        ConnectorLibraryHint? hint = null;
        if (library is not null)
        {
            var hash = await db.AgentScriptVersions.AsNoTracking().Where(v => v.ScriptId == library.Id && v.Number == library.CurrentVersion).Select(v => v.Hash)
                .FirstOrDefaultAsync(ct);
            hint = new ConnectorLibraryHint(library.Id, library.Name, library.CurrentVersion, hash != script.Hash);
        }
        var since = Clock.UtcNow.AddHours(-24);
        var byState = await db.ConnectorCalls.AsNoTracking().Where(x => x.ConnectorId == id && x.ReceivedAt >= since).GroupBy(x => x.State)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var queued = await db.ConnectorCalls.CountAsync(x => x.ConnectorId == id && x.State == ConnectorCallState.Queued, ct);
        var running = await db.ConnectorCalls.CountAsync(x => x.ConnectorId == id && x.State == ConnectorCallState.Running, ct);
        var baseUrl = urls.Base;
        var target = new ConnectorTargetDetail(script.Server, script.Name, script.ScriptText, script.Hash, script.ScriptRevision, script.TakenFrom, script.ParseState,
            parsed, script.ParseError, script.Function, script.RunAsKind, script.Account, script.Shell, script.TimeoutSeconds, script.NeedsReview,
            ConnectorTargets.Sentence(c.Type, script), found.Count == 1 ? new Ref(found[0].SensorId, found[0].Name) : null, found.Select(f => f.Name).ToList());
        var o = options.CurrentValue;
        return new ConnectorDetail(c.Id, c.Name, c.Slug, c.Purpose, c.Type, c.Enabled, c.Mode, c.WaitSeconds, o.WaitLimit, c.ReturnOutput, c.MaxCallsPerHour, c.MaxQueued,
            c.MaxQueueSeconds,
            new ConnectorReviewSettings(c.ReviewEnabled, c.ReviewPurpose, c.ReviewThreshold, review.DefaultThreshold, ConnectorReview.Checks(c.ReviewChecks),
                c.ReviewFallback, review.Unusable()),
            target, arguments, c.Grants.Select(g => CallerRef(g.Caller)).OrderBy(r => r.Name).ToList(), warnings, ConnectorGateway.Describe(c, baseUrl), hint,
            new ConnectorFigures(byState.Sum(b => b.Count), byState.ToDictionary(b => ConnectorGateway.StateName(b.Key), b => b.Count), queued, running),
            $"{baseUrl}{ConnectorPaths.V1}/{c.Slug}", baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase), Clock.AsUtc(c.CreatedAt), c.CreatedBy,
            Clock.AsUtc(c.UpdatedAt), UdpViews.NullableUtc(c.LastCallAt));
    }

    /// <summary>The connector's text against the library's current version of the script of the same name (§3.3).</summary>
    public async Task<ConnectorCompare> CompareAsync(Guid id, CancellationToken ct)
    {
        var c = await LoadAsync(id, ct);
        var language = ConnectorTargets.LanguageOf(c.Type);
        var library = await db.AgentScripts.AsNoTracking().Where(s => s.Language == language && s.Name == c.Script!.Name).FirstOrDefaultAsync(ct)
            ?? throw ApiException.NotFound($"A library script named {c.Script!.Name}");
        var version = await db.AgentScriptVersions.AsNoTracking().SingleAsync(v => v.ScriptId == library.Id && v.Number == library.CurrentVersion, ct);
        return new ConnectorCompare(library.Id, library.Name, version.Number, version.Text, version.Hash, c.Script.ScriptText, c.Script.Hash);
    }

    public async Task<ConnectorDetail> CreateAsync(ConnectorRequest request, CancellationToken ct)
    {
        var type = request.Type ?? throw ApiException.Invalid("type", "WindowsScript or LinuxScript.");
        new ApiValidator().Check(Enum.IsDefined(type), "type", "WindowsScript or LinuxScript.").ThrowIfInvalid();
        var v = new ApiValidator();
        await ValidateAsync(v, request, type, null, ct);
        v.ThrowIfInvalid();
        var (_, user) = UdpViews.User(accessor);
        var now = Clock.UtcNow;
        var connector = new ConnectorEntity
        {
            Id = Guid.NewGuid(), Type = type, Enabled = false, CreatedAt = now, UpdatedAt = now, CreatedBy = user,
        };
        Apply(connector, request);
        var script = new ConnectorScriptEntity { ConnectorId = connector.Id, ScriptRevision = 1 };
        ApplyTarget(script, request.Target, type);
        await ConnectorParses.ParseAtSaveAsync(db, type, script, ct);
        connector.Script = script;
        if (ConnectorTargets.Usable(type, script)?.ParametersOf(script.Function) is { } parameters)
        {
            connector.Arguments = parameters.Select((p, i) => ConnectorArguments.New(connector.Id, p, i)).ToList();
        }
        db.Connectors.Add(connector);
        await GrantAsync(connector, request.CallerIds ?? [], user, ct);
        UdpViews.Audit(db, accessor, AuditEvents.ConnectorChanged,
            $"connector {connector.Name} ({ConnectorTargets.Label(type)}) created, disabled: {ConnectorTargets.Sentence(type, script)}, text {script.Hash[..12]}",
            AuditTarget.Of(AuditTargets.Connector, connector.Id, connector.Name));
        await db.SaveChangesAsync(ct);
        await results.ParsePendingAsync(null, ct);
        // §12.3: the system sensor comes with the first connector.
        await systemSensors.IdAsync(SystemSensorKeys.Connectors, ct);
        return await DetailAsync(connector.Id, ct);
    }

    public async Task<ConnectorDetail> UpdateAsync(Guid id, ConnectorRequest request, CancellationToken ct)
    {
        var connector = await LoadAsync(id, ct, tracking: true);
        var script = connector.Script ?? throw ApiException.NotFound("Connector's target");
        var v = new ApiValidator().Check(request.Type is null || request.Type == connector.Type, "type",
            $"A connector's type can't change: {connector.Name} is a {ConnectorTargets.Label(connector.Type)}. Create a new connector.");
        await ValidateAsync(v, request, connector.Type, id, ct);
        v.ThrowIfInvalid();
        var (_, user) = UdpViews.User(accessor);
        var wasEnabled = connector.Enabled;
        Apply(connector, request);
        connector.UpdatedAt = Clock.UtcNow;
        var oldHash = script.Hash;
        var oldFunction = script.Function;
        var newText = request.Target.ScriptText.TrimStart('\uFEFF');
        var textChanged = !string.Equals(ScriptHashes.Of(newText), oldHash, StringComparison.Ordinal);
        ApplyTarget(script, request.Target, connector.Type);
        var changes = new List<string>();
        if (textChanged)
        {
            script.ScriptRevision++;
            await ConnectorParses.ParseAtSaveAsync(db, connector.Type, script, ct);
            changes.Add($"a new text, revision {script.ScriptRevision} ({oldHash[..12]} → {script.Hash[..12]})");
        }
        if (textChanged || !string.Equals(oldFunction, script.Function, StringComparison.Ordinal))
        {
            // §5.5: the choices follow the parse by name.
            if (ConnectorTargets.Usable(connector.Type, script)?.ParametersOf(script.Function) is { } parameters)
            {
                var reconciled = ConnectorArguments.Reconcile(connector.Id, connector.Arguments, parameters, options.CurrentValue.PatternTimeoutMs);
                db.ConnectorArguments.RemoveRange(reconciled.Removed);
                db.ConnectorArguments.AddRange(reconciled.Added);
                script.NeedsReview = reconciled.NeedsReview;
                changes.AddRange(reconciled.Changes);
            }
        }
        if (request.Arguments is { } arguments)
        {
            ApplyArguments(connector, arguments);
            script.NeedsReview = false;
            changes.Add("the arguments saved");
        }
        if (request.CallerIds is { } callerIds)
        {
            var wanted = callerIds.Distinct().ToHashSet();
            var gone = connector.Grants.Where(g => !wanted.Contains(g.CallerId)).ToList();
            db.ConnectorGrants.RemoveRange(gone);
            await GrantAsync(connector, wanted.Where(w => connector.Grants.All(g => g.CallerId != w)).ToList(), user, ct);
        }
        UdpViews.Audit(db, accessor, AuditEvents.ConnectorChanged,
            $"connector {connector.Name} changed{(changes.Count > 0 ? ": " + string.Join("; ", changes) : "")}",
            AuditTarget.Of(AuditTargets.Connector, connector.Id, connector.Name));
        await db.SaveChangesAsync(ct);
        if (wasEnabled && !connector.Enabled)
        {
            // §9.1: disabling cancels the connector's queued calls.
            await CancelQueuedAsync(c => c.ConnectorId == id, $"{connector.Name} was disabled while the call waited.", ct);
        }
        if (textChanged)
        {
            await results.ParsePendingAsync(null, ct);
        }
        return await DetailAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var connector = await db.Connectors.Include(c => c.Script).SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Connector");
        await CancelQueuedAsync(c => c.ConnectorId == id, $"{connector.Name} was deleted while the call waited.", ct);
        db.Connectors.Remove(connector);
        UdpViews.Audit(db, accessor, AuditEvents.ConnectorChanged, $"connector {connector.Name} deleted (its calls stay in the history)",
            AuditTarget.Of(AuditTargets.Connector, connector.Id, connector.Name));
        await db.SaveChangesAsync(ct);
    }

    private async Task GrantAsync(ConnectorEntity connector, IReadOnlyCollection<Guid> callerIds, string? user, CancellationToken ct)
    {
        if (callerIds.Count == 0)
        {
            return;
        }
        var callers = await db.ConnectorCallers.Where(c => callerIds.Contains(c.Id)).ToListAsync(ct);
        foreach (var caller in callers)
        {
            db.ConnectorGrants.Add(new ConnectorGrantEntity
            {
                CallerId = caller.Id, ConnectorId = connector.Id, Name = $"{caller.Name} → {connector.Name}", CreatedAt = Clock.UtcNow, CreatedBy = user,
            });
        }
    }

    private void Apply(ConnectorEntity c, ConnectorRequest r)
    {
        c.Name = r.Name.Trim();
        c.Slug = r.Slug.Trim().ToLowerInvariant();
        c.Purpose = r.Purpose.Trim();
        c.Enabled = r.Enabled ?? c.Enabled;
        c.Mode = r.Mode ?? c.Mode;
        c.WaitSeconds = r.WaitSeconds ?? c.WaitSeconds;
        c.ReturnOutput = r.ReturnOutput ?? c.ReturnOutput;
        c.MaxCallsPerHour = r.MaxCallsPerHour ?? c.MaxCallsPerHour;
        c.MaxQueued = r.MaxQueued ?? c.MaxQueued;
        c.MaxQueueSeconds = r.MaxQueueSeconds ?? c.MaxQueueSeconds;
        c.ReviewEnabled = r.ReviewEnabled ?? c.ReviewEnabled;
        c.ReviewPurpose = r.ReviewPurpose ?? c.ReviewPurpose;
        c.ReviewThreshold = r.ReviewThreshold;
        if (r.ReviewChecks is { } checks)
        {
            c.ReviewChecks = ConnectorReview.WriteChecks(checks.Select(k => new ConnectorReviewCheck(k.Label.Trim(), k.Question.Trim())));
        }
        c.ReviewFallback = r.ReviewFallback ?? c.ReviewFallback;
    }

    private static void ApplyTarget(ConnectorScriptEntity s, ConnectorTargetRequest t, ConnectorType type)
    {
        var linux = type == ConnectorType.LinuxScript;
        s.Server = t.Server.Trim();
        s.Name = t.ScriptName.Trim();
        s.ScriptText = t.ScriptText.TrimStart('\uFEFF');
        s.Hash = ScriptHashes.Of(s.ScriptText);
        s.TakenFrom = Text.Trimmed(t.TakenFrom) is { } from ? Cuts.Cut(from, 500) : s.TakenFrom;
        s.Function = Text.Trimmed(t.Function);
        s.RunAsKind = t.RunAsKind;
        s.Account = t.RunAsKind == RunAsKind.Service ? null : Text.Trimmed(t.Account);
        s.Shell = linux ? "bash" : t.Shell ?? "powershell";
        s.TimeoutSeconds = t.TimeoutSeconds ?? AgentScriptService.DefaultTimeoutSeconds;
    }

    private void ApplyArguments(ConnectorEntity connector, IReadOnlyList<ConnectorArgumentRequest> arguments)
    {
        foreach (var request in arguments)
        {
            var row = connector.Arguments.Single(a => string.Equals(a.Name, request.Name, StringComparison.OrdinalIgnoreCase));
            var p = ConnectorArguments.Parameter(row);
            row.Mode = request.Mode;
            row.Required = request.Mode == ConnectorArgumentMode.Exposed && (request.Required || p.Mandatory);
            row.Pattern = request.Mode == ConnectorArgumentMode.Exposed ? Text.Trimmed(request.Pattern) : null;
            row.FixedValue = request.Mode == ConnectorArgumentMode.Fixed && request.FixedValue is { } value ? value.GetRawText() : null;
            row.Description = Text.Trimmed(request.Description);
            row.Multiline = request.Mode == ConnectorArgumentMode.Exposed && request.Multiline && ConnectorArguments.KindOf(p) == ConnectorValueKind.Text;
        }
    }

    private async Task ValidateAsync(ApiValidator v, ConnectorRequest r, ConnectorType type, Guid? id, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var linux = type == ConnectorType.LinuxScript;
        var name = r.Name?.Trim() ?? "";
        var slug = r.Slug?.Trim().ToLowerInvariant() ?? "";
        v.Check(name.Length is >= 1 and <= MaxName, "name", $"The name has 1 to {MaxName} characters.")
            .Check(SlugPattern().IsMatch(slug), "slug", "2 to 63 characters: lower-case letters, digits and hyphens, starting with a letter or a digit.")
            .Check(r.Purpose is { Length: >= 1 and <= MaxPurpose }, "purpose", $"One paragraph on what the connector is for, at most {MaxPurpose} characters: the review reads it too.");
        if (name.Length > 0)
        {
            v.Check(!await db.Connectors.AnyAsync(c => c.Name == name && c.Id != id, ct), "name", "Another connector has this name.");
        }
        if (slug.Length > 0)
        {
            v.Check(!await db.Connectors.AnyAsync(c => c.Slug == slug && c.Id != id, ct), "slug", "Another connector has this slug.");
        }
        var t = r.Target;
        if (t is null)
        {
            v.Add("target", "The target: the server, the script's name and its text, the account.");
            return;
        }
        v.Check(t.Server is { Length: >= 1 and <= 500 } && t.Server.Trim().Length > 0, "target.server", "The server's name as PNMT shows it (WEB01 / Windows), or the host's name.")
            .Check(t.ScriptName is { Length: >= 1 and <= 200 } && t.ScriptName.Trim().Length > 0, "target.scriptName", "The script's name, 1 to 200 characters.")
            .Check(t.ScriptText is { Length: >= 1 and <= AgentScriptService.MaxScriptChars }, "target.scriptText",
                $"The script's text, 1 to {AgentScriptService.MaxScriptChars} characters.")
            .Check(Text.Trimmed(t.Function) is null || FunctionPattern().IsMatch(t.Function!.Trim()), "target.function",
                "A function's name: letters, digits, _ and -, starting with a letter or _.")
            .Check((t.TimeoutSeconds ?? AgentScriptService.DefaultTimeoutSeconds) is >= 10 and <= AgentScriptService.MaxTimeoutSeconds, "target.timeoutSeconds",
                $"Between 10 and {AgentScriptService.MaxTimeoutSeconds} seconds.");
        var account = Text.Trimmed(t.Account);
        if (linux)
        {
            v.Check(t.RunAsKind is RunAsKind.Service or RunAsKind.User, "target.runAsKind", "Service (root) or User (a local account by name).")
                .Check(t.RunAsKind != RunAsKind.User || (account is not null && LinuxAccountName().IsMatch(account)), "target.account", "A local account's name, as in /etc/passwd.")
                .Check(t.Shell is null or "bash", "target.shell", "bash.");
        }
        else
        {
            v.Check(t.RunAsKind is RunAsKind.Service or RunAsKind.Stored or RunAsKind.Gmsa, "target.runAsKind",
                "Service, Stored or Gmsa: nobody is there to type a password for a connector's call.")
                .Check(t.RunAsKind == RunAsKind.Service || account is { Length: >= 1 and <= 256 }, "target.account", "DOMAIN\\user, at most 256 characters.")
                .Check(t.RunAsKind != RunAsKind.Gmsa || account?.EndsWith('$') == true, "target.account", "A gMSA's name ends with $.")
                .Check(t.Shell is null or "powershell" or "pwsh", "target.shell", "powershell (Windows PowerShell 5.1) or pwsh.");
        }
        v.Check(r.WaitSeconds is null || (r.WaitSeconds >= 5 && r.WaitSeconds <= o.WaitLimit), "waitSeconds", $"Between 5 and {o.WaitLimit} seconds.")
            .Check(r.MaxCallsPerHour is null or >= 1 and <= 3600, "maxCallsPerHour", "Between 1 and 3600.")
            .Check(r.MaxQueued is null or >= 1 and <= 100, "maxQueued", "Between 1 and 100.")
            .Check(r.MaxQueueSeconds is null or >= 10 and <= 3600, "maxQueueSeconds", "Between 10 and 3600 seconds.")
            .Check(r.ReviewThreshold is null or >= 0.05 and <= 0.95, "reviewThreshold", "Between 0.05 and 0.95; empty for Ai__Guard__Threshold.")
            .Check(r.ReviewChecks is null || r.ReviewChecks.Count <= ConnectorReview.MaxChecks, "reviewChecks", $"At most {ConnectorReview.MaxChecks} checks.");
        foreach (var (check, i) in (r.ReviewChecks ?? []).Select((c, i) => (c, i)))
        {
            v.Check(check.Label?.Trim() is { Length: >= 1 and <= ConnectorReview.MaxLabel }, $"reviewChecks[{i}].label", $"A label of 1 to {ConnectorReview.MaxLabel} characters.")
                .Check(check.Question?.Trim() is { Length: >= 10 and <= ConnectorReview.MaxQuestion }, $"reviewChecks[{i}].question",
                    $"A yes-or-no question of 10 to {ConnectorReview.MaxQuestion} characters, answered yes when the call must be refused.");
        }
        if (r.ReviewEnabled == true && review.Unusable() is { } unusable)
        {
            v.Add("reviewEnabled", unusable);
        }
        if (r.CallerIds is { Count: > 0 } callerIds)
        {
            var known = await db.ConnectorCallers.CountAsync(c => callerIds.Contains(c.Id), ct);
            v.Check(known == callerIds.Distinct().Count(), "callerIds", "No such caller.");
        }
        if (r.Arguments is { } arguments && id is { } existing)
        {
            var rows = await db.ConnectorArguments.AsNoTracking().Where(a => a.ConnectorId == existing).ToListAsync(ct);
            foreach (var (argument, i) in arguments.Select((a, i) => (a, i)))
            {
                ValidateArgument(v, $"arguments[{i}]", argument, rows.FirstOrDefault(row => string.Equals(row.Name, argument.Name, StringComparison.OrdinalIgnoreCase)), o);
            }
        }
    }

    private static void ValidateArgument(ApiValidator v, string field, ConnectorArgumentRequest a, ConnectorArgumentEntity? row, ConnectorOptions o)
    {
        if (row is null)
        {
            v.Add($"{field}.name", $"The target has no parameter {a.Name}.");
            return;
        }
        var p = ConnectorArguments.Parameter(row);
        var kind = ConnectorArguments.KindOf(p);
        v.Check(Enum.IsDefined(a.Mode), $"{field}.mode", "Exposed, Fixed or Omitted.")
            .Check(a.Mode != ConnectorArgumentMode.Fixed || !p.IsSecure, $"{field}.mode", $"{p.Name} is secure: PNMT stores no script secrets, so it is exposed or omitted.")
            .Check(a.Mode != ConnectorArgumentMode.Omitted || !p.Mandatory, $"{field}.mode", $"{p.Name} is mandatory: expose it or fix it.")
            .Check(a.Description is null || a.Description.Length <= MaxDescription, $"{field}.description", $"At most {MaxDescription} characters.")
            .Check(!a.Multiline || kind == ConnectorValueKind.Text, $"{field}.multiline", "Only a text argument can be multi-line.");
        if (Text.Trimmed(a.Pattern) is { } pattern && a.Mode == ConnectorArgumentMode.Exposed)
        {
            v.Check(pattern.Length <= ConnectorPatterns.MaxLength, $"{field}.pattern", $"At most {ConnectorPatterns.MaxLength} characters.")
                .Check(kind != ConnectorValueKind.Bool, $"{field}.pattern", "A pattern on true or false says nothing.");
            if (ConnectorPatterns.Compile(pattern, o.PatternTimeoutMs).Error is { } error)
            {
                v.Add($"{field}.pattern", $"The pattern doesn't compile: {error}");
            }
        }
        if (a.Mode == ConnectorArgumentMode.Fixed && !p.IsSecure)
        {
            if (a.FixedValue is not { } value || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                v.Add($"{field}.fixedValue", $"A fixed argument needs its value.");
                return;
            }
            var rule = new ConnectorArgumentRule(p.Name, ConnectorArgumentMode.Exposed, false, null, value, false, p, null, 0);
            if (ConnectorArguments.ValueProblem(rule, value, o.MaxValueChars, o.PatternTimeoutMs) is { } problem)
            {
                v.Add($"{field}.fixedValue", $"The value {problem}.");
                return;
            }
            if (ScriptArguments.Check([p with { Mandatory = false }], new Dictionary<string, JsonElement> { [p.Name] = value }, []).FirstOrDefault() is { Message: { } message })
            {
                v.Add($"{field}.fixedValue", message);
            }
        }
    }

    /// <summary>
    /// <c>POST /connectors/{id}/check</c> (§11): sample arguments and a note against the connector's checks, and optionally the review, with
    /// its probabilities, which a person in PNMT may see. No run, no limits.
    /// </summary>
    public async Task<ConnectorCheckResult> CheckAsync(Guid id, ConnectorCheckRequest request, CancellationToken ct)
    {
        var connector = await LoadAsync(id, ct);
        var o = options.CurrentValue;
        var rules = connector.Arguments.OrderBy(a => a.Order).Select(ConnectorArguments.Rule).ToList();
        var given = request.Arguments ?? [];
        var check = ConnectorArguments.Check(rules, given, o.MaxValueChars, o.PatternTimeoutMs);
        var servers = await ConnectorTargets.ServersAsync(db, hub, ConnectorTargets.PlatformOf(connector.Type), ct);
        var found = ConnectorTargets.Match(servers, connector.Script!.Server);
        var warnings = await ConnectorTargets.WarningsAsync(db, hub, connector, connector.Script, servers, ct);
        ConnectorCallReview? reviewed = null;
        if (request.Review)
        {
            if (review.Unusable() is { } unusable)
            {
                throw ApiException.Conflict("review_unavailable", unusable);
            }
            var fixedArguments = rules.Where(r => r.Mode == ConnectorArgumentMode.Fixed && r.FixedValue is not null).ToDictionary(r => r.Parameter.Name, r => r.FixedValue!.Value);
            var state = ConnectorReview.State(connector, connector.Script, found.Count == 1 ? found[0].Name : null, fixedArguments, check.CallerPlain,
                check.Secure.ToDictionary(s => s.Key, s => s.Value.Length), null, Text.Trimmed(request.Note));
            reviewed = ReviewOf(await review.ReviewAsync(connector, state, ct));
        }
        return new ConnectorCheckResult(check.Ok, check.Ok ? null : check.Message, check.Fields, found.Count == 1 ? new Ref(found[0].SensorId, found[0].Name) : null,
            warnings, reviewed);
    }

    /// <summary>
    /// <c>POST /connectors/parse</c>: a text's parse before a save. Bash's header at once; PowerShell's when one exists for its hash (the
    /// library, a connector, or a recent parse), otherwise pending, and the text goes to a connected agent: ask again in a moment.
    /// </summary>
    public async Task<ConnectorParseResult> ParseAsync(ConnectorParseRequest request, CancellationToken ct)
    {
        new ApiValidator().Check(Enum.IsDefined(request.Type), "type", "WindowsScript or LinuxScript.")
            .Check(request.Text is { Length: >= 1 and <= AgentScriptService.MaxScriptChars }, "text", $"1 to {AgentScriptService.MaxScriptChars} characters.").ThrowIfInvalid();
        var language = ConnectorTargets.LanguageOf(request.Type);
        var hash = ScriptHashes.Of(request.Text);
        if (request.Type == ConnectorType.LinuxScript)
        {
            var header = AgentScriptResults.Capped(BashScriptHeader.Parse(request.Text));
            return new ConnectorParseResult(ScriptParseState.Parsed, results.Recent(hash, AgentPlatform.Linux) is { } agent
                ? AgentScriptResults.Merge(header, agent) : header, hash, results.IsParserAvailable(language));
        }
        var probe = new ConnectorScriptEntity { ConnectorId = Guid.Empty, ScriptText = request.Text };
        await ConnectorParses.ParseAtSaveAsync(db, request.Type, probe, ct);
        if (probe.Parsed is { } parsed)
        {
            return new ConnectorParseResult(ScriptParseState.Parsed, JsonSerializer.Deserialize<ParsedScript>(parsed, AgentJson.Options), hash, true);
        }
        if (results.Recent(hash, AgentPlatform.Windows) is { } recent)
        {
            return new ConnectorParseResult(ScriptParseState.Parsed, recent, hash, true);
        }
        var sent = await results.ParseTextAsync(language, request.Text, ct);
        return new ConnectorParseResult(ScriptParseState.Pending, null, hash, sent);
    }

    /// <summary><c>POST /connectors/patterns/test</c>: compiles a pattern and matches values, as a call would (§5.3).</summary>
    public ConnectorPatternResult TestPattern(ConnectorPatternRequest request)
    {
        var pattern = request.Pattern ?? "";
        if (pattern.Length is < 1 or > ConnectorPatterns.MaxLength)
        {
            return new ConnectorPatternResult(false, $"1 to {ConnectorPatterns.MaxLength} characters.", false, []);
        }
        var (regex, error) = ConnectorPatterns.Compile(pattern, options.CurrentValue.PatternTimeoutMs);
        if (regex is null)
        {
            return new ConnectorPatternResult(false, error, false, []);
        }
        return new ConnectorPatternResult(true, null, !regex.Options.HasFlag(RegexOptions.NonBacktracking),
            (request.Values ?? []).Take(20).Select(value => new ConnectorPatternMatch(value, ConnectorPatterns.Matches(regex, value))).ToList());
    }

    // --- the selectors (§3.3): they copy, they don't link --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ConnectorServerOption>> ServerOptionsAsync(ConnectorType type, CancellationToken ct) =>
        (await ConnectorTargets.ServersAsync(db, hub, ConnectorTargets.PlatformOf(type), ct)).Select(s => new ConnectorServerOption(s.SensorId, s.Name, s.Online,
            s.Capabilities.Contains(AgentCapabilities.Scripts), s.Capabilities.Contains(AgentCapabilities.ScriptFunctions),
            !s.Online ? "offline" : !s.Capabilities.Contains(AgentCapabilities.Scripts) ? "scripts off" : null)).ToList();

    public async Task<IReadOnlyList<ConnectorLibraryScript>> LibraryAsync(ConnectorType type, CancellationToken ct)
    {
        var language = ConnectorTargets.LanguageOf(type);
        var list = await db.AgentScripts.AsNoTracking().Where(s => s.Language == language).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Description, s.CurrentVersion, Versions = s.Versions.OrderByDescending(v => v.Number).Select(v => v.Number).ToList() })
            .ToListAsync(ct);
        return list.Select(s => new ConnectorLibraryScript(s.Id, s.Name, s.Description, s.CurrentVersion, s.Versions)).ToList();
    }

    public async Task<ConnectorLibraryVersion> LibraryVersionAsync(Guid scriptId, int number, CancellationToken ct)
    {
        var script = await db.AgentScripts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == scriptId, ct) ?? throw ApiException.NotFound("Script");
        var version = await db.AgentScriptVersions.AsNoTracking().SingleOrDefaultAsync(v => v.ScriptId == scriptId && v.Number == number, ct)
            ?? throw ApiException.NotFound("Version");
        var parsed = version.Parsed is null ? null : JsonSerializer.Deserialize<ParsedScript>(version.Parsed, AgentJson.Options);
        return new ConnectorLibraryVersion(script.Id, script.Name, version.Number, version.Text, version.Hash, version.ParseState, parsed,
            $"library: {script.Name} v{version.Number}");
    }

    public async Task<IReadOnlyList<ConnectorRegisteredScript>> RegisteredAsync(Guid sensorId, CancellationToken ct)
    {
        var local = await scripts.LocalScriptsAsync(sensorId, ct);
        var server = await db.Sensors.AsNoTracking().Where(s => s.Id == sensorId).Select(s => s.Parent.Name + " / " + s.Name).FirstOrDefaultAsync(ct) ?? "the server";
        return local.Scripts.Select(s => new ConnectorRegisteredScript(s.Script.Name, s.Script.Sha256, s.Script.Matches, s.Script.RunAs, s.Script.Account, s.RunAsName,
            s.Script.Matches ? s.Script.Text : null, s.Script.Parsed, $"{server}: registered {s.Script.Name}")).ToList();
    }

    /// <summary>
    /// The accounts (§3.3): on Windows the run-as accounts (never one typed per run) and the service, each with whether the server's policy
    /// allows it when a server is given; on Linux the server's accounts with whether run_as allows them.
    /// </summary>
    public async Task<IReadOnlyList<ConnectorAccountOption>> AccountsAsync(ConnectorType type, Guid? sensorId, CancellationToken ct)
    {
        if (type == ConnectorType.LinuxScript)
        {
            if (sensorId is null)
            {
                return [new ConnectorAccountOption(RunAsKind.Service, null, "root (the service)", null, "Choose a server to see its accounts.")];
            }
            var linux = await scripts.RunAsAccountsAsync(sensorId.Value, ct);
            return linux.Accounts.Select(a => new ConnectorAccountOption(a.Kind, a.Kind == RunAsKind.Service ? null : a.Name,
                a.Kind == RunAsKind.Service ? "root (the service)" : a.Name, a.Allowed, a.Refused)).ToList();
        }
        ScriptsSection? section = sensorId is { } id ? await AgentSections.ReadAsync<ScriptsSection>(db, id, SectionNames.Scripts, ct) : null;
        bool? Allowed(RunAsKind kind, string? account)
        {
            if (section is null)
            {
                return null;
            }
            if (!section.Allowed || !section.AnyScript)
            {
                return false;
            }
            var name = kind == RunAsKind.Service ? "service" : account ?? "";
            return kind == RunAsKind.Service ? section.RunAs.Contains("service", StringComparer.OrdinalIgnoreCase)
                : section.RunAs.Count == 0 || section.RunAs.Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        var options = new List<ConnectorAccountOption> { new(RunAsKind.Service, null, "the service (LocalSystem)", Allowed(RunAsKind.Service, null), null) };
        var stored = await db.WindowsRunAs.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        options.AddRange(stored.Select(r => r.Kind == RunAsKind.TypedPerRun
            ? new ConnectorAccountOption(r.Kind, r.Account, r.Name, false, "Its password is typed per run, and nobody is there to type it for a connector's call.")
            : new ConnectorAccountOption(r.Kind == RunAsKind.Service ? RunAsKind.Service : r.Kind, r.Account,
                r.Kind == RunAsKind.Service ? r.Name : $"{r.Name} ({r.Account})", Allowed(r.Kind, r.Account), null)));
        return options;
    }

    // --- callers (§6, §13.4) -----------------------------------------------------------------------------------------------------------

    public async Task<Paged<ConnectorCallerItem>> CallersAsync(PageQuery query, CancellationToken ct)
    {
        var q = db.ConnectorCallers.AsNoTracking().Include(c => c.Grants).ThenInclude(g => g.Connector).AsQueryable();
        if (Text.Pattern(query.Search) is { } pattern)
        {
            q = q.Where(c => EF.Functions.ILike(c.Name, pattern) || EF.Functions.ILike(c.Description ?? "", pattern));
        }
        var page = await Paging.PageAsync(q.OrderBy(c => c.Name), query, ct);
        var ids = page.Items.Select(c => c.Id).ToList();
        var counts = await CallerCountsAsync(ids, ct);
        return new Paged<ConnectorCallerItem>(page.Items.Select(c =>
        {
            var networks = ConnectorNetworks.Read(c.Networks);
            var count = counts.GetValueOrDefault(c.Id);
            return new ConnectorCallerItem(c.Id, c.Name, c.Description, c.Enabled, c.KeyHint, networks.Count, ConnectorNetworks.AnyAddress(networks),
                c.Grants.Select(g => new Ref(g.ConnectorId, g.Connector.Name)).OrderBy(r => r.Name).ToList(), UdpViews.NullableUtc(c.LastCallAt), c.LastCallFrom,
                count.Calls, count.Refused);
        }).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    private async Task<Dictionary<Guid, (int Calls, int Refused)>> CallerCountsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var since = Clock.UtcNow.AddHours(-24);
        return (await db.ConnectorCalls.AsNoTracking().Where(c => c.CallerId != null && ids.Contains(c.CallerId.Value) && c.ReceivedAt >= since)
            .GroupBy(c => c.CallerId).Select(g => new { g.Key, Calls = g.Count(), Refused = g.Count(c => c.State == ConnectorCallState.Refused) }).ToListAsync(ct))
            .ToDictionary(x => x.Key!.Value, x => (x.Calls, x.Refused));
    }

    public async Task<ConnectorCallerDetail> CallerAsync(Guid id, CancellationToken ct)
    {
        var c = await db.ConnectorCallers.AsNoTracking().Include(x => x.Grants).ThenInclude(g => g.Connector).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw ApiException.NotFound("Caller");
        var networks = ConnectorNetworks.Read(c.Networks);
        var inside = ConnectorNetworks.Inside(networks);
        var count = (await CallerCountsAsync([id], ct)).GetValueOrDefault(id);
        var (yours, hint) = YourAddress();
        return new ConnectorCallerDetail(c.Id, c.Name, c.Description, c.Enabled, c.KeyHint, Clock.AsUtc(c.KeyIssuedAt),
            networks.Select((n, i) => new ConnectorNetworkItem(n.Cidr, n.Label, inside[i])).ToList(), ConnectorNetworks.AnyAddress(networks),
            c.Grants.Select(g => new ConnectorCallerConnector(g.ConnectorId, g.Connector.Name, g.Connector.Slug, g.Connector.Enabled, g.Connector.Type))
                .OrderBy(x => x.Name).ToList(),
            UdpViews.NullableUtc(c.LastCallAt), c.LastCallFrom, count.Calls, count.Refused, yours, hint, Clock.AsUtc(c.CreatedAt), c.CreatedBy);
    }

    /// <summary>The address PNMT sees for the admin's browser (§6.3), with a hint when it is a Docker bridge's and Proxy isn't set.</summary>
    private (string? Address, string? Hint) YourAddress()
    {
        var address = accessor.HttpContext?.Connection.RemoteIpAddress;
        if (address is null)
        {
            return (null, null);
        }
        var plain = ConnectorNetworks.Plain(address);
        var docker = Hosting.ProxySetting.Docker.Contains(plain);
        var hint = docker && string.IsNullOrWhiteSpace(configuration["Proxy"])
            ? "This is a Docker bridge's address: PNMT sees the proxy, not your browser. Set Proxy=docker in .env (deploy/README.md), or callers' networks judge the proxy."
            : null;
        return (plain.ToString(), hint);
    }

    public async Task<ConnectorCallerCreated> CreateCallerAsync(ConnectorCallerRequest request, CancellationToken ct)
    {
        var networks = await ValidateCallerAsync(request, null, ct);
        var (_, user) = UdpViews.User(accessor);
        var now = Clock.UtcNow;
        var key = ConnectorKeys.New();
        var caller = new ConnectorCallerEntity
        {
            Id = Guid.NewGuid(), Name = request.Name.Trim(), Description = Text.Trimmed(request.Description), Enabled = request.Enabled == true && networks.Count > 0,
            Networks = ConnectorNetworks.Write(networks), KeyHash = ConnectorKeys.Hash(key), KeyProtected = protector.Protect(key), KeyHint = ConnectorKeys.Hint(key),
            KeyIssuedAt = now, CreatedAt = now, UpdatedAt = now, CreatedBy = user,
        };
        db.ConnectorCallers.Add(caller);
        await GrantCallerAsync(caller, request.ConnectorIds ?? [], user, ct);
        UdpViews.Audit(db, accessor, AuditEvents.ConnectorCallerChanged,
            $"caller {caller.Name} created{(caller.Enabled ? "" : ", disabled")}, its key …{caller.KeyHint}, {networks.Count} network(s)",
            AuditTarget.Of(AuditTargets.ConnectorCaller, caller.Id, caller.Name));
        await db.SaveChangesAsync(ct);
        return new ConnectorCallerCreated(await CallerAsync(caller.Id, ct), key);
    }

    public async Task<ConnectorCallerDetail> UpdateCallerAsync(Guid id, ConnectorCallerRequest request, CancellationToken ct)
    {
        var caller = await db.ConnectorCallers.Include(c => c.Grants).ThenInclude(g => g.Connector).SingleOrDefaultAsync(c => c.Id == id, ct)
            ?? throw ApiException.NotFound("Caller");
        var networks = await ValidateCallerAsync(request, id, ct);
        var (_, user) = UdpViews.User(accessor);
        caller.Name = request.Name.Trim();
        caller.Description = Text.Trimmed(request.Description);
        caller.Networks = ConnectorNetworks.Write(networks);
        caller.Enabled = request.Enabled ?? caller.Enabled;
        caller.UpdatedAt = Clock.UtcNow;
        if (request.ConnectorIds is { } connectorIds)
        {
            var wanted = connectorIds.Distinct().ToHashSet();
            db.ConnectorGrants.RemoveRange(caller.Grants.Where(g => !wanted.Contains(g.ConnectorId)).ToList());
            await GrantCallerAsync(caller, wanted.Where(w => caller.Grants.All(g => g.ConnectorId != w)).ToList(), user, ct);
        }
        UdpViews.Audit(db, accessor, AuditEvents.ConnectorCallerChanged, $"caller {caller.Name} changed",
            AuditTarget.Of(AuditTargets.ConnectorCaller, caller.Id, caller.Name));
        await db.SaveChangesAsync(ct);
        return await CallerAsync(id, ct);
    }

    /// <summary>Deleting a caller stops its key at once; its calls stay in the history, under its name.</summary>
    public async Task DeleteCallerAsync(Guid id, CancellationToken ct)
    {
        var caller = await db.ConnectorCallers.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Caller");
        db.ConnectorCallers.Remove(caller);
        UdpViews.Audit(db, accessor, AuditEvents.ConnectorCallerChanged, $"caller {caller.Name} deleted: its key …{caller.KeyHint} stops at once",
            AuditTarget.Of(AuditTargets.ConnectorCaller, caller.Id, caller.Name));
        await db.SaveChangesAsync(ct);
    }

    private async Task GrantCallerAsync(ConnectorCallerEntity caller, IReadOnlyCollection<Guid> connectorIds, string? user, CancellationToken ct)
    {
        if (connectorIds.Count == 0)
        {
            return;
        }
        var connectors = await db.Connectors.Where(c => connectorIds.Contains(c.Id)).ToListAsync(ct);
        foreach (var connector in connectors)
        {
            db.ConnectorGrants.Add(new ConnectorGrantEntity
            {
                CallerId = caller.Id, ConnectorId = connector.Id, Name = $"{caller.Name} → {connector.Name}", CreatedAt = Clock.UtcNow, CreatedBy = user,
            });
        }
    }

    private async Task<List<ConnectorNetwork>> ValidateCallerAsync(ConnectorCallerRequest request, Guid? id, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        var v = new ApiValidator()
            .Check(name.Length is >= 1 and <= MaxName, "name", $"The name has 1 to {MaxName} characters.")
            .Check(request.Description is null || request.Description.Length <= 2000, "description", "At most 2000 characters.")
            .Check((request.Networks ?? []).Count <= ConnectorNetworks.MaxEntries, "networks", $"At most {ConnectorNetworks.MaxEntries} entries.");
        if (name.Length > 0)
        {
            v.Check(!await db.ConnectorCallers.AnyAsync(c => c.Name == name && c.Id != id, ct), "name", "Another caller has this name.");
        }
        var networks = new List<ConnectorNetwork>();
        foreach (var (entry, i) in (request.Networks ?? []).Select((n, i) => (n, i)))
        {
            var (cidr, error) = ConnectorNetworks.Normalize(entry.Cidr);
            if (error is not null)
            {
                v.Add($"networks[{i}].cidr", error);
                continue;
            }
            v.Check(entry.Label is null || entry.Label.Length <= ConnectorNetworks.MaxLabel, $"networks[{i}].label", $"At most {ConnectorNetworks.MaxLabel} characters.");
            networks.Add(new ConnectorNetwork(cidr!, Text.Trimmed(entry.Label)));
        }
        // D3: an enabled caller needs at least one network; "anywhere" is 0.0.0.0/0 and ::/0, entered deliberately.
        var enabled = request.Enabled ?? (id is { } existing && await db.ConnectorCallers.AnyAsync(c => c.Id == existing && c.Enabled, ct));
        v.Check(!enabled || networks.Count > 0, "networks", "An enabled caller needs at least one network: for anywhere, 0.0.0.0/0 and ::/0.");
        if (request.ConnectorIds is { Count: > 0 } connectorIds)
        {
            var known = await db.Connectors.CountAsync(c => connectorIds.Contains(c.Id), ct);
            v.Check(known == connectorIds.Distinct().Count(), "connectorIds", "No such connector.");
        }
        v.ThrowIfInvalid();
        return networks;
    }

    /// <summary>The key, shown again (R8): every display is audited. 409 <c>key_lost</c> when the key ring that protected it is gone.</summary>
    public async Task<ConnectorCallerKey> KeyAsync(Guid id, CancellationToken ct)
    {
        var caller = await db.ConnectorCallers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound("Caller");
        var key = protector.Unprotect(caller.KeyProtected)
            ?? throw ApiException.Conflict("key_lost", "The key ring that protected this key is gone (a restore without the pnmtd-keys volume): the key still works, but can't be shown. Rotate it to get a new one.");
        UdpViews.Audit(db, accessor, AuditEvents.ConnectorCallerKeyViewed, $"caller {caller.Name}: its key …{caller.KeyHint} shown",
            AuditTarget.Of(AuditTargets.ConnectorCaller, caller.Id, caller.Name));
        await db.SaveChangesAsync(ct);
        return new ConnectorCallerKey(key, caller.KeyHint, Clock.AsUtc(caller.KeyIssuedAt));
    }

    /// <summary>A new key (§6.2): the old one stops at once, for every connector the caller has. Audited.</summary>
    public async Task<ConnectorCallerKey> RotateKeyAsync(Guid id, CancellationToken ct)
    {
        var caller = await db.ConnectorCallers.Include(c => c.Grants).ThenInclude(g => g.Connector).SingleOrDefaultAsync(c => c.Id == id, ct)
            ?? throw ApiException.NotFound("Caller");
        var old = caller.KeyHint;
        var key = ConnectorKeys.New();
        caller.KeyHash = ConnectorKeys.Hash(key);
        caller.KeyProtected = protector.Protect(key);
        caller.KeyHint = ConnectorKeys.Hint(key);
        caller.KeyIssuedAt = Clock.UtcNow;
        caller.UpdatedAt = Clock.UtcNow;
        UdpViews.Audit(db, accessor, AuditEvents.ConnectorCallerKeyRotated,
            $"caller {caller.Name}: key …{old} replaced by …{caller.KeyHint}; it stops at once for {(caller.Grants.Count == 0 ? "no connector" : string.Join(", ", caller.Grants.Select(g => g.Connector.Name)))}",
            AuditTarget.Of(AuditTargets.ConnectorCaller, caller.Id, caller.Name));
        await db.SaveChangesAsync(ct);
        return new ConnectorCallerKey(key, caller.KeyHint, Clock.AsUtc(caller.KeyIssuedAt));
    }
}
