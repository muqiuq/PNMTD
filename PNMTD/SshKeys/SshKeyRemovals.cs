using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Agents;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.SshKeyManager;

/// <summary>
/// Removing keys (docs/linux-sensor-plan.md §6.5), PNMTD's side of the protocol: a removal is <c>Pending</c> until its server's agent
/// is connected and announces <c>keys.remove.v1</c>, then <c>Sent</c> as a signed <c>remove-ssh-key</c>; the agent answers an
/// <c>ack</c> and a <c>key-removal</c> (in either order), whose outcome the row keeps (removed, not found, refused, failed). A removal
/// waits at most Agents__KeyRemovalPendingDays for an offline server, then <c>Expired</c>. Each result goes to the agent log, the
/// audit (<see cref="AuditEvents.SshKeyRemoved"/>) and, when the key was removed, a change line with 263: "Jan, old laptop (jdoe)
/// removed from root's authorized_keys by PNMT (philipp)".
/// </summary>
public sealed class SshKeyRemovals(IDbContextFactory<PnmtdDbContext> dbs, AgentKeyStore keys, IOptionsMonitor<AgentOptions> options,
    ILogger<SshKeyRemovals> logger)
{
    /// <summary>How long a removal waits for the agent's ack (or its result).</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A removal sent without an answer this long ago goes again at the next connection (the agent answers "not found" if it was done).</summary>
    public static readonly TimeSpan ResendAfter = TimeSpan.FromMinutes(10);

    public int PendingDays => Math.Clamp(options.CurrentValue.KeyRemovalPendingDays, 1, 365);

    /// <summary>
    /// Sends these removals of one server to its connection, one by one, each waiting for its answer. Rows that are no longer
    /// pending or sent are skipped. A refusal in the ack is the row's outcome; a result is recorded by <see cref="ResultAsync"/>.
    /// </summary>
    public async Task SendAsync(AgentConnection connection, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        foreach (var id in ids)
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            var row = await db.SshKeyRemovals.SingleOrDefaultAsync(r => r.Id == id && r.SensorId == connection.SensorId, ct);
            if (row is null || row.State is not (SshKeyRemovalState.Pending or SshKeyRemovalState.Sent))
            {
                continue;
            }
            row.State = SshKeyRemovalState.Sent;
            row.SentAt = Clock.UtcNow;
            row.Message = null;
            await db.SaveChangesAsync(ct);
            try
            {
                var answer = await connection.RequestAsync(keys, AgentMessageTypes.RemoveSshKey,
                    new RemoveSshKeyRequest(row.Id, row.Account, row.Path, row.Fingerprint), AnswerTimeout, ct);
                if (answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("removalId", out _))
                {
                    // The result came first and completed the request: recorded already.
                    continue;
                }
                if (AgentJson.Deserialize<Ack>(answer) is { Ok: false } refused)
                {
                    await RefusedAsync(connection, id, refused.Error ?? "The agent refused, without a reason.", ct);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await db.SshKeyRemovals.Where(r => r.Id == id && r.State == SshKeyRemovalState.Sent)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.Message, $"No answer from the agent within {(int)AnswerTimeout.TotalSeconds} s: sent again when it reconnects."), ct);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException)
            {
                logger.LogInformation(e, "Sending the key removal {Id} to {Agent} failed: pending again", id, connection.AgentName);
                await db.SshKeyRemovals.Where(r => r.Id == id && r.State == SshKeyRemovalState.Sent)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, SshKeyRemovalState.Pending).SetProperty(r => r.Message, "The connection ended while sending: pending."), ct);
            }
        }
    }

    /// <summary>
    /// A connected Linux agent (from <see cref="AgentHub"/>): its server's pending removals go now, and removals sent without an answer
    /// go again. Expired ones expire first. An agent that doesn't announce keys.remove.v1 any more gets none: they are refused.
    /// </summary>
    public async Task SendPendingAsync(AgentConnection connection, CancellationToken ct)
    {
        List<Guid> ids;
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            await ExpireAsync(db, Clock.UtcNow, connection.SensorId, ct);
            var resend = Clock.UtcNow - ResendAfter;
            ids = await db.SshKeyRemovals.AsNoTracking()
                .Where(r => r.SensorId == connection.SensorId
                    && (r.State == SshKeyRemovalState.Pending || (r.State == SshKeyRemovalState.Sent && r.SentAt < resend)))
                .OrderBy(r => r.RequestedAt).Select(r => r.Id).Take(500).ToListAsync(ct);
        }
        if (ids.Count == 0)
        {
            return;
        }
        if (!connection.Capabilities.Contains(AgentCapabilities.KeysRemove))
        {
            foreach (var id in ids)
            {
                await RefusedAsync(connection, id,
                    "The agent connected without keys.remove.v1: its config.yaml doesn't allow removing keys (ssh_keys.remove: true would). Nothing was removed.", ct);
            }
            return;
        }
        await SendAsync(connection, ids, ct);
    }

    /// <summary>A refusal by the agent's ack, or by PNMT when the agent can't remove keys: the row's outcome, the agent log, the audit.</summary>
    private async Task RefusedAsync(AgentConnection connection, Guid id, string reason, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var row = await db.SshKeyRemovals.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null || row.State is not (SshKeyRemovalState.Pending or SshKeyRemovalState.Sent))
        {
            return;
        }
        row.State = SshKeyRemovalState.Refused;
        row.Message = Cuts.Cut(reason, 2000);
        row.AppliedAt = Clock.UtcNow;
        Record(db, connection, row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The agent's <c>key-removal</c>: its outcome on the row (once: a result sent again changes nothing), the agent log, the audit,
    /// and for a removed key the change line "… removed from root's authorized_keys by PNMT (philipp)" with 263.
    /// </summary>
    public async Task ResultAsync(AgentConnection connection, SshKeyRemovalResult result, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.SshKeyRemovals.SingleOrDefaultAsync(r => r.Id == result.RemovalId && r.SensorId == connection.SensorId, ct);
        if (row is null)
        {
            AgentLog.Add(db, connection.SensorId, connection.AgentName, AgentLogKind.Warning,
                $"A key removal's result for a removal PNMT doesn't know ({result.RemovalId}): {result.Outcome}.", address: connection.Address);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return;
        }
        if (row.State is not (SshKeyRemovalState.Pending or SshKeyRemovalState.Sent))
        {
            return;
        }
        var now = Clock.UtcNow;
        row.State = result.Outcome switch
        {
            SshKeyRemovalOutcome.Removed => SshKeyRemovalState.Removed,
            SshKeyRemovalOutcome.NotFound => SshKeyRemovalState.NotFound,
            SshKeyRemovalOutcome.Refused => SshKeyRemovalState.Refused,
            _ => SshKeyRemovalState.Failed,
        };
        row.LinesRemoved = Math.Max(0, result.LinesRemoved);
        row.Backup = Cuts.CutOrNull(result.Backup, 500);
        row.NewFileHash = Cuts.CutOrNull(result.NewFileHash, 100);
        row.Message = Cuts.CutOrNull(result.Message, 2000);
        row.AppliedAt = now;
        Record(db, connection, row);
        if (row.State == SshKeyRemovalState.Removed)
        {
            var line = $"{row.KeyName ?? SshKeys.Short(row.Fingerprint)} removed from {SshKeyRegistry.File(row.Account, row.Path)} by PNMT ({row.RequestedByName ?? "an admin"})";
            db.AgentChanges.Add(new AgentChangeEntity
            {
                Id = Guid.NewGuid(), SensorId = connection.SensorId, Section = SectionNames.SshKeys, At = now, Summary = Cuts.Cut(line, 1000), Notable = true,
                Details = JsonSerializer.Serialize(new { Removal = row.Id, row.LinesRemoved, row.Backup, Key = row.Fingerprint }, AgentJson.Options),
            });
            var write = await db.Sensors.Where(s => s.Id == connection.SensorId).Select(s => s.Enabled && s.Parent.Enabled).SingleOrDefaultAsync(ct);
            if (write)
            {
                await SensorEventWriter.WriteAsync(db, connection.SensorId, PNMTStatusCodes.LINUX_CHANGED, Cuts.Cut(line, 1000), AgentPlatformInfo.Linux.EventSource, ct);
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The agent log's line and the audit's entry for an outcome, in the requester's name.</summary>
    private static void Record(PnmtdDbContext db, AgentConnection connection, SshKeyRemovalEntity row)
    {
        var what = $"{row.KeyName ?? SshKeys.Short(row.Fingerprint)} from {SshKeyRegistry.File(row.Account, row.Path)}";
        var outcome = row.State switch
        {
            SshKeyRemovalState.Removed => $"removed ({row.LinesRemoved} {(row.LinesRemoved == 1 ? "line" : "lines")}{(row.Backup is { } b ? $", backup {b}" : "")})",
            SshKeyRemovalState.NotFound => "not found: the key was gone already",
            SshKeyRemovalState.Refused => "refused",
            _ => "failed",
        };
        var message = $"Key removal: {what}: {outcome}{(row.Message is { } m ? $": {Cuts.Cut(m, 500)}" : "")}";
        AgentLog.Add(db, connection.SensorId, connection.AgentName, AgentLogKind.Request, message,
            new { Removal = row.Id, row.Account, row.Path, row.Fingerprint, row.State, row.LinesRemoved, row.Backup, row.NewFileHash, row.RequestedByName },
            connection.Address);
        // The agent did it, for the person who asked: theirs, so that the user filter finds it, with the agent as the actor.
        AuditLog.Record(db, AuditEvents.SshKeyRemoved, $"{row.ServerName}: {what}: {outcome}",
            new AuditWho(AuditActor.Agent, row.RequestedBy, row.RequestedByName ?? "agent", row.ServerName, connection.Address, null),
            AuditTarget.Of(AuditTargets.Sensor, connection.SensorId, row.ServerName));
    }

    /// <summary>
    /// Removals still pending (or sent without an answer) after Agents__KeyRemovalPendingDays expire: of one server (at its
    /// connection), or of all (the hourly maintenance). Saved at once.
    /// </summary>
    public static async Task<int> ExpireAsync(PnmtdDbContext db, int days, DateTime now, Guid? sensorId, CancellationToken ct)
    {
        var before = now.AddDays(-Math.Clamp(days, 1, 365));
        var q = db.SshKeyRemovals.Where(r => (r.State == SshKeyRemovalState.Pending || r.State == SshKeyRemovalState.Sent) && r.RequestedAt < before);
        if (sensorId is { } id)
        {
            q = q.Where(r => r.SensorId == id);
        }
        return await q.ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Message, r => r.State == SshKeyRemovalState.Pending
                ? $"The server didn't connect within {days} days: nothing was removed."
                : $"No answer from the agent within {days} days.")
            .SetProperty(r => r.State, SshKeyRemovalState.Expired)
            .SetProperty(r => r.AppliedAt, now), ct);
    }

    private Task<int> ExpireAsync(PnmtdDbContext db, DateTime now, Guid sensorId, CancellationToken ct) => ExpireAsync(db, PendingDays, now, sensorId, ct);
}
