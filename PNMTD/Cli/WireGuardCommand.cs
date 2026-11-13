using System.Runtime.InteropServices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Mikrotik;
using PNMTD.Services.Domain;
using PNMTD.Udp;
using PNMTD.WireGuard;
using PNMTD.WireGuard.Gateway;

namespace PNMTD.Cli;

/// <summary>
/// `pnmtd wireguard …` (docs/mikrotik-plan.md §12): the gateway, which the compose service pnmtd-wg runs as
/// root with NET_ADMIN, and the network's state.
/// </summary>
internal static class WireGuardCommand
{
    public const string Usage = """
          wireguard gateway                     the WireGuard gateway (the service pnmtd-wg: root, NET_ADMIN only)
          wireguard status                      the network, the endpoint in effect and every peer with its handshake
          wireguard tunnel add <name> [--owner <user>] [--routers all|<name>,<name>] [--expires <yyyy-mm-dd>]
                   [--out <file.conf>]          a custom tunnel; its wg-quick file to standard output or --out
        """;

    public static int Run(string[] args, IConfiguration configuration) => args switch
    {
        ["gateway"] => Gateway(configuration),
        ["status"] => Status(configuration),
        ["tunnel", "add", var name, .. var rest] => AddTunnel(configuration, name, new CommandLine.Options(rest)),
        _ => throw new CommandLineException($"Unknown wireguard command: {string.Join(' ', args)}"),
    };

