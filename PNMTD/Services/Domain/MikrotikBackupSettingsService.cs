using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

/// <summary>
/// The routers' backups for the whole installation (docs/mikrotik-backup-plan.md §2.2, §3.3, §3.5): the defaults for
/// new routers, changing many routers at once, and the backup key, set once and replaced or retired by an Admin.
/// </summary>
public sealed class MikrotikBackupSettingsService(PnmtdDbContext db, IHttpContextAccessor accessor, IOptionsMonitor<MikrotikOptions> options)
{
    public const int MaxRouters = 500;

    public async Task<MikrotikBackupSettings> GetAsync(CancellationToken ct)
    {
        var settings = await db.MikrotikSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == MikrotikSettingsEntity.SingletonId, ct);
        var keys = await db.MikrotikBackupKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        var counts = await db.MikrotikBackups.Where(b => b.EncryptedWith != null).GroupBy(b => b.EncryptedWith!)
            .Select(g => new { Fingerprint = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Fingerprint, g => g.Count, ct);
        var items = keys.Select(k => new MikrotikBackupKeyItem(k.Fingerprint, k.Recipient, k.Kind, Instants.Utc(k.CreatedAt), k.CreatedByName,
            UdpViews.NullableUtc(k.RetiredAt), counts.GetValueOrDefault(k.Fingerprint))).ToList();
        var active = settings?.BackupKeyId is { } id ? keys.FindIndex(k => k.Id == id) : -1;
        return new MikrotikBackupSettings(settings is null ? 24 : settings.DefaultBackupIntervalHours,
            active >= 0 && settings!.DefaultEncryptBackups, active >= 0 ? items[active] : null,
            await db.MikrotikRouters.CountAsync(r => r.EncryptBackups, ct), items);
    }

    public async Task<MikrotikBackupSettings> UpdateAsync(MikrotikBackupSettingsRequest request, CancellationToken ct)
    {
        var v = new ApiValidator();
        CheckInterval(v, request.DefaultBackupIntervalHours, "defaultBackupIntervalHours");
        v.ThrowIfInvalid();
        var settings = await MikrotikKeyStore.SettingsAsync(db, ct);
        if (request.DefaultEncryptBackups && settings.BackupKeyId is null)
        {
            throw ApiException.Invalid("defaultEncryptBackups", "Set up the backup key first.");
        }
        settings.DefaultBackupIntervalHours = request.DefaultBackupIntervalHours;
        settings.DefaultEncryptBackups = request.DefaultEncryptBackups;
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }

    public async Task<MikrotikRouterBackupResult> ApplyAsync(MikrotikRouterBackupRequest request, CancellationToken ct)
    {
        var v = new ApiValidator();
        var ids = request.SensorIds.Distinct().ToList();
        v.Check(ids.Count is >= 1 and <= MaxRouters, "sensorIds", $"1 to {MaxRouters} routers.");
        v.Check(request.SetSchedule || request.EncryptBackups is not null, "setSchedule", "Nothing to change.");
        if (request.SetSchedule)
        {
            CheckInterval(v, request.BackupIntervalHours, "backupIntervalHours");
        }
        v.ThrowIfInvalid();
        if (request.EncryptBackups == true && !await HasKeyAsync(ct))
        {
            throw ApiException.Invalid("encryptBackups", "Set up the backup key first.");
        }
        var routers = await db.MikrotikRouters.Include(r => r.Sensor).Where(r => ids.Contains(r.SensorId)).ToListAsync(ct);
        var changed = new HashSet<Guid>();
        var encryption = new List<string>();
        foreach (var router in routers)
        {
            if (request.SetSchedule && router.BackupIntervalHours != request.BackupIntervalHours)
            {
                router.BackupIntervalHours = request.BackupIntervalHours;
                changed.Add(router.SensorId);
            }
            if (request.EncryptBackups is { } encrypt && router.EncryptBackups != encrypt)
            {
                router.EncryptBackups = encrypt;
                changed.Add(router.SensorId);
                encryption.Add(router.Sensor.Name);
            }
        }
        if (encryption.Count > 0)
        {
            UdpViews.Audit(db, accessor, AuthEvents.MikrotikBackupEncryptionChanged,
                $"backups encrypted {(request.EncryptBackups == true ? "on" : "off")} for {encryption.Count} router(s): {string.Join(", ", encryption.Take(20))}");
        }
        await db.SaveChangesAsync(ct);
        return new MikrotikRouterBackupResult(changed.Count);
    }

