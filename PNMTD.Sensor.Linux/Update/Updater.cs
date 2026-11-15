using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.State;
using PNMTD.Sensor.Core.Update;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Update;

/// <summary>
/// <c>pnmt-agent update [file] [--force] [--check] [--rollback]</c> (docs/linux-sensor-plan.md §4.1, §4.4; docs/agent-update-plan.md
/// §6.2, §6.4). <b>Run by root on the server only</b>: PNMT has no message that starts it (§7).
/// <list type="bullet">
/// <item>Without a file: PNMT's signed manifest for Linux (<see cref="AgentUpdate"/>, the key pinned at the join, its expiry), the
/// version Linux agents get, only when newer (or with <c>--force</c>); this architecture's file, checked against the manifest's
/// size and SHA-256. With a file: that one.</item>
/// <item>The new binary must answer <c>version</c> as a PNMT agent for this architecture, and as the manifest's version.</item>
/// <item>It installs itself (<see cref="InstallSelf"/>): the service stops, the program is kept as
/// <c>/usr/local/sbin/pnmt-agent.previous</c>, replaced atomically, the unit rewritten, and the service started. When it doesn't
/// run within 30 seconds, the previous program goes back.</item>
/// <item><c>--rollback</c> puts <c>.previous</c> back the same way, and keeps the program it replaces as <c>.previous</c>.</item>
/// </list>
/// The identity, keys, state and config.yaml stay.
/// </summary>
internal static class Updater
{
    /// <summary>The program before the last update or rollback (§6.4).</summary>
    public const string Previous = AgentInfo.Program + ".previous";

    private static readonly TimeSpan RunningWithin = TimeSpan.FromSeconds(30);

