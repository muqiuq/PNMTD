using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;

namespace PNMTD.Connectors;

/// <summary>What one pass of the worker did, for the tests and the log.</summary>
public sealed record ConnectorPassResult(int Started, int Finished, int Waiting);

/// <summary>
/// The one background task that works the connectors' calls (docs/connectors-plan.md §9.1, D7, D20): the calls table is its queue. A
/// pass makes running calls final once their run is, expires calls that waited too long, and takes the queued calls oldest first: the
/// review (several at once), the target as it resolves now, the script's turn on its server, a free place under
/// Connectors__MaxRunningCalls, the start. Once a minute the maintenance: runs that never finished, the retention, old idempotency keys,
/// 233 after the quiet time. The only writer of a call after the endpoint stored it. A nudge starts a pass at once; the timer
/// (<see cref="ConnectorWorkerTask"/>) every Connectors__PassSeconds finds what a lost nudge left.
/// </summary>
public sealed class ConnectorWorker
{
    /// <summary>The advisory lock of the limit over all connectors (§9.5): "PNMTCON1".</summary>
    public const long CapacityLock = 0x504E4D54434F4E31;

    public const string LostMessage = "No result from the agent within the timeout: it may have restarted during the run.";

    public const string RestartMessage = "PNMT restarted while the call waited, and its secret arguments are gone: send it again.";

    private readonly IServiceScopeFactory scopes;
    private readonly IDbContextFactory<PnmtdDbContext> dbs;
    private readonly AgentHub hub;
    private readonly ConnectorReview review;
    private readonly ConnectorSecretVault vault;
    private readonly ConnectorCallSignals signals;
    private readonly ConnectorAlarms alarms;
    private readonly IOptionsMonitor<ConnectorOptions> options;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<ConnectorWorker> logger;
    private readonly SemaphoreSlim running = new(1, 1);
    private DateTime lastMaintenance = DateTime.MinValue;
    private DateTime lastRetention = DateTime.MinValue;
    private int pumping;
    private int dirty;

    public ConnectorWorker(IServiceScopeFactory scopes, IDbContextFactory<PnmtdDbContext> dbs, AgentHub hub, ConnectorReview review, ConnectorSecretVault vault,
        ConnectorCallSignals signals, ConnectorAlarms alarms, ConnectorQueue queue, IOptionsMonitor<ConnectorOptions> options, IHostApplicationLifetime lifetime,
        ILogger<ConnectorWorker> logger)
    {
        this.scopes = scopes;
        this.dbs = dbs;
        this.hub = hub;
        this.review = review;
        this.vault = vault;
        this.signals = signals;
        this.alarms = alarms;
        this.options = options;
        this.lifetime = lifetime;
        this.logger = logger;
        queue.Nudged += Nudge;
    }

    /// <summary>A call was stored, a run ended, an agent connected, a parse arrived: a pass now (unless Connectors__Nudge is off).</summary>
    public void Nudge()
    {
        if (options.CurrentValue.Nudge && options.CurrentValue.Enabled)
        {
            Wake();
        }
    }

