using System.Buffers.Binary;
using System.Diagnostics;
using System.ServiceProcess;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.State;
using PNMTD.Sensor.Core.Update;
using PNMTD.Sensor.Windows.Platform;

namespace PNMTD.Sensor.Windows.Service;

/// <summary>
/// The Windows agent's update (docs/agent-update-plan.md §6.2, §6.3): <c>pnmt-sensor.exe update [file] [--force] [--check]
/// [--rollback]</c> and the window's <i>Update</i> card. <b>Run by an administrator on the machine only</b>: PNMT has no message
/// that starts it (§7).
/// <list type="bullet">
/// <item><b>From the installed executable</b> (<c>C:\Program Files\PNMT Sensor\pnmt-sensor.exe</c>): PNMT's signed manifest for
/// Windows (<see cref="AgentUpdate"/>), the version Windows agents get, only when newer (or with <c>--force</c>); this
/// architecture's file, downloaded into the state folder (SYSTEM and Administrators only) and checked against the manifest.</item>
/// <item><b>From another executable</b> (one downloaded by hand): that one, as before 1.8. With a file: that file.</item>
/// <item>The executable's version resource must name PNMT Sensor and the manifest's version, and its PE header this machine's
/// architecture. Read, not run: the release builds them so.</item>
/// <item>It installs itself (hidden <c>update-install</c>): the service stops, the installed program is renamed to
/// <c>pnmt-sensor.previous.exe</c> (Windows renames a running executable, but doesn't overwrite one), the new one is copied in,
/// and the service starts. When it doesn't run within 30 seconds, the previous program goes back.</item>
/// <item><c>--rollback</c> puts <c>pnmt-sensor.previous.exe</c> back, and keeps the program it replaces as the previous one.</item>
/// </list>
/// agent.json, the key and the buffers stay.
/// </summary>
internal static class Updater
{
    private static readonly TimeSpan RunningWithin = TimeSpan.FromSeconds(30);

    /// <summary>The first Windows version with <c>update-install</c>: older ones install themselves with <c>update</c>.</summary>
    private const string FirstWithInstall = "1.8.0";

    /// <summary>Whether this process runs the installed program (else it is a newer executable someone downloaded).</summary>
    public static bool RunsInstalled => Same(AgentInfo.CurrentExecutable, AgentInfo.InstalledExecutable);