    /// <summary>Downloads (or takes <paramref name="file"/>), checks and hands over to the new binary; answers its version.</summary>
    public static async Task<string> UpdateAsync(string? file, bool force, IProgress<string> steps, CancellationToken ct)
    {
        var config = AgentFiles.ReadConfig();
        string candidate;
        string? expected = null;
        string? source;
        string? temporary = null;
        if (file is not null)
        {
            candidate = Path.GetFullPath(file);
            if (!File.Exists(candidate))
            {
                throw new InvalidOperationException($"{file} does not exist.");
            }
            source = $"the file {candidate}";
        }
        else
        {
            if (config is null)
            {
                throw new InvalidOperationException("Not joined: there is no PNMT to update from (pnmt-agent update <file> uses a file).");
            }
            var offer = await AgentUpdate.OfferAsync(config, AgentPlatform.Linux, AgentInfo.Architecture, AgentInfo.Version, steps, ct);
            if (!offer.Newer && !force)
            {
                // Not a failure: unattended runs of update exit 0 (L4).
                throw new AlreadyUpToDateException(offer.Older
                    ? $"Already up to date: this agent is {AgentInfo.Version}, newer than {offer.Version}, the version Linux agents get (--force installs it)."
                    : $"Already up to date: {AgentInfo.Version} (--force installs it again).");
            }
            var downloaded = await AgentUpdate.DownloadAsync(config, offer, Path.Combine(AgentInfo.StateFolder, "update"), f => LinuxPlatform.SecureFolder(f), steps, ct);
            candidate = temporary = downloaded.File;
            expected = downloaded.Version;
            source = downloaded.Source ?? "PNMT";
        }
        try
        {
            steps.Report("Checking the new binary");
            var answer = Tool.Run(candidate, ["version"], TimeSpan.FromSeconds(30));
            var problem = answer.Ok ? AgentUpdate.VersionProblem(answer.Output, AgentInfo.Product, AgentInfo.Architecture, expected) : answer.Reason;
            if (problem is not null)
            {
                throw new InvalidOperationException($"{candidate} is not installed: {problem}.");
            }
            var newVersion = AgentUpdate.VersionOf(answer.Output, AgentInfo.Product)!;
            steps.Report($"{newVersion}: {source}");
            HandOver(candidate, newVersion, steps);
            return newVersion;
        }
        finally
        {
            if (temporary is not null)
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary>
    /// <c>update --check</c> (§6.2): what PNMT offers, in a line; <see cref="AgentUpdate.NewerExitCode"/> when it is newer, else 0.
    /// Downloads nothing.
    /// </summary>
    public static async Task<(string Line, int ExitCode)> CheckAsync(IProgress<string> steps, CancellationToken ct)
    {
        var config = AgentFiles.ReadConfig() ?? throw new InvalidOperationException("Not joined: there is no PNMT to ask.");
        var offer = await AgentUpdate.OfferAsync(config, AgentPlatform.Linux, AgentInfo.Architecture, AgentInfo.Version, steps, ct);
        var from = offer.Asset?.Source is { } source ? $" ({source})" : "";
        if (offer.Newer)
        {
            return ($"PNMT offers {offer.Version}{from}; this agent is {AgentInfo.Version}: pnmt-agent update installs it.", AgentUpdate.NewerExitCode);
        }
        return (offer.Older
            ? $"Up to date: this agent is {AgentInfo.Version}, newer than {offer.Version}, the version Linux agents get (pnmt-agent update --force installs that)."
            : $"Up to date: {AgentInfo.Version}.", 0);
    }

    /// <summary>
    /// <c>update --rollback</c> (§6.2): the previous program installs itself, as an update does; the program it replaces is kept as
    /// <c>.previous</c>, so a second rollback goes forward again. Answers the version now running.
    /// </summary>
    public static string Rollback(IProgress<string> steps)
    {
        if (!File.Exists(Previous))
        {
            throw new InvalidOperationException($"There is nothing to roll back to: {Previous} is not there (an update keeps it).");
        }
        var answer = Tool.Run(Previous, ["version"], TimeSpan.FromSeconds(30));
        var problem = answer.Ok ? AgentUpdate.VersionProblem(answer.Output, AgentInfo.Product, AgentInfo.Architecture, null) : answer.Reason;
        if (problem is not null)
        {
            throw new InvalidOperationException($"{Previous} is not installed: {problem}.");
        }
        var version = AgentUpdate.VersionOf(answer.Output, AgentInfo.Product)!;
        // This program, kept aside: it becomes .previous, whether the previous one runs or not, so nothing is lost.
        var next = Previous + ".next";
        Systemd.Replace(next, AgentInfo.Program);
        try
        {
            HandOver(Previous, version, steps);
        }
        finally
        {
            File.Move(next, Previous, overwrite: true);
        }
        return version;
    }

    /// <summary>
    /// The new binary installs itself (hidden <c>update-install</c>), from where it is: this process runs from the file being replaced,
    /// and a single-file binary loads its assemblies from its own file, by path, so it must touch nothing new after.
    /// </summary>
    private static void HandOver(string candidate, string version, IProgress<string> steps)
    {
        steps.Report($"Handing over to {version}");
        var install = Tool.Run(candidate, ["update-install", "--from", AgentInfo.Version], TimeSpan.FromMinutes(3));
        foreach (var line in (install.Output + install.Errors).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            steps.Report(line.TrimStart('…', ' '));
        }
        if (install.ExitCode == AgentUpdate.RolledBackExitCode)
        {
            throw new InvalidOperationException($"{version} did not start: back on {AgentInfo.Version}. Its log: journalctl -u {AgentInfo.Unit}.");
        }
        if (!install.Ok)
        {
            throw new InvalidOperationException($"{version} could not install itself ({install.ExitCode}): {install.Reason}");
        }
    }

    /// <summary>
    /// <c>pnmt-agent update-install --from &lt;version&gt;</c>, run by <c>update</c> (or <c>--rollback</c>) from the new binary: stops
    /// the service, keeps the program as <c>.previous</c>, puts this binary in its place, writes its unit, starts the service, and
    /// waits until it runs as this version. If it doesn't, the previous program goes back (§6.2, step 7):
    /// <see cref="AgentUpdate.RolledBackExitCode"/>.
    /// </summary>
    public static int InstallSelf(string from, IProgress<string> steps)
    {
        var installed = Systemd.IsInstalled;
        var joined = AgentFiles.ReadConfig() is not null;
        if (installed)
        {
            steps.Report("Stopping the service");
            Systemd.Systemctl("stop", AgentInfo.Unit);
        }
        // A rollback runs from .previous: rollback keeps the program itself.
        var fromPrevious = string.Equals(Path.GetFullPath(AgentInfo.CurrentExecutable), Previous, StringComparison.Ordinal);
        var kept = false;
        if (!fromPrevious && File.Exists(AgentInfo.Program)
            && !string.Equals(Path.GetFullPath(AgentInfo.CurrentExecutable), AgentInfo.Program, StringComparison.Ordinal))
        {
            steps.Report($"Keeping {from} as {Previous}");
            Systemd.Replace(Previous, AgentInfo.Program);
            kept = true;
        }
        steps.Report($"Replacing {AgentInfo.Program}");
        Systemd.Replace(AgentInfo.Program, AgentInfo.CurrentExecutable);
        if (!installed && !joined)
        {
            Journal.Write($"{AgentInfo.Product} updated from {from} to {AgentInfo.Version} (not joined: no service).");
            return 0;
        }
        var since = DateTimeOffset.UtcNow;
        steps.Report("Writing the unit");
        Systemd.WriteUnit();
        // A service that exits at once fails systemctl start itself (L: the lab's broken build): that is "doesn't run" too.
        string? failed = null;
        try
        {
            Start(steps);
            steps.Report($"Waiting until {AgentInfo.Version} runs");
            if (AgentUpdate.WaitRunningAsync(AgentInfo.Version, since, Systemd.IsActive, RunningWithin, CancellationToken.None).GetAwaiter().GetResult())
            {
                Journal.Write($"{AgentInfo.Product} updated from {from} to {AgentInfo.Version}.");
                steps.Report($"{AgentInfo.Version} runs");
                return 0;
            }
            failed = $"it did not run within {RunningWithin.TotalSeconds:0} seconds";
        }
        catch (InvalidOperationException e)
        {
            failed = e.Message;
        }
        if (!kept)
        {
            Journal.Write($"{AgentInfo.Product} {AgentInfo.Version} was installed over {from} and does not run: {failed}.");
            throw new InvalidOperationException($"{AgentInfo.Version} does not run ({failed}), and there is no previous program to go back to.");
        }
        steps.Report($"{AgentInfo.Version} does not run ({failed}): putting {from} back");
        Systemd.Systemctl("stop", AgentInfo.Unit);
        Systemd.Replace(AgentInfo.Program, Previous);
        // The previous program writes its own unit; if it can't, this one's stays.
        var unit = Tool.Run(AgentInfo.Program, ["install-unit"], TimeSpan.FromSeconds(30));
        if (!unit.Ok)
        {
            steps.Report($"{from} could not write its unit ({unit.Reason}): this version's stays");
        }
        var back = DateTimeOffset.UtcNow;
        Start(steps);
        if (!AgentUpdate.WaitRunningAsync(from, back, Systemd.IsActive, RunningWithin, CancellationToken.None).GetAwaiter().GetResult())
        {
            Journal.Write($"{AgentInfo.Product} {AgentInfo.Version} did not run, and {from}, put back, does not run either.");
            throw new InvalidOperationException($"{AgentInfo.Version} did not run, and {from}, put back, does not run either: journalctl -u {AgentInfo.Unit}.");
        }
        Journal.Write($"{AgentInfo.Product} {AgentInfo.Version} did not run: {from} runs again.");
        steps.Report($"{from} runs again");
        return AgentUpdate.RolledBackExitCode;
    }

    private static void Start(IProgress<string> steps)
    {
        Systemd.Systemctl("daemon-reload");
        Systemd.Systemctl("enable", AgentInfo.Unit);
        steps.Report("Starting the service");
        Systemd.Systemctl("start", AgentInfo.Unit);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
