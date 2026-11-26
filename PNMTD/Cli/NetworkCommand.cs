using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PNMTD.Audit;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Network;
using PNMTD.Locations;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Network;
using PNMTD.Services.Domain;
using PNMTD.WireGuard;

namespace PNMTD.Cli;

/// <summary>
/// `pnmtd network …` (docs/network-map-plan.md §10.3): a scan run here, like one from the web (or a router's tables as it
/// printed them, for debugging the parser against a real router), the analysis, and the runs' status.
/// </summary>
internal static class NetworkCommand
{
    public const string Usage = """
          network scan <name|id> [--raw]        scan one router here, stored as a run like one from the web; --raw prints each
                                                table as the router printed it and stores nothing
          network scan --location <name> | --all
                                                scan a location's routers (with its sub-locations) or all routers here, one at a time
          network analyse                       run the network map's analysis on what is stored
          network status                        the running and queued scans, the last full one, the schedule
        """;

    public static int Run(string[] args, IConfiguration configuration)
    {
        using var provider = Services(configuration);
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        return args switch
        {
            ["scan", .. var rest] => Scan(sp, new CommandLine.Options(rest), rest),
            ["analyse" or "analyze"] => Analyse(sp),
            ["status"] => Status(sp),
            _ => throw new CommandLineException($"Unknown network command: {string.Join(' ', args)}"),
        };
    }

    private static ServiceProvider Services(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var connectionString = DatabaseSettings.ConnectionString(configuration);
        services.AddDbContextFactory<PnmtdDbContext>(o => PnmtdDbContext.Configure(o, connectionString));
        services.AddPnmtdDataProtection(configuration);
        services.AddHttpClient();
        services.AddDomainServices();
        services.AddMikrotik(configuration);
        services.AddWireGuard(configuration);
        services.AddNetworkMap(configuration);
        return services.BuildServiceProvider();
    }

