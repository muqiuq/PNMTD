using System.Security.Cryptography;
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

/// <summary>Firmware images and updates (docs/udp-iot-plan.md §4.11). Admin only; every upload, start and cancel is audited.</summary>
public sealed class UdpOtaService(PnmtdDbContext db, UdpNodeCache cache, UdpFirmwareCache firmwareCache, IOptionsMonitor<UdpOptions> options,
    IHttpContextAccessor accessor)
{
    public const int MaxVersionLength = 64;

    // --- firmware --------------------------------------------------------------------------

    public async Task<IReadOnlyList<UdpFirmwareItem>> FirmwareAsync(CancellationToken ct)
    {
        var images = await db.UdpFirmware.AsNoTracking().OrderByDescending(f => f.UploadedAt)
            .Select(f => new { f.Id, f.Name, f.Version, f.FileName, f.Size, f.Sha256, f.Notes, f.UploadedAt, f.UploadedByName })
            .ToListAsync(ct);
        // The first bytes only, for the magic hint: the images stay in the database.
        var heads = (await db.Database.SqlQueryRaw<FirmwareHead>("SELECT \"Id\", substring(\"Content\" from 1 for 4) AS \"Head\" FROM udp_firmware")
            .ToListAsync(ct)).ToDictionary(h => h.Id, h => h.Head);
        var jobs = await db.UdpOtaJobs.Where(j => j.FirmwareId != null).GroupBy(j => j.FirmwareId!.Value)
            .Select(g => new
            {
                g.Key,
                All = g.Count(),
                Open = g.Count(j => j.State == UdpOtaState.Queued || j.State == UdpOtaState.Offered || j.State == UdpOtaState.Downloading || j.State == UdpOtaState.Verified),
            })
            .ToDictionaryAsync(g => g.Key, ct);
        var versions = await db.UdpNodes.Where(n => n.FirmwareVersion != null).GroupBy(n => n.FirmwareVersion!)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return images.Select(f => new UdpFirmwareItem(f.Id, f.Name, f.Version, f.FileName, f.Size, Convert.ToHexString(f.Sha256).ToLowerInvariant(),
            f.Notes, Instants.Utc(f.UploadedAt), f.UploadedByName, jobs.GetValueOrDefault(f.Id)?.All ?? 0, jobs.GetValueOrDefault(f.Id)?.Open ?? 0,
            versions.GetValueOrDefault(f.Version), Magic(heads.GetValueOrDefault(f.Id) ?? []))).ToList();
    }

    private sealed class FirmwareHead
    {
        public Guid Id { get; set; }

        public byte[] Head { get; set; } = [];
    }

    /// <summary>A hint only, never a check: PNMT does not parse images (§4.11).</summary>
    public static string? Magic(byte[] head) => head switch
    {
        [0xE9, ..] => "ESP32 application image (0xE9)",
        [0x3D, 0xB8, 0xF3, 0x96, ..] => "MCUboot image (3D B8 F3 96)",
        [0x7F, (byte)'E', (byte)'L', (byte)'F', ..] => "ELF file: devices usually need the .bin",
        _ => null,
    };

    public async Task<UdpFirmwareUploaded> UploadAsync(IFormFile? file, string? name, string? version, string? notes, CancellationToken ct)
    {
        var max = options.CurrentValue.Ota.MaxImageBytes;
        var v = new ApiValidator();
        v.Check(file is { Length: > 0 }, "file", "The .bin to upload.");
        v.Check(file is null || file.Length <= max, "file", $"At most {max} bytes.");
        v.Check(Text.Trimmed(name) is { Length: <= 200 }, "name", "A name of 1 to 200 characters.");
        v.Check(Text.Trimmed(version) is { Length: <= MaxVersionLength }, "version", $"A version of 1 to {MaxVersionLength} characters.");
        v.Check(notes is null || notes.Length <= 4000, "notes", "At most 4000 characters.");
        v.ThrowIfInvalid();
        await using var stream = file!.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var content = buffer.ToArray();
        var sha = SHA256.HashData(content);
        var warnings = new List<string>();
        var same = await db.UdpFirmware.Where(f => f.Sha256 == sha).Select(f => f.Name + " " + f.Version).ToListAsync(ct);
        if (same.Count > 0)
        {
            warnings.Add($"The same image is uploaded already: {string.Join(", ", same)}.");
        }
        var (userId, userName) = UdpViews.User(accessor);
        var entity = new UdpFirmwareEntity
        {
            Id = Guid.NewGuid(),
            Name = name!.Trim(),
            Version = version!.Trim(),
            FileName = Path.GetFileName(file.FileName ?? "firmware.bin"),
            Size = content.Length,
            Sha256 = sha,
            Content = content,
            Notes = Text.Trimmed(notes),
            UploadedAt = Clock.UtcNow,
            UploadedBy = userId,
            UploadedByName = userName,
        };
        db.UdpFirmware.Add(entity);
        UdpViews.Audit(db, accessor, AuthEvents.UdpFirmwareUploaded,
            $"{entity.Name} {entity.Version}, {entity.Size} bytes, SHA-256 {Convert.ToHexString(sha).ToLowerInvariant()}");
        await db.SaveChangesAsync(ct);
        var magic = Magic(content.Take(4).ToArray());
        return new UdpFirmwareUploaded(new UdpFirmwareItem(entity.Id, entity.Name, entity.Version, entity.FileName, entity.Size,
            Convert.ToHexString(sha).ToLowerInvariant(), entity.Notes, Instants.Utc(entity.UploadedAt), userName, 0, 0, 0, magic), warnings);
    }

    public async Task<UdpFirmwareItem> UpdateFirmwareAsync(Guid id, UdpFirmwareRequest request, CancellationToken ct)
    {
        new ApiValidator()
            .Check(Text.Trimmed(request.Name) is { Length: <= 200 }, "name", "A name of 1 to 200 characters.")
            .Check(Text.Trimmed(request.Version) is { Length: <= MaxVersionLength }, "version", $"A version of 1 to {MaxVersionLength} characters.")
            .Check(request.Notes is null || request.Notes.Length <= 4000, "notes", "At most 4000 characters.")
            .ThrowIfInvalid();
        var image = await db.UdpFirmware.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw ApiException.NotFound("Firmware");
        image.Name = request.Name!.Trim();
        image.Version = request.Version!.Trim();
        image.Notes = Text.Trimmed(request.Notes);
        await db.SaveChangesAsync(ct);
        return (await FirmwareAsync(ct)).Single(f => f.Id == id);
    }

    public async Task DeleteFirmwareAsync(Guid id, CancellationToken ct)
    {
        var image = await db.UdpFirmware.Where(f => f.Id == id).Select(f => new { f.Name, f.Version }).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Firmware");
        if (await db.UdpOtaJobs.AnyAsync(j => j.FirmwareId == id && (j.State == UdpOtaState.Queued || j.State == UdpOtaState.Offered
            || j.State == UdpOtaState.Downloading || j.State == UdpOtaState.Verified), ct))
        {
            throw ApiException.Conflict("in_use", "An open update uses this image: cancel it first.");
        }
        UdpViews.Audit(db, accessor, AuthEvents.UdpFirmwareDeleted, $"{image.Name} {image.Version}");
        await db.SaveChangesAsync(ct);
        await db.UdpFirmware.Where(f => f.Id == id).ExecuteDeleteAsync(ct);
        firmwareCache.Evict(id);
    }

    public async Task<(byte[] Content, string FileName)> ContentAsync(Guid id, CancellationToken ct)
    {
        var image = await db.UdpFirmware.AsNoTracking().Where(f => f.Id == id).Select(f => new { f.Content, f.FileName }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Firmware");
        return (image.Content, image.FileName);
    }

    // --- updates ---------------------------------------------------------------------------

    public static double Progress(UdpOtaJobEntity j) => j.State == UdpOtaState.Applied || j.State == UdpOtaState.Verified
        ? 1
        : j.FirmwareSize == 0 ? 0 : Math.Clamp((double)j.HighestOffset / j.FirmwareSize, 0, 1);

    /// <summary>§3.13: IPv4, a 1-byte id, an 8-byte tag.</summary>
    public static UdpOtaEstimate Estimate(int size, int chunk)
    {
        var trips = Math.Max(1, (size + chunk - 1) / chunk);
        const int ip = 28;
        var down = (long)trips * (ip + 5 + 5 + 8) + size;
        var up = (long)trips * (ip + 5 + 6 + 8);
        return new UdpOtaEstimate(trips, down + up, trips * 0.1, trips * 2.0);
    }

    public static UdpOtaJobItem Item(UdpOtaJobEntity j) => new(j.Id, j.SensorId, j.DeviceId, j.NodeName, j.FirmwareId, j.FirmwareName, j.FirmwareVersion,
        j.FirmwareSize, Convert.ToHexString(j.FirmwareSha256).ToLowerInvariant(), j.UpdateId, j.ChunkSize, j.State, j.Reason,
        j.Reason is { } r ? (j.State == UdpOtaState.Declined ? Payloads.DeclineReason((byte)r) : Payloads.FailReason((byte)r)) : null,
        j.OfferAttempts, j.MaxOfferAttempts, j.BytesServed, j.ChunksServed, j.ChunkRetransmissions, j.HighestOffset, Progress(j),
        Instants.Utc(j.Created), j.CreatedByName, UdpViews.NullableUtc(j.OfferedAt), UdpViews.NullableUtc(j.StartedAt),
        UdpViews.NullableUtc(j.LastRequestAt), UdpViews.NullableUtc(j.FinishedAt), Instants.Utc(j.ExpiresAt), j.AppliedVersion,
        Estimate(j.FirmwareSize, j.ChunkSize));

    public async Task<UdpOtaJobsCreated> CreateJobsAsync(UdpOtaJobRequest request, CancellationToken ct)
    {
        var o = options.CurrentValue.Ota;
        var chunk = request.ChunkSize ?? o.DefaultChunkBytes;
        var days = request.ExpiresInDays ?? o.DefaultOfferExpiryDays;
        var attempts = request.MaxOfferAttempts ?? o.DefaultMaxOfferAttempts;
        new ApiValidator()
            .Check(request.FirmwareId != Guid.Empty, "firmwareId", "The image to install.")
            .Check(request.SensorIds is { Count: > 0 }, "sensorIds", "At least one node.")
            .Check(chunk is >= 64 and <= 480, "chunkSize", "Between 64 and 480 bytes.")
            .Check(days is >= 1 and <= 365, "expiresInDays", "Between 1 and 365 days.")
            .Check(attempts is >= 1 and <= 1000, "maxOfferAttempts", "Between 1 and 1000.")
            .ThrowIfInvalid();
        var image = await db.UdpFirmware.AsNoTracking().Where(f => f.Id == request.FirmwareId)
            .Select(f => new { f.Id, f.Name, f.Version, f.Size, f.Sha256 }).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Firmware");
        var ids = request.SensorIds!.Distinct().ToList();
        var nodes = await db.UdpNodes.Include(n => n.Sensor).Where(n => ids.Contains(n.SensorId)).ToListAsync(ct);
        var busy = (await db.UdpOtaJobs.Where(j => j.SensorId != null && ids.Contains(j.SensorId.Value)
                && (j.State == UdpOtaState.Queued || j.State == UdpOtaState.Offered || j.State == UdpOtaState.Downloading || j.State == UdpOtaState.Verified))
            .Select(j => j.SensorId!.Value).ToListAsync(ct)).ToHashSet();
        var sensorNames = await db.Sensors.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var (userId, userName) = UdpViews.User(accessor);
        var now = Clock.UtcNow;
        var created = new List<UdpOtaJobEntity>();
        var refused = new List<UdpOtaRefusal>();
        foreach (var id in ids)
        {
            var node = nodes.FirstOrDefault(n => n.SensorId == id);
            if (node is null)
            {
                refused.Add(new UdpOtaRefusal(id, sensorNames.GetValueOrDefault(id) ?? id.ToString(), "not a UDP node"));
                continue;
            }
            if (busy.Contains(id))
            {
                refused.Add(new UdpOtaRefusal(id, node.Sensor.Name, "an update is open already"));
                continue;
            }
            if (!SecuritySuites.FromMask(node.AllowedSuites).Any(s => s.IsKeyed()))
            {
                refused.Add(new UdpOtaRefusal(id, node.Sensor.Name, "no keyed suite allowed: OTA needs one"));
                continue;
            }
            var job = new UdpOtaJobEntity
            {
                Id = Guid.NewGuid(),
                SensorId = id,
                DeviceId = node.DeviceId,
                NodeName = node.Sensor.Name,
                FirmwareId = image.Id,
                FirmwareName = image.Name,
                FirmwareVersion = image.Version,
                FirmwareSize = image.Size,
                FirmwareSha256 = image.Sha256,
                UpdateId = node.NextUpdateId,
                ChunkSize = (short)chunk,
                State = UdpOtaState.Queued,
                MaxOfferAttempts = (short)attempts,
                Created = now,
                CreatedBy = userId,
                CreatedByName = userName,
                ExpiresAt = now.AddDays(days),
            };
            node.NextUpdateId = (short)((node.NextUpdateId + 1) % 256);
            db.UdpOtaJobs.Add(job);
            db.UdpCommandLog.Add(new UdpCommandLogEntity
            {
                Created = now,
                OtaJobId = job.Id,
                SensorId = id,
                DeviceId = node.DeviceId,
                NodeName = node.Sensor.Name,
                Kind = UdpLogKind.OtaQueued,
                CmdId = job.UpdateId,
                UserId = userId,
                UserName = userName,
                Note = $"{image.Name} {image.Version}, {image.Size} bytes, chunks of {chunk}",
            });
            created.Add(job);
        }
        if (created.Count > 0)
        {
            UdpViews.Audit(db, accessor, AuthEvents.UdpUpdateStarted,
                $"{image.Name} {image.Version} for {string.Join(", ", created.Select(j => $"{j.NodeName} ({j.DeviceId})"))}");
        }
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        return new UdpOtaJobsCreated(created.Select(Item).ToList(), refused);
    }

    public async Task<Paged<UdpOtaJobItem>> JobsAsync(UdpOtaJobQuery query, CancellationToken ct)
    {
        var jobs = db.UdpOtaJobs.AsNoTracking();
        if (query.State is { } state)
        {
            jobs = jobs.Where(j => j.State == state);
        }
        if (query.FirmwareId is { } firmwareId)
        {
            jobs = jobs.Where(j => j.FirmwareId == firmwareId);
        }
        if (query.SensorId is { } sensorId)
        {
            jobs = jobs.Where(j => j.SensorId == sensorId);
        }
        if (query.From is { } from)
        {
            var f = Clock.AsUtc(from);
            jobs = jobs.Where(j => j.Created >= f);
        }
        if (query.To is { } to)
        {
            var t = Clock.AsUtc(to);
            jobs = jobs.Where(j => j.Created <= t);
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            jobs = jobs.Where(j => EF.Functions.ILike(j.NodeName, pattern, @"\") || EF.Functions.ILike(j.FirmwareName, pattern, @"\"));
        }
        var page = await Paging.PageAsync(jobs.OrderByDescending(j => j.Created), query, ct);
        return new Paged<UdpOtaJobItem>(page.Items.Select(Item).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<IReadOnlyList<UdpOtaJobDetail>> NodeJobsAsync(Guid sensorId, CancellationToken ct)
    {
        var jobs = await db.UdpOtaJobs.AsNoTracking().Where(j => j.SensorId == sensorId).OrderByDescending(j => j.Created).Take(50).ToListAsync(ct);
        var ids = jobs.Select(j => (Guid?)j.Id).ToList();
        var log = await db.UdpCommandLog.AsNoTracking().Where(l => ids.Contains(l.OtaJobId)).OrderBy(l => l.Created).ThenBy(l => l.Id).ToListAsync(ct);
        return jobs.Select(j => new UdpOtaJobDetail(Item(j), log.Where(l => l.OtaJobId == j.Id).Select(UdpViews.Log).ToList())).ToList();
    }

    public async Task CancelJobAsync(Guid id, CancellationToken ct)
    {
        var job = await db.UdpOtaJobs.SingleOrDefaultAsync(j => j.Id == id, ct) ?? throw ApiException.NotFound("Update");
        if (!job.State.IsOpen())
        {
            throw ApiException.Conflict("not_open", $"The update is {job.State} already.");
        }
        var (userId, userName) = UdpViews.User(accessor);
        var now = Clock.UtcNow;
        cache.Invalidate();
        var was = job.State;
        job.State = UdpOtaState.Cancelled;
        job.FinishedAt = now;
        db.UdpCommandLog.Add(new UdpCommandLogEntity
        {
            Created = now,
            OtaJobId = job.Id,
            SensorId = job.SensorId,
            DeviceId = job.DeviceId,
            NodeName = job.NodeName,
            Kind = UdpLogKind.OtaCancelled,
            CmdId = job.UpdateId,
            UserId = userId,
            UserName = userName,
            Note = was == UdpOtaState.Downloading ? "during the download: the next GET is answered with abort" : $"while {was}",
        });
        UdpViews.Audit(db, accessor, AuthEvents.UdpUpdateCancelled, $"{job.FirmwareName} {job.FirmwareVersion} for {job.NodeName} ({job.DeviceId})");
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
    }
}
