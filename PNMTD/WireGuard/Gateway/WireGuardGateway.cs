using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.WireGuard;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.WireGuard.Gateway;

/// <summary>
/// The WireGuard gateway, <c>pnmtd wireguard gateway</c> (docs/mikrotik-plan.md §3.4): root in a container of
/// its own with only NET_ADMIN. It owns wg0 and its firewall, takes its peers from PostgreSQL, and passes
/// PNMTD's SSH connections into the tunnel over SOCKS5.
/// <list type="number">
/// <item>The nftables ruleset with its drop policies is loaded before wg0 exists: forwarding is never open.</item>
/// <item>wg0 is created, with the server address (.1) and the access address (.2).</item>
/// <item>Then, on <c>NOTIFY wireguard_changed</c> and every 30 seconds: the peers (<c>wg syncconf</c>, which
/// keeps live sessions), the ruleset (one atomic <c>nft -f</c>), and the SOCKS allowlist.</item>
/// <item>Every 15 seconds: <c>wg show dump</c> to <c>wg_peer_status</c>, and the heartbeat.</item>
/// </list>
/// </summary>
public sealed class WireGuardGateway(IDbContextFactory<PnmtdDbContext> dbs, WireGuardNetwork network, string connectionString, ILogger logger)
{
    public const string SocksUser = "pnmtd";

