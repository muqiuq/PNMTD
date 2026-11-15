using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Core.Diagnostics;
using PNMTD.Sensor.Core.Join;
using PNMTD.Sensor.Core.Logging;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Core.Service;
using PNMTD.Sensor.Core.State;
using PNMTD.Sensor.Core.Update;
using PNMTD.Sensor.Linux.Access;
using PNMTD.Sensor.Linux.Collectors;
using PNMTD.Sensor.Linux.Join;
using PNMTD.Sensor.Linux.Keys;
using PNMTD.Sensor.Linux.Platform;
using PNMTD.Sensor.Linux.Rights;
using PNMTD.Sensor.Linux.Scripts;
using PNMTD.Sensor.Linux.Update;

namespace PNMTD.Sensor.Linux;

/// <summary>
/// One binary, a command line and the service mode (docs/linux-sensor-plan.md §4.1): <c>join</c>, <c>status</c>, <c>config check</c>,
/// <c>collect</c>, <c>collect-now</c>, <c>log</c>, <c>update</c>, <c>uninstall</c>, <c>version</c>, and <c>service</c>, which systemd
/// starts. Every command but <c>version</c> and <c>help</c> needs root, and says so.
/// </summary>
internal static class Program
{
    private const string Usage = """
        pnmt-agent join --url <PNMT URL> --token <xxxx-xxxx> [--name <name>] [--allow-http] [--quiet]
                                       joins PNMT, installs and starts the service; exit 0 when connected, 3 when
                                       installed but not connected within 60 s. A missing URL or token is asked for.
                                       An http:// URL needs --allow-http: unencrypted, compare PNMT's key
        pnmt-agent status              the service, the connection, problems, diagnostics and the rights
        pnmt-agent keys                the authorized_keys files and keys as the agent sees them, and which PNMT
                                       may remove (and why not)
        pnmt-agent keys restore [<backup>] [--yes]
                                       lists the backups of removed keys; with one, shows what comes back, asks,
                                       and puts the file back with its owner's rights
        pnmt-agent config check        checks /etc/pnmt-agent/config.yaml and prints the rights it grants
        pnmt-agent collect [<section>] collects once and prints the sections as JSON (for support)
        pnmt-agent collect-now         asks the running service to collect everything now
        pnmt-agent log [-n <lines>] [-f]
                                       the log file's last lines (100), or follows it
        pnmt-agent update [<file>] [--force]
                                       the version Linux agents get, from PNMT, checked against its signed manifest;
                                       or a file. Stops the service, keeps the program as .previous, replaces it and
                                       starts it; back to .previous if it doesn't run. Identity, keys and config stay.
                                       Exit 0 when there is nothing newer (for a schedule of your own)
        pnmt-agent update --check      whether PNMT offers a newer version: exit 10 if so, 0 if not
        pnmt-agent update --rollback   puts the previous program back (and keeps this one as .previous)
        pnmt-agent uninstall [--purge] stops and deletes the service, says bye to PNMT, deletes the keys and
                                       /var/lib/pnmt-agent; config.yaml and the logs stay unless --purge
        pnmt-agent version
        pnmt-agent service             the service mode, started by systemd
        """;

    private static readonly string[] Commands =
        ["join", "status", "keys", "config", "collect", "collect-now", "log", "update", "uninstall", "service", "install-unit", "update-install", "internal"];

    /// <summary>The hidden commands the agent starts as a key file's owner (setpriv, §6.5): they run without root, and need none.</summary>
    private static readonly string[] AsOwnerCommands = ["rewrite-keys", "write-keys"];

    private static int Main(string[] args)
    {
        AgentFiles.Use(AgentInfo.Layout);
        if (args.Length == 0)
        {
            return Error(Usage, 2);
        }
        var command = args[0];
        if (command is "version" or "--version")
        {
            return Print($"{AgentInfo.Product} {AgentInfo.Version} ({AgentInfo.Architecture})", 0);
        }
        if (command is "help" or "--help" or "-h")
        {
            return Print(Usage, 0);
        }
        if (!Commands.Contains(command))
        {
            return Error(Usage, 2);
        }
        if (!Native.IsRoot && !(command == "internal" && args.Length > 1 && AsOwnerCommands.Contains(args[1])))
        {
            return Error($"pnmt-agent {command} needs root: sudo pnmt-agent {string.Join(' ', args)}", 1);
        }
        try
        {
            return command switch
            {
                "service" => RunService(),
                "join" => Join(args[1..]),
                "status" => Status(),
                "keys" => Keys(args[1..]),
                "config" when args is [_, "check"] => ConfigCheck(),
                "collect" => Collect(args[1..]),
                "collect-now" => CollectNow(),
                "log" => Log(args[1..]),
                "update" => Update(args[1..]),
                "uninstall" => Uninstall(args[1..]),
                "install-unit" => InstallUnit(),
                "update-install" when args is [_, "--from", var from] => UpdateInstall(from),
                "internal" => Internal(args[1..]),
                _ => Error(Usage, 2),
            };
        }
        catch (AgentJoinException e)
        {
            return Error($"Refused: {e.Message}", 1);
        }
        catch (Exception e)
        {
            return Error($"Failed: {e.Message}", 1);
        }
    }

