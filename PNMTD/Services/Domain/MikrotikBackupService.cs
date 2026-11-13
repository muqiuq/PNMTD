using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Services.Domain;

/// <summary>
/// A router's backups (docs/mikrotik-plan.md §6, §10): the list, an export's text, diffs between versions,
/// downloads, "Back up now", and deleting one. Exports with secrets and binary backups are an Admin's,
/// and every look at them is audit-logged (§6.6).
/// </summary>
public sealed partial class MikrotikBackupService(PnmtdDbContext db, RouterPollService polls, IHttpContextAccessor accessor)
{
    public async Task<Paged<MikrotikBackupItem>> ListAsync(Guid sensorId, MikrotikBackupQuery query, CancellationToken ct)
    {
        await EnsureRouterAsync(sensorId, ct);
        var backups = db.MikrotikBackups.AsNoTracking().Where(b => b.SensorId == sensorId);
        if (query.Kind is { } kind)
        {
            backups = backups.Where(b => b.Kind == kind);
        }
        var page = await Paging.PageAsync(backups.OrderByDescending(b => b.Number).Select(Projection), query, ct);
        return new Paged<MikrotikBackupItem>(page.Items.Select(Item).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<MikrotikBackupContent> ContentAsync(Guid sensorId, Guid backupId, CancellationToken ct)
    {
        var backup = await ReadableAsync(sensorId, backupId, ct);
        if (backup.Kind != MikrotikBackupKind.Export)
        {
            throw ApiException.Conflict("binary_backup", "A binary backup has no text: download it.");
        }
        AuditSecrets(backup, "viewed");
        await db.SaveChangesAsync(ct);
        return backup.EncryptedWith is null
            ? new MikrotikBackupContent(Item(backup), BackupStore.Gunzip(backup.Content))
            : new MikrotikBackupContent(Item(backup), null, Convert.ToBase64String(backup.Content));
    }

    /// <summary>The changes from <paramref name="against"/> (by default the version before) to this one.</summary>
    public async Task<MikrotikBackupDiff> DiffAsync(Guid sensorId, Guid backupId, Guid? against, CancellationToken ct)
    {
        var to = await ReadableAsync(sensorId, backupId, ct);
        MikrotikBackupEntity? from = against is { } a
            ? await ReadableAsync(sensorId, a, ct)
            : await db.MikrotikBackups.AsNoTracking()
                .Where(b => b.SensorId == sensorId && b.Kind == MikrotikBackupKind.Export && b.Number < to.Number)
                .OrderByDescending(b => b.Number).FirstOrDefaultAsync(ct);
        if (to.Kind != MikrotikBackupKind.Export || from is { Kind: not MikrotikBackupKind.Export })
        {
            throw ApiException.Conflict("binary_backup", "Only exports can be compared.");
        }
        if (from is { IncludesSecrets: true } && !IsAdmin())
        {
            throw SecretsForbidden();
        }
        if (to.EncryptedWith is not null || from?.EncryptedWith is not null)
        {
            throw ApiException.Conflict(ApiErrorCodes.BackupEncrypted, "An encrypted version: load the backup key to compare it in the browser.");
        }
        AuditSecrets(to, "compared");
        await db.SaveChangesAsync(ct);
        var diff = LineDiff.Compare(from is null ? "" : BackupStore.Gunzip(from.Content), BackupStore.Gunzip(to.Content));
        return new MikrotikBackupDiff(from is null ? null : Item(from), Item(to), diff.Added, diff.Removed,
            diff.Hunks.Select(h => new MikrotikDiffHunk(h.OldStart, h.OldCount, h.NewStart, h.NewCount,
                h.Lines.Select(l => new MikrotikDiffLine(l.Kind, l.Text, l.OldNumber, l.NewNumber)).ToList())).ToList(),
            diff.Unified(from is null ? "/dev/null" : FileName(from), FileName(to)));
    }

    /// <summary>The file: an export as <c>&lt;identity&gt;-&lt;date&gt;-v&lt;n&gt;.rsc</c>, to <c>/import</c> or to copy lines from.</summary>
    public async Task<(byte[] Content, string FileName, string ContentType)> DownloadAsync(Guid sensorId, Guid backupId, CancellationToken ct)
    {
        var backup = await ReadableAsync(sensorId, backupId, ct);
        AuditSecrets(backup, "downloaded");
        await db.SaveChangesAsync(ct);
        if (backup.EncryptedWith is not null)
        {
            // As stored: age, of the gzip of an export or of the binary file (docs/mikrotik-backup-plan.md §3.4).
            return (backup.Content, FileName(backup), "application/octet-stream");
        }
        return backup.Kind == MikrotikBackupKind.Export
            ? (Encoding.UTF8.GetBytes(BackupStore.Gunzip(backup.Content)), FileName(backup), "text/plain; charset=utf-8")
            : (backup.Content, FileName(backup), "application/octet-stream");
    }

    /// <summary>"Back up now" (§6.1): a poll with an export, in one session. Throws when either fails.</summary>
    public async Task<MikrotikBackupResult> BackupNowAsync(Guid sensorId, CancellationToken ct)
    {
        var router = await EnsureRouterAsync(sensorId, ct);
        if (!router.Mode.UsesSsh())
        {
            throw ApiException.Conflict("no_login", "A router in WireGuard only mode has no login to export with.");
        }
        var (userId, userName) = UdpViews.User(accessor);
        var outcome = await polls.PollAsync(sensorId, wait: true, ct, new PollRequest(ForceExport: true, MikrotikBackupTrigger.Manual, userId, userName));
        if (outcome.Reading is null)
        {
            throw ApiException.Conflict("router_unreachable", outcome.Message.Length > 0 ? outcome.Message : "The router did not answer.");
        }
        if (outcome.ExportError is { } error)
        {
            throw ApiException.Conflict("backup_failed", error);
        }
        if (outcome.Export is not { } stored)
        {
            throw ApiException.Conflict("backup_failed", "The router is below RouterOS 7.13: no backups.");
        }
        return stored.Backup is { } backup
            ? new MikrotikBackupResult(true, Item(backup), stored.First ? $"Version {backup.Number} stored." :
                stored.Counted ? $"Version {backup.Number} stored: +{stored.Added} −{stored.Removed} lines." : $"Version {backup.Number} stored.")
            : new MikrotikBackupResult(false, null, "The configuration is unchanged: nothing new to store.");
    }

    public async Task DeleteAsync(Guid sensorId, Guid backupId, CancellationToken ct)
    {
        var backup = await db.MikrotikBackups.SingleOrDefaultAsync(b => b.Id == backupId && b.SensorId == sensorId, ct)
            ?? throw ApiException.NotFound("Backup");
        UdpViews.Audit(db, accessor, AuthEvents.MikrotikBackupDeleted, $"router {backup.RouterName}: version {backup.Number} of {Instants.Utc(backup.CreatedAt):yyyy-MM-dd HH:mm} UTC deleted");
        db.MikrotikBackups.Remove(backup);
        // The next export is compared with what is left, not with the deleted version: the newest export's digests go.
        var router = await db.MikrotikRouters.SingleOrDefaultAsync(r => r.SensorId == sensorId, ct);
        var newest = backup.Kind == MikrotikBackupKind.Export && !await db.MikrotikBackups.AnyAsync(
            b => b.SensorId == sensorId && b.Kind == MikrotikBackupKind.Export && b.Number > backup.Number, ct);
        if (router is not null && newest)
        {
            router.LastExportHash = null;
            router.LastExportLineDigests = null;
        }
        await db.SaveChangesAsync(ct);
    }

    // --- helpers ------------------------------------------------------------------------------------------

    private async Task<MikrotikRouterEntity> EnsureRouterAsync(Guid sensorId, CancellationToken ct) =>
        await db.MikrotikRouters.AsNoTracking().SingleOrDefaultAsync(r => r.SensorId == sensorId, ct) ?? throw ApiException.NotFound("MikroTik router");

    /// <summary>The backup, if the caller may read its content: secrets and binary backups need Admin (§6.6).</summary>
    private async Task<MikrotikBackupEntity> ReadableAsync(Guid sensorId, Guid backupId, CancellationToken ct)
    {
        var backup = await db.MikrotikBackups.AsNoTracking().SingleOrDefaultAsync(b => b.Id == backupId && b.SensorId == sensorId, ct)
            ?? throw ApiException.NotFound("Backup");
        if ((backup.IncludesSecrets || backup.Kind == MikrotikBackupKind.Binary) && !IsAdmin())
        {
            throw SecretsForbidden();
        }
        return backup;
    }

    private static ApiException SecretsForbidden() =>
        new(StatusCodes.Status403Forbidden, ApiErrorCodes.ForbiddenRole, "Exports with secrets and binary backups are an Admin's.");

    private void AuditSecrets(MikrotikBackupEntity backup, string what)
    {
        if (backup.IncludesSecrets || backup.Kind == MikrotikBackupKind.Binary)
        {
            UdpViews.Audit(db, accessor, AuthEvents.MikrotikBackupDownloaded,
                $"router {backup.RouterName}: version {backup.Number} ({(backup.Kind == MikrotikBackupKind.Binary ? "binary" : "with secrets")}) {what}");
        }
    }

    private bool IsAdmin() => accessor.HttpContext is not { } http || http.User.IsInRole(Roles.Admin);

    /// <summary>The columns of the list, without the content.</summary>
    private static readonly System.Linq.Expressions.Expression<Func<MikrotikBackupEntity, MikrotikBackupEntity>> Projection = b => new MikrotikBackupEntity
    {
        Id = b.Id,
        SensorId = b.SensorId,
        RouterName = b.RouterName,
        Number = b.Number,
        Kind = b.Kind,
        Trigger = b.Trigger,
        JobId = b.JobId,
        CreatedAt = b.CreatedAt,
        CreatedBy = b.CreatedBy,
        CreatedByName = b.CreatedByName,
        RouterOsVersion = b.RouterOsVersion,
        Size = b.Size,
        LineCount = b.LineCount,
        ContentHash = b.ContentHash,
        IncludesSecrets = b.IncludesSecrets,
        LinesAdded = b.LinesAdded,
        LinesRemoved = b.LinesRemoved,
        Encrypted = b.Encrypted,
        EncryptedWith = b.EncryptedWith,
    };

    public static MikrotikBackupItem Item(MikrotikBackupEntity b) => new(b.Id, b.Number, b.Kind, b.Trigger, Instants.Utc(b.CreatedAt),
        b.CreatedByName, b.RouterOsVersion, b.Size, b.LineCount, b.LinesAdded, b.LinesRemoved, b.IncludesSecrets, b.Encrypted, b.EncryptedWith);

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex Unsafe();

    internal static string FileName(MikrotikBackupEntity b)
    {
        var name = Unsafe().Replace(b.RouterName, "-").Trim('-');
        var when = Clock.ToLocal(Instants.Utc(b.CreatedAt)).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var extension = b.Kind == MikrotikBackupKind.Binary ? "backup" : "rsc";
        // Encrypted: the age file, of the gzip for an export (age -d … | gunzip).
        var encrypted = b.EncryptedWith is null ? "" : b.Kind == MikrotikBackupKind.Binary ? ".age" : ".gz.age";
        return $"{(name.Length == 0 ? "router" : name)}-{when}-v{b.Number}.{extension}{encrypted}";
    }
}