    private static readonly TimeSpan SyncEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, List<DateTime>> endpointChanges = new();
    private readonly Dictionary<string, string> lastEndpoints = new();
    private readonly SemaphoreSlim changed = new(0);
    private string socksSecret = "";
    private AddressPlan? plan;
    private string? lastError;
    private string? appliedAddresses;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var o = network.Options;
        logger.LogInformation("WireGuard gateway {Version}: {Interface}, port {Port}, SOCKS {Socks}", ServerInfo.Version, o.Interface, o.ListenPort, o.SocksPort);
        // 1. Deny before anything exists.
        await RunAsync("nft", ["-f", "-"], NftRuleset.Build(o.Interface, "10.253.0.2", [], []), ct);
        // 2. The interface.
        var link = await RunAsync("ip", ["link", "show", o.Interface], null, ct, throwOnError: false);
        if (link.Exit != 0)
        {
            await RunAsync("ip", ["link", "add", o.Interface, "type", "wireguard"], null, ct);
        }
        await using var socks = new Socks5Server(o.SocksPort, SocksUser, () => socksSecret,
            address => plan?.Contains(address) == true, logger);
        socks.SetAllowed(await SyncAsync(ct));
        socks.Start();
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            // The tunnels reconnect now: until a stale-handshake period has passed, the poller writes no 440 for them.
            await db.WgNetwork.Where(n => n.Id == WgNetworkEntity.SingletonId)
                .ExecuteUpdateAsync(u => u.SetProperty(n => n.GatewayStartedAt, Clock.UtcNow), ct);
        }
        await StatusAsync(ct);
        _ = ListenAsync(ct);

        var nextSync = DateTime.UtcNow + SyncEvery;
        while (!ct.IsCancellationRequested)
        {
            bool signalled;
            try
            {
                // A NOTIFY ends the wait early; otherwise the status is due every 15 seconds.
                signalled = await changed.WaitAsync(StatusEvery, ct);
                while (changed.CurrentCount > 0)
                {
                    await changed.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            try
            {
                if (signalled || DateTime.UtcNow >= nextSync)
                {
                    socks.SetAllowed(await SyncAsync(ct));
                    nextSync = DateTime.UtcNow + SyncEvery;
                }
                lastError = null;
                await StatusAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                logger.LogError(ex, "WireGuard gateway");
                await HeartbeatAsync(CancellationToken.None);
            }
        }
        return 0;
    }

    /// <summary>Peers, addresses, firewall: what the database says now. Returns the SOCKS allowlist.</summary>
    private async Task<Dictionary<string, int>> SyncAsync(CancellationToken ct)
    {
        var o = network.Options;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var n = await network.EnsureAsync(db, ct);
        var settings = network.Settings(n);
        plan = settings.Plan;
        socksSecret = network.Unprotect(n.SocksSecretProtected) ?? throw new InvalidOperationException("The SOCKS secret cannot be decrypted: is the keys volume mounted?");
        var privateKey = network.Unprotect(n.PrivateKeyProtected) ?? throw new InvalidOperationException("The gateway's key cannot be decrypted: is the keys volume mounted?");
        var now = Clock.UtcNow;

        // Routers in a WireGuard mode with their sensor enabled; custom tunnels enabled and not expired.
        var routerPeers = await (from p in db.WgPeers
                                 join r in db.MikrotikRouters on p.Id equals r.WgPeerId
                                 join s in db.Sensors on r.SensorId equals s.Id
                                 where p.Kind == WgPeerKind.Router && p.Enabled && s.Enabled
                                       && (r.Mode == MikrotikMode.WireGuardSsh || r.Mode == MikrotikMode.WireGuardOnly)
                                 select new { p.PublicKey, p.Address, r.Mode, r.SshPort }).ToListAsync(ct);
        var customPeers = await db.WgPeers
            .Where(p => p.Kind == WgPeerKind.Custom && p.Enabled && (p.ExpiresAt == null || p.ExpiresAt > now))
            .Select(p => new { p.PublicKey, p.Address }).ToListAsync(ct);
        var peers = routerPeers.Select(p => new GatewayPeer(p.PublicKey, p.Address))
            .Concat(customPeers.Select(p => new GatewayPeer(p.PublicKey, p.Address)))
            .Where(p => IPAddress.TryParse(p.Address, out var ip) && settings.Plan.IsPeerAddress(ip))
            .ToList();

        // The addresses: .1 with the prefix (the route to every peer), .2 alone. Changed only with the prefix.
        var addresses = $"{settings.Plan.ServerAddress}/{settings.Plan.Length} {settings.Plan.AccessAddress}/32";
        if (addresses != appliedAddresses)
        {
            await RunAsync("ip", ["-4", "address", "flush", "dev", o.Interface], null, ct);
            await RunAsync("ip", ["address", "add", $"{settings.Plan.ServerAddress}/{settings.Plan.Length}", "dev", o.Interface], null, ct);
            await RunAsync("ip", ["address", "add", $"{settings.Plan.AccessAddress}/32", "dev", o.Interface], null, ct);
            appliedAddresses = addresses;
        }
        var conf = Path.Combine(Path.GetTempPath(), $"pnmt-{o.Interface}.conf");
        await File.WriteAllTextAsync(conf, "", ct);
        File.SetUnixFileMode(conf, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await File.WriteAllTextAsync(conf, GatewayConfig.SyncConf(privateKey, o.ListenPort, peers), ct);
        try
        {
            await RunAsync("wg", ["syncconf", o.Interface, conf], null, ct);
        }
        finally
        {
            File.Delete(conf);
        }
        await RunAsync("ip", ["link", "set", o.Interface, "mtu", settings.Mtu.ToString(System.Globalization.CultureInfo.InvariantCulture), "up"], null, ct);
        // Custom tunnels' access arrives with phase M4; until then forwarding stays closed.
        await RunAsync("nft", ["-f", "-"], NftRuleset.Build(o.Interface, settings.Plan.AccessAddress.ToString(), settings.ManagementPorts, []), ct);
        logger.LogInformation("Synced {Peers} peers ({Routers} routers)", peers.Count, routerPeers.Count);
        return routerPeers.Where(p => p.Mode == MikrotikMode.WireGuardSsh).ToDictionary(p => p.Address, p => p.SshPort);
    }

    /// <summary><c>wg show dump</c> into <c>wg_peer_status</c>, and the heartbeat.</summary>
    private async Task StatusAsync(CancellationToken ct)
    {
        var dump = await RunAsync("wg", ["show", network.Options.Interface, "dump"], null, ct);
        var states = GatewayConfig.ParseDump(dump.Output);
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var peers = await db.WgPeers.Select(p => new { p.Id, p.PublicKey }).ToDictionaryAsync(p => p.PublicKey, p => p.Id, ct);
        var existing = await db.WgPeerStatus.ToDictionaryAsync(s => s.PeerId, ct);
        foreach (var state in states)
        {
            if (!peers.TryGetValue(state.PublicKey, out var peerId))
            {
                continue;
            }
            if (!existing.TryGetValue(peerId, out var status))
            {
                status = new WgPeerStatusEntity { PeerId = peerId };
                db.WgPeerStatus.Add(status);
            }
            status.Endpoint = state.Endpoint;
            status.LatestHandshake = state.LatestHandshake;
            status.RxBytes = state.RxBytes;
            status.TxBytes = state.TxBytes;
            status.EndpointChanges24h = EndpointChanges(state.PublicKey, state.Endpoint, now);
            status.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await HeartbeatAsync(ct);
    }

    /// <summary>Two devices with one key take turns, so the endpoint keeps changing (§3.2): counted over 24 hours.</summary>
    private int EndpointChanges(string key, string? endpoint, DateTime now)
    {
        if (!endpointChanges.TryGetValue(key, out var changes))
        {
            endpointChanges[key] = changes = [];
        }
        if (endpoint is not null)
        {
            if (lastEndpoints.TryGetValue(key, out var last) && last != endpoint)
            {
                changes.Add(now);
            }
            lastEndpoints[key] = endpoint;
        }
        changes.RemoveAll(t => now - t > TimeSpan.FromHours(24));
        return changes.Count;
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            await db.WgNetwork.Where(n => n.Id == WgNetworkEntity.SingletonId).ExecuteUpdateAsync(u => u
                .SetProperty(n => n.GatewaySeenAt, Clock.UtcNow)
                .SetProperty(n => n.GatewayVersion, ServerInfo.Version)
                .SetProperty(n => n.GatewayError, lastError), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The heartbeat failed");
        }
    }

    /// <summary><c>LISTEN wireguard_changed</c> on a connection of its own; reconnects after a failure.</summary>
    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(ct);
                connection.Notification += (_, _) => changed.Release();
                await using (var listen = new NpgsqlCommand($"LISTEN {WireGuardNetwork.Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(ct);
                }
                while (!ct.IsCancellationRequested)
                {
                    await connection.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LISTEN {Channel} failed; retrying", WireGuardNetwork.Channel);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private sealed record ProcessResult(int Exit, string Output, string Error);

    private static async Task<ProcessResult> RunAsync(string file, string[] args, string? stdin, CancellationToken ct, bool throwOnError = true)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{file} did not start");
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var result = new ProcessResult(process.ExitCode, await output, await error);
        if (throwOnError && result.Exit != 0)
        {
            throw new InvalidOperationException($"{file} {string.Join(' ', args.Where(a => !a.EndsWith(".conf", StringComparison.Ordinal)))}: {result.Error.Trim()}");
        }
        return result;
    }
}
