using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Mikrotik;

/// <summary>What storing an export did: a new version, or none because nothing changed.</summary>
public sealed record StoredExport(MikrotikBackupEntity? Backup, bool Changed, int Added, int Removed, bool First);

/// <summary>
/// Storing exports (docs/mikrotik-plan.md §6.2, §6.3): normalised, hashed, compared with the router's last
/// stored export, and kept only when different, as gzip, with the line counts against the version before.
/// </summary>
public static class BackupStore
{
    public static async Task<StoredExport> SaveExportAsync(PnmtdDbContext db, MikrotikRouterEntity router, string routerName, string rawExport,
        bool includesSecrets, MikrotikBackupTrigger trigger, Guid? userId, string? userName, DateTime now, CancellationToken ct)
    {
        var normalised = ExportText.Normalise(rawExport);
        var hash = ExportText.Hash(normalised);
        router.LastExportAt = now;
        if (router.LastExportHash == hash
            && await db.MikrotikBackups.AnyAsync(b => b.SensorId == router.SensorId && b.Sha256 == hash, ct))
        {
            return new StoredExport(null, false, 0, 0, false);
        }
        var previous = await db.MikrotikBackups
            .Where(b => b.SensorId == router.SensorId && b.Kind == MikrotikBackupKind.Export)
            .OrderByDescending(b => b.Number)
            .FirstOrDefaultAsync(ct);
        var diff = previous is null ? null : LineDiff.Compare(Gunzip(previous.Content), normalised);
        var number = await db.MikrotikBackups.Where(b => b.SensorId == router.SensorId).Select(b => (int?)b.Number).MaxAsync(ct) ?? 0;
        var backup = new MikrotikBackupEntity
        {
            Id = Guid.NewGuid(),
            SensorId = router.SensorId,
            RouterName = routerName,
            Number = number + 1,
            Kind = MikrotikBackupKind.Export,
            Trigger = trigger,
            CreatedAt = now,
            CreatedBy = userId,
            CreatedByName = userName,
            RouterOsVersion = ExportText.Version(rawExport) ?? router.Version,
            Content = Gzip(normalised),
            Size = Encoding.UTF8.GetByteCount(normalised),
            LineCount = ExportText.LineCount(normalised),
            Sha256 = hash,
            IncludesSecrets = includesSecrets,
            LinesAdded = diff?.Added,
            LinesRemoved = diff?.Removed,
        };
        db.MikrotikBackups.Add(backup);
        router.LastExportHash = hash;
        return new StoredExport(backup, true, diff?.Added ?? 0, diff?.Removed ?? 0, previous is null);
    }

    public static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }
        return buffer.ToArray();
    }

    public static string Gunzip(byte[] content)
    {
        using var gzip = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
