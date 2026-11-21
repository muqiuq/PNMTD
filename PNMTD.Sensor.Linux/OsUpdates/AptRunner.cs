using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.OsUpdates;

/// <summary>Where a run's unit is: still running, ended with an exit code, or gone without one (stopped by its time limit, or lost).</summary>
internal sealed record UnitStatus(bool Running, int? ExitCode, string? Problem);

/// <summary>
/// apt-get in a transient systemd unit (docs/linux-os-updates-plan.md §4.3, O0): started by PID 1 with <c>systemd-run</c>, outside the
/// agent's sandbox (<c>ProtectSystem=full</c> makes /usr and /etc read-only for the agent) and independent of its process. Output goes
/// to a file (no <c>--pipe</c>: a broken pipe would kill dpkg midway when the agent restarts); a fixed wrapper the agent writes records
/// the exit status, and retries an update while another apt holds the lists' lock, which apt 2.6 doesn't wait for. Every argument is
/// the agent's own: PNMT names only the kind and the mode.
/// </summary>
internal static class AptRunner
{
    /// <summary>The wrapper's text. Written before each run, so a changed file never runs.</summary>
    public const string WrapperText = """
        #!/bin/sh
        # Written by pnmt-agent (docs/linux-os-updates-plan.md §4.3): runs apt-get with the arguments the agent chose, and records its
        # exit status for the agent. apt-get update doesn't wait for the lists' lock, so a locked update is tried again, 3 more times,
        # 2 minutes apart. Replaced before each run.
        attempt=1
        while :; do
          "$@"
          status=$?
          if [ "$status" -ne 0 ] && [ -n "$PNMT_RETRY_LOCKED" ] && [ "$attempt" -lt 4 ] && tail -n 5 "$PNMT_LOG_FILE" | grep -q "Could not get lock"; then
            echo "PNMT: another apt holds the lock; trying again in 2 minutes (attempt $((attempt + 1)) of 4)."
            sleep 120
            attempt=$((attempt + 1))
            continue
          fi
          break
        done
        printf '%s\n' "$status" > "$PNMT_EXIT_FILE.tmp" && mv -f "$PNMT_EXIT_FILE.tmp" "$PNMT_EXIT_FILE"
        exit "$status"

        """;

    /// <summary>The options of every run: no colour, no progress bar, wait up to 10 minutes for dpkg's lock, and the run's tag.</summary>
    public static List<string> CommonOptions(Guid runId) =>
        ["-q", "-o", "APT::Color=0", "-o", "Dpkg::Progress-Fancy=0", "-o", "DPkg::Lock::Timeout=600", "-o", $"PNMT::Run={runId:N}"];

    /// <summary>apt-get's arguments for a kind and mode (§4.3): fixed, local configuration files kept.</summary>
    public static List<string> Arguments(Guid runId, OsUpdateKind kind, OsUpgradeMode? mode)
    {
        var arguments = CommonOptions(runId);
        if (kind == OsUpdateKind.Update)
        {
            arguments.Add("update");
            return arguments;
        }
        arguments.InsertRange(0, ["-y"]);
        arguments.AddRange(["-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold"]);
        arguments.AddRange(mode == OsUpgradeMode.FullUpgrade ? ["full-upgrade"] : ["upgrade", "--with-new-pkgs"]);
        return arguments;
    }

    /// <summary>The command line as the log and PNMT show it.</summary>
    public static string CommandText(Guid runId, OsUpdateKind kind, OsUpgradeMode? mode) => "apt-get " + string.Join(' ', Arguments(runId, kind, mode));

    public static string UnitName(Guid runId) => $"pnmt-apt-{runId:N}";