    private static int Scan(IServiceProvider sp, CommandLine.Options o, string[] args)
    {
        var all = o.Flag("--all");
        var location = o.Optional("--location");
        var raw = o.Flag("--raw");
        var router = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a != location);
        o.EnsureAllUsed();
        if ((router is null ? 0 : 1) + (all ? 1 : 0) + (location is null ? 0 : 1) != 1)
        {
            throw new CommandLineException("network scan needs one router, --location <name> or --all.");
        }
        if (raw)
        {
            return router is null ? throw new CommandLineException("--raw is for one router.") : Raw(sp, MikrotikCommand.Resolve(sp, router));
        }
        using var db = sp.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();
        var tree = sp.GetRequiredService<LocationTree>().GetAsync().GetAwaiter().GetResult();
        Guid? sensorId = router is null ? null : MikrotikCommand.Resolve(sp, router);
        Guid? locationId = location is null ? null
            : tree.All.Where(l => string.Equals(l.Name, location, StringComparison.OrdinalIgnoreCase) || string.Equals(tree.Path(l.Id), location, StringComparison.OrdinalIgnoreCase))
                .Select(l => (Guid?)l.Id).ToList() switch
            {
                [var only] => only,
                [] => throw new CommandLineException($"No location named \"{location}\"."),
                var many => throw new CommandLineException($"{many.Count} locations are named \"{location}\": use its path (Zurich DC › Cage B)."),
            };
        var scope = sensorId is not null ? NetworkScanScope.Router : locationId is not null ? NetworkScanScope.Location : NetworkScanScope.All;
        if (NetworkScanQueue.SameScopeAsync(db, scope, locationId, sensorId, CancellationToken.None).GetAwaiter().GetResult() is { } same)
        {
            Console.Error.WriteLine($"This scan is {same.State.ToString().ToLowerInvariant()} already (run {same.Id}).");
            return 1;
        }
        var scan = NetworkScanQueue.CreateAsync(db, tree, scope, locationId, sensorId, NetworkScanTrigger.Manual, null, "cli", CancellationToken.None)
            .GetAwaiter().GetResult();
        AuditLog.Record(db, AuditEvents.NetworkScanStarted, $"Network scan of {scan.ScopeName} ({scan.Total} routers)", AuditWho.Cli,
            AuditTarget.Of(AuditTargets.NetworkScan, scan.Id, scan.ScopeName, sensorId, locationId));
        db.SaveChanges();
        Console.WriteLine($"Scanning {scan.ScopeName}: {scan.Total} router(s), one at a time.");
        var scanner = sp.GetRequiredService<NetworkScanner>();
        if (!scanner.Enabled)
        {
            throw new CommandLineException("The network map is turned off (NetworkMap__Enabled or Mikrotik__Enabled).");
        }
        scanner.RunHereAsync(CancellationToken.None, scan.Id).GetAwaiter().GetResult();
        var items = db.NetworkScanItems.AsNoTracking().Where(i => i.ScanId == scan.Id).OrderBy(i => i.Position).ToList();
        Console.WriteLine($"{"ROUTER",-28} {"STATE",-10} {"SECONDS",8}  RESULT");
        foreach (var i in items)
        {
            var counts = NetworkViews.Map<int>(i.Counts);
            var result = i.Error ?? (counts is null ? "" : string.Join(", ", counts.Where(c => c.Value > 0).Select(c => $"{c.Value} {c.Key}")));
            var tables = NetworkViews.Map<string>(i.TableErrors);
            Console.WriteLine($"{Cut(i.RouterName, 28),-28} {i.State,-10} {(i.DurationMs ?? 0) / 1000d,8:0.0}  {result}");
            foreach (var (table, error) in tables ?? new Dictionary<string, string>())
            {
                Console.WriteLine($"{"",-28} {"",-10} {"",8}  {table}: {error}");
            }
        }
        return items.All(i => i.State is NetworkScanItemState.Succeeded or NetworkScanItemState.Skipped) ? 0 : 1;
    }

    /// <summary>Each table as the router printed it, and what the parser made of it: nothing is stored.</summary>
    private static int Raw(IServiceProvider sp, Guid sensorId)
    {
        using var db = sp.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();
        var polls = sp.GetRequiredService<RouterPollService>();
        var router = db.MikrotikRouters.Include(r => r.Sensor).Single(r => r.SensorId == sensorId);
        var (target, error) = polls.TargetAsync(db, router, RouterPoll.UserName, polls.PnmtCredentialsAsync(CancellationToken.None).GetAwaiter().GetResult(),
            CancellationToken.None).GetAwaiter().GetResult();
        if (target is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        var tag = sp.GetRequiredService<MikrotikKeyStore>().GetAsync(CancellationToken.None).GetAwaiter().GetResult().Comment;
        var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<NetworkMapOptions>>().CurrentValue.Scan;
        var reading = new NetworkScanReading();
        var failed = false;
        var open = polls.Connector.ConnectAsync(target, CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            foreach (var table in NetworkScanScript.Order)
            {
                var exec = open.ExecAsync(NetworkScanScript.Script(table, tag), TimeSpan.FromSeconds(o.TableTimeoutSeconds), o.MaxOutputBytes, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Console.WriteLine($"--- {table} ({NetworkScanScript.Step(table)}): exit {exec.ExitStatus?.ToString(CultureInfo.InvariantCulture) ?? "-"}, " +
                    $"{exec.Output.Length:N0} characters, {exec.Duration.TotalSeconds:0.0} s{(exec.TimedOut ? ", timed out" : "")}{(exec.Truncated ? ", truncated" : "")}");
                Console.WriteLine(exec.Output.TrimEnd());
                if (exec.Error.Length > 0)
                {
                    Console.Error.WriteLine(exec.Error.TrimEnd());
                }
                try
                {
                    reading.Add(sensorId, table, exec.Output, Clock.UtcNow);
                }
                catch (FormatException ex)
                {
                    failed = true;
                    Console.Error.WriteLine($"{table}: {ex.Message}");
                }
            }
        }
        finally
        {
            open.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        reading.Complete();
        Console.Error.WriteLine($"Parsed: {reading.Ports.Count} ports ({reading.Ports.Count(p => p.DetectedType is not null)} with a type), " +
            string.Join(", ", reading.Observations.GroupBy(x => x.Source).Select(g => $"{g.Count()} {g.Key}")));
        return failed ? 1 : 0;
    }

    private static int Analyse(IServiceProvider sp)
    {
        var outcome = sp.GetRequiredService<NetworkAnalysis>().RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"{outcome.Interfaces} interfaces, {outcome.OpenSuggestions} open suggestions, {outcome.Confirmed} connections confirmed, " +
            $"{outcome.Unconfirmed} unconfirmed ({outcome.Duration.TotalMilliseconds:0} ms).");
        // The skip rules' hits (docs/network-suggestion-rules-plan.md §4.6).
        using var db = sp.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();
        foreach (var rule in db.NetworkSkipRules.AsNoTracking().OrderBy(r => r.CreatedAt).ToList())
        {
            Console.WriteLine(rule.Enabled
                ? $"Skip rule {rule.Name}: {rule.Hits} rows on {rule.HitRouters} router(s)"
                : $"Skip rule {rule.Name}: off");
        }
        return 0;
    }

    private static int Status(IServiceProvider sp)
    {
        using var db = sp.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();
        var active = db.NetworkScans.AsNoTracking().Where(s => s.State == NetworkScanState.Queued || s.State == NetworkScanState.Running)
            .OrderBy(s => s.Scope).ThenBy(s => s.CreatedAt).ToList();
        foreach (var s in active)
        {
            Console.WriteLine($"{s.State,-8} {s.ScopeName}: {s.Succeeded + s.Failed + s.Skipped} of {s.Total} done" +
                (s.CurrentRouterName is { } r ? $", now {r} ({s.CurrentStep})" : ""));
        }
        if (active.Count == 0)
        {
            Console.WriteLine("No scan is running or queued.");
        }
        var settings = db.NetworkMapSettings.AsNoTracking().SingleOrDefault() ?? new NetworkMapSettingsEntity();
        var last = NetworkScanSchedule.LastFullAsync(db, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"Last full scan: {(last is { } l ? Clock.ToLocal(l).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "never")}");
        Console.WriteLine(settings.ScanScheduleEnabled
            ? $"Schedule: every {settings.ScanIntervalDays} days at {settings.ScanHour:00}:00 ({Clock.TimeZone.Id}); next: " +
              (NetworkScanQueue.NextScheduled(last, settings) is { } next ? Clock.ToLocal(next).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "after the first full scan by hand")
            : "Schedule: off");
        return 0;
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
