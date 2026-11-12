using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Udp;
using PNMTD.Models.Enums;

namespace PNMTD.Udp;

/// <summary>What the listener needs to check a packet before it touches the database (§4.5).</summary>
public sealed record UdpNodeSnapshot(
    Guid SensorId,
    uint DeviceId,
    string Name,
    bool Enabled,
    KeyFamily Family,
    int AllowedSuites,
    byte[] Key,
    byte[] Token,
    UdpSessionMode SessionMode,
    byte[]? SessionKey,
    byte[]? PendingSessionKey,
    UdpAckMode AckMode,
    bool ReplayProtection,
    SecuritySuite? ResponseSuite);

/// <summary>
/// Every UDP node by device id, in memory. Whatever changes a node (the API, a JOIN) calls
/// <see cref="Invalidate"/>; the next lookup reloads all nodes in one query. A minute after a load
/// it reloads anyway, as a safety net.
/// </summary>
public sealed class UdpNodeCache(IDbContextFactory<PnmtdDbContext> dbs)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim reloading = new(1, 1);
    private volatile Dictionary<uint, UdpNodeSnapshot>? nodes;
    private DateTime loadedAt;
    private long generation;

    /// <summary>Changes on every <see cref="Invalidate"/>: the processor reloads a node's runtime state when it does.</summary>
    public long Generation => Interlocked.Read(ref generation);

    public void Invalidate()
    {
        Interlocked.Increment(ref generation);
        nodes = null;
    }

    public async ValueTask<UdpNodeSnapshot?> GetAsync(uint deviceId, CancellationToken ct)
    {
        var current = nodes;
        if (current is null || Clock.UtcNow - loadedAt > MaxAge)
        {
            current = await ReloadAsync(ct);
        }
        return current.GetValueOrDefault(deviceId);
    }

    public async ValueTask<IReadOnlyCollection<UdpNodeSnapshot>> AllAsync(CancellationToken ct)
    {
        var current = nodes ?? await ReloadAsync(ct);
        return current.Values;
    }

    private async Task<Dictionary<uint, UdpNodeSnapshot>> ReloadAsync(CancellationToken ct)
    {
        await reloading.WaitAsync(ct);
        try
        {
            if (nodes is { } fresh && Clock.UtcNow - loadedAt <= MaxAge)
            {
                return fresh;
            }
            while (true)
            {
                // An Invalidate during the query means the result may be old already: load again.
                var before = Generation;
                await using var db = await dbs.CreateDbContextAsync(ct);
                var rows = await db.UdpNodes.AsNoTracking()
                    .Select(n => new UdpNodeSnapshot(n.SensorId, (uint)n.DeviceId, n.Sensor.Name, n.Sensor.Enabled, n.KeyFamily, n.AllowedSuites,
                        n.Key, n.Token, n.SessionMode, n.SessionKey, n.PendingSessionKey, n.AckMode, n.ReplayProtection, n.ResponseSuite))
                    .ToListAsync(ct);
                var loaded = rows.ToDictionary(n => n.DeviceId);
                if (before != Generation)
                {
                    continue;
                }
                loadedAt = Clock.UtcNow;
                nodes = loaded;
                return loaded;
            }
        }
        finally
        {
            reloading.Release();
        }
    }
}
