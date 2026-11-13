using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Mikrotik;

/// <summary>The installation as routers see it: its tag, and PNMTD's public SSH keys.</summary>
public sealed record MikrotikInstallation(string Tag, string Instance, IReadOnlyList<MikrotikSshKeyEntity> Keys)
{
    /// <summary>The comment on everything a join script creates (§4.3).</summary>
    public string Comment => $"pnmt:{Tag}";

    /// <summary>The key join scripts install: the newest. The older ones are only offered until they are retired.</summary>
    public MikrotikSshKeyEntity Current => Keys[0];
}

/// <summary>
/// PNMTD's side of the router logins (docs/mikrotik-plan.md §4.2): the installation's tag and its SSH
/// key pairs, created on first use. The private keys are under Data Protection (purpose
/// PNMTD.Mikrotik.SshKey.v1), with the key ring on the keys volume, as the TOTP secrets are.
/// </summary>
public sealed partial class MikrotikKeyStore(IDbContextFactory<PnmtdDbContext> dbs, IDataProtectionProvider protection, IConfiguration configuration)
{
    private readonly IDataProtector protector = protection.CreateProtector("PNMTD.Mikrotik.SshKey.v1");
    private readonly SemaphoreSlim gate = new(1, 1);
    private MikrotikInstallation? cached;
    private IReadOnlyList<string>? cachedPrivateKeys;

    /// <summary>A constant for the advisory lock that makes the first creation happen once, across processes (the CLI).</summary>
    private const long CreationLock = 0x504E4D544D4B5431; // "PNMTMKT1"

    /// <summary>PublicBaseUrl's host, else the identity: in key comments and scripts, so a router's admin recognizes PNMT.</summary>
    public string Instance
    {
        get
        {
            var configured = Text.Trimmed(configuration["PublicBaseUrl"]);
            if (configured is not null && Uri.TryCreate(configured, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            {
                return uri.Host;
            }
            // The Identity setting directly: the CLI never sets ServerInfo.Identity.
            var identity = Text.Trimmed(configuration["Identity"]) ?? ServerInfo.Identity;
            return string.IsNullOrWhiteSpace(identity) || identity == "no identity set" ? "pnmt" : identity;
        }
    }

    public async Task<MikrotikInstallation> GetAsync(CancellationToken ct)
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

    /// <summary>The private keys in use, newest first, decrypted. Empty when the key ring that encrypted them is gone.</summary>
    public async Task<IReadOnlyList<string>> PrivateKeysAsync(CancellationToken ct)
    {
        if (cachedPrivateKeys is { } keys)
        {
            return keys;
        }
        var installation = await GetAsync(ct);
        var list = new List<string>();
        foreach (var key in installation.Keys)
        {
            try
            {
                list.Add(protector.Unprotect(key.PrivateKeyProtected));
            }
            catch (CryptographicException)
            {
                // A key ring that is gone (a restore without the keys volume): the key is useless.
            }
        }
        return cachedPrivateKeys = list;
    }

    /// <summary>
    /// A new key pair (§4.2): join scripts carry only it from now on. PNMTD still offers the older keys, because
    /// pnmt cannot add a key for itself: each router gets the new one with its script, then the old are retired.
    /// </summary>
    public async Task<MikrotikSshKeyEntity> RotateAsync(Guid? createdBy, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            var key = NewKey(db, createdBy);
            await db.SaveChangesAsync(ct);
            Invalidate();
            return key;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Stops offering every key but the newest. Returns how many were retired.</summary>
    public async Task<int> RetireOlderAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            var active = await db.MikrotikSshKeys.Where(k => k.RetiredAt == null).OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
            var now = Clock.UtcNow;
            foreach (var key in active.Skip(1))
            {
                key.RetiredAt = now;
            }
            await db.SaveChangesAsync(ct);
            Invalidate();
            return Math.Max(0, active.Count - 1);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate()
    {
        cached = null;
        cachedPrivateKeys = null;
    }

    private async Task<MikrotikInstallation> LoadOrCreateAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [CreationLock], ct);
        var settings = await db.MikrotikSettings.SingleOrDefaultAsync(s => s.Id == MikrotikSettingsEntity.SingletonId, ct);
        if (settings is null)
        {
            settings = new MikrotikSettingsEntity { Tag = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2)) };
            db.MikrotikSettings.Add(settings);
        }
        var keys = await db.MikrotikSshKeys.Where(k => k.RetiredAt == null).OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        if (keys.Count == 0)
        {
            keys.Add(NewKey(db, createdBy: null));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new MikrotikInstallation(settings.Tag, Instance, keys);
    }

    private MikrotikSshKeyEntity NewKey(PnmtdDbContext db, Guid? createdBy)
    {
        var id = Guid.NewGuid();
        var comment = $"pnmt-{id.ToString("N")[..6]}@{CommentHost().Replace(' ', '-')}";
        var pair = SshKeys.GenerateEd25519(comment);
        var entity = new MikrotikSshKeyEntity
        {
            Id = id,
            PublicKey = pair.PublicLine,
            Comment = comment,
            PrivateKeyProtected = protector.Protect(pair.PrivatePem),
            CreatedAt = Clock.UtcNow,
            CreatedBy = createdBy,
        };
        db.MikrotikSshKeys.Add(entity);
        return entity;
    }

    [GeneratedRegex("[^A-Za-z0-9.-]")]
    private static partial Regex NotHostCharacters();

    /// <summary>The comment must be one word for RouterOS: the instance with anything unusual replaced.</summary>
    private string CommentHost() => NotHostCharacters().Replace(Instance, "-");
}
