using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Udp;

/// <summary>
/// The images of open updates in memory (§4.11): loaded once from PostgreSQL at the first GET and
/// shared by every node that downloads them, evicted least recently used within <c>Udp:Ota:CacheBytes</c>.
/// Serving a chunk never queries the database.
/// </summary>
public sealed class UdpFirmwareCache(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<UdpOptions> options)
{
    private readonly ConcurrentDictionary<Guid, Entry> images = new();
    private readonly SemaphoreSlim loading = new(1, 1);

    private sealed class Entry(byte[] content)
    {
        public byte[] Content { get; } = content;

        public long LastUsed = Clock.UtcNow.Ticks;
    }

    public int Count => images.Count;

    public long Bytes => images.ToArray().Sum(kv => (long)kv.Value.Content.Length);

    public async ValueTask<byte[]?> GetAsync(Guid firmwareId, CancellationToken ct)
    {
        if (images.TryGetValue(firmwareId, out var hit))
        {
            Interlocked.Exchange(ref hit.LastUsed, Clock.UtcNow.Ticks);
            return hit.Content;
        }
        await loading.WaitAsync(ct);
        try
        {
            if (images.TryGetValue(firmwareId, out hit))
            {
                return hit.Content;
            }
            await using var db = await dbs.CreateDbContextAsync(ct);
            var content = await db.UdpFirmware.AsNoTracking().Where(f => f.Id == firmwareId).Select(f => f.Content).SingleOrDefaultAsync(ct);
            if (content is null)
            {
                return null;
            }
            images[firmwareId] = new Entry(content);
            Trim(firmwareId);
            return content;
        }
        finally
        {
            loading.Release();
        }
    }

    public void Evict(Guid firmwareId) => images.TryRemove(firmwareId, out _);

    private void Trim(Guid keep)
    {
        var limit = options.CurrentValue.Ota.CacheBytes;
        foreach (var (id, _) in images.ToArray().Where(kv => kv.Key != keep).OrderBy(kv => kv.Value.LastUsed))
        {
            if (Bytes <= limit)
            {
                break;
            }
            images.TryRemove(id, out _);
        }
    }
}
