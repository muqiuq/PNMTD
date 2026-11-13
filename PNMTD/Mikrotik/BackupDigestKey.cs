using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;

namespace PNMTD.Mikrotik;

/// <summary>
/// The installation's key for the exports' keyed digests (docs/mikrotik-backup-plan.md §3.7): 32 random bytes, made on
/// first use, kept under Data Protection in <c>mikrotik_settings</c>. If the key ring is lost, a new key is made, and
/// each router's next export counts as a change once.
/// </summary>
public sealed class BackupDigestKey(IDbContextFactory<PnmtdDbContext> dbs, IDataProtectionProvider protection, ILogger<BackupDigestKey> logger)
{
    private readonly IDataProtector protector = protection.CreateProtector("PNMTD.Mikrotik.BackupDigest.v1");
    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[]? key;

    public async Task<byte[]> GetAsync(CancellationToken ct)
    {
        if (key is { } known)
        {
            return known;
        }
        await gate.WaitAsync(ct);
        try
        {
            if (key is { } loaded)
            {
                return loaded;
            }
            await using var db = await dbs.CreateDbContextAsync(ct);
            var settings = await MikrotikKeyStore.SettingsAsync(db, ct);
            if (settings.BackupDigestKeyProtected is { } stored)
            {
                try
                {
                    return key = Convert.FromBase64String(protector.Unprotect(stored));
                }
                catch (CryptographicException ex)
                {
                    logger.LogWarning(ex, "The backup digest key cannot be read (the key ring changed): a new one is made, and each router's next export counts as a change once");
                }
            }
            var fresh = RandomNumberGenerator.GetBytes(32);
            settings.BackupDigestKeyProtected = protector.Protect(Convert.ToBase64String(fresh));
            await db.SaveChangesAsync(ct);
            return key = fresh;
        }
        finally
        {
            gate.Release();
        }
    }
}
