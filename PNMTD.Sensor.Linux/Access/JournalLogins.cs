using System.Security.Cryptography;
using System.Text;
using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Linux.Access;

/// <summary>
/// The last 24 hours in numbers (docs/linux-sensor-plan.md §5.6), in hourly buckets kept in <c>state/login-counts.json</c>, so a restart
/// doesn't forget the day: logins, failures against existing accounts (lockouts among them), admins' logins, lockouts, the internet's
/// invalid users and the elevations (sudo, su).
/// </summary>
internal sealed class LoginCounts
{
    public const int Successes = 0;
    public const int Failures = 1;
    public const int AdminLogins = 2;
    public const int Lockouts = 3;
    public const int InvalidUsers = 4;
    public const int Elevations = 5;

    private readonly Lock gate = new();

    /// <summary>The hour (Unix seconds of its start) → the six counts.</summary>
    public Dictionary<long, int[]> Hours { get; init; } = [];

    public void Add(DateTimeOffset at, int what, int count = 1)
    {
        var hour = at.ToUnixTimeSeconds() / 3600 * 3600;
        lock (gate)
        {
            if (!Hours.TryGetValue(hour, out var counts))
            {
                Hours[hour] = counts = new int[6];
            }
            if (counts.Length < 6)
            {
                Array.Resize(ref counts, 6);
                Hours[hour] = counts;
            }
            counts[what] += count;
            foreach (var old in Hours.Keys.Where(h => h < hour - 26 * 3600).ToList())
            {
                Hours.Remove(old);
            }
        }
    }

    /// <summary>The sums of the 24 hours before <paramref name="now"/>.</summary>
    public int[] Last24Hours(DateTimeOffset now)
    {
        var from = (now - TimeSpan.FromHours(24)).ToUnixTimeSeconds() / 3600 * 3600;
        var sums = new int[6];
        lock (gate)
        {
            foreach (var (hour, counts) in Hours.Where(h => h.Key >= from && h.Key <= now.ToUnixTimeSeconds()))
            {
                for (var i = 0; i < Math.Min(6, counts.Length); i++)
                {
                    sums[i] += counts[i];
                }
            }
        }
        return sums;
    }

    public Dictionary<string, int[]> ToState()
    {
        lock (gate)
        {
            return Hours.ToDictionary(h => h.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), h => h.Value.ToArray());
        }
    }

    public static LoginCounts FromState(Dictionary<string, int[]>? state) => new()
    {
        Hours = state?.Where(p => long.TryParse(p.Key, out _)).ToDictionary(p => long.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p => p.Value) ?? [],
    };
}

/// <summary>
/// Journal events → <see cref="LoginRecord"/>s (docs/linux-sensor-plan.md §5.6), as Windows' login stream does it (W§4.7), with Linux's
/// pairings. Pure of I/O: <see cref="JournalStream"/> feeds it entries and the time, and sends what it holds.
/// <list type="bullet">
/// <item><b>SSH sessions</b>: every success, held a few seconds until sshd says what started (VERBOSE's <c>Starting session:</c>), paired with
/// its end by the connection's address and port or the sshd PID (<see cref="LoginRecord.LogonId"/>). An SFTP session, a forced command, or
/// Proxmox VE's own SSH from a cluster node is a file transfer: aggregated per user and source.</item>
/// <item><b>Failures</b> against existing accounts: aggregated per user, source, kind and reason within the window. Keys the server refused
/// (<c>Failed publickey</c>, VERBOSE) only count when the connection didn't then log in: clients offer several keys.</item>
/// <item><b>Invalid users</b>: aggregated per source, with the first 10 names; a connection counts once, and again for each attempt after
/// its first.</item>
/// <item><b>sudo and su</b>: every success, with whom it became and (with <c>collect.sudo_commands</c>) the command; failures aggregated.
/// sudo's PAM line counts each wrong password (sudo-rs logs nothing else), and its summary line only what PAM's didn't.</item>
/// <item><b>Console logins</b>: login's PAM session, its terminal, its end.</item>
/// </list>
/// </summary>
internal sealed class JournalLogins(Func<string, bool> isAdmin, Func<bool> sudoCommands, Func<string?, bool>? clusterNode = null)
{
    public const int MaxBuffered = 5000;

