using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Enums;

namespace PNMTD.WireGuard;

/// <summary>A router as custom tunnels see it: its tunnel address, if its management access is on.</summary>
public sealed record ReachableRouter(Guid SensorId, string Name, string Address);

/// <summary>
/// What custom tunnels reach (docs/mikrotik-plan.md §3.5): routers in a WireGuard mode, enabled, with
/// management access on, and of those all or the selected ones. The gateway builds its firewall from it;
/// the admin page shows it.
/// </summary>
public static class TunnelReach
{
    public static async Task<IReadOnlyList<ReachableRouter>> RoutersAsync(PnmtdDbContext db, CancellationToken ct) =>
        await (from r in db.MikrotikRouters
               join s in db.Sensors on r.SensorId equals s.Id
               join p in db.WgPeers on r.WgPeerId equals p.Id
               where r.ManagementAccess && s.Enabled && p.Enabled
                     && (r.Mode == MikrotikMode.WireGuardSsh || r.Mode == MikrotikMode.WireGuardOnly)
               orderby s.Name
               select new ReachableRouter(s.Id, s.Name, p.Address)).ToListAsync(ct);

    /// <summary>Every enabled, unexpired tunnel with the routers it reaches.</summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ReachableRouter>>> TunnelsAsync(PnmtdDbContext db, CancellationToken ct)
    {
        var routers = await RoutersAsync(db, ct);
        var now = Clock.UtcNow;
        var tunnels = await db.WgPeers.Where(p => p.Kind == WgPeerKind.Custom).Select(p => new { p.Id, p.AccessAllRouters }).ToListAsync(ct);
        var access = (await db.WgPeerAccess.ToListAsync(ct)).ToLookup(a => a.PeerId, a => a.RouterSensorId);
        return tunnels.ToDictionary(t => t.Id, t => (IReadOnlyList<ReachableRouter>)(t.AccessAllRouters
            ? routers
            : routers.Where(r => access[t.Id].Contains(r.SensorId)).ToList()));
    }
}
