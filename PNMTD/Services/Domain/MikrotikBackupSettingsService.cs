using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

/// <summary>
/// The routers' backup settings for the whole installation (docs/mikrotik-backup-plan.md §2.2): the default
/// for new routers, and changing many routers at once.
/// </summary>
public sealed class MikrotikBackupSettingsService(PnmtdDbContext db)
{
    public const int MaxRouters = 500;

    public async Task<MikrotikBackupSettings> GetAsync(CancellationToken ct)
    {
        var settings = await db.MikrotikSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == MikrotikSettingsEntity.SingletonId, ct);
        return new MikrotikBackupSettings(settings is null ? 24 : settings.DefaultBackupIntervalHours);
    }

    public async Task<MikrotikBackupSettings> UpdateAsync(MikrotikBackupSettingsRequest request, CancellationToken ct)
    {
        var v = new ApiValidator();
        CheckInterval(v, request.DefaultBackupIntervalHours, "defaultBackupIntervalHours");
        v.ThrowIfInvalid();
        var settings = await MikrotikKeyStore.SettingsAsync(db, ct);
        settings.DefaultBackupIntervalHours = request.DefaultBackupIntervalHours;
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }

    public async Task<MikrotikRouterBackupResult> ApplyAsync(MikrotikRouterBackupRequest request, CancellationToken ct)
    {
        var v = new ApiValidator();
        var ids = request.SensorIds.Distinct().ToList();
        v.Check(ids.Count is >= 1 and <= MaxRouters, "sensorIds", $"1 to {MaxRouters} routers.");
        v.Check(request.SetSchedule, "setSchedule", "Nothing to change.");
        if (request.SetSchedule)
        {
            CheckInterval(v, request.BackupIntervalHours, "backupIntervalHours");
        }
        v.ThrowIfInvalid();
        var routers = await db.MikrotikRouters.Where(r => ids.Contains(r.SensorId)).ToListAsync(ct);
        var changed = 0;
        foreach (var router in routers.Where(r => r.BackupIntervalHours != request.BackupIntervalHours))
        {
            router.BackupIntervalHours = request.BackupIntervalHours;
            changed++;
        }
        await db.SaveChangesAsync(ct);
        return new MikrotikRouterBackupResult(changed);
    }

    private static void CheckInterval(ApiValidator v, int? hours, string field) =>
        v.Check(hours is null or (>= 1 and <= MikrotikRouterService.MaxBackupIntervalHours), field,
            $"Off, or between 1 and {MikrotikRouterService.MaxBackupIntervalHours} hours (a year).");
}
