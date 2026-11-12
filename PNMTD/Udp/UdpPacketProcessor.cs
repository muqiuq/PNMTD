using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Udp;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Udp;

/// <summary>
/// One datagram in, at most one datagram out (docs/udp-iot-plan.md §4.5, docs/udp-protocol.md §3.12).
/// The cheap checks come first and touch no database; a rejected packet writes nothing. An accepted
/// uplink is one transaction: counter window, event, last packet, command and update bookkeeping, the
/// response and its command-log rows. OTA GETs take a short path without a transaction (§4.11).
/// The listener hands every node's packets to one worker, so a node's packets arrive here in order.
/// </summary>
public sealed class UdpPacketProcessor(
    IDbContextFactory<PnmtdDbContext> dbs,
    UdpNodeCache cache,
    UdpStats stats,
    UdpRateLimiter limiter,
    UdpFirmwareCache firmware,
    IOptionsMonitor<UdpOptions> options,
    ILogger<UdpPacketProcessor> logger)
{
    private TimeSpan JoinInterval => TimeSpan.FromSeconds(Math.Max(0, options.CurrentValue.JoinIntervalSeconds));
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AliveDuringOta = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, NodeRuntime> runtimes = new();

    /// <summary>A node's state between packets, loaded from its row and reloaded when the node changes (cache generation).</summary>
    private sealed class NodeRuntime
    {
        public long Generation;
        public CounterState Counter;
        public long? LastUplinkFcnt;
        public byte[]? LastUplinkDigest;
        public DateTime? LastUplinkAt;
        public byte[]? LastResponse;
        public (Guid CommandId, short Attempt, short CmdId)? LastResponseCommand;
        public DateTime? LastJoinAt;
        public bool OtaLoaded;
        public OtaRuntime? Ota;
        public bool Dirty;
        public DateTime LastFlush;
        public DateTime? LastAliveEventAt;
        public DateTime? LastSeenAt;
        public string? LastEndpoint;
        public SecuritySuite? LastSuite;
    }

    private sealed class OtaRuntime
    {
        public Guid JobId;
        public byte UpdateId;
        public Guid? FirmwareId;
        public int Size;
        public int ChunkSize;
        public UdpOtaState State;
        public long PendingBytes;
        public int PendingChunks;
        public int PendingRefetches;
        public long ServedEnd;
        public DateTime? LastRequestAt;
    }

    private enum KeyUse { None, Root, Active, Pending }

    private sealed record Verified(byte[] Payload, long Full, CounterState NewState, bool Fresh, KeyUse Use, bool CounterReset);

    public async Task<byte[]?> ProcessAsync(byte[] datagram, IPEndPoint remote, CancellationToken ct)
    {
        stats.Datagram(datagram.Length);
        if (!PacketCodec.TryParse(datagram, out var packet, out var error))
        {
            var peeked = PeekDeviceId(datagram);
            var known = peeked is { } id ? await cache.GetAsync(id, ct) : null;
            if (known is not null)
            {
                stats.Reject(UdpRejectReason.Malformed, known.SensorId, remote, datagram, error.ToString());
            }
            else
            {
                stats.Unknown(peeked, remote, null, datagram, error.ToString());
            }
            return null;
        }
        var h = packet.Header;
        var node = await cache.GetAsync(h.DeviceId, ct);
        if (node is null)
        {
            stats.Unknown(h.DeviceId, remote, h.Suite, datagram, h.IsDownlink ? "a downlink type" : null);
            return null;
        }
        if (Precheck(node, h) is { } refused)
        {
            stats.Reject(refused.Reason, node.SensorId, remote, datagram, refused.Detail);
            return null;
        }
        var rt = await RuntimeAsync(node, ct);
        var ota = h.Type == PacketType.Ota ? await OtaAsync(rt, node.SensorId, ct) : null;
        if (!limiter.Allow(node.SensorId, remote.Address, otaGet: ota is { State: UdpOtaState.Offered or UdpOtaState.Downloading or UdpOtaState.Verified }))
        {
            stats.Reject(UdpRejectReason.RateLimited, node.SensorId, remote, datagram);
            return null;
        }
        if (h.Type == PacketType.Join && rt.LastJoinAt is { } lastJoin && Clock.UtcNow - lastJoin < JoinInterval
            && !(rt.LastUplinkFcnt == 0 && IsRetransmission(rt, 0, datagram)))
        {
            stats.Reject(UdpRejectReason.RateLimited, node.SensorId, remote, datagram, "at most one JOIN per 10 seconds");
            return null;
        }

        var verified = Verify(node, rt, packet, out var rejection);
        if (verified is null)
        {
            stats.Reject(rejection!.Value.Reason, node.SensorId, remote, datagram, rejection.Value.Detail);
            return null;
        }

        if (IsRetransmission(rt, verified.Full, datagram))
        {
            if (rt.LastResponseCommand is { } sent)
            {
                await LogResendAsync(node, rt, sent, Endpoints.Format(remote), h.Suite, verified.Full, ct);
            }
            if (rt.LastResponse is { } again)
            {
                stats.Response(node.SensorId, again.Length);
            }
            return rt.LastResponse;
        }

        if (!ValidPayload(h.Type, verified.Payload))
        {
            stats.Reject(UdpRejectReason.Malformed, node.SensorId, remote, datagram, $"the payload of a {h.Type.Name()} is invalid");
            return null;
        }

        var endpoint = Endpoints.Format(remote);
        try
        {
            if (h.Type == PacketType.Ota && Payloads.ReadOta(verified.Payload) is { Op: OtaOp.Get } get)
            {
                if (verified.Use == KeyUse.Pending && !await PromoteAsync(node, rt, verified, ct))
                {
                    return null;
                }
                var chunk = await ServeChunkAsync(node, rt, packet, get, verified, endpoint, datagram, ct);
                stats.Accept(node.SensorId, datagram.Length, verified.Full, counted: true);
                if (chunk is not null)
                {
                    stats.Response(node.SensorId, chunk.Length);
                }
                return chunk;
            }
            var response = await AcceptAsync(node, rt, packet, verified, endpoint, datagram, ct);
            stats.Accept(node.SensorId, datagram.Length, verified.Full, counted: h.Type != PacketType.Join);
            if (response is not null)
            {
                stats.Response(node.SensorId, response.Length);
            }
            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No answer: for the device that means "not stored", and it retries (§4.5 step 12).
            logger.LogError(ex, "UDP packet from {Remote} for node {DeviceId} failed", endpoint, h.DeviceId);
            runtimes.TryRemove(node.SensorId, out _);
            stats.Reject(UdpRejectReason.Error, node.SensorId, remote, datagram, ex.GetType().Name);
            return null;
        }
    }

    // --- the checks before the database -------------------------------------------------------

    private static uint? PeekDeviceId(byte[] datagram)
    {
        if (datagram.Length < 3 || datagram[0] >> 6 != PacketCodec.Version)
        {
            return null;
        }
        var length = (datagram[0] & 0x3) + 1;
        if (datagram.Length < 2 + length)
        {
            return null;
        }
        uint id = 0;
        for (var i = 0; i < length; i++)
        {
            id = (id << 8) | datagram[2 + i];
        }
        return id == 0 ? null : id;
    }

    private static (UdpRejectReason Reason, string? Detail)? Precheck(UdpNodeSnapshot node, PacketHeader h)
    {
        if (!node.Enabled)
        {
            return (UdpRejectReason.Disabled, null);
        }
        if (h.IsDownlink || h.More)
        {
            return (UdpRejectReason.WrongDirection, h.IsDownlink ? $"{h.Type.Name()} is a downlink type" : "M is set on an uplink");
        }
        if (h.Type == PacketType.Join)
        {
            if (node.SessionMode != UdpSessionMode.JoinRequired)
            {
                return (UdpRejectReason.SessionRequired, "a JOIN, but the node's session mode is None");
            }
            if (h.Suite != node.Family.JoinSuite())
            {
                return (UdpRejectReason.SuiteNotAllowed, $"a JOIN must use {node.Family.JoinSuite().Name()}");
            }
            if (h.Counter != 0)
            {
                return (UdpRejectReason.Malformed, "a JOIN has counter 0");
            }
            return null;
        }
        if (!h.Suite.InMask(node.AllowedSuites) || !h.Suite.BelongsTo(node.Family))
        {
            return (UdpRejectReason.SuiteNotAllowed, $"{h.Suite.Name()} is not allowed for this node");
        }
        if (h.Type == PacketType.Ota && !h.Suite.IsKeyed())
        {
            return (UdpRejectReason.SuiteNotAllowed, "OTA needs a keyed suite");
        }
        if (h.Suite.IsKeyed() && node.SessionMode == UdpSessionMode.JoinRequired && node.SessionKey is null && node.PendingSessionKey is null)
        {
            return (UdpRejectReason.SessionRequired, "no session yet: the node must JOIN first");
        }
        return null;
    }

    private Verified? Verify(UdpNodeSnapshot node, NodeRuntime rt, ParsedPacket packet, out (UdpRejectReason Reason, string? Detail)? rejection)
    {
        var h = packet.Header;
        rejection = null;
        if (h.Type == PacketType.Join)
        {
            var join = PacketCodec.Open(packet, node.Key, node.Token, 0);
            if (join is null)
            {
                rejection = (UdpRejectReason.BadTag, null);
                return null;
            }
            return new Verified(join, 0, rt.Counter, true, KeyUse.Root, false);
        }

        byte[]? payload = null;
        var use = KeyUse.None;
        var state = rt.Counter;
        long full;
        if (h.Suite.IsKeyed() && node.SessionMode == UdpSessionMode.JoinRequired)
        {
            full = FrameCounter.Reconstruct(null, h.Counter);
            if (node.PendingSessionKey is { } pending && (payload = PacketCodec.Open(packet, pending, node.Token, (uint)full)) is not null)
            {
                use = KeyUse.Pending;
                state = CounterState.Empty;
            }
            else if (node.SessionKey is { } session)
            {
                full = FrameCounter.Reconstruct(rt.Counter.High, h.Counter);
                if (full is >= 0 and <= FrameCounter.Max)
                {
                    payload = PacketCodec.Open(packet, session, node.Token, (uint)full);
                    use = KeyUse.Active;
                }
            }
        }
        else
        {
            full = FrameCounter.Reconstruct(rt.Counter.High, h.Counter);
            if (full is >= 0 and <= FrameCounter.Max || !h.Suite.IsKeyed())
            {
                payload = PacketCodec.Open(packet, node.Key, node.Token, (uint)Math.Clamp(full, 0, FrameCounter.Max));
                use = h.Suite.IsKeyed() ? KeyUse.Root : KeyUse.None;
            }
        }
        var enforce = node.ReplayProtection && (h.Suite.IsKeyed() || (node.ResponseSuite?.IsKeyed() ?? false));
        if (payload is null)
        {
            // A keyed packet whose counter cannot be reconstructed cannot be verified either.
            rejection = full is < 0 or > FrameCounter.Max && enforce ? (full < 0 ? UdpRejectReason.Replay : UdpRejectReason.CounterJump, null) : (UdpRejectReason.BadTag, null);
            return null;
        }
        full = Math.Clamp(full, 0, FrameCounter.Max);
        var verdict = FrameCounter.Check(state, full);
        var fresh = verdict is CounterVerdict.New or CounterVerdict.Reordered;
        if (!fresh && enforce && !IsRetransmission(rt, full, packet.Datagram.ToArray()))
        {
            rejection = verdict == CounterVerdict.Jump
                ? (UdpRejectReason.CounterJump, $"{full} is more than {FrameCounter.MaxGap} above {state.High}")
                : (UdpRejectReason.Replay, $"counter {full} was seen already (highest {state.High})");
            return null;
        }
        // Not enforced (NONE, TOKEN32, or replay protection off): a counter that goes back starts the window again.
        var reset = !fresh;
        var next = fresh ? FrameCounter.Accept(state, full) : FrameCounter.Accept(CounterState.Empty, full);
        return new Verified(payload, full, next, fresh, use, reset);
    }

    private bool IsRetransmission(NodeRuntime rt, long full, byte[] datagram) =>
        rt.LastUplinkFcnt == full && rt.LastUplinkDigest is { } digest && rt.LastUplinkAt is { } at
        && Clock.UtcNow - at < TimeSpan.FromSeconds(options.CurrentValue.RetransmissionWindowSeconds)
        && CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(datagram));

    private static bool ValidPayload(PacketType type, byte[] payload) => type switch
    {
        PacketType.Event => payload.Length >= 3,
        PacketType.CmdResult => payload.Length >= 2,
        PacketType.Join => payload.Length == 4,
        PacketType.Ota => Payloads.ReadOta(payload) is not null,
        _ => true,
    };

    // --- runtime state ------------------------------------------------------------------------

    private async Task<NodeRuntime> RuntimeAsync(UdpNodeSnapshot node, CancellationToken ct)
    {
        var generation = cache.Generation;
        if (runtimes.TryGetValue(node.SensorId, out var rt) && rt.Generation == generation)
        {
            return rt;
        }
        if (rt is { Dirty: true })
        {
            // The node changed (a reset, new keys): keep the download's progress, not the old counter.
            await FlushAsync(node, rt, counters: false, ct);
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var row = await db.UdpNodes.AsNoTracking().Where(n => n.SensorId == node.SensorId)
            .Select(n => new { n.FcntHigh, n.FcntWindow, n.LastUplinkFcnt, n.LastUplinkDigest, n.LastUplinkAt, n.LastResponse })
            .SingleOrDefaultAsync(ct);
        var fresh = new NodeRuntime
        {
            Generation = generation,
            Counter = new CounterState(row?.FcntHigh, unchecked((ulong)(row?.FcntWindow ?? 0))),
            LastUplinkFcnt = row?.LastUplinkFcnt,
            LastUplinkDigest = row?.LastUplinkDigest,
            LastUplinkAt = row?.LastUplinkAt,
            LastResponse = row?.LastResponse,
            LastJoinAt = rt?.LastJoinAt,
            LastAliveEventAt = rt?.LastAliveEventAt,
            LastFlush = Clock.UtcNow,
        };
        runtimes[node.SensorId] = fresh;
        return fresh;
    }

    private async Task<OtaRuntime?> OtaAsync(NodeRuntime rt, Guid sensorId, CancellationToken ct)
    {
        if (rt.OtaLoaded)
        {
            return rt.Ota;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var job = await db.UdpOtaJobs.AsNoTracking()
            .Where(j => j.SensorId == sensorId && (j.State == UdpOtaState.Offered || j.State == UdpOtaState.Downloading || j.State == UdpOtaState.Verified))
            .OrderByDescending(j => j.Created)
            .FirstOrDefaultAsync(ct);
        rt.Ota = job is null ? null : new OtaRuntime
        {
            JobId = job.Id,
            UpdateId = (byte)job.UpdateId,
            FirmwareId = job.FirmwareId,
            Size = job.FirmwareSize,
            ChunkSize = job.ChunkSize,
            State = job.State,
            ServedEnd = job.HighestOffset,
            LastRequestAt = job.LastRequestAt,
        };
        rt.OtaLoaded = true;
        return rt.Ota;
    }

    /// <summary>Writes what OTA GETs changed in memory: the counter window and last uplink, and the download's progress.</summary>
    private async Task FlushAsync(UdpNodeSnapshot node, NodeRuntime rt, bool counters, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        if (counters)
        {
            var high = rt.Counter.High;
            var window = unchecked((long)rt.Counter.Window);
            await db.UdpNodes.Where(n => n.SensorId == node.SensorId).ExecuteUpdateAsync(s => s
                .SetProperty(n => n.FcntHigh, high)
                .SetProperty(n => n.FcntWindow, window)
                .SetProperty(n => n.LastUplinkFcnt, rt.LastUplinkFcnt)
                .SetProperty(n => n.LastUplinkDigest, rt.LastUplinkDigest)
                .SetProperty(n => n.LastUplinkAt, rt.LastUplinkAt)
                .SetProperty(n => n.LastResponse, rt.LastResponse)
                .SetProperty(n => n.LastSeenAt, rt.LastSeenAt)
                .SetProperty(n => n.LastEndpoint, rt.LastEndpoint)
                .SetProperty(n => n.LastSuite, rt.LastSuite), ct);
        }
        if (rt.Ota is { } ota && (ota.PendingChunks > 0 || ota.PendingBytes > 0))
        {
            var (bytes, chunks, refetches, end, last) = (ota.PendingBytes, ota.PendingChunks, ota.PendingRefetches, ota.ServedEnd, ota.LastRequestAt);
            await db.UdpOtaJobs.Where(j => j.Id == ota.JobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.BytesServed, j => j.BytesServed + bytes)
                .SetProperty(j => j.ChunksServed, j => j.ChunksServed + chunks)
                .SetProperty(j => j.ChunkRetransmissions, j => j.ChunkRetransmissions + refetches)
                .SetProperty(j => j.HighestOffset, j => j.HighestOffset > end ? j.HighestOffset : end)
                .SetProperty(j => j.LastRequestAt, last), ct);
            ota.PendingBytes = 0;
            ota.PendingChunks = 0;
            ota.PendingRefetches = 0;
        }
        if (counters && (rt.LastAliveEventAt is not { } alive || now - alive >= AliveDuringOta))
        {
            // A long download must not end in 460: at most one 230 a minute (§4.2).
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await SensorEventWriter.WriteAsync(db, node.SensorId, PNMTStatusCodes.UDP_ALIVE, "Alive", "udp:" + rt.LastEndpoint, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            rt.LastAliveEventAt = now;
        }
        rt.Dirty = false;
        rt.LastFlush = now;
    }

    /// <summary>On shutdown: what the downloads changed in memory.</summary>
    public async Task FlushAllAsync(CancellationToken ct)
    {
        foreach (var (sensorId, rt) in runtimes.Where(kv => kv.Value.Dirty).ToList())
        {
            var node = (await cache.AllAsync(ct)).FirstOrDefault(n => n.SensorId == sensorId);
            if (node is not null)
            {
                try
                {
                    await FlushAsync(node, rt, counters: rt.Generation == cache.Generation, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Flushing UDP node {SensorId} failed", sensorId);
                }
            }
        }
    }

    // --- OTA GET: the short path ----------------------------------------------------------------

    private async Task<byte[]?> ServeChunkAsync(UdpNodeSnapshot node, NodeRuntime rt, ParsedPacket packet, OtaPayload get, Verified v,
        string endpoint, byte[] datagram, CancellationToken ct)
    {
        var h = packet.Header;
        var now = Clock.UtcNow;
        var ota = await OtaAsync(rt, node.SensorId, ct);
        byte[] data = [];
        var offset = get.Offset;
        if (ota is null || ota.UpdateId != get.UpdateId || ota.FirmwareId is null
            || await firmware.GetAsync(ota.FirmwareId.Value, ct) is not { } image)
        {
            offset = OtaChunkPayload.Abort;
        }
        else
        {
            if (ota.State == UdpOtaState.Offered)
            {
                await StartDownloadAsync(node, ota, endpoint, h.Suite, v.Full, datagram, ct);
            }
            var length = Math.Min(get.Length is { } l && l > 0 ? l : ota.ChunkSize, ota.ChunkSize);
            if (offset < image.Length)
            {
                var end = (int)Math.Min((long)offset + length, image.Length);
                data = image.AsSpan((int)offset, end - (int)offset).ToArray();
                if (end <= ota.ServedEnd)
                {
                    ota.PendingRefetches++;
                }
                ota.ServedEnd = Math.Max(ota.ServedEnd, end);
            }
            ota.PendingBytes += data.Length;
            ota.PendingChunks++;
            ota.LastRequestAt = now;
        }
        // The key of this GET's session: the pending one if this very packet promoted it.
        var sessionKey = v.Use == KeyUse.Pending ? node.PendingSessionKey : node.SessionKey;
        var suite = node.ResponseSuite is { } rs && rs.IsKeyed() && rs.BelongsTo(node.Family) ? rs : h.Suite;
        var key = node.SessionMode == UdpSessionMode.JoinRequired ? sessionKey ?? [] : node.Key;
        var response = PacketCodec.Build(new PacketHeader(PacketType.OtaChunk, suite, h.DeviceId, h.Counter, IdLength: h.IdLength),
            Payloads.OtaChunk(new OtaChunkPayload(get.UpdateId, offset, data)), key, node.Token, (uint)v.Full);

        rt.Counter = v.NewState;
        rt.LastUplinkFcnt = v.Full;
        rt.LastUplinkDigest = SHA256.HashData(datagram);
        rt.LastUplinkAt = now;
        rt.LastResponse = response;
        rt.LastResponseCommand = null;
        rt.LastSeenAt = now;
        rt.LastEndpoint = endpoint;
        rt.LastSuite = h.Suite;
        rt.Dirty = true;
        if (now - rt.LastFlush >= FlushInterval)
        {
            await FlushAsync(node, rt, counters: true, ct);
        }
        return response;
    }

    /// <summary>The first packet under a JOIN's pending session makes it the active one (§3.8), here for an OTA GET.</summary>
    private async Task<bool> PromoteAsync(UdpNodeSnapshot node, NodeRuntime rt, Verified v, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.UdpNodes.FromSqlInterpolated($"SELECT * FROM udp_nodes WHERE \"SensorId\" = {node.SensorId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (row?.PendingSessionKey is null || cache.Generation != rt.Generation)
        {
            cache.Invalidate();
            return false;
        }
        row.SessionKey = row.PendingSessionKey;
        row.PendingSessionKey = null;
        row.PendingSessionAt = null;
        row.SessionStartedAt = Clock.UtcNow;
        row.Counter = v.NewState;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        rt.Counter = v.NewState;
        cache.Invalidate();
        return true;
    }

    private async Task StartDownloadAsync(UdpNodeSnapshot node, OtaRuntime ota, string endpoint, SecuritySuite suite, long fcnt, byte[] datagram, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var job = await db.UdpOtaJobs.SingleOrDefaultAsync(j => j.Id == ota.JobId, ct);
        if (job is not null && job.State == UdpOtaState.Offered)
        {
            job.State = UdpOtaState.Downloading;
            job.StartedAt = Clock.UtcNow;
            db.UdpCommandLog.Add(LogRow(node.SensorId, job.DeviceId, job.NodeName, UdpLogKind.OtaStarted).Also(e => { e.OtaJobId = job.Id; e.Direction = UdpDirection.Up; e.Endpoint = endpoint; e.Suite = suite; e.Fcnt = fcnt; e.Datagram = datagram; e.CmdId = job.UpdateId; }));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        ota.State = UdpOtaState.Downloading;
    }

    // --- an accepted uplink: one transaction ----------------------------------------------------

    private async Task<byte[]?> AcceptAsync(UdpNodeSnapshot node, NodeRuntime rt, ParsedPacket packet, Verified v, string endpoint,
        byte[] datagram, CancellationToken ct)
    {
        if (rt.Dirty)
        {
            await FlushAsync(node, rt, counters: true, ct);
        }
        var h = packet.Header;
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.UdpNodes.FromSqlInterpolated($"SELECT * FROM udp_nodes WHERE \"SensorId\" = {node.SensorId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (row is null || cache.Generation != rt.Generation
            || (h.Type != PacketType.Join && (row.FcntHigh != rt.Counter.High || row.FcntWindow != unchecked((long)rt.Counter.Window))))
        {
            // Deleted, or changed since this packet was checked (a reset, new keys; also from
            // `pnmtd udp reset-counter` in another process): drop it, the device sends again.
            cache.Invalidate();
            runtimes.TryRemove(node.SensorId, out _);
            return null;
        }
        var name = await db.Sensors.Where(s => s.Id == node.SensorId).Select(s => s.Name).SingleAsync(ct);
        var ctx = new Accepted(db, row, name, h, v, endpoint, datagram, now);
        var sessionChanged = false;
        if (v.Use == KeyUse.Pending)
        {
            row.SessionKey = row.PendingSessionKey;
            row.PendingSessionKey = null;
            row.PendingSessionAt = null;
            row.SessionStartedAt = now;
            sessionChanged = true;
        }
        if (h.Type != PacketType.Join)
        {
            row.Counter = v.NewState;
        }
        row.LastSeenAt = now;
        row.LastEndpoint = endpoint;
        row.LastSuite = h.Suite;

        var code = PNMTStatusCodes.UDP_ALIVE;
        var message = "Alive";
        UdpLastPacketEntity? last = null;
        byte[]? response = null;
        var otaChanged = false;
        switch (h.Type)
        {
            case PacketType.Heartbeat:
                message = v.Payload.Length > 0 ? PacketCodec.Render(v.Payload) : "Alive";
                last = await LastPacketAsync(ctx, ct);
                break;
            case PacketType.Data:
                code = PNMTStatusCodes.UDP_DATA;
                message = v.Payload.Length > 0 ? PacketCodec.Render(v.Payload) : "(no data)";
                last = await LastPacketAsync(ctx, ct);
                break;
            case PacketType.Event:
                last = await LastPacketAsync(ctx, ct);
                break;
            case PacketType.Boot:
                var boot = Payloads.ReadBoot(v.Payload);
                code = PNMTStatusCodes.UDP_BOOT;
                message = $"Restarted: {Payloads.BootReason(boot.Reason)}" + (string.IsNullOrWhiteSpace(boot.Info) ? "" : $" ({boot.Info.Trim()})");
                row.LastBootAt = now;
                row.LastBootReason = boot.Reason;
                if (!string.IsNullOrWhiteSpace(boot.Info))
                {
                    row.FirmwareVersion = boot.Info.Trim();
                    otaChanged |= await AppliedByBootAsync(ctx, boot.Info.Trim(), ct);
                }
                last = await LastPacketAsync(ctx, ct);
                break;
            case PacketType.CmdResult:
                await MatchResultAsync(ctx, Payloads.ReadCmdResult(v.Payload)!, ct);
                break;
            case PacketType.Join:
                var devNonce = Payloads.ReadJoin(v.Payload)!.Value;
                var serverNonce = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
                row.PendingSessionKey = SessionKeys.Derive(row.KeyFamily, row.Key, devNonce, serverNonce);
                row.PendingSessionAt = now;
                sessionChanged = true;
                response = PacketCodec.Build(new PacketHeader(PacketType.JoinAccept, h.Suite, h.DeviceId, 0, IdLength: h.IdLength),
                    Payloads.JoinAccept(serverNonce), row.Key, row.Token, devNonce);
                last = await LastPacketAsync(ctx, ct);
                break;
            case PacketType.Ota:
                otaChanged |= await OtaReportAsync(ctx, Payloads.ReadOta(v.Payload)!, ct);
                break;
        }
        await SensorEventWriter.WriteAsync(db, row.SensorId, code, message, "udp:" + endpoint, ct);
        if (h.Type == PacketType.Join)
        {
            rt.LastJoinAt = now;
        }
        else
        {
            var answer = await RespondAsync(ctx, ct);
            response = answer.Bytes;
            rt.LastResponseCommand = answer.Command;
            otaChanged |= answer.OtaChanged;
        }
        if (last is not null)
        {
            last.Response = response;
        }
        row.LastUplinkFcnt = v.Full;
        row.LastUplinkDigest = SHA256.HashData(datagram);
        row.LastUplinkAt = now;
        row.LastResponse = response;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        rt.Counter = row.Counter;
        rt.LastUplinkFcnt = row.LastUplinkFcnt;
        rt.LastUplinkDigest = row.LastUplinkDigest;
        rt.LastUplinkAt = now;
        rt.LastResponse = response;
        rt.LastSeenAt = now;
        rt.LastEndpoint = endpoint;
        rt.LastSuite = h.Suite;
        rt.LastAliveEventAt = now;
        if (otaChanged)
        {
            rt.OtaLoaded = false;
        }
        if (sessionChanged)
        {
            cache.Invalidate();
        }
        if (v.CounterReset && rt.Counter.High is { } high && high > 1)
        {
            logger.LogDebug("UDP node {DeviceId}: counter reset to {Counter} (restart?)", h.DeviceId, high);
        }
        return response;
    }

    private sealed record Accepted(PnmtdDbContext Db, UdpNodeEntity Row, string Name, PacketHeader Header, Verified Verified,
        string Endpoint, byte[] Datagram, DateTime Now);

    private static async Task<UdpLastPacketEntity> LastPacketAsync(Accepted a, CancellationToken ct)
    {
        var last = await a.Db.UdpLastPackets.FindAsync([a.Row.SensorId, a.Header.Type], ct);
        if (last is null)
        {
            last = new UdpLastPacketEntity { SensorId = a.Row.SensorId, Type = a.Header.Type };
            a.Db.UdpLastPackets.Add(last);
        }
        last.ReceivedAt = a.Now;
        last.Endpoint = a.Endpoint;
        last.Suite = a.Header.Suite;
        last.Fcnt = a.Verified.Full;
        last.Datagram = a.Datagram;
        last.Payload = a.Verified.Payload;
        last.Response = null;
        return last;
    }

    private static UdpCommandLogEntity LogRow(Guid sensorId, long deviceId, string name, UdpLogKind kind) => new()
    {
        Created = Clock.UtcNow,
        SensorId = sensorId,
        DeviceId = deviceId,
        NodeName = name,
        Kind = kind,
    };

    private static UdpCommandLogEntity Log(Accepted a, UdpLogKind kind) => LogRow(a.Row.SensorId, a.Row.DeviceId, a.Name, kind);

    private static async Task MatchResultAsync(Accepted a, CmdResultPayload result, CancellationToken ct)
    {
        var command = await a.Db.UdpCommands
            .Where(c => c.SensorId == a.Row.SensorId && c.CmdId == result.CmdId && c.State == UdpCommandState.Sent)
            .OrderByDescending(c => c.LastSentAt)
            .FirstOrDefaultAsync(ct);
        var entry = Log(a, command is null ? UdpLogKind.UnmatchedResult : UdpLogKind.ResultReceived).Also(e => { e.CommandId = command?.Id; e.Direction = UdpDirection.Up; e.CmdId = result.CmdId; e.Endpoint = a.Endpoint; e.Suite = a.Header.Suite; e.Fcnt = a.Verified.Full; e.Datagram = a.Datagram; e.Payload = a.Verified.Payload; e.ResultStatus = result.Status; e.Note = command is null ? $"no open command with CMD_ID {result.CmdId}" : Payloads.CommandStatus(result.Status); });
        a.Db.UdpCommandLog.Add(entry);
        if (command is not null)
        {
            command.State = UdpCommandState.Completed;
            command.CompletedAt = a.Now;
            command.ResultStatus = result.Status;
            command.Result = result.Result;
        }
    }

    private static async Task<bool> OtaReportAsync(Accepted a, OtaPayload report, CancellationToken ct)
    {
        if (report.Op == OtaOp.Get)
        {
            return false;
        }
        var job = await a.Db.UdpOtaJobs
            .Where(j => j.SensorId == a.Row.SensorId && j.UpdateId == report.UpdateId
                && (j.State == UdpOtaState.Offered || j.State == UdpOtaState.Downloading || j.State == UdpOtaState.Verified))
            .OrderByDescending(j => j.Created)
            .FirstOrDefaultAsync(ct);
        if (job is null)
        {
            return false;
        }
        var (kind, note) = report.Op switch
        {
            OtaOp.Decline => (UdpLogKind.OtaDeclined, Payloads.DeclineReason(report.Reason)),
            OtaOp.Verified => (UdpLogKind.OtaVerified, "the SHA-256 matched on the device"),
            OtaOp.Failed => (UdpLogKind.OtaFailed, Payloads.FailReason(report.Reason)),
            _ => (UdpLogKind.OtaApplied, report.Version is { Length: > 0 } version ? $"running {version}" : "applied"),
        };
        switch (report.Op)
        {
            case OtaOp.Decline:
                job.State = UdpOtaState.Declined;
                job.Reason = report.Reason;
                job.FinishedAt = a.Now;
                break;
            case OtaOp.Verified:
                job.State = UdpOtaState.Verified;
                break;
            case OtaOp.Failed:
                job.State = UdpOtaState.Failed;
                job.Reason = report.Reason;
                job.FinishedAt = a.Now;
                break;
            case OtaOp.Applied:
                job.State = UdpOtaState.Applied;
                job.AppliedVersion = report.Version ?? job.FirmwareVersion;
                job.FinishedAt = a.Now;
                a.Row.FirmwareVersion = job.AppliedVersion;
                break;
        }
        a.Db.UdpCommandLog.Add(Log(a, kind).Also(e => { e.OtaJobId = job.Id; e.Direction = UdpDirection.Up; e.CmdId = job.UpdateId; e.Endpoint = a.Endpoint; e.Suite = a.Header.Suite; e.Fcnt = a.Verified.Full; e.Datagram = a.Datagram; e.Payload = a.Verified.Payload; e.ResultStatus = report.Op is OtaOp.Decline or OtaOp.Failed ? report.Reason : null; e.Note = note; }));
        return true;
    }

    /// <summary>§3.11: a BOOT whose INFO is the offered VERSION also means applied.</summary>
    private static async Task<bool> AppliedByBootAsync(Accepted a, string version, CancellationToken ct)
    {
        var job = await a.Db.UdpOtaJobs
            .Where(j => j.SensorId == a.Row.SensorId && j.FirmwareVersion == version
                && (j.State == UdpOtaState.Downloading || j.State == UdpOtaState.Verified))
            .OrderByDescending(j => j.Created)
            .FirstOrDefaultAsync(ct);
        if (job is null)
        {
            return false;
        }
        job.State = UdpOtaState.Applied;
        job.AppliedVersion = version;
        job.FinishedAt = a.Now;
        a.Db.UdpCommandLog.Add(Log(a, UdpLogKind.OtaApplied).Also(e => { e.OtaJobId = job.Id; e.Direction = UdpDirection.Up; e.CmdId = job.UpdateId; e.Endpoint = a.Endpoint; e.Suite = a.Header.Suite; e.Fcnt = a.Verified.Full; e.Datagram = a.Datagram; e.Payload = a.Verified.Payload; e.Note = $"BOOT reports {version}"; }));
        return true;
    }

    private sealed record Answer(byte[]? Bytes, (Guid CommandId, short Attempt, short CmdId)? Command, bool OtaChanged);

    /// <summary>
    /// The response slot of a delivery uplink (§3.10): the oldest deliverable command, else a waiting
    /// firmware offer, else an ACK when one is due. M says that more is waiting.
    /// </summary>
    private async Task<Answer> RespondAsync(Accepted a, CancellationToken ct)
    {
        var db = a.Db;
        var row = a.Row;
        var h = a.Header;
        var now = a.Now;
        var open = await db.UdpCommands
            .Where(c => c.SensorId == row.SensorId
                && (c.State == UdpCommandState.Queued || (c.State == UdpCommandState.Sent && c.Delivery == UdpCommandDelivery.ResultRequired)))
            .OrderBy(c => c.Created)
            .ToListAsync(ct);
        // The query filters on the stored state; this transaction may have changed it already (a
        // CMD_RESULT just completed a command), and EF hands back its tracked objects.
        open = open.Where(c => c.State == UdpCommandState.Queued || (c.State == UdpCommandState.Sent && c.Delivery == UdpCommandDelivery.ResultRequired)).ToList();
        var deliverable = new List<(UdpCommandEntity Command, SecuritySuite Suite)>();
        foreach (var c in open)
        {
            if (c.ExpiresAt is { } expires && expires <= now)
            {
                c.State = UdpCommandState.Expired;
                c.CompletedAt = now;
                db.UdpCommandLog.Add(Log(a, UdpLogKind.Expired).Also(e => { e.CommandId = c.Id; e.CmdId = c.CmdId; e.Note = "expired before its result arrived"; }));
                continue;
            }
            if (c.State == UdpCommandState.Sent && c.Attempts >= c.MaxAttempts)
            {
                c.State = UdpCommandState.Failed;
                c.CompletedAt = now;
                c.Note = "max attempts reached";
                db.UdpCommandLog.Add(Log(a, UdpLogKind.Failed).Also(e => { e.CommandId = c.Id; e.CmdId = c.CmdId; e.Note = $"no result after {c.Attempts} attempts"; }));
                continue;
            }
            var suite = c.Suite ?? row.ResponseSuite ?? h.Suite;
            if (KeyFor(row, suite, a.Verified, h.Suite) is not null)
            {
                deliverable.Add((c, suite));
            }
        }

        var (job, jobChanged) = await OfferableJobAsync(a, ct);
        var offerSuite = job is null ? (SecuritySuite?)null : OfferSuite(row, h.Suite);
        if (offerSuite is null)
        {
            job = null;
        }

        if (deliverable.Count > 0)
        {
            var (command, suite) = deliverable[0];
            var more = deliverable.Count > 1 || job is not null;
            command.State = UdpCommandState.Sent;
            command.Attempts++;
            command.LastSentAt = now;
            var bytes = PacketCodec.Build(new PacketHeader(PacketType.Command, suite, h.DeviceId, h.Counter, More: more, IdLength: h.IdLength),
                Payloads.Command((byte)command.CmdId, command.Payload), KeyFor(row, suite, a.Verified, h.Suite)!, row.Token, (uint)a.Verified.Full);
            db.UdpCommandLog.Add(Log(a, UdpLogKind.Sent).Also(e => { e.CommandId = command.Id; e.Direction = UdpDirection.Down; e.CmdId = command.CmdId; e.Attempt = command.Attempts; e.Endpoint = a.Endpoint; e.Suite = suite; e.Fcnt = a.Verified.Full; e.Datagram = bytes; e.Payload = command.Payload; e.Note = command.Delivery == UdpCommandDelivery.FireAndForget ? "fire and forget" : null; }));
            return new Answer(bytes, (command.Id, command.Attempts, command.CmdId), jobChanged);
        }

        if (job is not null && offerSuite is { } os)
        {
            job.State = UdpOtaState.Offered;
            job.OfferAttempts++;
            job.OfferedAt ??= now;
            var offer = Payloads.OtaOffer(new OtaOfferPayload((byte)job.UpdateId, (uint)job.FirmwareSize, (ushort)job.ChunkSize, job.FirmwareSha256, job.FirmwareVersion));
            var bytes = PacketCodec.Build(new PacketHeader(PacketType.OtaOffer, os, h.DeviceId, h.Counter, IdLength: h.IdLength),
                offer, KeyFor(row, os, a.Verified, h.Suite)!, row.Token, (uint)a.Verified.Full);
            db.UdpCommandLog.Add(Log(a, UdpLogKind.OtaOffered).Also(e => { e.OtaJobId = job.Id; e.Direction = UdpDirection.Down; e.CmdId = job.UpdateId; e.Attempt = job.OfferAttempts; e.Endpoint = a.Endpoint; e.Suite = os; e.Fcnt = a.Verified.Full; e.Datagram = bytes; e.Payload = offer; e.Note = $"{job.FirmwareName} {job.FirmwareVersion}, {job.FirmwareSize} bytes"; }));
            return new Answer(bytes, null, true);
        }

        if (row.AckMode == UdpAckMode.Always || (row.AckMode == UdpAckMode.OnRequest && h.AckRequested))
        {
            var suite = row.ResponseSuite is { } rs && KeyFor(row, rs, a.Verified, h.Suite) is not null ? rs : h.Suite;
            var bytes = PacketCodec.Build(new PacketHeader(PacketType.Ack, suite, h.DeviceId, h.Counter, IdLength: h.IdLength),
                [], KeyFor(row, suite, a.Verified, h.Suite) ?? [], row.Token, (uint)a.Verified.Full);
            return new Answer(bytes, null, jobChanged);
        }
        return new Answer(null, null, jobChanged);
    }

    /// <summary>
    /// The key for a downlink in <paramref name="suite"/>, or null when this slot cannot use it: a
    /// keyed suite without a key (no session yet), a suite of another family, or AES-CCM in answer
    /// to an unkeyed uplink whose counter was not fresh (the nonce would repeat, §3.5).
    /// </summary>
    private static byte[]? KeyFor(UdpNodeEntity row, SecuritySuite suite, Verified v, SecuritySuite uplinkSuite)
    {
        if (!suite.IsKeyed())
        {
            return suite == SecuritySuite.Token32 && row.Token.Length != 4 ? null : [];
        }
        if (!suite.BelongsTo(row.KeyFamily))
        {
            return null;
        }
        if (suite.IsEncrypted() && !uplinkSuite.IsKeyed() && !v.Fresh)
        {
            return null;
        }
        return row.SessionMode == UdpSessionMode.JoinRequired ? row.SessionKey : row.Key;
    }

    /// <summary>OTA downlinks: the response suite if it is keyed, else the uplink's if that is keyed (§3.11).</summary>
    private static SecuritySuite? OfferSuite(UdpNodeEntity row, SecuritySuite uplink)
    {
        var key = row.SessionMode == UdpSessionMode.JoinRequired ? row.SessionKey : row.Key;
        if (key is null)
        {
            return null;
        }
        if (row.ResponseSuite is { } rs && rs.IsKeyed() && rs.BelongsTo(row.KeyFamily))
        {
            return rs;
        }
        return uplink.IsKeyed() ? uplink : null;
    }

    private static async Task<(UdpOtaJobEntity? Job, bool Changed)> OfferableJobAsync(Accepted a, CancellationToken ct)
    {
        var jobs = await a.Db.UdpOtaJobs
            .Where(j => j.SensorId == a.Row.SensorId && (j.State == UdpOtaState.Queued || j.State == UdpOtaState.Offered))
            .OrderBy(j => j.Created)
            .ToListAsync(ct);
        var changed = false;
        // As for commands: a report in this transaction may have moved a job on already.
        foreach (var job in jobs.Where(j => j.State is UdpOtaState.Queued or UdpOtaState.Offered))
        {
            if (job.ExpiresAt <= a.Now)
            {
                job.State = UdpOtaState.Expired;
                job.FinishedAt = a.Now;
                a.Db.UdpCommandLog.Add(Log(a, UdpLogKind.OtaExpired).Also(e => { e.OtaJobId = job.Id; e.CmdId = job.UpdateId; e.Note = "the offer expired"; }));
                changed = true;
                continue;
            }
            if (job.OfferAttempts >= job.MaxOfferAttempts)
            {
                job.State = UdpOtaState.Failed;
                job.FinishedAt = a.Now;
                a.Db.UdpCommandLog.Add(Log(a, UdpLogKind.OtaFailed).Also(e => { e.OtaJobId = job.Id; e.CmdId = job.UpdateId; e.Note = $"the device never answered the offer ({job.OfferAttempts} offers)"; }));
                changed = true;
                continue;
            }
            return (job, changed);
        }
        return (null, changed);
    }

    private async Task LogResendAsync(UdpNodeSnapshot node, NodeRuntime rt, (Guid CommandId, short Attempt, short CmdId) sent, string endpoint,
        SecuritySuite suite, long fcnt, CancellationToken ct)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            db.UdpCommandLog.Add(LogRow(node.SensorId, node.DeviceId, node.Name, UdpLogKind.Resent).Also(e => { e.CommandId = sent.CommandId; e.Direction = UdpDirection.Down; e.CmdId = sent.CmdId; e.Attempt = sent.Attempt; e.Endpoint = endpoint; e.Suite = suite; e.Fcnt = fcnt; e.Datagram = rt.LastResponse; e.Note = "the uplink came again: the same response again, the same attempt"; }));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Logging a resend for UDP node {DeviceId} failed", node.DeviceId);
        }
    }
}
