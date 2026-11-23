using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using PNMTD.Services.Domain;
using PNMTD.Udp;
using PNMTD.WireGuard;

namespace PNMTD.Cli;

/// <summary>
/// `pnmtd mikrotik …` (docs/mikrotik-plan.md §12): the routers, a poll with what it read (or the raw
/// output, for debugging the parser against a real router), the join and removal scripts, the host
/// key, and the version catalog. The same services as the server, without the web host.
/// </summary>
internal static class MikrotikCommand
{
    public const string Usage = """
          mikrotik list                         every MikroTik router: mode, setup state, version, last poll
          mikrotik poll <name|id> [--raw]       poll one router now; --raw prints the router's output as it came
          mikrotik backup <name|id> [--print]   back the configuration up now (a version only if it changed);
                                                --print writes the newest stored export to standard output
          mikrotik script <name|id> [--removal] print the join script (audit-logged), or the removal script
                [--firewall | --no-firewall] [--removals | --no-removals]
                                                the join script with or without PNMT's firewall rules, and with or
                                                without removing what an earlier script created; left out, the
                                                router's own (those it was last shown with), which are then kept
          mikrotik accept-host-key <name|id>    accept the host key the router presented instead of the pinned one
          mikrotik versions                     fetch the newest RouterOS version of each channel now
          mikrotik run <name|id>… (--command <text> | --file <script.rsc>) [--admin <user> --password-stdin]
                [--timeout <seconds>] [--backup | --no-backup] [--one-at-a-time | --all-at-once]
                                                run a command (or a script) here, stored as a job like one from the
                                                web; as pnmt, or as the router's admin with the password from
                                                standard input. Prints the output grouped by router; exit 0 when
                                                every router succeeded
        """;

