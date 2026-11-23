using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Zabbix;

/// <summary>
/// The problems the poll saw, kept for the boards' history (docs/boards-plan.md §7.5). The poll asks <c>problem.get</c> with
/// <c>recent</c>, so Zabbix also returns the problems resolved within its <i>Display OK triggers for</i> (5 minutes by default), with
/// their recovery time: longer than the poll, so every recovery is seen with its exact time. A problem that leaves without one (PNMT
/// was down, the window is shorter than the poll, or it became hidden or left the host groups) is marked resolved between its last
/// sighting and now. What opened and closed while PNMT was down isn't seen. Only problems Zabbix shows are kept.
/// </summary>
public sealed class ZabbixHistory(IServiceScopeFactory scopes, IOptionsMonitor<ZabbixOptions> options, ILogger<ZabbixHistory> logger)
{
    public static readonly TimeSpan CleanupEvery = TimeSpan.FromHours(6);

    private DateTime lastCleanup = DateTime.MinValue;

    /// <summary>A shown problem as the poll saw it: open, or resolved at <see cref="ResolvedAt"/>.</summary>
    public sealed record Seen(long EventId, long TriggerId, string Name, ZabbixSeverity Severity, bool Suppressed, IReadOnlyList<ZxTriggerHost> Hosts,
        DateTime StartedAt, DateTime? ResolvedAt);

    /// <summary>After a successful poll: every shown problem upserted, and the open ones it didn't see marked as resolved in between.</summary>
    public async Task RecordAsync(IReadOnlyList<Seen> seen, DateTime now, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PnmtdDbContext>();
            var ids = seen.Select(s => s.EventId).ToList();
            var rows = await db.ZabbixProblemHistory.Where(r => ids.Contains(r.EventId) || r.ResolvedAt == null).ToDictionaryAsync(r => r.EventId, ct);
            foreach (var s in seen)
            {
                if (!rows.TryGetValue(s.EventId, out var row))
                {
                    row = new ZabbixProblemHistoryEntity { EventId = s.EventId, FirstSeenAt = now };
                    db.ZabbixProblemHistory.Add(row);
                    rows[s.EventId] = row;
                }
                row.TriggerId = s.TriggerId;
                row.Name = s.Name.Length <= 2048 ? s.Name : s.Name[..2047] + "…";
                row.Severity = s.Severity;
                row.Suppressed = s.Suppressed;
                row.HostIds = [.. s.Hosts.Select(h => ZabbixPoller.Int(h.Hostid))];
                row.HostNames = [.. s.Hosts.Select(h => string.IsNullOrEmpty(h.Name) ? h.Host : h.Name)];
                row.StartedAt = s.StartedAt;
                row.LastSeenAt = now;
                if (s.ResolvedAt is { } resolved && row.ResolvedAt is null)
                {
                    row.ResolvedAt = resolved;
                    row.ResolvedFrom = null;
                }
            }
            var seenIds = ids.ToHashSet();
            foreach (var gone in rows.Values.Where(r => r.ResolvedAt is null && !seenIds.Contains(r.EventId)))
            {
                gone.ResolvedFrom = gone.LastSeenAt;
                gone.ResolvedAt = now;
            }
            await db.SaveChangesAsync(ct);
            if (now - lastCleanup >= CleanupEvery)
            {
                var before = now.AddDays(-Math.Max(1, options.CurrentValue.HistoryDays));
                await db.ZabbixProblemHistory.Where(r => r.ResolvedAt != null && r.ResolvedAt < before).ExecuteDeleteAsync(ct);
                lastCleanup = now;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The history is the boards'; the poll's snapshot and the Zabbix sensors go on without it.
            logger.LogWarning(e, "Zabbix's problem history could not be written");
        }
    }
}
