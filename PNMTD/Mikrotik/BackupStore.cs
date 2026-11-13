using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Mikrotik;

/// <summary>
/// What storing an export did: a new version, or none because nothing changed. <see cref="Counted"/> is false when the
/// lines could not be counted (the version before is encrypted, and its digests are gone).
/// </summary>
public sealed record StoredExport(MikrotikBackupEntity? Backup, bool Changed, int Added, int Removed, bool First, bool Counted = true)
{
    /// <summary>243's text: "Configuration changed{cause}: +3 −1 lines (version 12)".</summary>
    public string Message(string cause = "") => Counted
        ? $"Configuration changed{cause}: +{Added} −{Removed} lines (version {Backup?.Number})"
        : $"Configuration changed{cause} (version {Backup?.Number})";
}

/// <summary>
/// A router's backups are to be encrypted, but the installation has no backup key (docs/mikrotik-backup-plan.md §3.3):
/// nothing is stored rather than a plaintext the admin did not want.
/// </summary>
public sealed class BackupKeyMissingException()
    : Exception("Encrypt backups is on, but the installation has no backup key: set one up, or turn it off for the router.");

/// <summary>
/// Storing exports (docs/mikrotik-plan.md §6.2, §6.3): normalised, compared with the router's newest export, and kept
/// only when different, as gzip, with the line counts against the version before. Compared by keyed digests
/// (docs/mikrotik-backup-plan.md §3.7), not by the text, which PNMT cannot read back from an encrypted backup.
/// </summary>
public static class BackupStore
{
    public static async Task<StoredExport> SaveExportAsync(PnmtdDbContext db, MikrotikRouterEntity router, string routerName, string rawExport,
        bool includesSecrets, MikrotikBackupTrigger trigger, Guid? userId, string? userName, DateTime now, byte[] digestKey, CancellationToken ct)
    {
        var normalised = ExportText.Normalise(rawExport);
        var digests = ExportDigest.Of(digestKey, normalised);
        router.LastExportAt = now;
        var previous = await db.MikrotikBackups
            .Where(b => b.SensorId == router.SensorId && b.Kind == MikrotikBackupKind.Export)
            .OrderByDescending(b => b.Number)
            .FirstOrDefaultAsync(ct);
        // What it is compared with: the newest export's digests, kept on the router. Without them (the first export
        // after the upgrade, or after the newest version was deleted) they come from the newest stored export itself.
        var before = router is { LastExportLineDigests: { } kept, LastExportHash: { } keptHash }
            ? new ExportDigests(keptHash, kept)
            : previous is not null && Readable(previous) ? ExportDigest.Of(digestKey, Gunzip(previous.Content)) : null;
        if (previous is not null && before?.Hash == digests.Hash)
        {
            // Unchanged; a version from before the keyed digests gets its keyed hash.
            previous.ContentHash = digests.Hash;
            router.LastExportHash = digests.Hash;
            router.LastExportLineDigests = digests.Lines;
            return new StoredExport(null, false, 0, 0, false);
        }
        var key = await KeyForAsync(db, router, ct);
        var counts = previous is not null && before is not null ? ExportDigest.Count(before.Lines, digests.Lines) : ((int, int)?)null;
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
            Content = key is null ? Gzip(normalised) : AgeEncryption.Encrypt(Gzip(normalised), AgeEncryption.ParseRecipient(key.Recipient)!),
            EncryptedWith = key?.Fingerprint,
            Size = Encoding.UTF8.GetByteCount(normalised),
            LineCount = ExportText.LineCount(normalised),
            ContentHash = digests.Hash,
            IncludesSecrets = includesSecrets,
            LinesAdded = counts?.Item1,
            LinesRemoved = counts?.Item2,
        };
        db.MikrotikBackups.Add(backup);
        router.LastExportHash = digests.Hash;
        router.LastExportLineDigests = digests.Lines;
        return new StoredExport(backup, true, counts?.Item1 ?? 0, counts?.Item2 ?? 0, previous is null, previous is null || counts is not null);
    }

    /// <summary>Whether PNMT can read a stored export's text: not an encrypted one.</summary>
    public static bool Readable(MikrotikBackupEntity backup) => backup.Kind == MikrotikBackupKind.Export && backup.EncryptedWith is null;

    /// <summary>
    /// The key a router's backups are encrypted with (docs/mikrotik-backup-plan.md §3.3): none while its encryption is
    /// off. Throws <see cref="BackupKeyMissingException"/> when it is on and there is no active key.
    /// </summary>
    public static async Task<MikrotikBackupKeyEntity?> KeyForAsync(PnmtdDbContext db, MikrotikRouterEntity router, CancellationToken ct)
    {
        if (!router.EncryptBackups)
        {
            return null;
        }
        var active = await db.MikrotikSettings.AsNoTracking().Where(s => s.Id == MikrotikSettingsEntity.SingletonId)
            .Select(s => s.BackupKeyId).SingleOrDefaultAsync(ct);
        var key = active is { } id ? await db.MikrotikBackupKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id && k.RetiredAt == null, ct) : null;
        return key ?? throw new BackupKeyMissingException();
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
