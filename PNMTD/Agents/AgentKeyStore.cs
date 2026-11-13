using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Agents;

/// <summary>PNMTD's signing key as the agents know it.</summary>
public sealed record AgentSigningKey(ECDsa Key, string PublicKey, string Fingerprint);

/// <summary>
/// PNMTD's agent signing key (docs/windows-sensor-plan.md §3.4): ECDSA P-256, created on first use in windows_settings,
/// the private key under Data Protection (purpose PNMTD.Windows.AgentSigning.v1) with the key ring on the keys volume,
/// which backup.sh covers. It signs the join's answer, the configuration and every request; agents pin it at the join.
/// </summary>
public sealed class AgentKeyStore(IDbContextFactory<PnmtdDbContext> dbs, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("PNMTD.Windows.AgentSigning.v1");
    private readonly SemaphoreSlim gate = new(1, 1);
    private AgentSigningKey? cached;

    /// <summary>The advisory lock that makes the first creation happen once, across processes (the CLI).</summary>
    private const long CreationLock = 0x504E4D5457494E31; // "PNMTWIN1"

    public async Task<AgentSigningKey> GetAsync(CancellationToken ct)
    {
        if (cached is { } c)
        {
            return c;
        }
        await gate.WaitAsync(ct);
        try
        {
            return cached ??= await LoadOrCreateAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SignedEnvelope> SignAsync<T>(string type, Guid agentId, T body, CancellationToken ct, TimeSpan? lifetime = null, string? requestId = null)
    {
        var key = await GetAsync(ct);
        return AgentSigning.Sign(key.Key, type, agentId, body, lifetime, requestId);
    }

    /// <summary>The settings row, created with the key if there is none yet.</summary>
    public async Task<WindowsSettingsEntity> SettingsAsync(PnmtdDbContext db, CancellationToken ct)
    {
        _ = await GetAsync(ct);
        return await db.WindowsSettings.SingleAsync(s => s.Id == WindowsSettingsEntity.SingletonId, ct);
    }

    private async Task<AgentSigningKey> LoadOrCreateAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [CreationLock], ct);
        var settings = await db.WindowsSettings.SingleOrDefaultAsync(s => s.Id == WindowsSettingsEntity.SingletonId, ct);
        if (settings is null)
        {
            using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = AgentSigning.PublicKey(created);
            settings = new WindowsSettingsEntity
            {
                SigningKeyProtected = protector.Protect(Convert.ToBase64String(created.ExportPkcs8PrivateKey())),
                SigningPublicKey = publicKey,
                SigningKeyFingerprint = AgentSigning.Fingerprint(publicKey),
                CreatedAt = Clock.UtcNow,
            };
            db.WindowsSettings.Add(settings);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);

        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(protector.Unprotect(settings.SigningKeyProtected)), out _);
        }
        catch (CryptographicException e)
        {
            key.Dispose();
            // A restore without the keys volume: the agents' pinned key can't be used any more. They must re-join.
            throw new InvalidOperationException(
                "PNMTD's agent signing key can't be decrypted: the Data Protection key ring that protected it is gone. Every Windows server must re-join.", e);
        }
        return new AgentSigningKey(key, settings.SigningPublicKey, settings.SigningKeyFingerprint);
    }
}
