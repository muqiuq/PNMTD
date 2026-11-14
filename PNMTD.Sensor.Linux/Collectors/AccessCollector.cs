using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Linux.Access;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

/// <summary>
/// <c>access</c> (docs/linux-sensor-plan.md §5.6 → <see cref="LinuxAccessSection"/>): the sessions now (logind), the logging check (a persistent
/// journal, its span and size, sshd's LogLevel, sudo's logging), each gap with the command that fixes it (the agent changes nothing), and the
/// last 24 hours from the login stream: logins, failures, admins' logins, lockouts, and apart the internet's invalid users and the
/// elevations. Every 5 minutes; the sessions, counts and the journal's span are volatile.
/// </summary>
internal sealed class AccessCollector(JournalStream stream) : Collector
{
    public override string Name => SectionNames.Access;

    public override TimeSpan Interval => TimeSpan.FromMinutes(5);

    private static readonly IReadOnlySet<string> VolatileNames = new HashSet<string>
    {
        "sessions", "last24Hours", "invalidUserFailures24Hours", "elevations24Hours", "oldestEntryAt", "spanDays", "journalBytes",
    };

    public override IReadOnlySet<string> Volatile => VolatileNames;

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var boot = Isolated.Part(errors, "boot time", () => (DateTimeOffset?)LinuxFacts.BootTime()) ?? DateTimeOffset.UtcNow;
        var sessions = Isolated.Part(errors, "sessions", () => LinuxFacts.Sessions(boot)) ?? [];
        var logging = Check(errors);
        var counts = Isolated.Part(errors, "the last 24 hours", stream.Last24Hours);
        return new LinuxAccessSection(
            sessions,
            logging,
            counts is null ? null : new AccessCounts(counts[LoginCounts.Successes], counts[LoginCounts.Failures], counts[LoginCounts.AdminLogins], counts[LoginCounts.Lockouts]),
            counts?[LoginCounts.InvalidUsers] ?? 0,
            counts?[LoginCounts.Elevations] ?? 0,
            logging.Fixes.Select(f => f.Key switch
            {
                "journal-persistent" => "the journal is not persistent",
                "sshd-loglevel" => $"sshd's LogLevel is {logging.SshdLogLevel} (VERBOSE recommended)",
                "sudo-logging" => "sudo's logging is turned off",
                _ => f.Problem,
            }).ToList());
    }

    /// <summary>The logging check of §5.6.</summary>
    private static LoggingCheck Check(List<string> errors)
    {
        var fixes = new List<LoggingFix>();
        var (storage, storageFile) = Isolated.Part(errors, "journald's storage", JournalFacts.Storage);
        storage ??= "auto";
        var persistent = storage == "persistent" || storage == "auto" && Directory.Exists("/var/log/journal");
        if (!persistent)
        {
            fixes.Add(storage is "volatile" or "none"
                ? new LoggingFix("journal-persistent",
                    $"journald keeps the journal {(storage == "none" ? "nowhere" : "in memory only")} (Storage={storage} in {storageFile}): logins are lost at every reboot.",
                    "mkdir -p /etc/systemd/journald.conf.d && printf '[Journal]\\nStorage=persistent\\n' > /etc/systemd/journald.conf.d/99-pnmt-persistent.conf && systemctl restart systemd-journald")
                : new LoggingFix("journal-persistent", "The journal is not persistent (/var/log/journal is missing): logins are lost at every reboot.",
                    "mkdir -p /var/log/journal && systemd-tmpfiles --create --prefix /var/log/journal && systemctl restart systemd-journald"));
        }
        var oldest = Isolated.Part(errors, "the journal's oldest entry", JournalFacts.OldestEntry);
        var bytes = Isolated.Part(errors, "the journal's size", JournalFacts.Bytes);
        string? logLevel = null;
        if (Sshd.Installed)
        {
            var settings = Isolated.Part(errors, "sshd -T", () => Sshd.Effective());
            logLevel = settings is null ? null : Sshd.One(settings, "loglevel")?.ToUpperInvariant();
            if (logLevel is "INFO" or "QUIET" or "FATAL" or "ERROR")
            {
                fixes.Add(new LoggingFix("sshd-loglevel", logLevel == "INFO"
                        ? "sshd logs at INFO: the keys of failed attempts aren't logged. VERBOSE logs them, and which session (SFTP or a shell) started."
                        : $"sshd logs at {logLevel}: logins are not logged at all.",
                    LogLevelFix()));
            }
        }
        var sudo = Isolated.Part(errors, "sudo's logging", SudoLoggingOff);
        if (sudo is { } off)
        {
            fixes.Add(new LoggingFix("sudo-logging", $"sudo's logging is turned off ({off.Line} in {off.File}): elevations are not logged.",
                $"visudo -f {off.File}   # remove the line: {off.Line}"));
        }
        var span = oldest is { } o ? Math.Round((DateTimeOffset.UtcNow - o).TotalDays, 1) : (double?)null;
        return new LoggingCheck(persistent, oldest, span, bytes, logLevel, sudo is null, fixes);
    }

    /// <summary>A drop-in where sshd_config includes sshd_config.d (Debian's and Ubuntu's default), else the line in sshd_config.</summary>
    private static string LogLevelFix()
    {
        var unit = File.Exists("/lib/systemd/system/ssh.service") || File.Exists("/usr/lib/systemd/system/ssh.service") ? "ssh" : "sshd";
        var includes = Sshd.Includes(Tool.ReadFile(Sshd.ConfigFile) ?? "").Any(i => i.Contains("sshd_config.d", StringComparison.Ordinal));
        return includes
            ? $"printf 'LogLevel VERBOSE\\n' > /etc/ssh/sshd_config.d/00-pnmt-loglevel.conf && sshd -t && systemctl try-reload-or-restart {unit}"
            : $"echo 'LogLevel VERBOSE' >> /etc/ssh/sshd_config && sshd -t && systemctl try-reload-or-restart {unit}";
    }

    /// <summary>
    /// A sudoers line that turns sudo's logging off (<c>Defaults !syslog</c>, <c>!log_allowed</c>, <c>!log_denied</c>), or null. sudo-rs has no
    /// such setting.
    /// </summary>
    private static (string File, string Line)? SudoLoggingOff()
    {
        if (!Tool.Exists("sudo") && !Tool.Exists("sudo-rs"))
        {
            return null;
        }
        foreach (var (file, text) in Accounts.SudoersFiles())
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("Defaults", StringComparison.Ordinal)
                    && Regex.IsMatch(line, @"(^|[\s,])!\s*(syslog|log_allowed|log_denied)\b|\bsyslog\s*=\s*(""""|false)"))
                {
                    return (file, line);
                }
            }
        }
        return null;
    }
}