    private static ServiceProvider Services(IConfiguration configuration, bool readOnlyKeys, bool domain = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information)
            .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));
        var connectionString = DatabaseSettings.ConnectionString(configuration);
        services.AddDbContextFactory<PnmtdDbContext>(o => PnmtdDbContext.Configure(o, connectionString));
        var protection = services.AddPnmtdDataProtection(configuration);
        if (readOnlyKeys)
        {
            // The gateway mounts the key ring read-only: it decrypts, it never adds a key.
            protection.DisableAutomaticKeyGeneration();
        }
        services.AddWireGuard(configuration);
        if (domain)
        {
            // The tunnel service and what it needs: the domain services, the MikroTik key store (the instance's name).
            services.AddDbContext<PnmtdDbContext>(o => PnmtdDbContext.Configure(o, connectionString));
            services.AddDomainServices();
            services.AddMikrotik(configuration);
            services.AddUdpNodes(configuration);
            services.AddHttpClient();
        }
        return services.BuildServiceProvider();
    }

    private static int Gateway(IConfiguration configuration)
    {
        using var provider = Services(configuration, readOnlyKeys: true);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("pnmtd-wg");
        using var stopping = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stopping.Cancel(); });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stopping.Cancel(); });
        var dbs = provider.GetRequiredService<IDbContextFactory<PnmtdDbContext>>();
        WaitForSchemaAsync(dbs, logger, stopping.Token).GetAwaiter().GetResult();
        var gateway = new WireGuardGateway(dbs, provider.GetRequiredService<WireGuardNetwork>(), DatabaseSettings.ConnectionString(configuration), logger);
        return gateway.RunAsync(stopping.Token).GetAwaiter().GetResult();
    }

    /// <summary>The app applies the migrations; the gateway may start first and waits for the WireGuard tables.</summary>
    private static async Task WaitForSchemaAsync(IDbContextFactory<PnmtdDbContext> dbs, ILogger logger, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var db = await dbs.CreateDbContextAsync(ct);
                await db.WgNetwork.AnyAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is PostgresException or NpgsqlException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                if (attempt % 6 == 0)
                {
                    logger.LogInformation("Waiting for the database and PNMTD's migrations: {Error}", ex.Message);
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private static int AddTunnel(IConfiguration configuration, string name, CommandLine.Options o)
    {
        var owner = o.Optional("--owner");
        var routers = o.Optional("--routers") ?? "all";
        var expires = o.Optional("--expires");
        var output = o.Optional("--out");
        o.EnsureAllUsed();
        using var provider = Services(configuration, readOnlyKeys: false, domain: true);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PnmtdDbContext>();
        Guid? ownerId = null;
        if (owner is not null)
        {
            var normalized = owner.Trim().ToLowerInvariant();
            ownerId = db.Users.Where(u => u.NormalizedUsername == normalized).Select(u => (Guid?)u.Id).SingleOrDefault()
                ?? throw new CommandLineException($"No user named {owner}.");
        }
        List<Guid> routerIds = [];
        if (routers != "all")
        {
            foreach (var routerName in routers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                routerIds.Add(db.Sensors.Where(s => s.Type == Lib.Models.Enum.SensorType.MIKROTIK && s.Name.ToLower() == routerName.ToLower())
                    .Select(s => (Guid?)s.Id).SingleOrDefault() ?? throw new CommandLineException($"No MikroTik router named {routerName}."));
            }
        }
        DateTime? expiresAt = expires is null ? null
            : DateTime.TryParse(expires, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
                ? Clock.ToUtc(d) : throw new CommandLineException("--expires: a date such as 2026-12-31.");
        var tunnels = scope.ServiceProvider.GetRequiredService<Services.Domain.WireGuardTunnelService>();
        var tunnel = tunnels.CreateAsync(new Api.V1.Contracts.WireGuardTunnelRequest
        {
            Name = name, OwnerUserId = ownerId, AccessAllRouters = routers == "all", RouterIds = routerIds, ExpiresAt = expiresAt,
        }, CancellationToken.None).GetAwaiter().GetResult();
        var config = tunnels.ConfigAsync(tunnel.Id, null, CancellationToken.None).GetAwaiter().GetResult();
        if (output is null)
        {
            Console.Write(config.Content);
        }
        else
        {
            File.WriteAllText(output, "");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.WriteAllText(output, config.Content);
        }
        Console.Error.WriteLine($"Tunnel {tunnel.Name}: {tunnel.Address}, reaching {tunnel.Reaches.Count} router(s) on ports {string.Join(",", config.ManagementPorts)}.");
        return 0;
    }

    private static int Status(IConfiguration configuration)
    {
        using var provider = Services(configuration, readOnlyKeys: false);
        var network = provider.GetRequiredService<WireGuardNetwork>();
        using var db = provider.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();
        var settings = network.SettingsAsync(db, CancellationToken.None).GetAwaiter().GetResult();
        var now = Clock.UtcNow;
        Console.WriteLine($"WireGuard     {(network.Enabled ? "on" : "off (WireGuard__Enabled)")}");
        Console.WriteLine($"Gateway       {(settings.GatewayUp(now, network.Options.GatewayDownSeconds) ? "up" : "down")}" +
            $"{(settings.GatewaySeenAt is { } seen ? $", seen {Clock.ToLocal(seen):yyyy-MM-dd HH:mm:ss}" : ", never seen")}" +
            $"{(settings.GatewayError is { } error ? $", error: {error}" : "")}");
        Console.WriteLine($"Endpoint      {settings.Endpoint ?? "unknown: set PNMTD_WG_EXTERNAL_HOST or PublicBaseUrl"} " +
            $"(host from {settings.ExternalHostSource}, port from {settings.ExternalPortSource})");
        Console.WriteLine($"Network       {settings.Plan} (server {settings.Plan.ServerAddress}, access {settings.Plan.AccessAddress})");
        Console.WriteLine($"Public key    {settings.GatewayPublicKey}");
        var peers = db.WgPeers.AsNoTracking().OrderBy(p => p.Address).ToList();
        var status = db.WgPeerStatus.AsNoTracking().ToDictionary(s => s.PeerId);
        Console.WriteLine();
        Console.WriteLine($"{"ADDRESS",-15} {"KIND",-7} {"NAME",-28} {"HANDSHAKE",-20} ENDPOINT");
        foreach (var p in peers)
        {
            status.TryGetValue(p.Id, out var s);
            var handshake = s?.LatestHandshake is { } h ? Clock.ToLocal(h).ToString("yyyy-MM-dd HH:mm:ss") : "never";
            Console.WriteLine($"{p.Address,-15} {p.Kind,-7} {(p.Name.Length > 28 ? p.Name[..27] + "…" : p.Name),-28} {handshake,-20} {s?.Endpoint ?? ""}");
        }
        return 0;
    }
}
