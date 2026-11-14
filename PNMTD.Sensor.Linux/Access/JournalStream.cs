using System.Globalization;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Core.Service;
using PNMTD.Sensor.Core.State;
using PNMTD.Sensor.Linux.Collectors;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Access;

/// <summary>
/// Logins and elevations as they happen (docs/linux-sensor-plan.md §5.6): every 15 seconds <c>journalctl -o json --after-cursor=…</c>, filtered
/// to sshd (and OpenSSH 10's sshd-session and sshd-auth), sudo, sudo-rs, su, login and systemd-logind, or to entries at warning and above,
/// which make the <c>journal</c> section be collected again (§5.7). The first time, the last hour; at most 20,000 entries per poll, a backlog
/// read on at the next. The cursor is saved in <c>state/bookmarks.json</c> once the entries are handled, so after a restart or time offline
/// the logins are read on, as long as the journal holds them. <see cref="JournalLines"/> parses, <see cref="JournalLogins"/> pairs and
/// aggregates; lines that match no pattern are counted and reported once an hour, never dropped silently. Never recorded: the agent's own
/// script runs (their units, <c>pnmt-script-*</c>), cron's and systemd's own PAM sessions (other identifiers). Buffered while offline, at
/// most 5,000 records.
/// </summary>
internal sealed class JournalStream : IAgentFeature
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    public const int MaxPerPoll = 20_000;

    private const string BookmarksFile = "bookmarks.json";
    private const string CountsFile = "login-counts.json";

    private readonly ProblemReporter problems;
    private readonly SectionScheduler sections;
    private readonly ILogger<JournalStream> logger;
    private readonly JournalLogins logins;
    private readonly Dictionary<string, int> unmatched = new(StringComparer.Ordinal);
    private HashSet<string> admins = new(StringComparer.Ordinal);
    private HashSet<string> clusterAddresses = new(StringComparer.Ordinal);
    private DateTimeOffset adminsReadAt = DateTimeOffset.MinValue;
    private DateTimeOffset unmatchedSince = DateTimeOffset.UtcNow;
    private string? cursor;
    private bool countsLoaded;

    public JournalStream(ProblemReporter problems, SectionScheduler sections, IEnumerable<IAgentRightsSource> rights, ILogger<JournalStream> logger)
    {
        this.problems = problems;
        this.sections = sections;
        this.logger = logger;
        var source = rights.FirstOrDefault();
        logins = new JournalLogins(user => admins.Contains(user), () => source?.Current.Collect.SudoCommands == true, address => address is not null && clusterAddresses.Contains(address));
    }

    public string Name => "logins";

    public IReadOnlyList<string> Capabilities => [AgentCapabilities.Access];

    public void Configure(AgentConfig config) => logins.Window = TimeSpan.FromMinutes(Math.Clamp(config.Access.AggregateMinutes, 1, 60));

    /// <summary>The last 24 hours in numbers, for the access section: from the stream, or from its saved counts when it doesn't run (<c>collect</c>).</summary>
    public int[] Last24Hours()
    {
        LoadCounts();
        return logins.Counts.Last24Hours(DateTimeOffset.UtcNow);
    }

    private void LoadCounts()
    {
        if (countsLoaded)
        {
            return;
        }
        countsLoaded = true;
        try
        {
            logins.Counts = LoginCounts.FromState(AgentFiles.ReadState<Dictionary<string, int[]>>(CountsFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Tool.Exists("journalctl"))
        {
            logger.LogWarning("journalctl is not installed: logins and elevations are not recorded.");
            return;
        }
        cursor = AgentFiles.ReadState<Dictionary<string, string>>(BookmarksFile)?.GetValueOrDefault("journal");
        LoadCounts();
        while (!ct.IsCancellationRequested)
        {
            RefreshAccounts();
            await Isolated.RunAsync(problems, "logins", "reading the journal", TimeSpan.FromSeconds(90), _ =>
            {
                Poll();
                return Task.CompletedTask;
            }, ct);
            logins.Flush(DateTimeOffset.UtcNow);
            ReportUnmatched();
            await Task.Delay(PollInterval, ct);
        }
    }

    /// <summary>One poll: the entries after the cursor (or of the last hour), handed to the parsers, and the cursor saved after them.</summary>
    private void Poll()
    {
        var arguments = new List<string> { "-o", "json", "--no-pager", "-q", "--output-fields=MESSAGE,SYSLOG_IDENTIFIER,_PID,PRIORITY,_SYSTEMD_UNIT" };
        arguments.Add(cursor is null ? $"--since=@{DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()}" : $"--after-cursor={cursor}");
        arguments.AddRange(JournalLines.Identifiers.Select(i => $"SYSLOG_IDENTIFIER={i}"));
        // …or anything at warning and above, for the journal section (§5.7): journalctl ORs the two groups at "+".
        arguments.Add("+");
        arguments.AddRange(Enumerable.Range(0, 5).Select(p => $"PRIORITY={p}"));
        var count = 0;
        var warning = false;
        string? last = null;
        var (code, errors) = Tool.Stream("journalctl", arguments, TimeSpan.FromSeconds(80), line =>
        {
            if (JournalLines.ParseJson(line) is not { } record)
            {
                return true;
            }
            count++;
            last = record.Cursor;
            Handle(record, ref warning);
            return count < MaxPerPoll;
        });
        if (code is not (null or 0) && last is null && errors.Contains("cursor", StringComparison.OrdinalIgnoreCase))
        {
            // A cursor of a journal that was rotated away or reset: start again with the last hour.
            problems.Report("logins", "the journal's cursor", $"journalctl refused the saved cursor ({errors}): reading on from the last hour.");
            cursor = null;
            SaveCursor();
            return;
        }
        if (code is not (null or 0) && last is null)
        {
            throw new InvalidOperationException($"journalctl failed ({code}): {errors}");
        }
        if (last is not null)
        {
            cursor = last;
            SaveCursor();
            SaveCounts();
        }
        if (count > 0)
        {
            logger.LogDebug("The journal: {Count} entries{More}, {Pending} logins to send", count, count >= MaxPerPoll ? " (the rest at the next poll)" : "", logins.Pending);
        }
        if (warning)
        {
            sections.CollectSoon([SectionNames.Journal]);
        }
    }

    private void Handle(JournalRecord record, ref bool warning)
    {
        if (record.Unit is { } unit && unit.StartsWith("pnmt-script-", StringComparison.Ordinal))
        {
            // The agent's own script runs are in the script history (§5.6).
            return;
        }
        var login = record.Identifier is { } identifier && JournalLines.Identifiers.Contains(identifier);
        if (!login)
        {
            warning |= record.Priority is <= 4;
            return;
        }
        try
        {
            if (JournalLines.Parse(record.Identifier, record.Message, record.At, record.Pid) is { } e)
            {
                logins.Handle(e, record.Cursor);
            }
            else
            {
                unmatched[record.Identifier!] = unmatched.GetValueOrDefault(record.Identifier!) + 1;
                logger.LogDebug("The journal: a line of {Identifier} matched no pattern: {Message}", record.Identifier, record.Message);
            }
        }
        catch (Exception e)
        {
            problems.Report("logins", $"parsing a line of {record.Identifier}", e);
        }
    }

    /// <summary>Lines no pattern matched, once an hour (§5.6): how many of each identifier; their text is in the log at debug level only.</summary>
    private void ReportUnmatched()
    {
        if (unmatched.Count == 0 || DateTimeOffset.UtcNow - unmatchedSince < TimeSpan.FromHours(1))
        {
            return;
        }
        var total = unmatched.Values.Sum();
        problems.Report("logins", "parsing", $"{total} journal lines of the login sources matched no pattern in the last hour "
            + $"({string.Join(", ", unmatched.OrderByDescending(u => u.Value).Select(u => $"{u.Key}: {u.Value}"))}); with log_level: debug the log has them.");
        unmatched.Clear();
        unmatchedSince = DateTimeOffset.UtcNow;
    }

    private void SaveCursor()
    {
        try
        {
            AgentFiles.WriteState(BookmarksFile, new Dictionary<string, string?> { ["journal"] = cursor });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Report("logins", "saving the cursor", e);
        }
    }

    private void SaveCounts()
    {
        try
        {
            AgentFiles.WriteState(CountsFile, logins.Counts.ToState());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("login-counts.json: {Message}", e.Message);
        }
    }

    /// <summary>
    /// The administrators (§5.6: uid 0, sudo, admin and wheel, sudoers' ALL rules, as the system section has them) and, on Proxmox VE, the
    /// cluster's node addresses, every 15 minutes.
    /// </summary>
    private void RefreshAccounts()
    {
        if (DateTimeOffset.UtcNow - adminsReadAt < TimeSpan.FromMinutes(15))
        {
            return;
        }
        adminsReadAt = DateTimeOffset.UtcNow;
        try
        {
            var users = Accounts.ParsePasswd(File.ReadAllText("/etc/passwd"));
            var groups = Accounts.ParseGroup(File.ReadAllText("/etc/group"));
            admins = Accounts.Administrators(users, groups, Accounts.ReadSudoRules()).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception e)
        {
            problems.Report("logins", "reading the administrators", e);
        }
        try
        {
            // At INFO sshd never says which session started: accepted logins then go at once instead of waiting for it.
            logins.SessionTypes = !Sshd.Installed || Sshd.One(Sshd.Effective(), "loglevel")?.ToUpperInvariant() is not ("INFO" or "QUIET" or "FATAL" or "ERROR");
        }
        catch (Exception e) when (e is InvalidOperationException or Core.Collectors.NotAvailableException or TimeoutException)
        {
            logins.SessionTypes = true;
        }
        if (LinuxOs.IsProxmox() && Tool.ReadFile("/etc/pve/corosync.conf") is { } corosync)
        {
            clusterAddresses = corosync.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("ring", StringComparison.Ordinal) && l.Contains("_addr:", StringComparison.Ordinal))
                .Select(l => l[(l.IndexOf(':') + 1)..].Trim()).ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// For the lab and support (<c>pnmt-agent internal logins</c>): the records the stream would make of the journal's last minutes, with a
    /// parser and pairing of their own; nothing is sent or saved.
    /// </summary>
    public static (List<LoginRecord> Records, Dictionary<string, int> Unmatched, int Entries) Preview(TimeSpan since, Func<string, bool> isAdmin, bool sudoCommands)
    {
        var preview = new JournalLogins(isAdmin, () => sudoCommands);
        var unmatched = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = 0;
        var arguments = new List<string> { "-o", "json", "--no-pager", "-q", "--output-fields=MESSAGE,SYSLOG_IDENTIFIER,_PID,PRIORITY,_SYSTEMD_UNIT",
            $"--since=@{DateTimeOffset.UtcNow.Subtract(since).ToUnixTimeSeconds()}" };
        arguments.AddRange(JournalLines.Identifiers.Select(i => $"SYSLOG_IDENTIFIER={i}"));
        Tool.Stream("journalctl", arguments, TimeSpan.FromSeconds(120), line =>
        {
            if (JournalLines.ParseJson(line) is not { } record || record.Unit?.StartsWith("pnmt-script-", StringComparison.Ordinal) == true)
            {
                return true;
            }
            entries++;
            if (JournalLines.Parse(record.Identifier, record.Message, record.At, record.Pid) is { } e)
            {
                preview.Handle(e, record.Cursor);
            }
            else
            {
                unmatched[$"{record.Identifier}: {record.Message}"] = unmatched.GetValueOrDefault($"{record.Identifier}: {record.Message}") + 1;
            }
            return entries < MaxPerPoll;
        });
        preview.Flush(DateTimeOffset.MaxValue);
        return (preview.Take(int.MaxValue), unmatched, entries);
    }

    public async Task ReportAsync(AgentSession session, CancellationToken ct)
    {
        var lost = logins.TakeDropped();
        if (lost > 0)
        {
            problems.Report("logins", "buffer", $"{lost} logins were dropped while offline (at most {JournalLogins.MaxBuffered} are kept).");
        }
        var batch = logins.Take(Math.Clamp(session.Config.Access.MaxBatch, 50, 1000));
        if (batch.Count == 0)
        {
            return;
        }
        try
        {
            await session.SendAsync(AgentMessageTypes.Access, new AccessBatch(batch), ct);
            logger.LogDebug("Sent {Count} logins to PNMT", batch.Count);
        }
        catch
        {
            logins.Return(batch);
            throw;
        }
    }

    public Task<Ack?> HandleAsync(AgentSession session, SignedContent request, CancellationToken ct) => Task.FromResult<Ack?>(null);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"logins ({logins.Pending} pending)");
}