    // --- the service ----------------------------------------------------------------------------------------

    private static int RunService()
    {
        var platform = LinuxPlatform.Instance;
        try
        {
            platform.SecureFolders();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The service reports it once its problems are set up (AgentService secures the folders again).
        }
        var logs = new DailyFileLoggerProvider(AgentInfo.LogFolder, AgentInfo.Name);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crashed(logs, e.ExceptionObject as Exception);
        // No defaults: no appsettings.json watched in /usr/local/sbin, no environment configuration; systemd's lifetime (Type=notify).
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });
        builder.Services.AddSystemd();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
        builder.Logging.AddProvider(logs);
        AddAgent(builder.Services, logs);
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentService>());
        using var host = builder.Build();
        platform.Rights = host.Services.GetRequiredService<LinuxRights>();
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            host.Services.GetRequiredService<ProblemReporter>().Report("agent", "background task", e.Exception);
            e.SetObserved();
        };
        Journal.Write($"{AgentInfo.Product} {AgentInfo.Version} started.");
        host.Run();
        Journal.Write($"{AgentInfo.Product} stopped.");
        return 0;
    }

    /// <summary>The agent's parts, for the service and for <c>collect</c>. The features' order is their capabilities' order in the hello.</summary>
    internal static void AddAgent(IServiceCollection services, DailyFileLoggerProvider logs)
    {
        services.AddSingleton<IAgentPlatform>(LinuxPlatform.Instance);
        services.AddSingleton(logs);
        Isolated.Logger = logs.CreateLogger("Resilience.Isolated");
        Tool.Logger = logs.CreateLogger("Platform.Tool");
        services.AddSingleton<ProblemReporter>();
        services.AddSingleton<Collector, SystemCollector>();
        services.AddSingleton<Collector, HardwareCollector>();
        services.AddSingleton<Collector, NetworkCollector>();
        services.AddSingleton<Collector, ServicesCollector>();
        services.AddSingleton<Collector, RemoteAccessCollector>();
        services.AddSingleton<Collector, AccessCollector>();
        services.AddSingleton<Collector, JournalCollector>();
        services.AddSingleton<SshKeysCollector>();
        services.AddSingleton<Collector>(sp => sp.GetRequiredService<SshKeysCollector>());
        // config.yaml (§4.6): the rights for the hello and for the features, watched; a change reconnects.
        services.AddSingleton<LinuxRights>();
        services.AddSingleton<IAgentRightsSource>(sp => sp.GetRequiredService<LinuxRights>());
        services.AddSingleton<IAgentFeature>(sp => sp.GetRequiredService<LinuxRights>());
        // Logins and elevations from the journal (§5.6): access.v1; the access collector reads its counts.
        services.AddSingleton<JournalStream>();
        services.AddSingleton<IAgentFeature>(sp => sp.GetRequiredService<JournalStream>());
        // Diagnostics (§7): the shared core's, unchanged.
        services.AddSingleton<IAgentFeature, DiagnosticsFeature>();
        // SSH keys (§6): keys.v1, keys.remove.v1 while config.yaml allows removal; the inotify watch.
        services.AddSingleton<KeyRemoval>();
        services.AddSingleton<IAgentFeature, KeysFeature>();
        // Bash scripts (§8): parse.v1, scripts.v1 while config.yaml enables them.
        services.AddSingleton<IAgentFeature, ScriptsFeature>();
        services.AddSingleton<SectionScheduler>();
        services.AddSingleton<IHeartbeatSampler, LinuxHeartbeatSampler>();
        services.AddSingleton<AgentService>();
    }

    /// <summary>Rule 5 (W§3.8): logged as fatal, a marker for the next start, the journal told, and exit; systemd starts it again.</summary>
    private static void Crashed(DailyFileLoggerProvider logs, Exception? exception)
    {
        try
        {
            logs.CreateLogger("agent").LogCritical(exception, "Crashed: {Message}", exception?.Message);
            Directory.CreateDirectory(AgentInfo.StateFolder);
            File.WriteAllText(AgentInfo.Layout.CrashMarkerFile, $"{exception?.GetType().Name}: {exception?.Message}");
            Journal.Write($"{AgentInfo.Product} crashed: {exception}", warning: true);
            logs.Dispose();
        }
        finally
        {
            Environment.Exit(1);
        }
    }

    // --- join ---------------------------------------------------------------------------------------------------

    private static int Join(string[] args)
    {
        string? url = null, token = null, name = null;
        var quiet = false;
        var allowHttp = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Length:
                    url = args[++i];
                    break;
                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;
                case "--name" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--allow-http":
                    allowHttp = true;
                    break;
                default:
                    return Error(Usage, 2);
            }
        }
        // §4.4 step 3: the machine first.
        if (LinuxOs.JoinRefusal() is { } refusal)
        {
            return Error($"Not joined: {refusal}", 1);
        }
        try
        {
            LinuxPlatform.Instance.SecureFolders();
            var probe = Path.Combine(AgentInfo.DataFolder, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Error($"Not joined: {AgentInfo.DataFolder} can't be written ({e.Message}).", 1);
        }
        var os = LinuxOs.Read();
        if (!os.Supported && !quiet)
        {
            Console.WriteLine($"Note: {os.PrettyName} is not a supported version (docs/linux-sensor-plan.md §4.8): the agent runs anyway, flagged in PNMT.");
        }
        url ??= Ask("PNMT's URL: ", secret: false);
        token ??= Ask("Token (xxxx-xxxx): ", secret: true);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
        {
            return Error(Usage, 2);
        }
        Task<bool> ConfirmHttp(Uri address, string fingerprint)
        {
            if (!allowHttp)
            {
                throw new AgentJoinException("https_required",
                    $"{address} is plain http://, not encrypted. Add --allow-http to join anyway; PNMT's key, as this URL answers, is {fingerprint}: compare it with PNMT's Add server dialog.");
            }
            Console.WriteLine($"Plain http://, confirmed with --allow-http. PNMT's key, as this URL answers: {fingerprint}");
            return Task.FromResult(true);
        }
        var result = Joiner.JoinAsync(LinuxPlatform.Instance, url, token, name, new Steps(quiet), ConfirmHttp, CancellationToken.None).GetAwaiter().GetResult();
        if (!quiet)
        {
            Console.WriteLine($"Joined as {result.Grant.HostName} / {result.Grant.SensorName}.");
        }
        // Both fingerprints always: PNMT's agent log shows the same (§4.4 step 10).
        Console.WriteLine($"This server's key: {result.AgentFingerprint}");
        Console.WriteLine($"PNMT's key:        {result.PnmtdFingerprint}");
        Journal.Write($"Joined PNMT as {result.Grant.HostName} / {result.Grant.SensorName} (key {result.AgentFingerprint}).");
        return result.Connected ? 0 : Error("Installed, but not connected within 60 s: see pnmt-agent log.", 3);
    }

    /// <summary>A missing URL or token from the terminal, the token without echo; null without a terminal.</summary>
    private static string? Ask(string prompt, bool secret)
    {
        if (Console.IsInputRedirected)
        {
            return null;
        }
        Console.Write(prompt);
        if (!secret)
        {
            return Console.ReadLine()?.Trim();
        }
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                text.Length = Math.Max(0, text.Length - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
        Console.WriteLine();
        return text.ToString().Trim();
    }

    /// <summary>The join's steps, printed as they happen (Progress&lt;T&gt; would print them later, from the thread pool).</summary>
    private sealed class Steps(bool quiet) : IProgress<string>
    {
        public void Report(string value)
        {
            if (!quiet)
            {
                Console.WriteLine($"… {value}");
            }
        }
    }

    // --- status, config check, keys -----------------------------------------------------------------------------

    private static int Status()
    {
        var config = AgentFiles.ReadConfig();
        var status = AgentFiles.ReadStatus();
        Console.WriteLine($"{AgentInfo.Product} {AgentInfo.Version} ({AgentInfo.Architecture})");
        Console.WriteLine($"Service:     {(Systemd.IsInstalled ? Systemd.State() ?? "unknown" : "not installed")} ({AgentInfo.Unit})");
        var rights = ConfigYaml.Read().Rights;
        if (config is null)
        {
            Console.WriteLine("Not joined: pnmt-agent join --url … --token …");
            Console.WriteLine($"Rights:      {RightsLine(rights)}");
            return 1;
        }
        Console.WriteLine($"Server:      {config.HostName} / {config.SensorName} at {config.Url}{(config.PlainHttp ? " (plain http://)" : "")}");
        Console.WriteLine($"Agent:       {config.AgentId}, key {config.AgentKeyFingerprint}");
        Console.WriteLine($"PNMT's key:  {config.PnmtdFingerprint}");
        if (status is not null)
        {
            var stale = DateTimeOffset.UtcNow - status.UpdatedAt > TimeSpan.FromMinutes(1) ? $" (status.json is from {status.UpdatedAt.ToLocalTime():u}: the service isn't writing it)" : "";
            Console.WriteLine($"Connection:  {(status.Connected ? $"connected since {status.ConnectedSince?.ToLocalTime():u}" : status.State)}; last heartbeat "
                + $"{(status.LastHeartbeatAt is { } at ? at.ToLocalTime().ToString("u") : "none yet")}{stale}");
            foreach (var p in status.Problems.Take(5))
            {
                Console.WriteLine($"Problem:     {p.Component}, {p.Operation}: {p.Message} ({p.Count}×)");
            }
            foreach (var d in status.Diagnostics)
            {
                Console.WriteLine($"Diagnostics: {d.SetName} ({d.Mode}{(d.Enabled ? "" : ", paused")}): {d.LastOutcome ?? "not run yet"}{(d.LastMessage is { } m ? $", {m}" : "")}");
            }
            foreach (var gap in status.AuditGaps)
            {
                Console.WriteLine($"Logging gap: {gap}");
            }
            foreach (var s in status.Sections ?? [])
            {
                Console.WriteLine($"Section:     {s.Name}: collected {s.CollectedAt?.ToLocalTime():u}, {s.Bytes} bytes{(s.Errors > 0 ? $", {s.Errors} errors" : "")}"
                    + $"{(s.SentAt is { } sent ? $", sent {sent.ToLocalTime():u}" : ", not sent yet")}");
            }
        }
        Console.WriteLine($"Rights:      {RightsLine(rights)}");
        return status?.Connected == true ? 0 : 1;
    }

    private static string RightsLine(AgentRights rights) => rights.Source switch
    {
        AgentRights.Refused => $"everything off: {AgentInfo.ConfigFile} is refused ({rights.Problem})",
        AgentRights.FromDefaults => $"{rights.Describe()} (no {AgentInfo.ConfigFile}: the defaults)",
        _ => rights.Describe() + (rights.Warnings.Count > 0 ? $" ({rights.Warnings.Count} warnings: pnmt-agent config check)" : ""),
    };

    /// <summary>§4.6: the file checked as the service reads it, and the rights it grants, or why everything is off.</summary>
    private static int ConfigCheck()
    {
        var config = ConfigYaml.Read();
        var rights = config.Rights;
        switch (rights.Source)
        {
            case AgentRights.Refused:
                Console.WriteLine($"{AgentInfo.ConfigFile} is refused: {rights.Problem}.");
                Console.WriteLine("Everything is off until it is fixed: a broken or unsafe file grants nothing.");
                return 1;
            case AgentRights.FromDefaults:
                Console.WriteLine($"{AgentInfo.ConfigFile} is not there: the defaults apply, everything that changes something is off.");
                break;
            default:
                Console.WriteLine($"{AgentInfo.ConfigFile}: owned by root, not writable by others, valid. Applied.");
                break;
        }
        Console.WriteLine($"Key removal:    {(rights.Keys.Remove ? $"allowed for {(rights.Keys.Users.Count == 0 ? "every account" : string.Join(", ", rights.Keys.Users))}" : "off")}");
        Console.WriteLine($"Protected keys: {(rights.Keys.Protect.Count == 0 ? "none" : string.Join(", ", rights.Keys.Protect))}");
        if (rights.Keys.WritablePaths.Count > 0)
        {
            var unit = Tool.ReadFile(AgentInfo.UnitFile) ?? "";
            var missing = rights.Keys.WritablePaths.Where(p => !unit.Contains($"ReadWritePaths=-{p}\n", StringComparison.Ordinal)).ToList();
            Console.WriteLine($"Writable paths: {string.Join(", ", rights.Keys.WritablePaths)}"
                + (missing.Count == 0 ? "" : $" (not in the unit yet: pnmt-agent update, or join again, writes them)"));
        }
        else
        {
            Console.WriteLine("Writable paths: none");
        }
        Console.WriteLine($"Scripts:        {(!rights.Scripts.Enabled ? "off"
            : $"as {(rights.Scripts.RunAs.Count == 0 ? "no account (run_as is empty)" : string.Join(", ", rights.Scripts.RunAs))}; "
              + (rights.Scripts.AllowedHashes.Count == 0 ? "any script of PNMT's library" : $"only {rights.Scripts.AllowedHashes.Count} allowed scripts")
              + $"; at most {rights.Scripts.MaxTimeoutMinutes} min")}");
        Console.WriteLine($"Collect:        sudo's command lines {(rights.Collect.SudoCommands ? "yes" : "no")}; crontab lines in full {(rights.Collect.CronCommands ? "yes" : "no")}");
        Console.WriteLine($"Log level:      {(config.Debug ? "debug" : "info")}");
        foreach (var warning in rights.Warnings)
        {
            Console.WriteLine($"Warning:        {warning}");
        }
        return 0;
    }

    /// <summary>§4.1: <c>keys</c> prints the files and keys as the agent sees them; <c>keys restore</c> lists or puts back a backup (§6.5).</summary>
    private static int Keys(string[] args) => args switch
    {
        [] => KeysList(),
        ["restore"] => KeysRestoreList(),
        ["restore", var backup] when !backup.StartsWith('-') => KeysRestore(backup, yes: false),
        ["restore", var backup, "--yes"] => KeysRestore(backup, yes: true),
        ["restore", "--yes", var named] => KeysRestore(named, yes: true),
        _ => Error(Usage, 2),
    };

    /// <summary>The rights as config.yaml grants them now, for the command line's one-off runs.</summary>
    private sealed class FileRights : IAgentRightsSource
    {
        public AgentRights Current { get; } = ConfigYaml.Read().Rights;

        public int Version => 0;
    }

    private static int KeysList()
    {
        var rights = new FileRights();
        var errors = new List<string>();
        var (section, _) = new SshKeysCollector([rights]).Scan(rights.Current, errors);
        var s = section.Sshd;
        Console.WriteLine(s.Available
            ? $"sshd:        AuthorizedKeysFile {string.Join(' ', s.AuthorizedKeysFiles)}; PubkeyAuthentication {(s.PubkeyAuthentication ? "yes" : "no")}; "
              + $"PermitRootLogin {s.PermitRootLogin}; StrictModes {(s.StrictModes ? "yes" : "no")}{(s.MatchBlocks ? "; Match blocks" : "")}"
            : "sshd:        not available (the default pattern is read)");
        var keys = rights.Current.Keys;
        Console.WriteLine($"Removal:     {(keys.Remove ? $"allowed for {(keys.Users.Count == 0 ? "every account" : string.Join(", ", keys.Users))}" : "off (config.yaml: ssh_keys.remove: false)")}"
            + (keys.Protect.Count > 0 ? $"; {keys.Protect.Count} protected keys" : ""));
        if (section.Files.Count == 0)
        {
            Console.WriteLine("No authorized_keys files were found.");
        }
        foreach (var f in section.Files)
        {
            Console.WriteLine();
            Console.WriteLine($"{f.Account}  {f.Path}{(f.Target is { } t ? $" -> {t}" : "")}{(f.ClusterWide ? "  (cluster-wide)" : "")}");
            Console.WriteLine($"  {f.Owner}:{f.Group} {f.Mode}, {f.Size} bytes, {f.Keys.Count} key{(f.Keys.Count == 1 ? "" : "s")}{(f.Sha256 is { } h ? $", SHA-256 {h[..16]}…" : "")}");
            Console.WriteLine(f.Removable ? "  PNMT may remove keys here." : $"  PNMT may not remove keys here: {f.NotRemovable}.");
            foreach (var problem in f.Problems)
            {
                Console.WriteLine($"  Problem: {problem}");
            }
            foreach (var k in f.Keys)
            {
                Console.WriteLine($"  line {k.Line,-4} {k.Type} {k.Bits} {k.Fingerprint}{(k.Comment is { } c ? $" \"{c}\"" : "")}"
                    + (k.Options.Count > 0 ? $"  options: {string.Join(',', k.Options.Select(o => o.Split('=')[0]))}" : "")
                    + (k.Protected ? $"  PROTECTED ({k.ProtectedBecause})" : ""));
                foreach (var note in k.Notes)
                {
                    Console.WriteLine($"            {note}");
                }
            }
            if (f.InvalidLines.Count > 0)
            {
                Console.WriteLine($"  Lines that are not keys: {string.Join(", ", f.InvalidLines)}");
            }
        }
        foreach (var ca in section.CaKeys)
        {
            Console.WriteLine($"CA key:      {ca.Type} {ca.Bits} {ca.Fingerprint}{(ca.Comment is { } c ? $" \"{c}\"" : "")} ({ca.Source})");
        }
        foreach (var note in section.Notes)
        {
            Console.WriteLine($"Note:        {note}");
        }
        foreach (var error in errors)
        {
            Console.Error.WriteLine($"Error:       {error}");
        }
        return 0;
    }

    private static int KeysRestoreList()
    {
        var backups = KeyBackups.List();
        if (backups.Count == 0)
        {
            return Print("No backups of removed keys (they are kept 30 days, at least the last 10 per file).", 0);
        }
        Console.WriteLine("Backups of removed keys, newest first (pnmt-agent keys restore <name>):");
        foreach (var backup in backups)
        {
            Console.WriteLine($"  {KeyBackups.Describe(backup)}");
        }
        return 0;
    }

    /// <summary>
    /// §6.5: puts a key file back from its backup, after showing which keys come back (and which would go: added since), with a confirmation
    /// (<c>--yes</c> for scripts); with the owner's rights for a user's file. Then the service collects ssh-keys.
    /// </summary>
    private static int KeysRestore(string name, bool yes)
    {
        var backup = KeyBackups.Find(name);
        if (backup is null)
        {
            return Error($"No backup {name}: pnmt-agent keys restore lists them.", 1);
        }
        var account = KeyAccount.Read().FirstOrDefault(a => a.Name == backup.Account);
        if (account is null)
        {
            return Error($"The account {backup.Account} doesn't exist any more: restore {backup.Path} by hand from {Path.Combine(KeyBackups.Folder, backup.Name)}.", 1);
        }
        var content = KeyBackups.Content(backup);
        string Owner(uint uid) => uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var current = KeyFiles.Inspect(account, backup.Path, Owner);
        if (current is { UserSymlink: true } || current?.Link is { Symlink: true })
        {
            return Error($"{backup.Path} is a symlink now: not written through. Restore it by hand.", 1);
        }
        static Dictionary<string, string> KeysOf(byte[]? bytes) => bytes is null ? []
            : System.Text.Encoding.UTF8.GetString(bytes).Split('\n').Select(l => SshKeys.ParseLine(l.TrimEnd('\r')).Key).Where(k => k is not null)
                .GroupBy(k => k!.Fingerprint).ToDictionary(g => g.Key, g => $"{g.First()!.Type} {g.Key}{(g.First()!.Comment is { } c ? $" \"{c}\"" : "")}");
        var before = KeysOf(content);
        var now = KeysOf(current?.Content);
        Console.WriteLine($"Restore {backup.Path} ({backup.Account}) from {backup.Name}, {backup.At.ToLocalTime():yyyy-MM-dd HH:mm}:");
        foreach (var added in before.Where(b => !now.ContainsKey(b.Key)))
        {
            Console.WriteLine($"  comes back:  {added.Value}");
        }
        foreach (var lost in now.Where(n => !before.ContainsKey(n.Key)))
        {
            Console.WriteLine($"  goes again:  {lost.Value}  (added since the backup)");
        }
        if (before.Keys.All(now.ContainsKey) && now.Keys.All(before.ContainsKey))
        {
            Console.WriteLine("  the same keys as now");
        }
        if (!yes)
        {
            if (Console.IsInputRedirected)
            {
                return Error("Not restored: confirm with --yes.", 1);
            }
            Console.Write("Restore it? [y/N] ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            {
                return Print("Not restored.", 1);
            }
        }
        var ownerUid = current?.Facts?.Uid ?? backup.Uid;
        var mode = KeyBackups.ParseMode(backup.Mode);
        string hash;
        if (ownerUid == 0)
        {
            hash = KeyRewrite.WriteAll(backup.Path, content, mode, (0, current?.Facts?.Gid ?? backup.Gid));
        }
        else
        {
            if (ownerUid != account.Uid)
            {
                return Error($"{backup.Path} is owned by uid {ownerUid}, not by {account.Name}: not restored.", 1);
            }
            hash = KeyRemoval.RunAsOwner(account, ownerUid, ["internal", "write-keys", "--path", backup.Path, "--mode", backup.Mode],
                Convert.ToBase64String(content)).Sha256 ?? "";
        }
        var text = $"Restored {backup.Path} ({backup.Account}) from the backup {backup.Name} (pnmt-agent keys restore): SHA-256 {hash[..Math.Min(16, hash.Length)]}….";
        Journal.Write(text, warning: true);
        try
        {
            LinuxPlatform.SecureFolder(AgentInfo.StateFolder);
            File.WriteAllText(AgentInfo.Layout.CollectNowFile, SectionNames.SshKeys + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return Print(text + (Systemd.IsActive() ? " The service collects ssh-keys now." : ""), 0);
    }

    // --- internal: what the agent starts as a key file's owner, and local test hooks --------------------------------

    /// <summary>
    /// Hidden commands. <c>rewrite-keys</c> and <c>write-keys</c> are started by the agent as a key file's owner (setpriv, §6.5 step 6) and
    /// answer one JSON line. <c>remove-key</c>, <c>parse-script</c> and <c>run-script</c> run the service's flows locally, for the lab, with
    /// config.yaml's rights as they are.
    /// </summary>
    private static int Internal(string[] args)
    {
        string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        IEnumerable<string> Options(string name) => args.Select((a, i) => (a, i)).Where(p => p.a == name && p.i + 1 < args.Length).Select(p => args[p.i + 1]);
        var json = new JsonSerializerOptions(AgentJson.Options) { WriteIndented = false };
        switch (args.FirstOrDefault())
        {
            case "rewrite-keys" when Option("--path") is { } path && Option("--fingerprint") is { } fingerprint:
                try
                {
                    var (removed, sha) = KeyRewrite.RemoveKey(path, fingerprint, Option("--sha256"), keepOwner: false);
                    Console.WriteLine(JsonSerializer.Serialize(new RewriteAnswer(removed, sha, null), json));
                    return 0;
                }
                catch (KeyFileChangedException e)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new RewriteAnswer(0, null, e.Message), json));
                    return 4;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new RewriteAnswer(0, null, e.Message), json));
                    return 1;
                }
            case "write-keys" when Option("--path") is { } path && Option("--mode") is { } mode:
                try
                {
                    var content = Convert.FromBase64String(Console.In.ReadToEnd().Trim());
                    var sha = KeyRewrite.WriteAll(path, content, KeyBackups.ParseMode(mode), null);
                    Console.WriteLine(JsonSerializer.Serialize(new RewriteAnswer(0, sha, null), json));
                    return 0;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new RewriteAnswer(0, null, e.Message), json));
                    return 1;
                }
            case "remove-key" when Option("--account") is { } account && Option("--path") is { } path && Option("--fingerprint") is { } fingerprint:
            {
                using var provider = OneOff(out var logs);
                var result = provider.GetRequiredService<KeyRemoval>().Remove(new RemoveSshKeyRequest(Guid.NewGuid(), account, path, fingerprint), "local");
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(AgentJson.Options) { WriteIndented = true }));
                return result.Outcome is SshKeyRemovalOutcome.Removed or SshKeyRemovalOutcome.NotFound ? 0 : 1;
            }
            case "logins":
            {
                var minutes = args.Length > 1 && int.TryParse(args[1], out var m) ? m : 60;
                var users = Accounts.ParsePasswd(File.ReadAllText("/etc/passwd"));
                var admins = Accounts.Administrators(users, Accounts.ParseGroup(File.ReadAllText("/etc/group")), Accounts.ReadSudoRules()).Select(a => a.Name).ToHashSet();
                var (records, unmatched, entries) = JournalStream.Preview(TimeSpan.FromMinutes(minutes), admins.Contains, ConfigYaml.Read().Rights.Collect.SudoCommands);
                Console.WriteLine(JsonSerializer.Serialize(records, new JsonSerializerOptions(AgentJson.Options) { WriteIndented = true }));
                Console.Error.WriteLine($"{entries} journal entries of the last {minutes} min, {records.Count} records, {unmatched.Values.Sum()} lines matched no pattern");
                foreach (var (line, count) in unmatched)
                {
                    Console.Error.WriteLine($"  unmatched ({count}×): {line}");
                }
                return 0;
            }
            case "parse-script" when args.Length == 2:
                Console.WriteLine(JsonSerializer.Serialize(BashScripts.Parse(File.ReadAllText(args[1])), new JsonSerializerOptions(AgentJson.Options) { WriteIndented = true }));
                return 0;
            case "run-script" when args.Length >= 2 && Option("--as") is { } runAs:
            {
                var text = File.ReadAllText(args[1]);
                using var temporary = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
                var publicKey = Convert.ToBase64String(temporary.ExportSubjectPublicKeyInfo());
                var runId = Guid.NewGuid();
                var arguments = Options("--arg").Select(a => a.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 && p[1] is "true" or "false"
                    ? JsonSerializer.SerializeToElement(p[1] == "true") : JsonSerializer.SerializeToElement(p.Length > 1 ? p[1] : ""));
                var secrets = Options("--secret").Select(a => a.Split('=', 2)).ToDictionary(p => p[0], p => AgentSecrets.Encrypt(publicKey, p.Length > 1 ? p[1] : "", runId));
                var timeout = int.TryParse(Option("--timeout"), out var seconds) ? seconds : 300;
                var request = new RunScriptRequest(runId, Option("--name") ?? Path.GetFileNameWithoutExtension(args[1]), ScriptHashes.Of(text), text, arguments, secrets,
                    new RunAsSpec(runAs == "root" ? RunAsKind.Service : RunAsKind.User, runAs == "root" ? null : runAs, null), timeout, "bash");
                var (checkedRun, refusal) = BashScripts.Check(request, ConfigYaml.Read().Rights.Scripts);
                if (checkedRun is null)
                {
                    return Error($"Refused: {refusal}", 1);
                }
                using var logs = new DailyFileLoggerProvider(AgentInfo.LogFolder, $"{AgentInfo.Name}-cli");
                var result = BashScripts.Run(checkedRun, secret => AgentSecrets.Decrypt(temporary, secret, runId));
                ScriptsFeature.Log(logs.CreateLogger("Scripts.ScriptsFeature"), checkedRun, result);
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(AgentJson.Options) { WriteIndented = true }));
                return result.State == ScriptRunState.Done ? 0 : 1;
            }
            default:
                return Error("pnmt-agent internal: rewrite-keys, write-keys, remove-key, logins, parse-script, run-script (for the agent itself and the lab).", 2);
        }
    }

    /// <summary>The agent's parts for a one-off command (collect, internal remove-key), logging to the command line's own log file.</summary>
    private static ServiceProvider OneOff(out DailyFileLoggerProvider logs)
    {
        LinuxPlatform.SecureFolder(AgentInfo.LogFolder);
        logs = new DailyFileLoggerProvider(AgentInfo.LogFolder, $"{AgentInfo.Name}-cli");
        var services = new ServiceCollection();
        var provider = logs;
        services.AddLogging(b => b.AddProvider(provider));
        AddAgent(services, logs);
        return services.BuildServiceProvider();
    }

    // --- collect, collect-now, log --------------------------------------------------------------------------------

    private static int Collect(string[] args)
    {
        if (args.Length > 1 || args.Length == 1 && !SectionNames.IsKnown(AgentPlatform.Linux, args[0]))
        {
            return Error($"pnmt-agent collect [<section>]: one of {string.Join(", ", SectionNames.Linux)}", 2);
        }
        LinuxPlatform.SecureFolder(AgentInfo.LogFolder);
        using var logs = new DailyFileLoggerProvider(AgentInfo.LogFolder, $"{AgentInfo.Name}-cli");
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        AddAgent(services, logs);
        using var provider = services.BuildServiceProvider();
        var sections = provider.GetRequiredService<SectionScheduler>().CollectOnceAsync(CancellationToken.None, args).GetAwaiter().GetResult();
        if (args.Length == 1 && sections.Count == 0)
        {
            return Error($"This agent doesn't collect {args[0]} yet.", 1);
        }
        Console.WriteLine(JsonSerializer.Serialize(sections, new JsonSerializerOptions(AgentJson.Options) { WriteIndented = true }));
        return 0;
    }

    private static int CollectNow()
    {
        LinuxPlatform.SecureFolder(AgentInfo.StateFolder);
        File.WriteAllText(AgentInfo.Layout.CollectNowFile, DateTimeOffset.UtcNow.ToString("O"));
        return Print(Systemd.IsActive()
            ? "Asked: the service collects every section within a few seconds and sends them while connected."
            : "Asked, but the service isn't running: it collects when it starts (systemctl start pnmt-agent).", 0);
    }

    private static int Log(string[] args)
    {
        var lines = 100;
        var follow = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-n" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n > 0:
                    lines = Math.Min(n, 100_000);
                    i++;
                    break;
                case "-f":
                    follow = true;
                    break;
                default:
                    return Error(Usage, 2);
            }
        }
        foreach (var line in DailyFileLoggerProvider.TailOf(AgentInfo.LogFolder, AgentInfo.Name, lines))
        {
            Console.WriteLine(line);
        }
        return follow ? Follow() : 0;
    }

    /// <summary>Follows today's log file, and the next day's when the date changes; Ctrl-C ends it.</summary>
    private static int Follow()
    {
        string Today() => Path.Combine(AgentInfo.LogFolder, $"{AgentInfo.Name}-{DateTime.Now:yyyy-MM-dd}.log");
        var file = Today();
        var position = File.Exists(file) ? new FileInfo(file).Length : 0;
        while (true)
        {
            Thread.Sleep(500);
            if (Today() != file)
            {
                file = Today();
                position = 0;
            }
            if (!File.Exists(file))
            {
                continue;
            }
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < position)
            {
                position = 0;
            }
            stream.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            position = stream.Position;
            Console.Write(text);
        }
    }

    // --- update, uninstall ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>update</c> (docs/agent-update-plan.md §6.2, §6.4): run by root on this server, never started by PNMT. <c>--check</c> asks
    /// only; <c>--rollback</c> puts the previous program back.
    /// </summary>
    private static int Update(string[] args)
    {
        if (args is ["--check"])
        {
            var (line, code) = Updater.CheckAsync(new Steps(true), CancellationToken.None).GetAwaiter().GetResult();
            return Print(line, code);
        }
        if (args is ["--rollback"])
        {
            var was = AgentInfo.Version;
            var now = Updater.Rollback(new Steps(false));
            return Print($"{AgentInfo.Product} {now} runs again instead of {was}, which is kept as {Updater.Previous}: pnmt-agent update --rollback switches back.", 0);
        }
        var force = args.Contains("--force");
        var files = args.Where(a => a != "--force").ToList();
        if (files.Count > 1 || files.Any(f => f.StartsWith('-')))
        {
            return Error(Usage, 2);
        }
        var before = AgentInfo.Version;
        string after;
        try
        {
            after = Updater.UpdateAsync(files.FirstOrDefault(), force, new Steps(false), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (AlreadyUpToDateException e)
        {
            return Print(e.Message, 0);
        }
        // The new binary wrote the journal: this process runs from a replaced file and loads nothing new from here on.
        return Print($"Updated {AgentInfo.Product} from {before} to {after}; the service runs again.", 0);
    }

    private static int Uninstall(string[] args)
    {
        if (args.Any(a => a != "--purge"))
        {
            return Error(Usage, 2);
        }
        foreach (var line in Uninstaller.UninstallAsync(args.Contains("--purge"), CancellationToken.None).GetAwaiter().GetResult())
        {
            Console.WriteLine(line);
        }
        Journal.Write($"{AgentInfo.Product} uninstalled.");
        // Last: a single-file binary loads its assemblies from its own file, so nothing may come after (L3 in the lab).
        if (File.Exists(Updater.Previous))
        {
            File.Delete(Updater.Previous);
        }
        if (File.Exists(AgentInfo.Program))
        {
            Console.WriteLine($"{AgentInfo.Program} is deleted.");
            Console.Out.Flush();
            File.Delete(AgentInfo.Program);
        }
        return 0;
    }

    /// <summary>Hidden, for update: the new binary installs itself from where it runs (<see cref="Updater.InstallSelf"/>).</summary>
    private static int UpdateInstall(string from) => Updater.InstallSelf(from, new Steps(false));

    /// <summary>Hidden, for update: the new binary writes its own unit (with config.yaml's writable paths).</summary>
    private static int InstallUnit()
    {
        Systemd.WriteUnit();
        return 0;
    }

    private static int Print(string text, int code)
    {
        Console.WriteLine(text);
        return code;
    }

    private static int Error(string text, int code)
    {
        Console.Error.WriteLine(text);
        return code;
    }
}