    /// <summary>
    /// The public key new backups are encrypted with (§3.5); the key before it is retired, and the routers whose
    /// backups are encrypted go on with the new one. HTTPS only: on plain HTTP the key could be swapped on its way.
    /// </summary>
    public async Task<MikrotikBackupSettings> SetKeyAsync(MikrotikBackupKeyRequest request, CancellationToken ct)
    {
        if (accessor.HttpContext is { } http && !http.Request.IsHttps && !options.CurrentValue.AllowPasswordsOverHttp)
        {
            throw ApiException.Invalid("recipient", "The backup key only over HTTPS: on plain HTTP it could be swapped on its way.");
        }
        var recipient = request.Recipient.Trim();
        var publicKey = AgeEncryption.ParseRecipient(recipient)
            ?? throw ApiException.Invalid("recipient", "An age public key: age1… (age-keygen prints it, or generate one in this browser).");
        var fingerprint = AgeEncryption.Fingerprint(publicKey);
        var settings = await MikrotikKeyStore.SettingsAsync(db, ct);
        var now = Clock.UtcNow;
        var (userId, userName) = UdpViews.User(accessor);
        var key = await db.MikrotikBackupKeys.SingleOrDefaultAsync(k => k.Fingerprint == fingerprint, ct);
        if (key is null)
        {
            key = new MikrotikBackupKeyEntity
            {
                Id = Guid.NewGuid(), Recipient = recipient, Fingerprint = fingerprint, CreatedAt = now, CreatedBy = userId, CreatedByName = userName,
            };
            db.MikrotikBackupKeys.Add(key);
        }
        key.RetiredAt = null;
        string? replaced = null;
        if (settings.BackupKeyId is { } old && old != key.Id && await db.MikrotikBackupKeys.SingleOrDefaultAsync(k => k.Id == old, ct) is { } before)
        {
            before.RetiredAt = now;
            replaced = before.Fingerprint;
        }
        settings.BackupKeyId = key.Id;
        UdpViews.Audit(db, accessor, AuthEvents.MikrotikBackupKeySet, replaced is null ? $"backup key {fingerprint} set" : $"backup key {fingerprint} replaces {replaced}");
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }

    /// <summary>Encryption off (§3.5): the key is retired, every router's backups are plaintext again from now on.</summary>
    public async Task<MikrotikBackupSettings> RetireKeyAsync(CancellationToken ct)
    {
        var settings = await MikrotikKeyStore.SettingsAsync(db, ct);
        if (settings.BackupKeyId is not { } id || await db.MikrotikBackupKeys.SingleOrDefaultAsync(k => k.Id == id, ct) is not { } key)
        {
            throw ApiException.Conflict("no_backup_key", "There is no backup key: encryption is off.");
        }
        key.RetiredAt = Clock.UtcNow;
        settings.BackupKeyId = null;
        settings.DefaultEncryptBackups = false;
        var routers = await db.MikrotikRouters.Where(r => r.EncryptBackups).ToListAsync(ct);
        foreach (var router in routers)
        {
            router.EncryptBackups = false;
        }
        UdpViews.Audit(db, accessor, AuthEvents.MikrotikBackupKeyRetired, $"backup key {key.Fingerprint} retired: encryption off for {routers.Count} router(s)");
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }

    private async Task<bool> HasKeyAsync(CancellationToken ct) =>
        await db.MikrotikSettings.AnyAsync(s => s.Id == MikrotikSettingsEntity.SingletonId && s.BackupKeyId != null, ct);

    private static void CheckInterval(ApiValidator v, int? hours, string field) =>
        v.Check(hours is null or (>= 1 and <= MikrotikRouterService.MaxBackupIntervalHours), field,
            $"Off, or between 1 and {MikrotikRouterService.MaxBackupIntervalHours} hours (a year).");
}