    /// <summary>
    /// Starts the unit and returns at once, or answers why it couldn't (no systemd-run, no D-Bus). An update may run 15 minutes plus
    /// its lock waits; an upgrade has no limit (§4.3: killing dpkg midway breaks the system).
    /// </summary>
    public static string? Start(OsUpdatesStore store, RunRecord run)
    {
        if (!Tool.Exists("systemd-run"))
        {
            return "systemd-run is not installed: apt runs in a transient systemd unit.";
        }
        if (!Tool.Exists("apt-get"))
        {
            return "apt-get is not installed.";
        }
        store.Ensure();
        var temporary = store.Wrapper + ".tmp";
        File.WriteAllText(temporary, WrapperText);
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.Move(temporary, store.Wrapper, overwrite: true);
        OsUpdatesStore.TryDelete(store.ExitFile(run.RunId));
        var log = store.RawLog(run.RunId);
        File.WriteAllText(log, $"PNMT: {run.Command}{(run.RequestedByName is { } who ? $" (for {who})" : "")}, started {run.StartedAt:yyyy-MM-dd HH:mm:ss} UTC\n");
        File.SetUnixFileMode(log, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var arguments = new List<string>
        {
            $"--unit={run.Unit}", $"--description=PNMT: {run.Command}", "--quiet",
            "-p", $"StandardOutput=append:{log}", "-p", "StandardError=inherit",
            "-p", "Nice=10", "-p", "IOSchedulingClass=best-effort", "-p", "IOSchedulingPriority=7",
            "-E", "DEBIAN_FRONTEND=noninteractive", "-E", "LC_ALL=C.UTF-8", "-E", "NEEDRESTART_MODE=l", "-E", "APT_LISTCHANGES_FRONTEND=none",
            "-E", "UCF_FORCE_CONFFOLD=1", "-E", $"PNMT_EXIT_FILE={store.ExitFile(run.RunId)}", "-E", $"PNMT_LOG_FILE={log}",
        };
        if (run.Kind == OsUpdateKind.Update)
        {
            arguments.AddRange(["-p", "RuntimeMaxSec=1800", "-E", "PNMT_RETRY_LOCKED=1"]);
        }
        arguments.AddRange(["/bin/sh", store.Wrapper, Tool.Find("apt-get")!, .. Arguments(run.RunId, run.Kind, run.Mode)]);
        ToolResult result;
        try
        {
            result = Tool.Run("systemd-run", arguments, TimeSpan.FromSeconds(30));
        }
        catch (NotAvailableException e)
        {
            return $"apt can't run in a transient unit: {e.Message}. OS updates need D-Bus (apt install dbus).";
        }
        catch (TimeoutException)
        {
            return "systemd-run didn't answer within 30 s.";
        }
        if (!result.Ok)
        {
            var reason = result.Reason;
            return reason.Contains("Failed to connect to bus", StringComparison.Ordinal)
                ? "OS updates need D-Bus (systemd-run talks to systemd over it): apt install dbus."
                : $"systemd-run failed: {reason}";
        }
        return null;
    }

    /// <summary>
    /// Where the run is: the wrapper's exit file first; otherwise systemd's view of the unit (active: running; failed: ended, with
    /// its main status or why it was stopped; gone without an exit file: lost).
    /// </summary>
    public static UnitStatus Status(OsUpdatesStore store, RunRecord run)
    {
        if (ReadExit(store.ExitFile(run.RunId)) is { } code)
        {
            return new UnitStatus(false, code, null);
        }
        Dictionary<string, string> properties;
        try
        {
            properties = Properties(Tool.Text("systemctl", ["show", run.Unit, "-p", "LoadState,ActiveState,Result,ExecMainStatus,ExecMainCode"], TimeSpan.FromSeconds(15)));
        }
        catch (Exception e) when (e is NotAvailableException or TimeoutException or InvalidOperationException)
        {
            // systemd can't be asked now: still running, as far as the agent knows.
            return new UnitStatus(true, null, null);
        }
        var active = properties.GetValueOrDefault("ActiveState");
        if (active is "active" or "activating" or "reloading" or "deactivating")
        {
            return new UnitStatus(true, null, null);
        }
        // The exit file may have been written between the two reads.
        if (ReadExit(store.ExitFile(run.RunId)) is { } late)
        {
            return new UnitStatus(false, late, null);
        }
        var result = properties.GetValueOrDefault("Result");
        if (active == "failed")
        {
            int? status = int.TryParse(properties.GetValueOrDefault("ExecMainStatus"), out var s) ? s : null;
            var problem = result switch
            {
                "timeout" => "systemd stopped it at its time limit.",
                "signal" or "core-dump" => $"It was killed by signal {status}.",
                "exit-code" => null,
                _ => $"systemd says: {result}.",
            };
            return new UnitStatus(false, result == "exit-code" ? status : null, problem);
        }
        // Inactive or not found, without an exit file: it never ran its end, or the server restarted during it.
        return new UnitStatus(false, null, properties.GetValueOrDefault("LoadState") == "not-found"
            ? "The unit is gone without an exit status: the server may have restarted during the run."
            : "The unit ended without an exit status.");
    }

    /// <summary>Clears a failed unit, so systemd forgets it (<c>systemctl reset-failed</c>).</summary>
    public static void Forget(RunRecord run)
    {
        try
        {
            Tool.Run("systemctl", ["reset-failed", run.Unit], TimeSpan.FromSeconds(15));
        }
        catch (Exception e) when (e is NotAvailableException or TimeoutException or InvalidOperationException)
        {
        }
    }

    /// <summary>Whether another PNMT run's unit is active (one at a time, §4.4).</summary>
    public static bool AnyActive()
    {
        try
        {
            var output = Tool.Text("systemctl", ["list-units", "--no-legend", "--plain", "--state=active,activating", "pnmt-apt-*"], TimeSpan.FromSeconds(15));
            return output.Split('\n').Any(l => l.TrimStart().StartsWith("pnmt-apt-", StringComparison.Ordinal));
        }
        catch (Exception e) when (e is NotAvailableException or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }

    public static int? ReadExit(string path)
    {
        try
        {
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var code) ? code : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary><c>systemctl show</c>'s <c>Key=Value</c> lines.</summary>
    public static Dictionary<string, string> Properties(string output) => output.Split('\n')
        .Select(l => l.Trim()).Where(l => l.Contains('='))
        .Select(l => (Key: l[..l.IndexOf('=')], Value: l[(l.IndexOf('=') + 1)..]))
        .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
}