    /// <summary>How long an accepted SSH login waits for <c>Starting session:</c> before it goes as a session.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(5);

    /// <summary>How long refused keys wait for the connection to log in or end (sshd's LoginGraceTime).</summary>
    public static readonly TimeSpan RefusalHold = TimeSpan.FromMinutes(2);

    private readonly Lock gate = new();
    private readonly Dictionary<long, LoginRecord> pending = [];
    private readonly Dictionary<string, (LoginRecord Record, DateTimeOffset Start)> aggregates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (LoginRecord Record, DateTimeOffset At)> heldAccepts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(JournalEvent Event, string Cursor)>> heldRefusals = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (string Connection, DateTimeOffset At)> pidConnection = [];
    private readonly Dictionary<string, (LoginRecord Record, DateTimeOffset At)> openSsh = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (LoginRecord Record, DateTimeOffset At)> openByPid = [];
    private readonly Dictionary<string, (int Attempts, DateTimeOffset At)> invalidConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (int Counted, DateTimeOffset At)> sudoFailures = [];
    private DateTimeOffset latest = DateTimeOffset.MinValue;
    private int dropped;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether sshd says what a session starts (LogLevel VERBOSE or above): only then does an accepted login wait for it. At INFO it never
    /// comes (L0), so the login goes at once.
    /// </summary>
    public bool SessionTypes { get; set; } = true;

    public LoginCounts Counts { get; set; } = new();

    public int Pending
    {
        get
        {
            lock (gate)
            {
                return pending.Count;
            }
        }
    }

