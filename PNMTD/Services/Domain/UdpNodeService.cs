using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Udp;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Udp;

namespace PNMTD.Services.Domain;

/// <summary>
/// UDP nodes (docs/udp-iot-plan.md §4.3, §5): their settings as part of the sensor, keys generated
/// or entered, resets, the last packets and rejections, and the admin page's list and status. Every
/// change invalidates the listener's cache before and after it is saved, so no packet is checked
/// against settings that are already gone.
/// </summary>
public sealed class UdpNodeService(
    PnmtdDbContext db,
    UdpNodeCache cache,
    UdpStats stats,
    UdpListenerState listener,
    UdpEndpoint endpoint,
    UdpFirmwareCache firmware,
    MonitoringReader reader,
    IOptionsMonitor<UdpOptions> options,
    IHttpContextAccessor accessor)
{
    public const int MaxNotes = 4000;

    /// <summary>How many ids, from a random one on, a new node tries before its creation fails.</summary>
    public const int DeviceIdTries = 100;

    // --- as part of the sensor --------------------------------------------------------------

    /// <summary>Validates <c>SensorRequest.udp</c> into <paramref name="v"/>; <paramref name="existing"/> is the node on update.</summary>
    public async Task ValidateAsync(UdpNodeRequest? request, UdpNodeEntity? existing, Guid? sensorId, ApiValidator v, CancellationToken ct)
    {
        if (request is null)
        {
            return;
        }
        v.Check(Enum.IsDefined(request.KeyFamily), "udp.keyFamily", "HmacSha256, Aes, HmacSha1 or HmacMd5.");
        v.Check(Enum.IsDefined(request.SessionMode), "udp.sessionMode", "None or JoinRequired.");
        v.Check(Enum.IsDefined(request.AckMode), "udp.ackMode", "Never, OnRequest or Always.");
        if (request.DeviceId is { } id)
        {
            v.Check(id is >= 1 and <= uint.MaxValue, "udp.deviceId", "Between 1 and 4294967295.");
            if (id is >= 1 and <= uint.MaxValue)
            {
                var taken = await db.UdpNodes.AnyAsync(n => n.DeviceId == id && n.SensorId != sensorId, ct);
                v.Check(!taken, "udp.deviceId", $"Device id {id} belongs to another node.");
            }
        }
        if (request.AllowedSuites is { } suites)
        {
            v.Check(suites.Count > 0, "udp.allowedSuites", "Allow at least one suite.");
            foreach (var suite in suites.Distinct())
            {
                v.Check(Enum.IsDefined(suite) && suite.BelongsTo(request.KeyFamily), "udp.allowedSuites",
                    $"{suite} does not belong to {request.KeyFamily}: NONE, TOKEN32 or the family's suites.");
            }
        }
        if (request.ResponseSuite is { } response)
        {
            v.Check(Enum.IsDefined(response) && response.BelongsTo(request.KeyFamily), "udp.responseSuite",
                $"{response} does not belong to {request.KeyFamily}.");
        }
        UdpViews.ParseSecret(request.Key, SecuritySuites.KeyLength, "udp.key", v, "key");
        UdpViews.ParseSecret(request.Token, SecuritySuites.TokenLength, "udp.token", v, "token");
        if (existing is not null && existing.KeyFamily != request.KeyFamily && request.Key is null)
        {
            // A key is never used with two families (§3.5): a new family gets a new key.
        }
    }

    /// <summary>Creates the node of a new UDP_NODE sensor, or updates it (a missing request keeps an existing node as it is).</summary>
    public async Task<IReadOnlyList<string>> ApplyAsync(SensorEntity sensor, UdpNodeRequest? request, CancellationToken ct)
    {
        var node = await db.UdpNodes.SingleOrDefaultAsync(n => n.SensorId == sensor.Id, ct);
        if (node is not null && request is null)
        {
            return [];
        }
        request ??= new UdpNodeRequest();
        var (userId, _) = UdpViews.User(accessor);
        var now = Clock.UtcNow;
        var creating = node is null;
        if (node is null)
        {
            node = new UdpNodeEntity
            {
                SensorId = sensor.Id,
                DeviceId = request.DeviceId ?? await RandomFreeDeviceIdAsync(ct),
                Created = now,
                Key = UdpViews.NewSecret(SecuritySuites.KeyLength),
                Token = UdpViews.NewSecret(SecuritySuites.TokenLength),
                KeySource = UdpSecretSource.Generated,
                TokenSource = UdpSecretSource.Generated,
                KeyChangedAt = now,
                KeyChangedBy = userId,
                TokenChangedAt = now,
                TokenChangedBy = userId,
            };
            db.UdpNodes.Add(node);
        }
        var familyChanged = !creating && node.KeyFamily != request.KeyFamily;
        node.DeviceId = request.DeviceId ?? node.DeviceId;
        node.KeyFamily = request.KeyFamily;
        node.AllowedSuites = SecuritySuites.Mask(request.AllowedSuites is { Count: > 0 } s ? s.Distinct() : [DefaultSuite(request.KeyFamily)]);
        node.SessionMode = request.SessionMode;
        node.AckMode = request.AckMode;
        node.ReplayProtection = request.ReplayProtection;
        node.ResponseSuite = request.ResponseSuite;
        var changedSecret = false;
        if (UdpViews.ParseHex(request.Key ?? "") is { Length: SecuritySuites.KeyLength } key && request.Key is not null)
        {
            changedSecret |= SetKey(node, key, UdpSecretSource.Manual, userId, now);
        }
        else if (familyChanged)
        {
            changedSecret |= SetKey(node, UdpViews.NewSecret(SecuritySuites.KeyLength), UdpSecretSource.Generated, userId, now);
        }
        if (UdpViews.ParseHex(request.Token ?? "") is { Length: SecuritySuites.TokenLength } token && request.Token is not null)
        {
            changedSecret |= SetToken(node, token, UdpSecretSource.Manual, userId, now);
        }
        if (changedSecret && !creating)
        {
            UdpViews.Audit(db, accessor, AuditEvents.UdpKeyChanged, $"node {sensor.Name} ({node.DeviceId}): settings saved with a new key or token");
        }
        if (node.SessionMode == UdpSessionMode.None)
        {
            node.SessionKey = null;
            node.PendingSessionKey = null;
            node.PendingSessionAt = null;
        }
        cache.Invalidate();
        return await WarningsAsync(node, ct);
    }

    /// <summary>The type changed away from UDP_NODE: the node row and its last packets go; commands and updates stay.</summary>
    public async Task RemoveAsync(Guid sensorId, CancellationToken ct)
    {
        cache.Invalidate();
        await db.UdpLastPackets.Where(p => p.SensorId == sensorId).ExecuteDeleteAsync(ct);
        await db.UdpNodes.Where(n => n.SensorId == sensorId).ExecuteDeleteAsync(ct);
        stats.Forget(sensorId);
    }

    public void Changed() => cache.Invalidate();

    public static SecuritySuite DefaultSuite(KeyFamily family) => SecuritySuites.Of(family)[0];

    /// <summary>
    /// A random device id within <c>Udp:DeviceIdBytes</c> (2: 1–65,535). When it is taken, the next
    /// ones (+1, from the largest back to 1), at most <see cref="DeviceIdTries"/> in all; then the
    /// request fails and the id has to be entered.
    /// </summary>
    private async Task<long> RandomFreeDeviceIdAsync(CancellationToken ct)
    {
        var max = MaxDeviceId(options.CurrentValue.DeviceIdBytes);
        var start = Random.Shared.NextInt64(1, max + 1);
        var candidates = DeviceIdCandidates(start, max).ToList();
        var used = (await db.UdpNodes.Where(n => candidates.Contains(n.DeviceId)).Select(n => n.DeviceId).ToListAsync(ct)).ToHashSet();
        // Also the ids unsaved nodes of this request took.
        used.UnionWith(db.ChangeTracker.Entries<UdpNodeEntity>().Select(e => e.Entity.DeviceId));
        return candidates.FirstOrDefault(id => !used.Contains(id)) is var free and > 0
            ? free
            : throw ApiException.Invalid("udp.deviceId",
                $"No free device id: {start} and the {candidates.Count - 1} after it are taken. Enter one, or allow longer ids (Udp__DeviceIdBytes).");
    }

    /// <summary>The largest id of <paramref name="bytes"/> header bytes (clamped to 1–4).</summary>
    public static long MaxDeviceId(int bytes) => (1L << (8 * Math.Clamp(bytes, 1, 4))) - 1;

    /// <summary>The ids a new node tries: <paramref name="start"/>, then +1, after <paramref name="max"/> on from 1.</summary>
    public static IEnumerable<long> DeviceIdCandidates(long start, long max) =>
        Enumerable.Range(0, (int)Math.Min(DeviceIdTries, max)).Select(i => (start - 1 + i) % max + 1);

    private static bool SetKey(UdpNodeEntity node, byte[] key, UdpSecretSource source, Guid? userId, DateTime now)
    {
        var changed = !node.Key.AsSpan().SequenceEqual(key);
        node.Key = key;
        node.KeySource = source;
        node.KeyChangedAt = now;
        node.KeyChangedBy = userId;
        if (changed)
        {
            ResetState(node);
        }
        return changed;
    }

    private static bool SetToken(UdpNodeEntity node, byte[] token, UdpSecretSource source, Guid? userId, DateTime now)
    {
        var changed = !node.Token.AsSpan().SequenceEqual(token);
        node.Token = token;
        node.TokenSource = source;
        node.TokenChangedAt = now;
        node.TokenChangedBy = userId;
        if (changed)
        {
            ResetState(node);
        }
        return changed;
    }

    /// <summary>§4.3: sessions, the counter window and the retransmission cache go with the old key.</summary>
    private static void ResetState(UdpNodeEntity node)
    {
        node.SessionKey = null;
        node.SessionStartedAt = null;
        node.PendingSessionKey = null;
        node.PendingSessionAt = null;
        ResetCounter(node);
    }

    private static void ResetCounter(UdpNodeEntity node)
    {
        node.FcntHigh = null;
        node.FcntWindow = 0;
        node.PlainFcntHigh = null;
        node.PlainFcntWindow = 0;
        node.LastUplinkFcnt = null;
        node.LastUplinkDigest = null;
        node.LastUplinkAt = null;
        node.LastResponse = null;
    }

    private async Task<IReadOnlyList<string>> WarningsAsync(UdpNodeEntity node, CancellationToken ct)
    {
        var warnings = new List<string>();
        var key = node.Key;
        if (await db.UdpNodes.AnyAsync(n => n.SensorId != node.SensorId && n.Key == key, ct))
        {
            warnings.Add("Another node uses the same key: one compromised device would expose the other.");
        }
        warnings.AddRange(SuiteWarnings(node));
        return warnings;
    }

    private static IEnumerable<string> SuiteWarnings(UdpNodeEntity node)
    {
        var allowed = SecuritySuites.FromMask(node.AllowedSuites);
        if (node.ResponseSuite is null && allowed.Any(s => !s.IsKeyed()))
        {
            yield return "Commands answer in the suite the uplink came with: an unsigned or token-only uplink gets an unsigned or token-only command, which anyone who sees a packet can forge. Set a keyed response suite for commands.";
        }
        else if (node.ResponseSuite is { } rs && !rs.IsKeyed())
        {
            yield return $"The response suite {rs.Name()} is not signed: commands can be forged.";
        }
        if (allowed.Any(s => s.IsLegacy()))
        {
            yield return "HMAC-MD5 and HMAC-SHA1 are legacy suites, for devices that have nothing better.";
        }
        if (!allowed.Any(s => s.IsKeyed()))
        {
            yield return "No keyed suite is allowed: firmware updates need one.";
        }
    }

    // --- the sensor's view --------------------------------------------------------------------

    public async Task<UdpNodeDetail?> DetailAsync(Guid sensorId, CancellationToken ct)
    {
        var node = await db.UdpNodes.AsNoTracking().SingleOrDefaultAsync(n => n.SensorId == sensorId, ct);
        if (node is null)
        {
            return null;
        }
        var users = await UserNamesAsync([node.KeyChangedBy, node.TokenChangedBy], ct);
        var openCommands = await db.UdpCommands.CountAsync(c => c.SensorId == sensorId
            && (c.State == UdpCommandState.Queued || (c.State == UdpCommandState.Sent && c.Delivery == UdpCommandDelivery.ResultRequired)), ct);
        var update = (await OpenUpdatesAsync([sensorId], ct)).GetValueOrDefault(sensorId);
        var numbers = stats.Numbers(sensorId);
        var warnings = SuiteWarnings(node).ToList();
        var key = node.Key;
        if (await db.UdpNodes.AnyAsync(n => n.SensorId != sensorId && n.Key == key, ct))
        {
            warnings.Insert(0, "Another node uses the same key: one compromised device would expose the other.");
        }
        if (numbers.BadTagWarning)
        {
            warnings.Insert(0, "Many packets with a bad tag arrived recently: someone may be guessing tags, or the device has a wrong key.");
        }
        if (!listener.Listening)
        {
            warnings.Insert(0, listener.Enabled ? $"The UDP listener is not running: {listener.Error}" : "The UDP listener is off (Udp__Enabled=false).");
        }
        return new UdpNodeDetail(node.DeviceId, UdpViews.IdBytes(node.DeviceId), node.KeyFamily, SecuritySuites.FromMask(node.AllowedSuites),
            node.SessionMode, node.SessionKey is not null, node.PendingSessionKey is not null, UdpViews.NullableUtc(node.SessionStartedAt),
            node.AckMode, node.ReplayProtection, node.ResponseSuite,
            node.KeySource, Instants.Utc(node.KeyChangedAt), users.GetValueOrDefault(node.KeyChangedBy ?? Guid.Empty),
            node.TokenSource, Instants.Utc(node.TokenChangedAt), users.GetValueOrDefault(node.TokenChangedBy ?? Guid.Empty),
            UdpViews.HighestFcnt(node), UdpViews.NullableUtc(node.LastSeenAt), node.LastEndpoint, node.LastSuite, UdpViews.NullableUtc(node.LastBootAt),
            node.LastBootAt is null ? null : Payloads.BootReason(node.LastBootReason), node.FirmwareVersion,
            UdpViews.Numbers(numbers), numbers.BadTagWarning, openCommands, update, listener.Enabled, endpoint.Value, warnings);
    }

    public async Task<UdpSecrets?> SecretsAsync(Guid sensorId, CancellationToken ct) =>
        await db.UdpNodes.AsNoTracking().Where(n => n.SensorId == sensorId)
            .Select(n => new UdpSecrets(Convert.ToHexString(n.Key), Convert.ToHexString(n.Token)))
            .SingleOrDefaultAsync(ct);

    private async Task<UdpNodeEntity> NodeAsync(Guid sensorId, CancellationToken ct) =>
        await db.UdpNodes.Include(n => n.Sensor).SingleOrDefaultAsync(n => n.SensorId == sensorId, ct) ?? throw ApiException.NotFound("UDP node");

    /// <summary><c>PUT /sensors/{id}/udp/key</c>: values entered by hand.</summary>
    public async Task<UdpSecretsChanged> SetSecretsAsync(Guid sensorId, UdpKeyRequest request, CancellationToken ct)
    {
        var v = new ApiValidator();
        v.Check(request.Key is not null || request.Token is not null, "key", "A key, a token or both.");
        var key = UdpViews.ParseSecret(request.Key, SecuritySuites.KeyLength, "key", v, "key");
        var token = UdpViews.ParseSecret(request.Token, SecuritySuites.TokenLength, "token", v, "token");
        v.ThrowIfInvalid();
        var node = await NodeAsync(sensorId, ct);
        var (userId, _) = UdpViews.User(accessor);
        var now = Clock.UtcNow;
        cache.Invalidate();
        if (key is not null)
        {
            SetKey(node, key, UdpSecretSource.Manual, userId, now);
        }
        if (token is not null)
        {
            SetToken(node, token, UdpSecretSource.Manual, userId, now);
        }
        UdpViews.Audit(db, accessor, AuditEvents.UdpKeyChanged,
            $"node {node.Sensor.Name} ({node.DeviceId}): {Which(key is not null, token is not null)} entered manually");
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        return new UdpSecretsChanged(new UdpSecrets(Convert.ToHexString(node.Key), Convert.ToHexString(node.Token)), await WarningsAsync(node, ct));
    }

    public async Task<UdpSecretsChanged> RegenerateAsync(Guid sensorId, UdpRegenerateRequest request, CancellationToken ct)
    {
        new ApiValidator().Check(request.Key || request.Token, "key", "Regenerate the key, the token or both.").ThrowIfInvalid();
        var node = await NodeAsync(sensorId, ct);
        var (userId, _) = UdpViews.User(accessor);
        var now = Clock.UtcNow;
        cache.Invalidate();
        if (request.Key)
        {
            SetKey(node, UdpViews.NewSecret(SecuritySuites.KeyLength), UdpSecretSource.Generated, userId, now);
        }
        if (request.Token)
        {
            SetToken(node, UdpViews.NewSecret(SecuritySuites.TokenLength), UdpSecretSource.Generated, userId, now);
        }
        UdpViews.Audit(db, accessor, AuditEvents.UdpKeyChanged,
            $"node {node.Sensor.Name} ({node.DeviceId}): {Which(request.Key, request.Token)} regenerated");
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        return new UdpSecretsChanged(new UdpSecrets(Convert.ToHexString(node.Key), Convert.ToHexString(node.Token)), await WarningsAsync(node, ct));
    }

    private static string Which(bool key, bool token) => (key, token) switch
    {
        (true, true) => "key and token",
        (true, false) => "key",
        _ => "token",
    };

    /// <summary>§3.7: the next packet sets a new highest counter.</summary>
    public async Task ResetCounterAsync(Guid sensorId, CancellationToken ct)
    {
        var node = await NodeAsync(sensorId, ct);
        cache.Invalidate();
        ResetCounter(node);
        UdpViews.Audit(db, accessor, AuditEvents.UdpCounterReset, $"node {node.Sensor.Name} ({node.DeviceId}): counter window reset");
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
    }

    /// <summary>§3.8: drops the active and the pending session; the device must JOIN again.</summary>
    public async Task ResetSessionAsync(Guid sensorId, CancellationToken ct)
    {
        var node = await NodeAsync(sensorId, ct);
        cache.Invalidate();
        ResetState(node);
        UdpViews.Audit(db, accessor, AuditEvents.UdpSessionReset, $"node {node.Sensor.Name} ({node.DeviceId}): session reset");
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
    }

    public async Task<IReadOnlyList<UdpPacketView>> LastPacketsAsync(Guid sensorId, CancellationToken ct)
    {
        await EnsureNodeAsync(sensorId, ct);
        var packets = await db.UdpLastPackets.AsNoTracking().Where(p => p.SensorId == sensorId).ToListAsync(ct);
        return packets.OrderByDescending(p => p.ReceivedAt).Select(p => new UdpPacketView(p.Type, Instants.Utc(p.ReceivedAt), p.Endpoint, p.Suite, p.Fcnt,
            p.Datagram, p.Payload, UdpViews.Fields(p.Datagram, p.Payload, true) ?? [], p.Response,
            UdpViews.Fields(p.Response, null, p.Response is null ? null : true))).ToList();
    }

    public async Task<IReadOnlyList<UdpRejectionView>> RejectionsAsync(Guid sensorId, CancellationToken ct)
    {
        await EnsureNodeAsync(sensorId, ct);
        return stats.Rejections(sensorId)
            .Select(r => new UdpRejectionView(r.At, r.Reason, r.Endpoint, r.Datagram, r.Detail,
                UdpViews.Fields(r.Datagram, null, r.Reason == UdpRejectReason.BadTag ? false : null)))
            .ToList();
    }

    public async Task EnsureNodeAsync(Guid sensorId, CancellationToken ct)
    {
        if (!await db.UdpNodes.AnyAsync(n => n.SensorId == sensorId, ct))
        {
            throw ApiException.NotFound("UDP node");
        }
    }

    // --- the admin page -----------------------------------------------------------------------

    private static readonly string[] NodeSortFields = ["name", "host", "deviceid", "lastseen", "state"];

    public async Task<Paged<UdpNodeListItem>> ListAsync(UdpNodeQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "name", NodeSortFields);
        var nodes = await db.UdpNodes.AsNoTracking()
            .Select(n => new
            {
                Node = n,
                n.Sensor.Name,
                n.Sensor.Enabled,
                n.Sensor.Notes,
                n.Sensor.ParentId,
                HostName = n.Sensor.Parent.Name,
            })
            .ToListAsync(ct);
        var ids = nodes.Select(n => n.Node.SensorId).ToList();
        var snapshot = await reader.SnapshotAsync(s => s.Type == Lib.Models.Enum.SensorType.UDP_NODE, ct);
        var open = await db.UdpCommands
            .Where(c => c.SensorId != null && ids.Contains(c.SensorId.Value)
                && (c.State == UdpCommandState.Queued || (c.State == UdpCommandState.Sent && c.Delivery == UdpCommandDelivery.ResultRequired)))
            .GroupBy(c => c.SensorId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var updates = await OpenUpdatesAsync(ids, ct);
        var users = await UserNamesAsync(nodes.Select(n => n.Node.KeyChangedBy).ToList(), ct);
        var items = nodes.Select(n =>
        {
            var numbers = stats.Numbers(n.Node.SensorId);
            var state = snapshot.Sensors.TryGetValue(n.Node.SensorId, out var status) ? status.State : SensorState.NoData;
            return new UdpNodeListItem(n.Node.SensorId, n.Name, new Ref(n.ParentId, n.HostName), n.Node.DeviceId, UdpViews.IdBytes(n.Node.DeviceId),
                n.Enabled, state, n.Node.KeyFamily, SecuritySuites.FromMask(n.Node.AllowedSuites), n.Node.KeySource, Instants.Utc(n.Node.KeyChangedAt),
                users.GetValueOrDefault(n.Node.KeyChangedBy ?? Guid.Empty), n.Node.TokenSource, n.Node.SessionMode, n.Node.SessionKey is not null,
                n.Node.AckMode, n.Node.ResponseSuite, UdpViews.NullableUtc(n.Node.LastSeenAt), n.Node.LastEndpoint, n.Node.LastSuite, n.Node.FcntHigh,
                open.GetValueOrDefault(n.Node.SensorId), n.Node.FirmwareVersion, updates.GetValueOrDefault(n.Node.SensorId),
                numbers.Accepted, numbers.Loss, numbers.Rejected, numbers.BadTagWarning, n.Notes);
        })
            .Where(i => query.State is not { } s || i.State == s)
            .Where(i => query.KeyFamily is not { } f || i.KeyFamily == f)
            .Where(i => query.Enabled is not { } e || i.Enabled == e)
            .Where(i => string.IsNullOrWhiteSpace(query.FirmwareVersion) || string.Equals(i.FirmwareVersion, query.FirmwareVersion.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(i => Text.Contains(i.Name, query.Search) || Text.Contains(i.Host.Name, query.Search) || Text.Contains(i.Notes, query.Search)
                || (query.Search is { } q && i.DeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture) == q.Trim()));
        IOrderedEnumerable<UdpNodeListItem> sorted = field switch
        {
            "host" => items.OrderBy(i => i.Host.Name, StringComparer.OrdinalIgnoreCase),
            "deviceid" => items.OrderBy(i => i.DeviceId),
            "lastseen" => items.OrderBy(i => i.LastSeenAt ?? DateTime.MinValue),
            "state" => items.OrderBy(i => SensorService.StateOrder(i.State)),
            _ => items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        var list = (descending ? sorted.Reverse() : sorted.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        return Paging.Page(list, query);
    }

    public async Task<UdpStatus> StatusAsync(CancellationToken ct)
    {
        var names = await db.UdpNodes.AsNoTracking().Select(n => new { n.SensorId, n.Sensor.Name }).ToListAsync(ct);
        var warned = stats.NodesWithBadTagWarning().ToHashSet();
        var warnings = names.Where(n => warned.Contains(n.SensorId))
            .Select(n => new UdpWarning(n.SensorId, n.Name, "Many packets with a bad tag arrived recently."))
            .ToList();
        return new UdpStatus(listener.Enabled, listener.Listening, listener.Port, endpoint.Value, listener.Error, listener.Since, listener.QueueDepth,
            stats.Received, stats.Accepted, stats.Responses, stats.BytesIn, stats.BytesOut, stats.PacketsLastMinute, UdpViews.Reasons(stats.Rejected),
            names.Count, warnings, firmware.Count, firmware.Bytes);
    }

    public async Task<IReadOnlyList<UdpUnknownSenderItem>> UnknownSendersAsync(CancellationToken ct)
    {
        var known = (await db.UdpNodes.Select(n => n.DeviceId).ToListAsync(ct)).ToHashSet();
        return stats.UnknownSenders()
            .Select(s => new UdpUnknownSenderItem(s.DeviceId, s.Address, s.FirstSeen, s.LastSeen, s.Count, s.LastSuite, s.LastDatagram, s.Problem,
                s.DeviceId is { } id && known.Contains(id)))
            .ToList();
    }

    private async Task<Dictionary<Guid, UdpOtaProgress>> OpenUpdatesAsync(IReadOnlyCollection<Guid> sensorIds, CancellationToken ct)
    {
        var jobs = await db.UdpOtaJobs.AsNoTracking()
            .Where(j => j.SensorId != null && sensorIds.Contains(j.SensorId.Value)
                && (j.State == UdpOtaState.Queued || j.State == UdpOtaState.Offered || j.State == UdpOtaState.Downloading || j.State == UdpOtaState.Verified))
            .OrderByDescending(j => j.Created)
            .ToListAsync(ct);
        return jobs.GroupBy(j => j.SensorId!.Value).ToDictionary(g => g.Key, g =>
        {
            var j = g.First();
            return new UdpOtaProgress(j.Id, j.FirmwareName, j.FirmwareVersion, j.State, j.HighestOffset, j.FirmwareSize, UdpOtaService.Progress(j));
        });
    }

    private async Task<Dictionary<Guid, string>> UserNamesAsync(IReadOnlyCollection<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return wanted.Count == 0
            ? []
            : await db.Users.Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, ct);
    }

    public int MaxCommandBytes => options.CurrentValue.Commands.MaxBytes;
}