    /// <summary>
    /// The update (§6.2). Answers the version now installed. <see cref="AlreadyUpToDateException"/> when PNMT offers nothing newer.
    /// </summary>
    public static async Task<string> UpdateAsync(string? file, bool force, IProgress<string> steps, CancellationToken ct)
    {
        string candidate;
        string? expected = null;
        string source;
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
        else if (!RunsInstalled && ServiceControl.IsInstalled)
        {
            // A newer executable someone downloaded and ran: it installs itself, as before 1.8.
            var (mine, problem) = Inspect(AgentInfo.CurrentExecutable, null);
            steps.Report($"Installing this executable, {mine ?? AgentInfo.Version}");
            if (problem is not null)
            {
                throw new InvalidOperationException($"This executable is not installed: {problem}.");
            }
            var code = InstallSelf(InstalledVersionText(), steps);
            if (code == AgentUpdate.RolledBackExitCode)
            {
                throw new InvalidOperationException($"{AgentInfo.Version} did not start: the previous version runs again. See the log.");
            }
            return AgentInfo.Version;
        }
        else
        {
            var config = AgentFiles.ReadConfig() ?? throw new InvalidOperationException("Not joined: there is no PNMT to update from (pnmt-sensor.exe update <file> uses a file).");
            var offer = await AgentUpdate.OfferAsync(config, AgentPlatform.Windows, AgentInfo.Architecture, AgentInfo.Version, steps, ct);
            if (!offer.Newer && !force)
            {
                throw new AlreadyUpToDateException(offer.Older
                    ? $"Already up to date: this agent is {AgentInfo.Version}, newer than {offer.Version}, the version Windows agents get (--force installs it)."
                    : $"Already up to date: {AgentInfo.Version} (--force installs it again).");
            }
            // The update folder inherits the state folder's rights: SYSTEM and Administrators only.
            var downloaded = await AgentUpdate.DownloadAsync(config, offer, AgentInfo.UpdateFolder, f =>
            {
                WindowsPlatform.Secure();
                Directory.CreateDirectory(f);
            }, steps, ct);
            candidate = temporary = downloaded.File;
            expected = downloaded.Version;
            source = downloaded.Source ?? "PNMT";
        }
        try
        {
            steps.Report("Checking the new executable");
            var (version, problem) = Inspect(candidate, expected);
            if (problem is not null)
            {
                throw new InvalidOperationException($"{candidate} is not installed: {problem}.");
            }
            steps.Report($"{version}: {source}");
            HandOver(candidate, version!, steps);
            return version!;
        }
        finally
        {
            if (temporary is not null)
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary><c>update --check</c>: what PNMT offers, in a line; <see cref="AgentUpdate.NewerExitCode"/> when it is newer, else 0.</summary>
    public static async Task<(string Line, int ExitCode, UpdateOffer Offer)> CheckAsync(IProgress<string> steps, CancellationToken ct)
    {
        var config = AgentFiles.ReadConfig() ?? throw new InvalidOperationException("Not joined: there is no PNMT to ask.");
        var offer = await AgentUpdate.OfferAsync(config, AgentPlatform.Windows, AgentInfo.Architecture, InstalledVersionText(), steps, ct);
        var from = offer.Asset?.Source is { } s ? $" ({s})" : "";
        if (offer.Newer)
        {
            return ($"PNMT offers {offer.Version}{from}; this agent is {InstalledVersionText()}.", AgentUpdate.NewerExitCode, offer);
        }
        return (offer.Older
            ? $"Up to date: this agent is {InstalledVersionText()}, newer than {offer.Version}, the version Windows agents get."
            : $"Up to date: {InstalledVersionText()}.", 0, offer);
    }

    /// <summary>The previous program's version, when there is one to roll back to.</summary>
    public static string? PreviousVersion() => File.Exists(AgentInfo.PreviousExecutable) ? Inspect(AgentInfo.PreviousExecutable, null).Version : null;

    /// <summary>
    /// <c>update --rollback</c> (§6.2): the previous program installs itself; the program it replaces becomes the previous one, so a
    /// second rollback switches back. Answers the version now installed.
    /// </summary>
    public static string Rollback(IProgress<string> steps)
    {
        var previous = AgentInfo.PreviousExecutable;
        if (!File.Exists(previous))
        {
            throw new InvalidOperationException($"There is nothing to roll back to: {previous} is not there (an update keeps it).");
        }
        var (version, problem) = Inspect(previous, null);
        if (problem is not null)
        {
            throw new InvalidOperationException($"{previous} is not installed: {problem}.");
        }
        // The installed program goes aside first (renamed: this process may run from it), so the previous one can take its place.
        var next = Path.Combine(AgentInfo.ProgramFolder, "pnmt-sensor.next.exe");
        File.Move(AgentInfo.InstalledExecutable, next, overwrite: true);
        try
        {
            HandOver(previous, version!, steps);
        }
        finally
        {
            if (File.Exists(AgentInfo.InstalledExecutable))
            {
                File.Move(next, previous, overwrite: true);
            }
            else
            {
                // The previous program didn't install itself: this one goes back.
                File.Move(next, AgentInfo.InstalledExecutable);
                TryStart();
            }
        }
        return version!;
    }

    /// <summary>
    /// Runs the new executable's <c>update-install --from</c> (from 1.8; an older one installs itself with <c>update</c>, after the
    /// installed program is renamed to the previous one), shows its lines, and fails when it failed or rolled back.
    /// </summary>
    private static void HandOver(string candidate, string version, IProgress<string> steps)
    {
        steps.Report($"Handing over to {version}");
        var legacy = AgentVersionOrder.IsValid(version) && AgentVersionOrder.Compare(version, FirstWithInstall) < 0;
        if (legacy && File.Exists(AgentInfo.InstalledExecutable) && !Same(candidate, AgentInfo.PreviousExecutable))
        {
            // Its update copies itself over the installed program, which this process may run: renamed, the path is free.
            MoveAside(AgentInfo.PreviousExecutable);
            File.Move(AgentInfo.InstalledExecutable, AgentInfo.PreviousExecutable);
        }
        string[] arguments = legacy ? ["update"] : ["update-install", "--from", AgentInfo.Version];
        var (exitCode, lines) = Run(candidate, arguments, TimeSpan.FromMinutes(3));
        foreach (var line in lines)
        {
            steps.Report(line.TrimStart('…', ' '));
        }
        if (exitCode == AgentUpdate.RolledBackExitCode)
        {
            throw new InvalidOperationException($"{version} did not start: back on {AgentInfo.Version}. Its log is in {AgentInfo.LogFolder}.");
        }
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"{version} could not install itself ({exitCode}){(lines.Count > 0 ? ": " + lines[^1] : "")}.");
        }
    }

