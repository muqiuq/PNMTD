using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Udp;
using PNMTD.Models.Enums;

namespace PNMTD.Udp;

/// <summary>A rejected packet of a known node, kept in memory only (§4.7).</summary>
public sealed record UdpRejection(DateTime At, UdpRejectReason Reason, string Endpoint, byte[] Datagram, string? Detail);

/// <summary>A sender PNMT does not know, grouped by device id and address (§4.7).</summary>
public sealed record UdpUnknownSender(uint? DeviceId, string Address, DateTime FirstSeen, DateTime LastSeen, long Count,
    SecuritySuite? LastSuite, byte[] LastDatagram, string? Problem);

/// <summary>A node's numbers over the last 24 hours (since the restart at most).</summary>
public sealed record UdpNodeNumbers(long Accepted, long Rejected, long BytesIn, long BytesOut, double? Loss,
    IReadOnlyDictionary<UdpRejectReason, long> RejectedByReason, bool BadTagWarning);

/// <summary>
/// The listener's counters, per reason and per node, the nodes' recent rejections, the unknown
/// senders and the "rejected packets" warning (§4.7, §4.10). Memory only: they start again with
/// the process, and nothing here grows with the traffic beyond its bounds.
/// </summary>
public sealed class UdpStats(IOptionsMonitor<UdpOptions> options, ILogger<UdpStats> logger)
{
    private long received;
    private long accepted;
    private long responses;
    private long bytesIn;
    private long bytesOut;
    private readonly ConcurrentDictionary<UdpRejectReason, long> rejected = new();
    private readonly ConcurrentDictionary<Guid, NodeStats> perNode = new();
    private readonly ConcurrentDictionary<(uint?, string), UdpUnknownSender> unknown = new();
    private readonly MinuteCounter perMinute = new();
    private readonly Lock evicting = new();

    public DateTime Since { get; } = Clock.UtcNow;

    public long Received => Interlocked.Read(ref received);

    public long Accepted => Interlocked.Read(ref accepted);

    public long Responses => Interlocked.Read(ref responses);

    public long BytesIn => Interlocked.Read(ref bytesIn);

    public long BytesOut => Interlocked.Read(ref bytesOut);

    public IReadOnlyDictionary<UdpRejectReason, long> Rejected => rejected.ToArray().ToDictionary(kv => kv.Key, kv => kv.Value);

    public int PacketsLastMinute => perMinute.Count;

    public void Datagram(int length)
    {
        Interlocked.Increment(ref received);
        Interlocked.Add(ref bytesIn, length);
        perMinute.Add();
    }

    public void Response(Guid? sensorId, int length)
    {
        Interlocked.Increment(ref responses);
        Interlocked.Add(ref bytesOut, length);
        if (sensorId is { } id)
        {
            Node(id).Add(Clock.UtcNow, b => b.BytesOut += length);
        }
    }

    public void Accept(Guid sensorId, int length, long fcnt, bool counted)
    {
        Interlocked.Increment(ref accepted);
        Node(sensorId).Add(Clock.UtcNow, b =>
        {
            b.Accepted++;
            b.BytesIn += length;
            if (counted)
            {
                b.MinFcnt = Math.Min(b.MinFcnt, fcnt);
                b.MaxFcnt = Math.Max(b.MaxFcnt, fcnt);
            }
        });
    }

    /// <summary>Counts a rejection; for a known node it also joins the node's recent rejections.</summary>
    public void Reject(UdpRejectReason reason, Guid? sensorId, IPEndPoint remote, byte[] datagram, string? detail = null)
    {
        rejected.AddOrUpdate(reason, 1, (_, n) => n + 1);
        if (sensorId is not { } id)
        {
            return;
        }
        var node = Node(id);
        var now = Clock.UtcNow;
        node.Add(now, b =>
        {
            b.Rejected++;
            b.BytesIn += datagram.Length;
            b.Reasons[reason] = b.Reasons.GetValueOrDefault(reason) + 1;
        });
        node.Remember(new UdpRejection(now, reason, Endpoints.Format(remote), datagram, detail), options.CurrentValue.Diagnostics.RecentRejectionsPerNode);
        if (reason == UdpRejectReason.BadTag)
        {
            var d = options.CurrentValue.Diagnostics;
            if (node.BadTag(now, TimeSpan.FromMinutes(d.BadTagWindowMinutes), d.BadTagWarningThreshold))
            {
                logger.LogWarning("UDP node {SensorId}: more than {Threshold} packets with a bad tag within {Minutes} minutes, the last from {Remote}",
                    id, d.BadTagWarningThreshold, d.BadTagWindowMinutes, Endpoints.Format(remote));
            }
        }
    }