    public static int Run(string[] args, IConfiguration configuration)
    {
        using var provider = Services(configuration);
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        return args switch
        {
            ["list"] => List(sp),
            ["poll", var router, .. var rest] => Poll(sp, router, new CommandLine.Options(rest)),
            ["backup", var router, .. var rest] => Backup(sp, router, new CommandLine.Options(rest)),
            ["script", var router, .. var rest] => Script(sp, router, new CommandLine.Options(rest)),
            ["accept-host-key", var router] => AcceptHostKey(sp, router),
            ["versions"] => Versions(sp),
            ["run", .. var rest] => RunCommand(sp, rest),
            _ => throw new CommandLineException($"Unknown mikrotik command: {string.Join(' ', args)}"),
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
        services.AddUdpNodes(configuration);
        services.AddMikrotik(configuration);
        services.AddWireGuard(configuration);
        return services.BuildServiceProvider();
    }

    private static int List(IServiceProvider sp)
    {
        using var db = sp.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();
        var routers = db.MikrotikRouters.AsNoTracking().Include(r => r.Sensor).ThenInclude(s => s.Parent)
            .OrderBy(r => r.Sensor.Name).ToList();
        if (routers.Count == 0)
        {
            Console.WriteLine("No MikroTik routers.");
            return 0;
        }
        Console.WriteLine($"{"NAME",-28} {"HOST",-20} {"MODE",-16} {"SETUP",-15} {"VERSION",-10} {"LAST POLL",-17} ERROR");
        foreach (var r in routers)
        {
            var lastPoll = r.LastPollAt is { } at ? Clock.ToLocal(at).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "never";
            Console.WriteLine($"{Cut(r.Sensor.Name, 28),-28} {Cut(r.Sensor.Parent?.Name ?? "", 20),-20} {r.Mode.Label(),-16} {r.SetupState,-15} " +
                $"{r.Version ?? "-",-10} {lastPoll,-17} {r.LastError ?? ""}");
        }
        return 0;
    }

    private static int Poll(IServiceProvider sp, string name, CommandLine.Options o)
    {
        var raw = o.Flag("--raw");
        o.EnsureAllUsed();
        var id = Resolve(sp, name);
        var outcome = sp.GetRequiredService<RouterPollService>().PollAsync(id, wait: true, CancellationToken.None).GetAwaiter().GetResult();
        if (raw)
        {
            Console.WriteLine(outcome.RawOutput ?? "(no output)");
            Console.Error.WriteLine(outcome.Message);
            return outcome.Succeeded ? 0 : 1;
        }
        if (outcome.Reading is { } r)
        {
            Console.WriteLine($"Identity      {r.Identity}");
            Console.WriteLine($"RouterOS      {r.VersionText} ({r.Channel})");
            Console.WriteLine($"Model         {r.DisplayModel}{(r.SerialNumber is { } sn ? $", serial number {sn}" : "")}");
            Console.WriteLine($"Uptime        {r.Uptime}");
            Console.WriteLine($"CPU           {r.CpuLoad}% of {r.CpuCount} × {r.Cpu}");
            Console.WriteLine($"Memory        {Mb(r.FreeMemory)} free of {Mb(r.TotalMemory)}");
            Console.WriteLine($"Storage       {Mb(r.FreeStorage)} free of {Mb(r.TotalStorage)}");
            if (r.FirmwareCurrent is not null)
            {
                Console.WriteLine($"Firmware      {r.FirmwareCurrent} (upgrade: {r.FirmwareUpgrade})");
            }
            Console.WriteLine($"Group         pnmt-read: {string.Join(',', r.GroupPolicies)}");
            Console.WriteLine($"SSH keys      {string.Join(", ", r.SshKeyOwners)}");
        }
        foreach (var e in outcome.Events)
        {
            Console.WriteLine($"Event         {e.Code} {e.Message}");
        }
        if (outcome.Events.Count == 0)
        {
            Console.WriteLine(outcome.Message);
        }
        return outcome.Succeeded ? 0 : 1;
    }

    private static int Backup(IServiceProvider sp, string name, CommandLine.Options o)
    {
        var print = o.Flag("--print");
        o.EnsureAllUsed();
        var id = Resolve(sp, name);
        var outcome = sp.GetRequiredService<RouterPollService>()
            .PollAsync(id, wait: true, CancellationToken.None, new PollRequest(ForceExport: true, Models.Enums.MikrotikBackupTrigger.Manual, null, "cli"))
            .GetAwaiter().GetResult();
        if (outcome.Reading is null || outcome.ExportError is not null || outcome.Export is null)
        {
            Console.Error.WriteLine(outcome.ExportError ?? (outcome.Message.Length > 0 ? outcome.Message : "No export."));
            return 1;
        }
        var stored = outcome.Export;
        Console.Error.WriteLine(stored.Backup is { } b
            ? $"Version {b.Number} stored{(stored.First ? "" : $": +{stored.Added} −{stored.Removed} lines")}."
            : "The configuration is unchanged: nothing new to store.");
        if (print)
        {
            using var db = sp.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();
            var newest = db.MikrotikBackups.AsNoTracking().Where(x => x.SensorId == id && x.Kind == Models.Enums.MikrotikBackupKind.Export)
                .OrderByDescending(x => x.Number).First();
            Console.Write(BackupStore.Gunzip(newest.Content));
        }
        return 0;
    }

    private static int Script(IServiceProvider sp, string name, CommandLine.Options o)
    {
        var removal = o.Flag("--removal");
        // docs/mikrotik-join-options-plan.md §2.5.
        bool? firewall = o.Flag("--firewall") ? true : o.Flag("--no-firewall") ? false : null;
        bool? removals = o.Flag("--removals") ? true : o.Flag("--no-removals") ? false : null;
        o.EnsureAllUsed();
        if (removal && (firewall is not null || removals is not null))
        {
            throw new CommandLineException("The removal script has no options: it removes everything a join script created.");
        }
        var id = Resolve(sp, name);
        var script = sp.GetRequiredService<MikrotikRouterService>().ScriptAsync(id, removal, firewall, removals, CancellationToken.None).GetAwaiter().GetResult();
        Console.Write(script.Script);
        foreach (var note in script.Notes)
        {
            Console.Error.WriteLine($"Note: {note}");
        }
        return 0;
    }

    private static int AcceptHostKey(IServiceProvider sp, string name)
    {
        var id = Resolve(sp, name);
        var detail = sp.GetRequiredService<MikrotikRouterService>().AcceptHostKeyAsync(id, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"Pinned {detail.HostKey?.Type} SHA256:{detail.HostKey?.Fingerprint}. The next poll uses it.");
        return 0;
    }

    private static int Versions(IServiceProvider sp)
    {
        sp.GetRequiredService<MikrotikKeyStore>().GetAsync(CancellationToken.None).GetAwaiter().GetResult();
        var latest = sp.GetRequiredService<VersionCatalog>().RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        foreach (var (channel, version) in latest)
        {
            var released = version.ReleasedAt is { } at ? $" (released {Clock.ToLocal(at):yyyy-MM-dd})" : "";
            Console.WriteLine($"{channel,-12} {version.Version}{released}");
        }
        return latest.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// A remote command (§8, §12) run in this process: the jobs are claimed when they are created, so the
    /// server's runner leaves them alone, and the admin password never leaves this process.
    /// </summary>
    private static int RunCommand(IServiceProvider sp, string[] args)
    {
        var names = args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var o = new CommandLine.Options(args[names.Count..]);
        if (names.Count == 0)
        {
            throw new CommandLineException("mikrotik run needs at least one router.");
        }
        var text = o.Optional("--command");
        var file = o.Optional("--file");
        if ((text is null) == (file is null))
        {
            throw new CommandLineException("Give either --command <text> or --file <script.rsc>.");
        }
        text ??= File.ReadAllText(file!);
        var admin = o.Optional("--admin");
        var fromStdin = o.Flag("--password-stdin");
        if (admin is not null != fromStdin)
        {
            throw new CommandLineException("--admin <user> goes with --password-stdin: a password is never an argument.");
        }
        int? timeout = o.Optional("--timeout") is { } t
            ? int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds : throw new CommandLineException("--timeout takes seconds.")
            : null;
        bool? backup = o.Flag("--backup") ? true : o.Flag("--no-backup") ? false : null;
        bool? oneAtATime = o.Flag("--one-at-a-time") ? true : o.Flag("--all-at-once") ? false : null;
        o.EnsureAllUsed();
        var ids = names.Select(n => Resolve(sp, n)).ToList();
        var password = admin is not null ? Console.In.ReadLine() is { Length: > 0 } line ? line : throw new CommandLineException("--password-stdin: no password on standard input.") : null;

        var commands = sp.GetRequiredService<MikrotikCommandService>();
        var created = commands.CreateAsync(new Api.V1.Contracts.MikrotikCommandRequest
        {
            SensorIds = ids, Command = text, RunAs = admin is null ? Models.Enums.MikrotikRunAs.Pnmt : Models.Enums.MikrotikRunAs.Admin,
            Username = admin, Password = password, TimeoutSeconds = timeout, BackupBeforeAndAfter = backup, OneAtATime = oneAtATime,
        }, CancellationToken.None, claim: true).GetAwaiter().GetResult();
        foreach (var refusal in created.Refused)
        {
            Console.Error.WriteLine($"{refusal.Name}: refused: {refusal.Reason}");
        }
        if (created.Jobs.Count == 0)
        {
            return 1;
        }
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        sp.GetRequiredService<JobRunner>().RunHereAsync(created.Jobs.Select(j => j.Id).ToList(), cancel.Token).GetAwaiter().GetResult();

        var batch = commands.BatchAsync(created.BatchId, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var group in batch.Groups)
        {
            Console.WriteLine($"=== {string.Join(", ", group.Jobs.Select(j => j.RouterName))}: {group.State}" +
                (group.Jobs[0].Error is { } error ? $" ({error})" : ""));
            if (!string.IsNullOrEmpty(group.Output))
            {
                Console.Write(group.Output.EndsWith('\n') ? group.Output : group.Output + "\n");
            }
            if (group.OutputTruncated)
            {
                Console.WriteLine("[output cut]");
            }
        }
        var all = batch.Groups.SelectMany(g => g.Jobs).ToList();
        return created.Refused.Count == 0 && all.All(j => j.State == Models.Enums.MikrotikJobState.Succeeded) ? 0 : 1;
    }

    /// <summary>A sensor id, or the name of exactly one MikroTik router (case-insensitive).</summary>
    internal static Guid Resolve(IServiceProvider sp, string nameOrId)
    {
        using var db = sp.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();
        if (Guid.TryParse(nameOrId, out var id))
        {
            return db.MikrotikRouters.Any(r => r.SensorId == id) ? id : throw new CommandLineException($"No MikroTik router with the id {id}.");
        }
        var matches = db.Sensors.AsNoTracking()
            .Where(s => s.Type == SensorType.MIKROTIK && s.Name.ToLower() == nameOrId.ToLower())
            .Select(s => s.Id).ToList();
        return matches switch
        {
            [var only] => only,
            [] => throw new CommandLineException($"No MikroTik router named \"{nameOrId}\"."),
            _ => throw new CommandLineException($"{matches.Count} MikroTik routers are named \"{nameOrId}\": use the id."),
        };
    }

    private static string Mb(long? bytes) => bytes is { } b ? $"{b / 1024d / 1024d:0.0} MiB" : "?";

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