    /// <summary>
    /// <c>pnmt-sensor.exe update-install --from &lt;version&gt;</c>, run from the new executable: stops the service, renames the
    /// installed program to <c>pnmt-sensor.previous.exe</c>, copies this one in its place, configures and starts the service, and
    /// waits until it runs as this version. If it doesn't, the previous program goes back: <see cref="AgentUpdate.RolledBackExitCode"/>.
    /// </summary>
    public static int InstallSelf(string from, IProgress<string> steps)
    {
        var installed = ServiceControl.IsInstalled;
        var joined = AgentFiles.ReadConfig() is not null;
        var target = AgentInfo.InstalledExecutable;
        var previous = AgentInfo.PreviousExecutable;
        if (installed)
        {
            steps.Report("Stopping the service");
            ServiceControl.Stop();
        }
        var kept = false;
        if (File.Exists(target) && !Same(AgentInfo.CurrentExecutable, target))
        {
            if (Same(AgentInfo.CurrentExecutable, previous))
            {
                // A rollback: the rollback keeps the installed program itself.
                MoveAside(target);
            }
            else
            {
                steps.Report($"Keeping {from} as {previous}");
                MoveAside(previous);
                File.Move(target, previous);
                kept = true;
            }
        }
        if (!Same(AgentInfo.CurrentExecutable, target))
        {
            steps.Report($"Copying {AgentInfo.Version} to {target}");
            Directory.CreateDirectory(AgentInfo.ProgramFolder);
            File.Copy(AgentInfo.CurrentExecutable, target, overwrite: true);
        }
        if (!installed && !joined)
        {
            Program.WriteEventLog($"PNMT Sensor updated from {from} to {AgentInfo.Version} (not joined: no service).", EventLogEntryType.Information);
            return 0;
        }
        ServiceControl.Install(target);
        string failed;
        try
        {
            var since = DateTimeOffset.UtcNow;
            steps.Report("Starting the service");
            ServiceControl.Start();
            steps.Report($"Waiting until {AgentInfo.Version} runs");
            if (AgentUpdate.WaitRunningAsync(AgentInfo.Version, since, IsRunning, RunningWithin, CancellationToken.None).GetAwaiter().GetResult())
            {
                Program.WriteEventLog($"PNMT Sensor updated from {from} to {AgentInfo.Version}.", EventLogEntryType.Information);
                steps.Report($"{AgentInfo.Version} runs");
                CleanUp();
                return 0;
            }
            failed = $"it did not run within {RunningWithin.TotalSeconds:0} seconds";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            failed = e.Message;
        }
        if (!kept)
        {
            Program.WriteEventLog($"PNMT Sensor {AgentInfo.Version} was installed over {from} and does not run: {failed}.", EventLogEntryType.Error);
            throw new InvalidOperationException($"{AgentInfo.Version} does not run ({failed}), and there is no previous program to go back to.");
        }
        steps.Report($"{AgentInfo.Version} does not run ({failed}): putting {from} back");
        ServiceControl.Stop();
        File.Delete(target);
        File.Move(previous, target);
        var back = DateTimeOffset.UtcNow;
        ServiceControl.Start();
        if (!AgentUpdate.WaitRunningAsync(from, back, IsRunning, RunningWithin, CancellationToken.None).GetAwaiter().GetResult())
        {
            Program.WriteEventLog($"PNMT Sensor {AgentInfo.Version} did not run, and {from}, put back, does not run either.", EventLogEntryType.Error);
            throw new InvalidOperationException($"{AgentInfo.Version} did not run, and {from}, put back, does not run either: see {AgentInfo.LogFolder}.");
        }
        Program.WriteEventLog($"PNMT Sensor {AgentInfo.Version} did not run: {from} runs again.", EventLogEntryType.Warning);
        steps.Report($"{from} runs again");
        return AgentUpdate.RolledBackExitCode;
    }