    public void Unknown(uint? deviceId, IPEndPoint remote, SecuritySuite? suite, byte[] datagram, string? problem)
    {
        rejected.AddOrUpdate(deviceId is null ? UdpRejectReason.Malformed : UdpRejectReason.UnknownDevice, 1, (_, n) => n + 1);
        var now = Clock.UtcNow;
        var address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4().ToString() : remote.Address.ToString();
        unknown.AddOrUpdate((deviceId, address),
            _ => new UdpUnknownSender(deviceId, address, now, now, 1, suite, datagram, problem),
            (_, s) => s with { LastSeen = now, Count = s.Count + 1, LastSuite = suite ?? s.LastSuite, LastDatagram = datagram, Problem = problem });
        var limit = options.CurrentValue.Diagnostics.UnknownSenders;
        if (unknown.Count > limit && evicting.TryEnter())
        {
            try
            {
                // ToArray is ConcurrentDictionary's own snapshot: LINQ's copy of a dictionary that other
                // workers change at the same time can fail or see empty entries.
                var snapshot = unknown.ToArray();
                foreach (var oldest in snapshot.OrderBy(kv => kv.Value.LastSeen).Take(snapshot.Length - limit))
                {
                    unknown.TryRemove(oldest.Key, out _);
                }
            }
            finally
            {
                evicting.Exit();
            }
        }
    }

    public IReadOnlyList<UdpUnknownSender> UnknownSenders() => unknown.ToArray().Select(kv => kv.Value).OrderByDescending(s => s.LastSeen).ToList();

    public void ForgetUnknown(uint deviceId)
    {
        foreach (var key in unknown.Keys.Where(k => k.Item1 == deviceId).ToList())
        {
            unknown.TryRemove(key, out _);
        }
    }

    public IReadOnlyList<UdpRejection> Rejections(Guid sensorId) =>
        perNode.TryGetValue(sensorId, out var node) ? node.Recent() : [];

    public UdpNodeNumbers Numbers(Guid sensorId) =>
        perNode.TryGetValue(sensorId, out var node) ? node.Numbers(Clock.UtcNow) : new UdpNodeNumbers(0, 0, 0, 0, null, new Dictionary<UdpRejectReason, long>(), false);

    public IReadOnlyList<Guid> NodesWithBadTagWarning() =>
        perNode.ToArray().Where(kv => kv.Value.Numbers(Clock.UtcNow).BadTagWarning).Select(kv => kv.Key).ToList();

    public void Forget(Guid sensorId) => perNode.TryRemove(sensorId, out _);

    private NodeStats Node(Guid sensorId) => perNode.GetOrAdd(sensorId, _ => new NodeStats());

    private sealed class Bucket
    {
        public DateTime Hour;
        public long Accepted;
        public long Rejected;
        public long BytesIn;
        public long BytesOut;
        public long MinFcnt = long.MaxValue;
        public long MaxFcnt = long.MinValue;
        public readonly Dictionary<UdpRejectReason, long> Reasons = [];
    }

    private sealed class NodeStats
    {
        private readonly Lock gate = new();
        private readonly Bucket[] buckets = new Bucket[24];
        private readonly Queue<UdpRejection> recent = new();
        private readonly Queue<DateTime> badTags = new();
        private DateTime? warningUntil;

        public void Add(DateTime now, Action<Bucket> change)
        {
            var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
            lock (gate)
            {
                var slot = hour.Hour;
                if (buckets[slot] is not { } bucket || bucket.Hour != hour)
                {
                    bucket = buckets[slot] = new Bucket { Hour = hour };
                }
                change(bucket);
            }
        }

        public void Remember(UdpRejection rejection, int limit)
        {
            lock (gate)
            {
                recent.Enqueue(rejection);
                while (recent.Count > Math.Max(1, limit))
                {
                    recent.Dequeue();
                }
            }
        }

        /// <summary>True when this bad tag starts a warning: more than the threshold within the window.</summary>
        public bool BadTag(DateTime now, TimeSpan window, int threshold)
        {
            lock (gate)
            {
                badTags.Enqueue(now);
                while (badTags.Count > 0 && now - badTags.Peek() > window)
                {
                    badTags.Dequeue();
                }
                if (badTags.Count > threshold)
                {
                    var starts = warningUntil is null || warningUntil < now;
                    warningUntil = now + window;
                    return starts;
                }
                return false;
            }
        }

        public IReadOnlyList<UdpRejection> Recent()
        {
            lock (gate)
            {
                return recent.Reverse().ToList();
            }
        }