    /// <summary>The record id PNMTD deduplicates on: a 64-bit hash of the entry's cursor (§5.6).</summary>
    public static long RecordId(string cursor) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(cursor)), 0);

    public void Handle(JournalEvent e, string cursor)
    {
        lock (gate)
        {
            if (e.At > latest)
            {
                latest = e.At;
            }
            switch (e.Kind)
            {
                case JournalEventKind.SshAccepted:
                    Accepted(e, cursor);
                    break;
                case JournalEventKind.SshChild when e.Pid is { } parent && e.ChildPid is { } child && pidConnection.TryGetValue(parent, out var known):
                    pidConnection[child] = (known.Connection, e.At);
                    break;
                case JournalEventKind.SshSession:
                    if (e.Pid is { } sessionPid && e.Connection is { } c)
                    {
                        pidConnection[sessionPid] = (c, e.At);
                    }
                    SessionStarted(e.Connection, e.Session);
                    break;
                case JournalEventKind.SshSubsystem when e.Pid is { } subsystemPid && pidConnection.TryGetValue(subsystemPid, out var conn):
                    SessionStarted(conn.Connection, e.Session);
                    break;
                case JournalEventKind.SshDisconnected:
                    Ended(e.Connection, e.At);
                    break;
                case JournalEventKind.SshClosed:
                    Closed(e);
                    break;
                case JournalEventKind.SshFailed:
                    SshFailure(e, cursor);
                    break;
                case JournalEventKind.SshInvalidUser when e.Connection is { } invalidConnection:
                    invalidConnections[invalidConnection] = (0, e.At);
                    Invalid(e, cursor, e.Address, LogonKind.Ssh, e.User, 1);
                    break;
                case JournalEventKind.SshInvalidUser:
                    Invalid(e, cursor, e.Address, LogonKind.Ssh, e.User, 1);
                    break;
                case JournalEventKind.SshMaxTries when !e.InvalidUser:
                    Failure(e, cursor, e.User!, LogonKind.Ssh, LinuxLogins.Lockout, e.Address, e.Port, 1);
                    break;
                case JournalEventKind.SessionOpened when e.Service == "login":
                    Console(e, cursor);
                    break;
                case JournalEventKind.ConsoleTty:
                    Console(e, cursor);
                    break;
                case JournalEventKind.SessionClosed:
                    SessionClosed(e);
                    break;
                case JournalEventKind.LoginFailed when e.InvalidUser:
                    Invalid(e, cursor, e.Address ?? e.Tty, LogonKind.Console, null, 1);
                    break;
                case JournalEventKind.LoginFailed:
                    Failure(e, cursor, e.User!, LogonKind.Console, e.Reason, e.Address, null, 1, tty: e.Tty);
                    break;
                case JournalEventKind.Lockout when e.InvalidUser || e.User is null:
                    Invalid(e, cursor, e.Address ?? e.Tty, KindOf(e.Service), null, 1);
                    break;
                case JournalEventKind.Lockout:
                    Lockout(e, cursor);
                    break;
                case JournalEventKind.SudoAccepted:
                    Elevation(e, cursor, LogonKind.Sudo);
                    break;
                case JournalEventKind.SudoAuthFailure when e.User is { } sudoUser:
                    if (e.Pid is { } failurePid)
                    {
                        sudoFailures[failurePid] = (sudoFailures.GetValueOrDefault(failurePid).Counted + 1, e.At);
                    }
                    Failure(e, cursor, sudoUser, LogonKind.Sudo, LinuxLogins.WrongPassword, null, null, 1);
                    break;
                case JournalEventKind.SudoFailed:
                    SudoFailed(e, cursor);
                    break;
                case JournalEventKind.SuAccepted:
                    Elevation(e, cursor, LogonKind.Su);
                    break;
                case JournalEventKind.SuFailed:
                    Failure(e, cursor, e.User!, LogonKind.Su, e.Reason ?? LinuxLogins.WrongPassword, null, null, 1, target: e.Target, tty: e.Tty);
                    break;
            }
        }
    }

    /// <summary>
    /// What waited long enough goes (at the end of each poll): accepted logins whose session type never came (sshd's INFO level), refused keys
    /// of connections still open after the grace time; and what is stale is forgotten.
    /// </summary>
    public void Flush(DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var (connection, held) in heldAccepts.Where(h => now - h.Value.At >= Hold).ToList())
            {
                heldAccepts.Remove(connection);
                Session(held.Record, connection, transfer: false);
            }
            foreach (var (connection, refusals) in heldRefusals.Where(h => h.Value.Count > 0 && now - h.Value[0].Event.At >= RefusalHold).ToList())
            {
                heldRefusals.Remove(connection);
                Refused(refusals);
            }
            var reference = latest > DateTimeOffset.MinValue && latest < now ? latest : now;
            Forget(invalidConnections, i => reference - i.At > Window);
            Forget(sudoFailures, s => reference - s.At > Window);
            foreach (var stale in aggregates.Where(a => reference - a.Value.Start > Window * 2).Select(a => a.Key).ToList())
            {
                aggregates.Remove(stale);
            }
            Bound(pidConnection, p => p.At, 20_000);
            Bound(openSsh, o => o.At, 10_000);
            Bound(openByPid, o => o.At, 10_000);
        }
    }

    /// <summary>At most <paramref name="max"/> records, oldest first, taken for sending.</summary>
    public List<LoginRecord> Take(int max)
    {
        lock (gate)
        {
            var batch = pending.Values.OrderBy(r => r.At).Take(max).ToList();
            foreach (var record in batch)
            {
                pending.Remove(record.RecordId);
            }
            return batch;
        }
    }

    /// <summary>Records that could not be sent go back, unless a newer state of the same record came meanwhile.</summary>
    public void Return(IEnumerable<LoginRecord> records)
    {
        lock (gate)
        {
            foreach (var record in records)
            {
                pending.TryAdd(record.RecordId, record);
            }
        }
    }

    public int TakeDropped() => Interlocked.Exchange(ref dropped, 0);

    // --- SSH ----------------------------------------------------------------------------------------------------

    private void Accepted(JournalEvent e, string cursor)
    {
        var connection = e.Connection ?? $"pid {e.Pid}";
        if (e.Pid is { } pid)
        {
            pidConnection[pid] = (connection, e.At);
        }
        // The client got in: the keys it offered before don't count.
        heldRefusals.Remove(connection);
        invalidConnections.Remove(connection);
        var user = e.User ?? "?";
        var method = Method(e.Method);
        var record = new LoginRecord(LinuxLogins.Channel, RecordId(cursor), e.At, null, 1, LogonOutcome.Success, null, user, null, isAdmin(user), LogonKind.Ssh,
            e.Certificate is null ? method : $"{method}, {e.Certificate}", Address(e.Address), e.Port, null, null, e.Pid?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null, e.Fingerprint);
        if (clusterNode?.Invoke(e.Address) == true && user == "root")
        {
            // Proxmox VE's own SSH between the cluster's nodes (§5.6): a file transfer, aggregated.
            Session(record, connection, transfer: true);
            return;
        }
        if (!SessionTypes)
        {
            Session(record, connection, transfer: false);
            return;
        }
        heldAccepts[connection] = (record, e.At);
    }

    private void SessionStarted(string? connection, string? session)
    {
        if (connection is null || !heldAccepts.Remove(connection, out var held))
        {
            return;
        }
        Session(held.Record, connection, transfer: session is "sftp" or "forced");
    }

    /// <summary>An SSH login goes: as a session of its own, or as a file transfer folded into the user's and source's aggregate.</summary>
    private void Session(LoginRecord record, string connection, bool transfer)
    {
        Counts.Add(record.At, LoginCounts.Successes);
        if (record.IsAdmin)
        {
            Counts.Add(record.At, LoginCounts.AdminLogins);
        }
        if (transfer)
        {
            Aggregate($"t|{record.User}|{record.SourceAddress}", record with { Kind = LogonKind.Sftp, LogonId = null }, 1, null);
            return;
        }
        Add(record);
        openSsh[connection] = (record, record.At);
    }

    private void Ended(string? connection, DateTimeOffset at)
    {
        if (connection is null)
        {
            return;
        }
        if (heldAccepts.Remove(connection, out var held))
        {
            Session(held.Record, connection, transfer: false);
        }
        if (openSsh.Remove(connection, out var open))
        {
            Add(open.Record with { LoggedOffAt = at });
        }
    }

    private void Closed(JournalEvent e)
    {
        if (e.Connection is not { } connection)
        {
            return;
        }
        if (heldRefusals.Remove(connection, out var refusals))
        {
            Refused(refusals);
        }
        invalidConnections.Remove(connection);
        if (!e.Preauth)
        {
            Ended(connection, e.At);
        }
    }

    private void SshFailure(JournalEvent e, string cursor)
    {
        var connection = e.Connection ?? $"pid {e.Pid}";
        if (e.InvalidUser)
        {
            // "Invalid user" counted the connection's first attempt; each further one counts.
            if (invalidConnections.TryGetValue(connection, out var seen))
            {
                invalidConnections[connection] = (seen.Attempts + 1, e.At);
                Invalid(e, cursor, e.Address, LogonKind.Ssh, e.User, seen.Attempts == 0 ? 0 : 1);
            }
            else
            {
                invalidConnections[connection] = (1, e.At);
                Invalid(e, cursor, e.Address, LogonKind.Ssh, e.User, 1);
            }
            return;
        }
        if (e.Method == "publickey")
        {
            if (!heldRefusals.TryGetValue(connection, out var list))
            {
                heldRefusals[connection] = list = [];
            }
            list.Add((e, cursor));
            return;
        }
        Failure(e, cursor, e.User ?? "?", LogonKind.Ssh, e.Method is "password" or "keyboard-interactive/pam" or "keyboard-interactive" ? LinuxLogins.WrongPassword
            : $"failed {Method(e.Method)}", e.Address, e.Port, 1);
    }

    private void Refused(List<(JournalEvent Event, string Cursor)> refusals)
    {
        foreach (var (e, cursor) in refusals)
        {
            Failure(e, cursor, e.User ?? "?", LogonKind.Ssh, LinuxLogins.KeyRefused, e.Address, e.Port, 1, fingerprint: e.Fingerprint);
        }
    }

    // --- console, sudo, su, PAM sessions -------------------------------------------------------------------------

    /// <summary>A console login: login's PAM session or its <c>LOGIN ON</c> line, whichever comes first; the other adds the terminal.</summary>
    private void Console(JournalEvent e, string cursor)
    {
        if (e.Pid is { } pid && openByPid.TryGetValue(pid, out var open) && open.Record.Kind == LogonKind.Console)
        {
            if (e.Tty is not null && open.Record.Workstation is null)
            {
                var updated = open.Record with { Workstation = e.Tty, SourceAddress = open.Record.SourceAddress ?? Address(e.Address) };
                openByPid[pid] = (updated, open.At);
                if (pending.ContainsKey(updated.RecordId))
                {
                    pending[updated.RecordId] = updated;
                }
            }
            return;
        }
        var user = e.User ?? "?";
        var record = new LoginRecord(LinuxLogins.Channel, RecordId(cursor), e.At, null, 1, LogonOutcome.Success, null, user, null, isAdmin(user), LogonKind.Console,
            "password", Address(e.Address), null, e.Tty, null, e.Pid?.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
        Counts.Add(e.At, LoginCounts.Successes);
        if (record.IsAdmin)
        {
            Counts.Add(e.At, LoginCounts.AdminLogins);
        }
        Add(record);
        if (e.Pid is { } p)
        {
            openByPid[p] = (record, e.At);
        }
    }

    private void SessionClosed(JournalEvent e)
    {
        if (e.Pid is not { } pid)
        {
            return;
        }
        if (e.Service == "sshd")
        {
            if (pidConnection.TryGetValue(pid, out var connection))
            {
                Ended(connection.Connection, e.At);
            }
            return;
        }
        if (openByPid.Remove(pid, out var open) && open.Record.Count == 1)
        {
            Add(open.Record with { LoggedOffAt = e.At });
        }
    }

    private void Elevation(JournalEvent e, string cursor, LogonKind kind)
    {
        var user = e.User ?? "?";
        var record = new LoginRecord(LinuxLogins.Channel, RecordId(cursor), e.At, null, 1, LogonOutcome.Success, null, user, null, isAdmin(user), kind, null, null, null,
            e.Tty, e.Target is null ? null : e.Target == "root", e.Pid?.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, e.Target,
            kind == LogonKind.Sudo && sudoCommands() ? e.Command : null);
        Counts.Add(e.At, LoginCounts.Elevations);
        Add(record);
        if (e.Pid is { } pid)
        {
            openByPid[pid] = (record, e.At);
        }
    }

    /// <summary>sudo's summary of a failure: wrong passwords only beyond what its PAM lines counted already; not in sudoers and the rest as they come.</summary>
    private void SudoFailed(JournalEvent e, string cursor)
    {
        var user = e.User ?? "?";
        if (e.Reason == LinuxLogins.WrongPassword)
        {
            var counted = e.Pid is { } pid && sudoFailures.Remove(pid, out var known) ? known.Counted : 0;
            var extra = Math.Max(0, e.Attempts - counted);
            if (extra > 0)
            {
                Failure(e, cursor, user, LogonKind.Sudo, LinuxLogins.WrongPassword, null, null, extra, target: e.Target, tty: e.Tty, command: e.Command);
            }
            return;
        }
        Failure(e, cursor, user, LogonKind.Sudo, e.Reason, null, null, 1, target: e.Target, tty: e.Tty, command: e.Command);
    }

    private void Lockout(JournalEvent e, string cursor)
    {
        var user = e.User!;
        Counts.Add(e.At, LoginCounts.Failures);
        Counts.Add(e.At, LoginCounts.Lockouts);
        Add(new LoginRecord(LinuxLogins.Channel, RecordId(cursor), e.At, null, 1, LogonOutcome.Failure, LinuxLogins.Lockout, user, null, isAdmin(user), KindOf(e.Service),
            null, Address(e.Address), null, e.Tty, null, null, null));
    }

    // --- failures and aggregates ---------------------------------------------------------------------------------

    private void Failure(JournalEvent e, string cursor, string user, LogonKind kind, string? reason, string? address, int? port, int count, string? target = null,
        string? tty = null, string? command = null, string? fingerprint = null)
    {
        Counts.Add(e.At, LoginCounts.Failures, count);
        if (reason == LinuxLogins.Lockout)
        {
            Counts.Add(e.At, LoginCounts.Lockouts, count);
        }
        var record = new LoginRecord(LinuxLogins.Channel, RecordId(cursor), e.At, null, count, LogonOutcome.Failure, reason, user, null, isAdmin(user), kind,
            kind is LogonKind.Ssh ? Method(e.Method) : null, Address(address), port, tty, null, null, null, fingerprint, target,
            kind == LogonKind.Sudo && sudoCommands() ? command : null);
        Aggregate($"f|{user}|{Address(address)}|{kind}|{reason}", record, count, null);
    }

    /// <summary>Failures for accounts that don't exist: one aggregate per source, the first 10 names tried (§5.6). Never 464 on PNMTD.</summary>
    private void Invalid(JournalEvent e, string cursor, string? source, LogonKind kind, string? tried, int count)
    {
        Counts.Add(e.At, LoginCounts.InvalidUsers, count);
        var record = new LoginRecord(LinuxLogins.Channel, RecordId(cursor), e.At, null, Math.Max(1, count), LogonOutcome.Failure, LinuxLogins.InvalidUser,
            LinuxLogins.InvalidUser, null, false, kind, null, Address(source), null, kind == LogonKind.Console ? e.Tty : null, null, null, null,
            TriedUsers: tried is null ? [] : [tried]);
        Aggregate($"i|{Address(source)}|{kind}", record, count, tried);
    }

    private void Aggregate(string key, LoginRecord record, int count, string? tried)
    {
        key = key.ToLowerInvariant();
        if (aggregates.TryGetValue(key, out var current) && record.At - current.Start <= Window && record.At >= current.Start)
        {
            var tries = current.Record.TriedUsers;
            if (tried is not null && tries is not null && tries.Count < 10 && !tries.Contains(tried, StringComparer.Ordinal))
            {
                tries = [.. tries, tried];
            }
            var merged = current.Record with
            {
                Count = current.Record.Count + count,
                LastAt = record.At,
                IsAdmin = current.Record.IsAdmin || record.IsAdmin,
                LogonId = null,
                TriedUsers = tries,
            };
            aggregates[key] = (merged, current.Start);
            Add(merged);
            return;
        }
        if (count <= 0)
        {
            // A further attempt of a connection whose aggregate is gone: it was counted when it came.
            return;
        }
        aggregates[key] = (record, record.At);
        Add(record);
    }

    private void Add(LoginRecord record)
    {
        pending[record.RecordId] = record;
        if (pending.Count > MaxBuffered)
        {
            pending.Remove(pending.MinBy(p => p.Value.At).Key);
            dropped++;
        }
    }

    // --- helpers ------------------------------------------------------------------------------------------------

    /// <summary>The method as §5.6 names it: publickey, password, keyboard-interactive, gssapi, hostbased.</summary>
    public static string? Method(string? method) => method switch
    {
        null => null,
        _ when method.StartsWith("keyboard-interactive", StringComparison.Ordinal) => "keyboard-interactive",
        _ when method.StartsWith("gssapi", StringComparison.Ordinal) => "gssapi",
        _ => method,
    };

    private static LogonKind KindOf(string? service) => service switch
    {
        "sshd" => LogonKind.Ssh,
        "su" or "su-l" => LogonKind.Su,
        "sudo" or "sudo-i" => LogonKind.Sudo,
        _ => LogonKind.Console,
    };

    /// <summary>IPv4-mapped IPv6 as IPv4; the rest as sshd wrote it.</summary>
    private static string? Address(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;

    private static void Forget<TKey, TValue>(Dictionary<TKey, TValue> map, Func<TValue, bool> stale) where TKey : notnull
    {
        foreach (var key in map.Where(p => stale(p.Value)).Select(p => p.Key).ToList())
        {
            map.Remove(key);
        }
    }

    private static void Bound<TKey, TValue>(Dictionary<TKey, TValue> map, Func<TValue, DateTimeOffset> at, int max) where TKey : notnull
    {
        if (map.Count <= max)
        {
            return;
        }
        foreach (var key in map.OrderBy(p => at(p.Value)).Take(map.Count - max / 2).Select(p => p.Key).ToList())
        {
            map.Remove(key);
        }
    }
}