    /// <summary>
    /// An executable's version and why it can't be installed (null: it can): its version resource must name PNMT Sensor (and
    /// <paramref name="expected"/>, the manifest's version), its PE header this machine's architecture.
    /// </summary>
    public static (string? Version, string? Problem) Inspect(string path, string? expected)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = info.ProductVersion?.Split('+')[0].Trim() ?? "";
            var answer = $"{info.ProductName} {version} ({ArchitectureOf(path)})";
            return (AgentVersionOrder.IsValid(version) ? version : null, AgentUpdate.VersionProblem(answer, AgentInfo.Product, AgentInfo.Architecture, expected));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return (null, $"it can't be read: {e.Message}");
        }
    }

    /// <summary>The PE header's machine as an architecture: x64, arm64, or what it is.</summary>
    private static string ArchitectureOf(string path)
    {
        var head = new byte[4096];
        int length;
        using (var file = File.OpenRead(path))
        {
            length = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        if (length < 64 || head[0] != 'M' || head[1] != 'Z')
        {
            return "no Windows executable";
        }
        var pe = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x3C));
        if (pe < 64 || pe > length - 6 || !head.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8))
        {
            return "no Windows executable";
        }
        return BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(pe + 4)) switch
        {
            0x8664 => "x64",
            0xAA64 => "arm64",
            0x014C => "x86",
            var other => $"machine 0x{other:X4}",
        };
    }

    /// <summary>The installed program's version, as its version resource says; this one's when it can't be read.</summary>
    public static string InstalledVersionText() =>
        File.Exists(AgentInfo.InstalledExecutable) && Inspect(AgentInfo.InstalledExecutable, null).Version is { } v ? v : AgentInfo.Version;

    private static bool IsRunning() => ServiceControl.Status() == ServiceControllerStatus.Running;

    /// <summary>
    /// Runs an executable with its output read (a pipe: the agent from 1.8 writes there instead of attaching to a console) and
    /// answers its exit code and lines; killed after <paramref name="timeout"/>.
    /// </summary>
    private static (int ExitCode, List<string> Lines) Run(string path, string[] arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        var lines = new List<string>();
        using var process = new Process { StartInfo = start };
        void Add(object _, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (lines)
                {
                    lines.Add(e.Data.Trim());
                }
            }
        }
        process.OutputDataReceived += Add;
        process.ErrorDataReceived += Add;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{Path.GetFileName(path)} {string.Join(' ', arguments)} did not finish within {timeout.TotalMinutes:0} minutes.");
        }
        process.WaitForExit();
        return (process.ExitCode, lines);
    }

    /// <summary>Out of the way: deleted, or, while something runs it, renamed to be deleted at the next update.</summary>
    private static void MoveAside(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Move(path, $"{path}.{Guid.NewGuid():N}.old");
        }
    }

    /// <summary>
    /// What earlier updates renamed and couldn't delete. Not <c>pnmt-sensor.next.exe</c>: during a rollback it is the program the
    /// caller keeps as the previous one once this install is done.
    /// </summary>
    private static void CleanUp()
    {
        foreach (var old in Directory.EnumerateFiles(AgentInfo.ProgramFolder, "*.old"))
        {
            TryDelete(old);
        }
    }

    private static void TryStart()
    {
        try
        {
            ServiceControl.Start();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
        }
    }

    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