        public UdpNodeNumbers Numbers(DateTime now)
        {
            lock (gate)
            {
                var live = buckets.Where(b => b is not null && now - b.Hour < TimeSpan.FromHours(24)).ToList();
                var accepted = live.Sum(b => b.Accepted);
                var min = live.Select(b => b.MinFcnt).DefaultIfEmpty(long.MaxValue).Min();
                var max = live.Select(b => b.MaxFcnt).DefaultIfEmpty(long.MinValue).Max();
                var counted = live.Where(b => b.MaxFcnt >= b.MinFcnt).Sum(b => b.Accepted);
                double? loss = max >= min && max - min + 1 > 0 ? Math.Clamp(1 - (double)counted / (max - min + 1), 0, 1) : null;
                var reasons = new Dictionary<UdpRejectReason, long>();
                foreach (var b in live)
                {
                    foreach (var (reason, n) in b.Reasons)
                    {
                        reasons[reason] = reasons.GetValueOrDefault(reason) + n;
                    }
                }
                return new UdpNodeNumbers(accepted, live.Sum(b => b.Rejected), live.Sum(b => b.BytesIn), live.Sum(b => b.BytesOut), loss,
                    reasons, warningUntil is { } until && until > now);
            }
        }
    }

    /// <summary>Datagrams in the last 60 seconds, in one-second slots.</summary>
    private sealed class MinuteCounter
    {
        private readonly long[] seconds = new long[60];
        private readonly long[] stamps = new long[60];

        public void Add()
        {
            var now = Clock.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            var slot = (int)(now % 60);
            if (Interlocked.Exchange(ref stamps[slot], now) != now)
            {
                Interlocked.Exchange(ref seconds[slot], 0);
            }
            Interlocked.Increment(ref seconds[slot]);
        }

        public int Count
        {
            get
            {
                var now = Clock.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                long total = 0;
                for (var i = 0; i < 60; i++)
                {
                    if (now - Interlocked.Read(ref stamps[i]) < 60)
                    {
                        total += Interlocked.Read(ref seconds[i]);
                    }
                }
                return (int)total;
            }
        }
    }
}

/// <summary>
/// Token buckets per node (30 a minute, a burst of 10; OTA GETs during an open update at their own,
/// higher rate) and per source address (§4.5). Memory stays bounded: idle buckets are dropped.
/// </summary>
public sealed class UdpRateLimiter(IOptionsMonitor<UdpOptions> options)
{
    private readonly ConcurrentDictionary<Guid, TokenBucket> nodes = new();
    private readonly ConcurrentDictionary<Guid, TokenBucket> ota = new();
    private readonly ConcurrentDictionary<string, TokenBucket> addresses = new();

    public bool Allow(Guid sensorId, IPAddress address, bool otaGet)
    {
        var o = options.CurrentValue;
        var now = Clock.UtcNow;
        var key = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
        if (!Take(addresses, key, o.RateLimit.PerAddressPerSecond, Math.Max(1, o.RateLimit.PerAddressPerSecond), now))
        {
            return false;
        }
        return otaGet
            ? Take(ota, sensorId, o.Ota.GetsPerSecondPerNode, Math.Max(1, o.Ota.GetsPerSecondPerNode), now)
            : Take(nodes, sensorId, o.RateLimit.PerNodePerMinute / 60.0, Math.Max(1, o.RateLimit.PerNodeBurst), now);
    }

    private static bool Take<TKey>(ConcurrentDictionary<TKey, TokenBucket> buckets, TKey key, double perSecond, int burst, DateTime now)
        where TKey : notnull
    {
        if (buckets.Count > 50_000)
        {
            foreach (var idle in buckets.ToArray().Where(kv => now - kv.Value.Updated > TimeSpan.FromMinutes(10)).Select(kv => kv.Key))
            {
                buckets.TryRemove(idle, out _);
            }
        }
        return buckets.GetOrAdd(key, _ => new TokenBucket(burst, now)).Take(perSecond, burst, now);
    }

    private sealed class TokenBucket(int tokens, DateTime now)
    {
        private readonly Lock gate = new();
        private double available = tokens;

        public DateTime Updated { get; private set; } = now;

        public bool Take(double perSecond, int burst, DateTime now)
        {
            lock (gate)
            {
                available = Math.Min(burst, available + Math.Max(0, (now - Updated).TotalSeconds) * perSecond);
                Updated = now;
                if (available < 1)
                {
                    return false;
                }
                available -= 1;
                return true;
            }
        }
    }
}

public static class Endpoints
{
    /// <summary>"203.0.113.5:40123" or "[2001:db8::1]:40123"; IPv4 addresses the dual-stack socket maps to IPv6 are shown as IPv4.</summary>
    public static string Format(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port).ToString() : endpoint.ToString();
}
