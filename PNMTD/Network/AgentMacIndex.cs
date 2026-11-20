using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Network;
using PNMTD.Models.Db;

namespace PNMTD.Network;

/// <summary>
/// The servers' MACs as rows (docs/network-map-plan.md §6.2): the adapters of a WINDOWS or LINUX agent's <c>network</c>
/// section, replaced whenever a new one is stored, so a server that reports later matches its interfaces at once.
/// </summary>
public static class AgentMacIndex
{
    /// <summary>Replaces the server's rows from its section; tracked, saved with the caller's SaveChanges.</summary>
    public static async Task ReplaceAsync(PnmtdDbContext db, Guid sensorId, NetworkSection? section, CancellationToken ct)
    {
        var old = await db.AgentAdapterMacs.Where(m => m.SensorId == sensorId).ToListAsync(ct);
        db.AgentAdapterMacs.RemoveRange(old);
        foreach (var row in Rows(sensorId, section))
        {
            var tracked = old.FirstOrDefault(o => o.Mac == row.Mac && o.Adapter == row.Adapter);
            if (tracked is not null)
            {
                // The same key removed and added in one save: update it instead.
                db.Entry(tracked).State = EntityState.Unchanged;
                tracked.Kind = row.Kind;
                tracked.SpeedBps = row.SpeedBps;
                tracked.Virtual = row.Virtual;
            }
            else
            {
                db.AgentAdapterMacs.Add(row);
            }
        }
    }

    /// <summary>One row per adapter with a MAC; adapters that share one (a team) each get theirs.</summary>
    public static IEnumerable<AgentAdapterMacEntity> Rows(Guid sensorId, NetworkSection? section) =>
        (section?.Adapters ?? [])
            .Select(a => (Adapter: a, Mac: Mac.Normalize(a.Mac)))
            .Where(a => a.Mac is not null && !string.IsNullOrWhiteSpace(a.Adapter.Name))
            .GroupBy(a => (a.Mac, Name: a.Adapter.Name.Length <= 200 ? a.Adapter.Name : a.Adapter.Name[..200]))
            .Select(g => new AgentAdapterMacEntity
            {
                SensorId = sensorId,
                Mac = g.Key.Mac!,
                Adapter = g.Key.Name,
                Kind = g.First().Adapter.Kind,
                SpeedBps = g.First().Adapter.SpeedBps,
                Virtual = g.First().Adapter.Virtual,
            });
}
