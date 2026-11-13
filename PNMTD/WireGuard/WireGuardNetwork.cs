using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.WireGuard;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.WireGuard;

/// <summary>Where a setting's value comes from (§3.3): the environment wins over the admin page, which wins over the default.</summary>
public enum WgSettingSource
{
    Default = 0,
    Ui = 1,
    Environment = 2,
}

/// <summary>The network as it is in effect: the stored settings with the environment applied (§3.3).</summary>
public sealed record WgNetworkSettings(
    AddressPlan Plan,
    string? ExternalHost,
    WgSettingSource ExternalHostSource,
    int ExternalPort,
    WgSettingSource ExternalPortSource,
    int PublishedPort,
    int ListenPort,
    int KeepaliveSeconds,
    int Mtu,
    int StaleHandshakeSeconds,
    IReadOnlyList<int> ManagementPorts,
    string GatewayPublicKey,
    DateTime ScriptSettingsChangedAt,
    DateTime? GatewaySeenAt,
    string? GatewayVersion,
    string? GatewayError,
    DateTime? GatewayStartedAt = null)
{
    /// <summary>"vpn.example.com:51820", or null when no external host is known (a warning on the overview).</summary>
    public string? Endpoint => ExternalHost is null ? null : $"{(ExternalHost.Contains(':') ? $"[{ExternalHost}]" : ExternalHost)}:{ExternalPort}";

    public bool GatewayUp(DateTime now, int downSeconds) => GatewaySeenAt is { } seen && now - seen <= TimeSpan.FromSeconds(downSeconds);

    /// <summary>
    /// The gateway started less than a stale-handshake period ago: a tunnel without a fresh handshake is
    /// still reconnecting, not down (the lab: a router behind NAT took minutes after a gateway restart).
    /// </summary>
    public bool Settling(DateTime now) => GatewayStartedAt is { } started && now - started < TimeSpan.FromSeconds(StaleHandshakeSeconds);
}

/// <summary>
/// The tunnel network (docs/mikrotik-plan.md §3.2, §3.3): created on first use with the gateway's key pair
/// and the SOCKS secret, its settings with their sources, and the keys under Data Protection (purpose
/// PNMTD.WireGuard.Key.v1). After every change of peers or settings the app sends
/// <c>NOTIFY wireguard_changed</c>, and the gateway resyncs at once (§3.4).
/// </summary>
public sealed class WireGuardNetwork(IDataProtectionProvider protection, IOptionsMonitor<WireGuardOptions> options, IConfiguration configuration)
{
    public const string Channel = "wireguard_changed";

    private readonly IDataProtector protector = protection.CreateProtector("PNMTD.WireGuard.Key.v1");

    /// <summary>A constant for the advisory lock of the first creation (app, CLI and gateway may race).</summary>
    private const long CreationLock = 0x504E4D5457474E31; // "PNMTWGN1"

    public bool Enabled => options.CurrentValue.Enabled;

    public WireGuardOptions Options => options.CurrentValue;