    private void Wake()
    {
        Volatile.Write(ref dirty, 1);
        if (Interlocked.CompareExchange(ref pumping, 1, 0) == 0)
        {
            _ = Task.Run(PumpAsync);
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref dirty, 0) == 1 && !lifetime.ApplicationStopping.IsCancellationRequested)
            {
                try
                {
                    var result = await RunOnceAsync(lifetime.ApplicationStopping, maintenance: false);
                    if (result.Started > 0)
                    {
                        // A run may be final at once (refused at the send, offline, or a quick result): the next pass takes it.
                        Volatile.Write(ref dirty, 1);
                    }
                }
                catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    logger.LogError(e, "The connectors' worker failed");
                }
            }
        }
        finally
        {
            Volatile.Write(ref pumping, 0);
            if (Volatile.Read(ref dirty) == 1 && !lifetime.ApplicationStopping.IsCancellationRequested && Interlocked.CompareExchange(ref pumping, 1, 0) == 0)
            {
                _ = Task.Run(PumpAsync);
            }
        }
    }

    /// <summary>
    /// One pass (§9.1). One at a time; public for the tests, which drive it by hand. <paramref name="maintenance"/>: null once a minute,
    /// true now, false not.
    /// </summary>
    public async Task<ConnectorPassResult> RunOnceAsync(CancellationToken ct, bool? maintenance = null)
    {
        await running.WaitAsync(ct);
        var finals = new List<Guid>();
        var started = 0;
        var waiting = 0;
        try
        {
            var o = options.CurrentValue;
            await using var db = await dbs.CreateDbContextAsync(ct);

            // 1. Running calls take their run's state once it is final.
            var active = await db.ConnectorCalls.Where(c => c.State == ConnectorCallState.Running).ToListAsync(ct);
            if (active.Count > 0)
            {
                var runIds = active.Where(c => c.RunId is not null).Select(c => c.RunId!.Value).ToList();
                var runs = await db.AgentScriptRuns.AsNoTracking().Where(r => runIds.Contains(r.Id))
                    .Select(r => new { r.Id, r.State, r.FinishedAt, r.Message }).ToDictionaryAsync(r => r.Id, ct);
                foreach (var call in active)
                {
                    if (call.RunId is not { } runId || !runs.TryGetValue(runId, out var run))
                    {
                        Finish(call, ConnectorCallState.Lost, "The run is gone from the history.");
                        finals.Add(call.Id);
                        continue;
                    }
                    if (run.State is ScriptRunState.Sent or ScriptRunState.Running)
                    {
                        continue;
                    }
                    var state = run.State switch
                    {
                        ScriptRunState.Done => ConnectorCallState.Done,
                        ScriptRunState.TimedOut => ConnectorCallState.TimedOut,
                        ScriptRunState.Offline => ConnectorCallState.Offline,
                        ScriptRunState.Refused => ConnectorCallState.Refused,
                        _ when run.Message == LostMessage => ConnectorCallState.Lost,
                        _ => ConnectorCallState.Failed,
                    };
                    Finish(call, state, run.Message, state == ConnectorCallState.Refused ? "agent" : null, run.FinishedAt);
                    finals.Add(call.Id);
                }
                await db.SaveChangesAsync(ct);
            }

            // 2. Queued calls past their connector's waiting time expire, with what they waited for.
            var now = Clock.UtcNow;
            var expired = await db.ConnectorCalls.Where(c => c.State == ConnectorCallState.Queued && c.ExpiresAt != null && c.ExpiresAt <= now).ToListAsync(ct);
            foreach (var call in expired)
            {
                Finish(call, ConnectorCallState.Expired, $"Still queued after {(int)(now - call.ReceivedAt).TotalSeconds} s, waiting for {WaitingText(call.WaitingFor)}.");
                vault.Remove(call.Id);
                finals.Add(call.Id);
            }
            if (expired.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            // 3. The queue, oldest first.
            var queued = await db.ConnectorCalls.Where(c => c.State == ConnectorCallState.Queued).OrderBy(c => c.ReceivedAt).ThenBy(c => c.Id).Take(500).ToListAsync(ct);
            if (queued.Count > 0)
            {
                var (s, w) = await QueueAsync(db, queued, finals, o, ct);
                started += s;
                waiting += w;
            }

            if (maintenance == true || (maintenance is null && Clock.UtcNow - lastMaintenance >= TimeSpan.FromMinutes(1)))
            {
                lastMaintenance = Clock.UtcNow;
                await MaintainAsync(db, finals, o, ct);
            }
            db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_CONNECTOR_WORK);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            running.Release();
            foreach (var id in finals)
            {
                signals.Complete(id);
            }
        }
        return new ConnectorPassResult(started, finals.Count, waiting);
    }

    private static void Finish(ConnectorCallEntity call, ConnectorCallState state, string? message, string? refusedBy = null, DateTime? at = null)
    {
        call.State = state;
        call.Message = Cuts.CutOrNull(message, 2000);
        call.RefusedBy = refusedBy ?? call.RefusedBy;
        call.WaitingFor = null;
        call.FinishedAt = at ?? Clock.UtcNow;
    }

    public static string WaitingText(string? waitingFor) => waitingFor switch
    {
        "review" => "the review",
        "turn" => "its turn: the script was running on the server",
        "capacity" => "a free place: PNMT ran its most connector calls",
        "server_offline" => "the server's agent, which was offline",
        "parameters_pending" => "the text's parse",
        _ => "the worker",
    };

    private sealed record Loaded(ConnectorEntity Connector, IReadOnlyList<ConnectorArgumentRule> Rules);

    /// <summary>W1 to W5 of §4.3 for the queued calls, in their order; what started and what still waits.</summary>
    private async Task<(int Started, int Waiting)> QueueAsync(PnmtdDbContext db, List<ConnectorCallEntity> queued, List<Guid> finals, ConnectorOptions o, CancellationToken ct)
    {
        var connectorIds = queued.Where(c => c.ConnectorId is not null).Select(c => c.ConnectorId!.Value).Distinct().ToList();
        var connectors = (await db.Connectors.AsNoTracking().Include(c => c.Script).Include(c => c.Arguments).Where(c => connectorIds.Contains(c.Id)).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => new Loaded(c, c.Arguments.OrderBy(a => a.Order).Select(ConnectorArguments.Rule).ToList()));
        var servers = new Dictionary<AgentPlatform, IReadOnlyList<ConnectorServer>>();
        async Task<IReadOnlyList<ConnectorServer>> ServersAsync(AgentPlatform platform) =>
            servers.TryGetValue(platform, out var known) ? known : servers[platform] = await ConnectorTargets.ServersAsync(db, hub, platform, ct);

        // Calls that can't go on: their connector disabled or deleted while they waited, their secrets lost to a restart.
        foreach (var call in queued)
        {
            if (call.ConnectorId is not { } id || !connectors.TryGetValue(id, out var loaded) || !loaded.Connector.Enabled || loaded.Connector.Script is null)
            {
                Finish(call, ConnectorCallState.Cancelled, "The connector was disabled or deleted while the call waited.");
                finals.Add(call.Id);
            }
            else if (call.SecureNames.Count > 0 && vault.Get(call.Id) is null)
            {
                Finish(call, ConnectorCallState.Expired, RestartMessage);
                finals.Add(call.Id);
            }
        }

        // W1, the review: once per call, several at once (§9.1), before a call waits for its turn.
        var toReview = queued.Where(c => c.State == ConnectorCallState.Queued && c.Review is null && connectors[c.ConnectorId!.Value].Connector.ReviewEnabled).ToList();
        if (toReview.Count > 0)
        {
            foreach (var call in toReview)
            {
                call.WaitingFor = "review";
            }
            await db.SaveChangesAsync(ct);
            using var gate = new SemaphoreSlim(Math.Clamp(o.WorkerParallelism, 1, 16));
            var outcomes = await Task.WhenAll(toReview.Select(async call =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var loaded = connectors[call.ConnectorId!.Value];
                    return (Call: call, Record: await review.ReviewAsync(loaded.Connector, ReviewState(loaded, call), ct));
                }
                finally
                {
                    gate.Release();
                }
            }));
            var refusals = new List<(ConnectorCallEntity Call, string Label)>();
            foreach (var (call, record) in outcomes)
            {
                var connector = connectors[call.ConnectorId!.Value].Connector;
                if (record.State == ConnectorReviewRecord.Unavailable && connector.ReviewFallback == ConnectorReviewFallback.RunUnreviewed)
                {
                    call.Review = (record with { State = ConnectorReviewRecord.Unreviewed }).ToJson();
                    call.WaitingFor = "worker";
                    continue;
                }
                call.Review = record.ToJson();
                call.WaitingFor = "worker";
                if (record.State == ConnectorReviewRecord.Refused)
                {
                    refusals.Add((call, record.RefusedLabel ?? "a check"));
                    End(call, $"The review refused the call: {record.RefusedLabel}.", "review");
                }
                else if (record.State == ConnectorReviewRecord.Unavailable)
                {
                    End(call, $"The review couldn't run, and this connector refuses calls then: {record.Reason}", "review_unavailable");
                }
            }
            await db.SaveChangesAsync(ct);
            foreach (var (call, label) in refusals)
            {
                await alarms.RefusedAsync($"{call.CallerName} ({call.ConnectorName}): the review refused a call ({label}), from {call.From}.",
                    AuditWho.Connector(call.From, call.CallerId ?? Guid.Empty, call.CallerName, call.ConnectorId),
                    AuditTarget.Of(AuditTargets.Connector, call.ConnectorId ?? Guid.Empty, call.ConnectorName, call.CallerId), ct);
            }
        }

        void End(ConnectorCallEntity call, string message, string refusedBy)
        {
            // §4.8: a dry run ends as checked, with what would have refused it.
            Finish(call, call.DryRun ? ConnectorCallState.Checked : ConnectorCallState.Refused, message, refusedBy);
            vault.Remove(call.Id);
            finals.Add(call.Id);
        }

        // W2 to W5, in the order the calls came.
        var claimed = new HashSet<(Guid, string)>();
        var runningNow = await db.ConnectorCalls.CountAsync(c => c.State == ConnectorCallState.Running, ct);
        var started = 0;
        var waiting = 0;
        foreach (var call in queued.Where(c => c.State == ConnectorCallState.Queued).ToList())
        {
            var loaded = connectors[call.ConnectorId!.Value];
            var connector = loaded.Connector;
            var script = connector.Script!;
            void Wait(string reason)
            {
                if (call.WaitingFor != reason)
                {
                    call.WaitingFor = reason;
                }
                waiting++;
            }

            // W2: the target as it resolves now.
            var found = ConnectorTargets.Match(await ServersAsync(ConnectorTargets.PlatformOf(connector.Type)), script.Server);
            if (found.Count != 1)
            {
                End(call, found.Count == 0
                    ? $"\"{script.Server}\" finds no {(connector.Type == ConnectorType.LinuxScript ? "Linux" : "Windows")} server with an agent."
                    : $"\"{script.Server}\" finds {found.Count} servers: {string.Join(", ", found.Select(f => f.Name))}.",
                    found.Count == 0 ? "server_not_found" : "server_ambiguous");
                continue;
            }
            var server = found[0];
            call.ServerSensorId = server.SensorId;
            call.ServerName = server.Name;
            var key = (server.SensorId, script.Name.ToLowerInvariant());
            var parsed = ConnectorTargets.Usable(connector.Type, script);
            if (parsed is null)
            {
                if (script.ParseState == ScriptParseState.Failed)
                {
                    End(call, $"No agent could parse the connector's text{(script.ParseError is { } e ? $": {e}" : "")}.", "script_invalid");
                    continue;
                }
                claimed.Add(key);
                Wait("parameters_pending");
                continue;
            }
            if (parsed.Errors.Count > 0)
            {
                End(call, $"The connector's text has errors: {parsed.Errors[0].Message} (line {parsed.Errors[0].Line}).", "script_invalid");
                continue;
            }
            if (script.Function is { } function && parsed.Function(function) is null)
            {
                End(call, $"The connector's text defines no function {function} at its top level.", "script_invalid");
                continue;
            }
            if (script.NeedsReview)
            {
                End(call, "The connector's text changed, and its arguments wait for an admin.", "arguments_need_review");
                continue;
            }
            var given = Given(call);
            var check = ConnectorArguments.Check(loaded.Rules, given, o.MaxValueChars, o.PatternTimeoutMs);
            if (!check.Ok)
            {
                End(call, $"The connector changed while the call waited: {check.Message} " + string.Join("; ", check.Fields.Select(f => $"{f.Key}: {f.Value}")),
                    "arguments_invalid");
                continue;
            }
            if (!server.Online)
            {
                if (call.DryRun)
                {
                    End(call, $"The checks passed; {server.Name}'s agent is offline, so a call would wait for it.", "server_offline");
                    continue;
                }
                claimed.Add(key);
                Wait("server_offline");
                continue;
            }
            if (script.Function is not null && !server.Capabilities.Contains(AgentCapabilities.ScriptFunctions))
            {
                End(call, $"The agent on {server.Name} can't call functions: update it.", "agent_too_old");
                continue;
            }

            // W3: a dry run ends here.
            if (call.DryRun)
            {
                Finish(call, ConnectorCallState.Checked, $"The checks passed: the call would run on {server.Name}.");
                vault.Remove(call.Id);
                finals.Add(call.Id);
                continue;
            }

            // W4: the script's turn on the server, and a free place under the limit over all connectors.
            if (!claimed.Add(key))
            {
                Wait("turn");
                continue;
            }
            if (runningNow >= o.RunningLimit)
            {
                Wait("capacity");
                continue;
            }

            // W5: the start, through the one start of a run (§9.2). A start that throws leaves the call queued for the next pass.
            string? outcome;
            try
            {
                outcome = await StartAsync(db, call, connector, script, server, check, o, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Starting the run of call {Call} ({Connector}) failed", call.Id, connector.Name);
                claimed.Remove(key);
                Wait("worker");
                continue;
            }
            if (outcome is null)
            {
                started++;
                runningNow++;
                continue;
            }
            if (outcome != Gone)
            {
                Wait(outcome);
            }
        }
        await db.SaveChangesAsync(ct);
        return (started, waiting);
    }

    /// <summary>A queued call's arguments as the caller sent them: the stored plain ones and the secrets from memory.</summary>
    private Dictionary<string, JsonElement> Given(ConnectorCallEntity call)
    {
        var given = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (call.Arguments is { } json)
        {
            foreach (var property in JsonDocument.Parse(json).RootElement.EnumerateObject())
            {
                given[property.Name] = property.Value.Clone();
            }
        }
        foreach (var (name, value) in vault.Get(call.Id) ?? new Dictionary<string, string>())
        {
            given[name] = JsonSerializer.SerializeToElement(value);
        }
        return given;
    }

    private IReadOnlyList<string> ReviewState(Loaded loaded, ConnectorCallEntity call)
    {
        var plain = new Dictionary<string, JsonElement>();
        if (call.Arguments is { } json)
        {
            foreach (var property in JsonDocument.Parse(json).RootElement.EnumerateObject())
            {
                plain[property.Name] = property.Value.Clone();
            }
        }
        var secrets = (vault.Get(call.Id) ?? new Dictionary<string, string>()).ToDictionary(s => s.Key, s => s.Value.Length);
        var fixedArguments = loaded.Rules.Where(r => r.Mode == ConnectorArgumentMode.Fixed && r.FixedValue is not null)
            .ToDictionary(r => r.Parameter.Name, r => r.FixedValue!.Value);
        return ConnectorReview.State(loaded.Connector, loaded.Connector.Script!, call.ServerName, fixedArguments, plain, secrets, call.Reference, call.Note);
    }

    /// <summary>
    /// Starts the call's run: in a scope of its own, under the limit's lock, the call running with its run's id in the same transaction
    /// as the run's row. Null when it started; <see cref="Gone"/> when it was cancelled meanwhile; otherwise what it waits for (<c>turn</c>,
    /// <c>capacity</c>).
    /// </summary>
    private async Task<string?> StartAsync(PnmtdDbContext db, ConnectorCallEntity call, ConnectorEntity connector, ConnectorScriptEntity script, ConnectorServer server,
        ConnectorArgumentCheck check, ConnectorOptions o, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AgentScriptService>();
        var linux = connector.Type == ConnectorType.LinuxScript;
        var spec = new ScriptRunSpec(server.SensorId, server.Name, server.Platform, server.EncryptionKey, server.Capabilities, server.Rights, script.Name,
            script.ScriptText, script.Hash, null, script.ScriptRevision, script.Function, script.RunAsKind,
            script.RunAsKind == RunAsKind.Service ? null : script.Account, ConnectorTargets.RunAsName(script.RunAsKind, script.Account, linux), null,
            linux ? "bash" : script.Shell, script.TimeoutSeconds, check.Plain, check.Secure, $"{call.CallerName} via {connector.Name}", call.Id, connector.Id);
        var who = AuditWho.Connector(call.From, call.CallerId ?? Guid.Empty, call.CallerName, connector.Id);
        var detail = $"script {script.Name} (revision {script.ScriptRevision} of connector {connector.Name}){(script.Function is { } f ? $", function {f}," : "")} on {server.Name} "
            + $"as {spec.RunAsName}, call {call.Id}{(call.Reference is { } r ? $" ({r})" : "")}";
        var limit = o.RunningLimit;
        try
        {
            var start = await service.StartAsync(spec, AgentScriptService.BusyMode.Wait,
                async (inner, token) =>
                {
                    await inner.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({CapacityLock})", token);
                    return await inner.ConnectorCalls.CountAsync(c => c.State == ConnectorCallState.Running, token) >= limit ? "capacity" : null;
                },
                async (inner, row, token) =>
                {
                    var at = Clock.UtcNow;
                    var updated = await inner.ConnectorCalls.Where(c => c.Id == call.Id && c.State == ConnectorCallState.Queued).ExecuteUpdateAsync(u => u
                        .SetProperty(c => c.State, ConnectorCallState.Running).SetProperty(c => c.RunId, row.Id).SetProperty(c => c.StartedAt, at)
                        .SetProperty(c => c.WaitingFor, (string?)null).SetProperty(c => c.ServerSensorId, server.SensorId).SetProperty(c => c.ServerName, server.Name)
                        .SetProperty(c => c.ScriptName, script.Name).SetProperty(c => c.ScriptRevision, script.ScriptRevision).SetProperty(c => c.Hash, script.Hash)
                        .SetProperty(c => c.Function, script.Function), token);
                    if (updated == 0)
                    {
                        throw new CallGoneException();
                    }
                },
                (who, detail), ct);
            if (start.Row is null)
            {
                return start.WaitingFor ?? "turn";
            }
        }
        catch (CallGoneException)
        {
            // Cancelled meanwhile: nothing started.
            db.Entry(call).State = EntityState.Detached;
            return Gone;
        }
        // The call is running now, written by the start's transaction: this context's copy is stale.
        db.Entry(call).State = EntityState.Detached;
        vault.Remove(call.Id);
        return null;
    }

    private sealed class CallGoneException : Exception;

    private const string Gone = "gone";

    /// <summary>
    /// Once a minute (§9.1): runs that never finished (§9.4, for every run), the retention (once an hour), old idempotency keys, and
    /// 233 after the quiet time.
    /// </summary>
    private async Task MaintainAsync(PnmtdDbContext db, List<Guid> finals, ConnectorOptions o, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var candidates = await db.AgentScriptRuns.Where(r => (r.State == ScriptRunState.Sent || r.State == ScriptRunState.Running) && r.StartedAt < now.AddMinutes(-5))
            .ToListAsync(ct);
        var lost = candidates.Where(r => Clock.AsUtc(r.StartedAt).AddSeconds(r.TimeoutSeconds) + TimeSpan.FromMinutes(5) <= now).ToList();
        foreach (var run in lost)
        {
            run.State = ScriptRunState.Failed;
            run.Message = LostMessage;
            run.FinishedAt = now;
        }
        if (lost.Count > 0)
        {
            var ids = lost.Select(r => r.Id).ToList();
            var calls = await db.ConnectorCalls.Where(c => c.State == ConnectorCallState.Running && c.RunId != null && ids.Contains(c.RunId.Value)).ToListAsync(ct);
            foreach (var call in calls)
            {
                Finish(call, ConnectorCallState.Lost, LostMessage);
                finals.Add(call.Id);
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Closed {Count} script runs without a result within their timeout", lost.Count);
        }

        var hours = Math.Max(1, o.IdempotencyHours);
        var before = now.AddHours(-hours);
        await db.ConnectorCalls.Where(c => c.IdempotencyKey != null && c.ReceivedAt < before)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.IdempotencyKey, (string?)null), ct);

        if (now - lastRetention >= TimeSpan.FromHours(1))
        {
            lastRetention = now;
            await RetainAsync(db, o.KeepCalls, ct);
        }
        await alarms.QuietAsync(ct);
    }

    /// <summary>
    /// The newest Connectors__KeepCalls calls of all connectors together (§10.4, D16), and the runs of the calls that go; an active call is
    /// never deleted. The number of calls deleted.
    /// </summary>
    public static async Task<int> RetainAsync(PnmtdDbContext db, int keep, CancellationToken ct)
    {
        keep = Math.Max(1, keep);
        var cutoff = await db.ConnectorCalls.OrderByDescending(c => c.ReceivedAt).Skip(keep - 1).Select(c => (DateTime?)c.ReceivedAt).FirstOrDefaultAsync(ct);
        if (cutoff is null)
        {
            return 0;
        }
        var old = db.ConnectorCalls.Where(c => c.ReceivedAt < cutoff && c.State != ConnectorCallState.Queued && c.State != ConnectorCallState.Running);
        var runIds = await old.Where(c => c.RunId != null).Select(c => c.RunId!.Value).ToListAsync(ct);
        var deleted = await old.ExecuteDeleteAsync(ct);
        foreach (var chunk in runIds.Chunk(1000))
        {
            await db.AgentScriptRuns.Where(r => chunk.Contains(r.Id) && r.ConnectorId != null).ExecuteDeleteAsync(ct);
        }
        return deleted;
    }
}

/// <summary>The worker on its timer (§9.1): every Connectors__PassSeconds; nudges wake it in between.</summary>
public sealed class ConnectorWorkerTask(ConnectorWorker worker, IOptionsMonitor<ConnectorOptions> options, ILogger<ConnectorWorkerTask> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (options.CurrentValue.Enabled)
                {
                    try
                    {
                        await worker.RunOnceAsync(stoppingToken);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        logger.LogError(e, "The connectors' worker failed");
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.PassSeconds, 1, 60)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