    public async Task<WgNetworkEntity> EnsureAsync(PnmtdDbContext db, CancellationToken ct)
    {
        var network = await db.WgNetwork.SingleOrDefaultAsync(n => n.Id == WgNetworkEntity.SingletonId, ct);
        if (network is not null)
        {
            return network;
        }
        var ownTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownTransaction ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [CreationLock], ct);
        network = await db.WgNetwork.SingleOrDefaultAsync(n => n.Id == WgNetworkEntity.SingletonId, ct);
        if (network is null)
        {
            var (privateKey, publicKey) = WireGuardKeys.Generate();
            var now = Clock.UtcNow;
            network = new WgNetworkEntity
            {
                Prefix = AddressPlan.DefaultPrefix,
                PublicKey = publicKey,
                PrivateKeyProtected = Protect(privateKey),
                SocksSecretProtected = Protect(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))),
                ChangedAt = now,
                ScriptSettingsChangedAt = now,
            };
            db.WgNetwork.Add(network);
            await db.SaveChangesAsync(ct);
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
        return network;
    }

    public WgNetworkSettings Settings(WgNetworkEntity n)
    {
        var o = options.CurrentValue;
        var envHost = Text.Trimmed(o.ExternalHost);
        var uiHost = Text.Trimmed(n.ExternalHost);
        var (host, hostSource) = envHost is not null ? (envHost, WgSettingSource.Environment)
            : uiHost is not null ? (uiHost, WgSettingSource.Ui)
            : (PublicHost(), WgSettingSource.Default);
        var (port, portSource) = o.ExternalPort is > 0 and <= 65535 and var ep ? (ep, WgSettingSource.Environment)
            : n.ExternalPort is > 0 and <= 65535 and var up ? (up, WgSettingSource.Ui)
            : (o.PublishedPort, WgSettingSource.Default);
        return new WgNetworkSettings(AddressPlan.Parse(n.Prefix), host, hostSource, port, portSource, o.PublishedPort, o.ListenPort,
            n.KeepaliveSeconds, n.Mtu, n.StaleHandshakeSeconds, n.ManagementPorts, n.PublicKey, n.ScriptSettingsChangedAt,
            n.GatewaySeenAt, n.GatewayVersion, n.GatewayError, n.GatewayStartedAt);
    }

    public async Task<WgNetworkSettings> SettingsAsync(PnmtdDbContext db, CancellationToken ct) => Settings(await EnsureAsync(db, ct));

    /// <summary>PublicBaseUrl's host: the default external host.</summary>
    private string? PublicHost() =>
        Uri.TryCreate(Text.Trimmed(configuration["PublicBaseUrl"]), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : null;

    public string Protect(string secret) => protector.Protect(secret);

    /// <summary>Null when the key ring that encrypted it is gone.</summary>
    public string? Unprotect(string protectedSecret)
    {
        try
        {
            return protector.Unprotect(protectedSecret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Tells the gateway to resync now; it resyncs every 30 seconds anyway.</summary>
    public static Task NotifyAsync(PnmtdDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync($"NOTIFY {Channel}", ct);

    /// <summary>A new router or custom peer: the next free address, a new key pair.</summary>
    public async Task<WgPeerEntity> NewPeerAsync(PnmtdDbContext db, Models.Enums.WgPeerKind kind, string name, Guid? userId, CancellationToken ct)
    {
        var network = await EnsureAsync(db, ct);
        var plan = AddressPlan.Parse(network.Prefix);
        var used = (await db.WgPeers.Select(p => p.Address).ToListAsync(ct))
            .Concat(db.ChangeTracker.Entries<WgPeerEntity>().Select(e => e.Entity.Address))
            .Select(a => System.Net.IPAddress.TryParse(a, out var ip) ? ip : null).OfType<System.Net.IPAddress>();
        var address = plan.Allocate(used) ?? throw Api.ApiException.Conflict("network_full",
            $"The tunnel network {plan} is full: choose a larger prefix on the admin page.");
        var (privateKey, publicKey) = WireGuardKeys.Generate();
        var now = Clock.UtcNow;
        var peer = new WgPeerEntity
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Name = name,
            Address = address.ToString(),
            PublicKey = publicKey,
            PrivateKeyProtected = Protect(privateKey),
            KeysCreatedAt = now,
            KeysCreatedBy = userId,
            CreatedAt = now,
            CreatedBy = userId,
        };
        db.WgPeers.Add(peer);
        return peer;
    }

    /// <summary>New keys for a peer: every copy of its script or file stops working (§3.2).</summary>
    public void Regenerate(WgPeerEntity peer, Guid? userId)
    {
        var (privateKey, publicKey) = WireGuardKeys.Generate();
        peer.PublicKey = publicKey;
        peer.PrivateKeyProtected = Protect(privateKey);
        peer.KeysCreatedAt = Clock.UtcNow;
        peer.KeysCreatedBy = userId;
    }
}
