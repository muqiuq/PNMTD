using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Linux.Access;

/// <summary>What a journal line about a login or an elevation says (docs/linux-sensor-plan.md §5.6).</summary>
internal enum JournalEventKind
{
    /// <summary>Known, and of no use for the logins: pam_env's complaints, "Connection from", "Postponed publickey", logind's buttons.</summary>
    Ignored,

    /// <summary>sshd: <c>Accepted publickey for U from A port P ssh2: ED25519 SHA256:…</c> (password, keyboard-interactive, gssapi too).</summary>
    SshAccepted,

    /// <summary>sshd: <c>Failed password for [invalid user] U from A port P ssh2</c>, <c>Failed publickey … : RSA SHA256:…</c> (VERBOSE).</summary>
    SshFailed,

    /// <summary>sshd: <c>Invalid user U from A port P</c>, once per connection.</summary>
    SshInvalidUser,

    /// <summary>sshd: <c>maximum authentication attempts exceeded for [invalid user] U from A port P ssh2</c>.</summary>
    SshMaxTries,

    /// <summary>sshd: <c>Disconnected from user U A port P</c>, a session's end.</summary>
    SshDisconnected,

    /// <summary>sshd: a connection that ended before it logged in (<c>Connection closed by authenticating user U A port P [preauth]</c>).</summary>
    SshClosed,

    /// <summary>sshd: <c>User child is on pid N</c>, the privilege-separated child of a session.</summary>
    SshChild,

    /// <summary>sshd (VERBOSE): <c>Starting session: subsystem 'sftp' for U from A port P id N</c>, shell, command or a forced command.</summary>
    SshSession,

    /// <summary>sshd (older): <c>subsystem request for sftp by user U</c>.</summary>
    SshSubsystem,

    /// <summary>pam_unix: <c>pam_unix(login:session): session opened for user U(uid=N) by …</c>.</summary>
    SessionOpened,

    /// <summary>pam_unix: <c>pam_unix(sshd:session): session closed for user U</c>.</summary>
    SessionClosed,

    /// <summary>sudo: <c>U : TTY=… ; PWD=… ; USER=root ; COMMAND=…</c>.</summary>
    SudoAccepted,

    /// <summary>sudo: <c>U : 3 incorrect password attempts ; …</c>, <c>U : user NOT in sudoers ; …</c>, <c>command not allowed</c>.</summary>
    SudoFailed,

    /// <summary>sudo and sudo-rs: <c>pam_unix(sudo:auth): authentication failure; … ruser=U … user=U</c> (sudo-rs logs no summary).</summary>
    SudoAuthFailure,

    /// <summary>su: <c>(to T) U on pts/1</c>.</summary>
    SuAccepted,

    /// <summary>su: <c>FAILED SU (to T) U on pts/1</c>.</summary>
    SuFailed,

    /// <summary>login: <c>ROOT LOGIN ON tty1</c>, <c>LOGIN ON tty1 BY U</c>.</summary>
    ConsoleTty,

    /// <summary>login: <c>FAILED LOGIN (1) on '/dev/tty1' FOR 'root', Authentication failure</c>.</summary>
    LoginFailed,

    /// <summary>pam_faillock: <c>Consecutive login failures for user U account temporarily locked</c>; login's <c>TOO MANY LOGIN TRIES</c>.</summary>
    Lockout,

    /// <summary>systemd-logind: <c>New session c2 of user U.</c>, with its class and type on systemd 256 and later.</summary>
    LogindNew,

    /// <summary>systemd-logind: <c>Removed session c2.</c></summary>
    LogindRemoved,
}

/// <summary>
/// A parsed journal line: its kind and what it names. <see cref="Service"/> is the PAM service (<c>sshd</c>, <c>login</c>, <c>su-l</c>,
/// <c>sudo</c>); <see cref="Session"/> the logind session's id, or for <see cref="JournalEventKind.SshSession"/> what started (<c>shell</c>,
/// <c>command</c>, <c>sftp</c>, <c>forced</c>); <see cref="Certificate"/> a certificate's id and CA. A forced command's text is never kept.
/// </summary>
internal sealed record JournalEvent(JournalEventKind Kind, DateTimeOffset At, int? Pid)
{
    public string? User { get; init; }
    public string? Method { get; init; }
    public string? Address { get; init; }
    public int? Port { get; init; }
    public string? KeyType { get; init; }
    public string? Fingerprint { get; init; }
    public string? Certificate { get; init; }
    public bool InvalidUser { get; init; }
    public string? Target { get; init; }
    public string? Tty { get; init; }
    public string? Command { get; init; }
    public string? Reason { get; init; }
    public int Attempts { get; init; } = 1;
    public int? ChildPid { get; init; }
    public string? Service { get; init; }
    public string? Session { get; init; }
    public string? Class { get; init; }
    public bool Preauth { get; init; }

    /// <summary>The connection's address and port: what sshd's lines of one connection share, whatever process wrote them.</summary>
    public string? Connection => Address is null ? null : $"{Address} {Port}";
}

/// <summary>One entry of <c>journalctl -o json</c>, as the stream needs it.</summary>
internal sealed record JournalRecord(string Cursor, DateTimeOffset At, string? Identifier, string Message, int? Pid, int? Priority, string? Unit, string? Boot);

/// <summary>
/// The journal's login lines (docs/linux-sensor-plan.md §5.6, §14): <b>pure functions</b> from an identifier, a message, a time and a PID to
/// a <see cref="JournalEvent"/>, or null when no pattern matches (the stream counts and reports those). OpenSSH's and sudo's lines are not
/// localised, so a fixed set of patterns reads them; they are tested against the lines L0 captured from Debian 12 and 13 and Ubuntu 22.04,
/// 24.04 and 26.04 (<c>PNMTD.Tests/Fixtures/Linux/journal/</c>). OpenSSH ≥ 10 logs as <c>sshd-session</c> (and <c>sshd-auth</c>), older as
/// <c>sshd</c>; Ubuntu 26.04's sudo-rs as <c>sudo-rs</c> or <c>sudo</c>, with two spaces after the colon and its failures only as PAM's line.
/// </summary>
internal static partial class JournalLines
{
    /// <summary>The identifiers the stream reads (§5.6).</summary>
    public static readonly IReadOnlyList<string> Identifiers = ["sshd", "sshd-session", "sshd-auth", "sudo", "sudo-rs", "su", "login", "systemd-logind"];

    public static bool IsSshd(string? identifier) => identifier is "sshd" or "sshd-session" or "sshd-auth";

    public static bool IsSudo(string? identifier) => identifier is "sudo" or "sudo-rs";

    // --- sshd ---------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"^Accepted (?<method>\S+) for (?<user>.+?) from (?<addr>\S+) port (?<port>\d+)(?: ssh2)?(?:: (?<keytype>\S+) (?<fp>SHA256:[A-Za-z0-9+/=]+)(?: ID (?<certid>.+?) \(serial (?<serial>\d+)\) CA (?<catype>\S+) (?<cafp>SHA256:[A-Za-z0-9+/=]+))?)?\s*$")]
    private static partial Regex SshAccepted();

    [GeneratedRegex(@"^Failed (?<method>\S+) for (?<invalid>invalid user )?(?<user>.*?) from (?<addr>\S+) port (?<port>\d+)(?: ssh2)?(?:: (?<keytype>\S+) (?<fp>SHA256:[A-Za-z0-9+/=]+).*)?\s*$")]
    private static partial Regex SshFailed();

    [GeneratedRegex(@"^Invalid user (?<user>.*?) from (?<addr>\S+?)(?: port (?<port>\d+))?\s*$")]
    private static partial Regex SshInvalid();

    [GeneratedRegex(@"^(?:error: )?maximum authentication attempts exceeded for (?<invalid>invalid user )?(?<user>.*?) from (?<addr>\S+) port (?<port>\d+)")]
    private static partial Regex SshMaxTries();

    [GeneratedRegex(@"^Disconnected from user (?<user>.+?) (?<addr>\S+) port (?<port>\d+)\s*$")]
    private static partial Regex SshDisconnected();

    [GeneratedRegex(@"^(?:Connection closed by|Disconnected from|Connection reset by|Disconnecting) (?:(?<invalid>invalid|authenticating) user (?<user>.*?) )?(?<addr>[0-9a-fA-F.:]+) port (?<port>\d+)(?::.*?)?(?<preauth> \[preauth\])?\s*$")]
    private static partial Regex SshConnectionEnd();

    [GeneratedRegex(@"^Received disconnect from (?<addr>\S+) port (?<port>\d+):.*?(?<preauth>\[preauth\])?\s*$")]
    private static partial Regex SshReceivedDisconnect();

    [GeneratedRegex(@"^User child is on pid (?<pid>\d+)\s*$")]
    private static partial Regex SshChild();

    [GeneratedRegex(@"^Starting session: (?<type>shell|command|subsystem '(?<sub>[^']*)'|forced-command \([^)]*\) '.*')(?: on (?<tty>\S+))? for (?<user>\S+) from (?<addr>\S+) port (?<port>\d+)(?: id \d+)?\s*$")]
    private static partial Regex SshSession();

    [GeneratedRegex(@"^subsystem request for (?<sub>\S+) by user (?<user>\S+)")]
    private static partial Regex SshSubsystem();

    // --- PAM ----------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"^pam_unix\((?<service>[^:)]+):session\): session opened for user (?<user>[^(\s]+)(?:\(uid=\d+\))?(?: by .*)?$")]
    private static partial Regex PamOpened();

    [GeneratedRegex(@"^pam_unix\((?<service>[^:)]+):session\): session closed for user (?<user>\S+)\s*$")]
    private static partial Regex PamClosed();

    [GeneratedRegex(@"^pam_unix\((?<service>sudo|sudo-i):auth\): authentication failure;(?<fields>.*)$")]
    private static partial Regex PamSudoFailure();

    [GeneratedRegex(@"^pam_faillock\((?<service>[^:)]+):auth\): Consecutive login failures for user (?<user>\S+) account temporarily locked")]
    private static partial Regex Faillock();

    [GeneratedRegex(@"\bruser=(?<ruser>\S*)")]
    private static partial Regex PamRuser();

    [GeneratedRegex(@"\buser=(?<user>\S+)")]
    private static partial Regex PamUser();

    // --- su, login, logind --------------------------------------------------------------------------------------

    [GeneratedRegex(@"^(?<failed>FAILED SU )?\(to (?<target>\S+)\) (?<user>\S+) on (?<tty>\S+)\s*$")]
    private static partial Regex Su();

    [GeneratedRegex(@"^(?<failed>FAILED su|Successful su) for (?<target>\S+) by (?<user>\S+)\s*$")]
    private static partial Regex SuShadow();

    [GeneratedRegex(@"^ROOT LOGIN (?:ON|on) (?<tty>\S+)(?: FROM (?<host>\S+))?")]
    private static partial Regex RootLogin();

    [GeneratedRegex(@"^LOGIN ON (?<tty>\S+) BY (?<user>\S+)(?: FROM (?<host>\S+))?")]
    private static partial Regex UserLogin();

    [GeneratedRegex(@"^(?<kind>FAILED LOGIN|TOO MANY LOGIN TRIES) \((?<n>\d+)\) on '(?<tty>[^']*)'(?: FROM '(?<host>[^']*)')? FOR '(?<user>[^']*)', (?<reason>.*)$")]
    private static partial Regex LoginFailed();

    [GeneratedRegex(@"^New session '?(?<id>[^' ]+?)'? of user '?(?<user>[^']+?)'?(?: with class '(?<class>[^']+)' and type '(?<type>[^']+)')?\.?\s*$")]
    private static partial Regex LogindNew();

    [GeneratedRegex(@"^Removed session '?(?<id>[^' ]+?)'?\.?\s*$")]
    private static partial Regex LogindRemoved();

    /// <summary>sshd's lines that say nothing about who logged in (§5.6): the internet's noise, the daemon's own, VERBOSE's extras.</summary>
    private static readonly string[] SshdNoise =
    [
        "Connection from ", "Accepted key ", "Postponed ", "Partial ", "Server listening on", "Received signal", "Received SIGHUP", "Exiting on signal", "srclimit_penalise",
        "drop connection", "beginning MaxStartups", "exited MaxStartups", "Timeout before authentication", "error: ", "fatal: ", "warning: ",
        "banner exchange", "Unable to negotiate", "Bad protocol version", "Protocol major versions differ", "Did not receive identification string",
        "ssh_dispatch_run_fatal", "Read error from remote host", "Close session: ", "Transferred: ", "session opened for local user",
        "session closed for local user", "Changed root directory", "pam_", "PAM ", "rexec line", "Deprecated option", "debug", "Invalid user",
        "Missing privilege separation", "Set /proc/self/oom_score_adj", "Starting session: ", "Received disconnect", "Disconnecting ",
        "Connection closed by ", "Connection reset by ", "Disconnected from ", "Failed none for ", "kex_exchange_identification",
        "Accepted certificate ID", "userauth_pubkey", "Authentication refused", "Bad packet length", "Corrupted MAC", "message authentication code",
        "Unable to load host key", "Server ready", "Unsupported option", "rekeying", "Ignored ",
    ];

    /// <summary>
    /// The line's event, or null when it is a line of a login identifier that no pattern matches (or of another identifier). Pure: the
    /// same input gives the same output, and nothing is read or written.
    /// </summary>
    public static JournalEvent? Parse(string? identifier, string message, DateTimeOffset at, int? pid)
    {
        var text = message.TrimEnd('\n', '\r');
        if (IsSshd(identifier))
        {
            return Sshd(text, at, pid);
        }
        if (IsSudo(identifier))
        {
            return Sudo(text, at, pid);
        }
        return identifier switch
        {
            "su" => SuLine(text, at, pid),
            "login" => Login(text, at, pid),
            "systemd-logind" => Logind(text, at, pid),
            _ => null,
        };
    }

    private static int? Int(Group group) => group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static JournalEvent? Sshd(string text, DateTimeOffset at, int? pid)
    {
        Match m;
        if ((m = SshAccepted().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.SshAccepted, at, pid)
            {
                User = m.Groups["user"].Value,
                Method = m.Groups["method"].Value,
                Address = m.Groups["addr"].Value,
                Port = Int(m.Groups["port"]),
                KeyType = m.Groups["keytype"].Success ? m.Groups["keytype"].Value : null,
                Fingerprint = m.Groups["fp"].Success ? m.Groups["fp"].Value : null,
                Certificate = m.Groups["certid"].Success
                    ? $"certificate {m.Groups["certid"].Value} (serial {m.Groups["serial"].Value}), CA {m.Groups["catype"].Value} {m.Groups["cafp"].Value}"
                    : null,
            };
        }
        if ((m = SshFailed().Match(text)).Success)
        {
            var method = m.Groups["method"].Value;
            if (method == "none" && !m.Groups["invalid"].Success)
            {
                // A client's first probe ("none"), before it offers anything: no failure of its own.
                return new JournalEvent(JournalEventKind.Ignored, at, pid);
            }
            return new JournalEvent(JournalEventKind.SshFailed, at, pid)
            {
                User = m.Groups["user"].Value,
                Method = method,
                InvalidUser = m.Groups["invalid"].Success,
                Address = m.Groups["addr"].Value,
                Port = Int(m.Groups["port"]),
                KeyType = m.Groups["keytype"].Success ? m.Groups["keytype"].Value : null,
                Fingerprint = m.Groups["fp"].Success ? m.Groups["fp"].Value : null,
            };
        }
        if ((m = SshInvalid().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.SshInvalidUser, at, pid)
            {
                User = m.Groups["user"].Value,
                InvalidUser = true,
                Address = m.Groups["addr"].Value,
                Port = Int(m.Groups["port"]),
            };
        }
        if ((m = SshMaxTries().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.SshMaxTries, at, pid)
            {
                User = m.Groups["user"].Value,
                InvalidUser = m.Groups["invalid"].Success,
                Address = m.Groups["addr"].Value,
                Port = Int(m.Groups["port"]),
            };
        }
        if ((m = SshDisconnected().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.SshDisconnected, at, pid) { User = m.Groups["user"].Value, Address = m.Groups["addr"].Value, Port = Int(m.Groups["port"]) };
        }
        if ((m = SshConnectionEnd().Match(text)).Success || (m = SshReceivedDisconnect().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.SshClosed, at, pid)
            {
                User = m.Groups["user"].Success && m.Groups["user"].Length > 0 ? m.Groups["user"].Value : null,
                InvalidUser = m.Groups["invalid"].Success && m.Groups["invalid"].Value == "invalid",
                Address = m.Groups["addr"].Value,
                Port = Int(m.Groups["port"]),
                Preauth = m.Groups["preauth"].Success && m.Groups["preauth"].Length > 0,
            };
        }
        if ((m = SshChild().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.SshChild, at, pid) { ChildPid = Int(m.Groups["pid"]) };
        }
        if ((m = SshSession().Match(text)).Success)
        {
            var type = m.Groups["type"].Value;
            return new JournalEvent(JournalEventKind.SshSession, at, pid)
            {
                User = m.Groups["user"].Value,
                Address = m.Groups["addr"].Value,
                Port = Int(m.Groups["port"]),
                Tty = m.Groups["tty"].Success ? m.Groups["tty"].Value : null,
                Session = type.StartsWith("subsystem", StringComparison.Ordinal)
                    ? m.Groups["sub"].Value is "sftp" or "internal-sftp" ? "sftp" : $"subsystem {m.Groups["sub"].Value}"
                    : type.StartsWith("forced-command", StringComparison.Ordinal) ? "forced" : type,
            };
        }
        if ((m = SshSubsystem().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.SshSubsystem, at, pid)
            {
                User = m.Groups["user"].Value,
                Session = m.Groups["sub"].Value is "sftp" or "internal-sftp" ? "sftp" : $"subsystem {m.Groups["sub"].Value}",
            };
        }
        if (Pam(text, at, pid) is { } pam)
        {
            return pam;
        }
        return SshdNoise.Any(n => text.StartsWith(n, StringComparison.Ordinal)) ? new JournalEvent(JournalEventKind.Ignored, at, pid) : null;
    }

    /// <summary>The PAM lines every identifier shares: sessions opened and closed, sudo's failures, pam_faillock; other pam_ lines are ignored.</summary>
    private static JournalEvent? Pam(string text, DateTimeOffset at, int? pid)
    {
        Match m;
        if ((m = PamOpened().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.SessionOpened, at, pid) { Service = m.Groups["service"].Value, User = m.Groups["user"].Value };
        }
        if ((m = PamClosed().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.SessionClosed, at, pid) { Service = m.Groups["service"].Value, User = m.Groups["user"].Value };
        }
        if ((m = Faillock().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.Lockout, at, pid) { Service = m.Groups["service"].Value, User = m.Groups["user"].Value, Reason = "pam_faillock" };
        }
        if ((m = PamSudoFailure().Match(text)).Success)
        {
            var fields = m.Groups["fields"].Value;
            var ruser = PamRuser().Match(fields) is { Success: true } r && r.Groups["ruser"].Length > 0 ? r.Groups["ruser"].Value : null;
            var user = ruser ?? (PamUser().Match(fields) is { Success: true } u ? u.Groups["user"].Value : null);
            return user is null
                ? new JournalEvent(JournalEventKind.Ignored, at, pid)
                : new JournalEvent(JournalEventKind.SudoAuthFailure, at, pid) { Service = m.Groups["service"].Value, User = user };
        }
        return text.StartsWith("pam_", StringComparison.Ordinal) || text.StartsWith("PAM ", StringComparison.Ordinal)
            ? new JournalEvent(JournalEventKind.Ignored, at, pid)
            : null;
    }

    /// <summary>
    /// sudo's summary line, <c>user : [reason ; ][TTY=… ; ]PWD=… ; USER=… ; [GROUP=… ; ]COMMAND=…</c> (sudo-rs: two spaces after the colon),
    /// and the PAM lines.
    /// </summary>
    private static JournalEvent? Sudo(string text, DateTimeOffset at, int? pid)
    {
        var line = text.Trim();
        var colon = line.IndexOf(" : ", StringComparison.Ordinal);
        if (colon > 0 && line.Contains("COMMAND=", StringComparison.Ordinal) && !line[..colon].Contains(' '))
        {
            var user = line[..colon];
            var rest = line[(colon + 3)..].Trim();
            string? reason = null, tty = null, target = null, command = null;
            var parts = rest.Split(" ; ");
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (part.StartsWith("COMMAND=", StringComparison.Ordinal))
                {
                    // The command is last and may itself hold " ; ".
                    command = string.Join(" ; ", parts.Skip(i))["COMMAND=".Length..].Trim();
                    break;
                }
                if (part.StartsWith("TTY=", StringComparison.Ordinal))
                {
                    tty = part[4..];
                }
                else if (part.StartsWith("USER=", StringComparison.Ordinal))
                {
                    target = part[5..];
                }
                else if (part.Contains('=') && Regex.IsMatch(part, "^[A-Z]+="))
                {
                    // PWD, GROUP, ENV, TSID, CHROOT, CWD.
                }
                else if (reason is null && tty is null && target is null)
                {
                    reason = part;
                }
            }
            if (reason is null)
            {
                return new JournalEvent(JournalEventKind.SudoAccepted, at, pid) { User = user, Target = target, Tty = tty is "unknown" ? null : tty, Command = command };
            }
            var attempts = Regex.Match(reason, @"^(\d+) incorrect password attempts?") is { Success: true } wrong ? int.Parse(wrong.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            return new JournalEvent(JournalEventKind.SudoFailed, at, pid)
            {
                User = user,
                Target = target,
                Tty = tty is "unknown" ? null : tty,
                Command = command,
                Attempts = Math.Max(1, attempts),
                Reason = attempts > 0 ? LinuxLogins.WrongPassword
                    : reason.Contains("NOT in sudoers", StringComparison.Ordinal) ? LinuxLogins.NotInSudoers
                    : reason.Contains("command not allowed", StringComparison.Ordinal) ? "command not allowed"
                    : reason.Contains("password is required", StringComparison.Ordinal) ? "password required"
                    : reason.Contains("NOT authorized on host", StringComparison.Ordinal) ? "not allowed on this host"
                    : reason.ToLowerInvariant(),
            };
        }
        return Pam(text, at, pid);
    }

    private static JournalEvent? SuLine(string text, DateTimeOffset at, int? pid)
    {
        Match m;
        if ((m = Su().Match(text)).Success)
        {
            return new JournalEvent(m.Groups["failed"].Success ? JournalEventKind.SuFailed : JournalEventKind.SuAccepted, at, pid)
            {
                User = m.Groups["user"].Value,
                Target = m.Groups["target"].Value,
                Tty = m.Groups["tty"].Value is "none" or "???" ? null : m.Groups["tty"].Value,
                Reason = m.Groups["failed"].Success ? LinuxLogins.WrongPassword : null,
            };
        }
        if ((m = SuShadow().Match(text)).Success)
        {
            var failed = m.Groups["failed"].Value.StartsWith("FAILED", StringComparison.Ordinal);
            return new JournalEvent(failed ? JournalEventKind.SuFailed : JournalEventKind.SuAccepted, at, pid)
            {
                User = m.Groups["user"].Value,
                Target = m.Groups["target"].Value,
                Reason = failed ? LinuxLogins.WrongPassword : null,
            };
        }
        if (Regex.IsMatch(text, @"^[+-] \S+ \S+:\S+$"))
        {
            // shadow's su: "+ pts/0 deploy:root", the same as the line above.
            return new JournalEvent(JournalEventKind.Ignored, at, pid);
        }
        return Pam(text, at, pid);
    }

    private static JournalEvent? Login(string text, DateTimeOffset at, int? pid)
    {
        Match m;
        if ((m = RootLogin().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.ConsoleTty, at, pid)
            {
                User = "root",
                Tty = m.Groups["tty"].Value,
                Address = m.Groups["host"].Success ? m.Groups["host"].Value : null,
            };
        }
        if ((m = UserLogin().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.ConsoleTty, at, pid)
            {
                User = m.Groups["user"].Value,
                Tty = m.Groups["tty"].Value,
                Address = m.Groups["host"].Success ? m.Groups["host"].Value : null,
            };
        }
        if ((m = LoginFailed().Match(text)).Success)
        {
            var user = m.Groups["user"].Value;
            var reason = m.Groups["reason"].Value;
            var invalid = user == "UNKNOWN" || reason.Contains("not known", StringComparison.OrdinalIgnoreCase);
            var tooMany = m.Groups["kind"].Value.StartsWith("TOO MANY", StringComparison.Ordinal);
            return new JournalEvent(tooMany ? JournalEventKind.Lockout : JournalEventKind.LoginFailed, at, pid)
            {
                User = invalid ? null : user,
                InvalidUser = invalid,
                Tty = m.Groups["tty"].Value.Replace("/dev/", "", StringComparison.Ordinal),
                Address = m.Groups["host"].Success && m.Groups["host"].Length > 0 ? m.Groups["host"].Value : null,
                Service = "login",
                Reason = tooMany ? "too many tries"
                    : invalid ? LinuxLogins.InvalidUser
                    : reason.Contains("Authentication failure", StringComparison.OrdinalIgnoreCase) ? LinuxLogins.WrongPassword
                    : reason.ToLowerInvariant(),
            };
        }
        if (text.StartsWith("LOGIN ", StringComparison.Ordinal) || text.StartsWith("DIALUP ", StringComparison.Ordinal))
        {
            return new JournalEvent(JournalEventKind.Ignored, at, pid);
        }
        return Pam(text, at, pid);
    }

    private static JournalEvent Logind(string text, DateTimeOffset at, int? pid)
    {
        Match m;
        if ((m = LogindNew().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.LogindNew, at, pid)
            {
                Session = m.Groups["id"].Value,
                User = m.Groups["user"].Value,
                Class = m.Groups["class"].Success ? m.Groups["class"].Value : null,
            };
        }
        if ((m = LogindRemoved().Match(text)).Success)
        {
            return new JournalEvent(JournalEventKind.LogindRemoved, at, pid) { Session = m.Groups["id"].Value };
        }
        // Buttons, seats, power keys, "Session c5 logged out": logind's other lines are not about logins.
        return new JournalEvent(JournalEventKind.Ignored, at, pid);
    }

    // --- journalctl's JSON --------------------------------------------------------------------------------------

    /// <summary>
    /// One line of <c>journalctl -o json</c>, or null when it isn't an entry. A field journald couldn't keep as UTF-8 text (a byte in another
    /// encoding, a control character) is an array of numbers: decoded as UTF-8 with replacements. A field with several values is an array
    /// of strings: the first.
    /// </summary>
    public static JournalRecord? ParseJson(string line)
    {
        if (line.Length == 0 || line[0] != '{')
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var cursor = Field(root, "__CURSOR");
            if (cursor is null)
            {
                return null;
            }
            var at = long.TryParse(Field(root, "__REALTIME_TIMESTAMP"), NumberStyles.None, CultureInfo.InvariantCulture, out var us)
                ? DateTimeOffset.FromUnixTimeMilliseconds(us / 1000)
                : DateTimeOffset.UtcNow;
            return new JournalRecord(cursor, at, Field(root, "SYSLOG_IDENTIFIER"), Field(root, "MESSAGE") ?? "",
                int.TryParse(Field(root, "_PID"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : null,
                int.TryParse(Field(root, "PRIORITY"), NumberStyles.None, CultureInfo.InvariantCulture, out var priority) ? priority : null,
                Field(root, "UNIT") ?? Field(root, "_SYSTEMD_UNIT"), Field(root, "_BOOT_ID"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A field as text: a string, the first of several, or bytes decoded as UTF-8.</summary>
    public static string? Field(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Array when value.GetArrayLength() > 0 && value[0].ValueKind == JsonValueKind.Number:
                var bytes = new byte[value.GetArrayLength()];
                var i = 0;
                foreach (var b in value.EnumerateArray())
                {
                    bytes[i++] = b.TryGetByte(out var x) ? x : (byte)'?';
                }
                return Encoding.UTF8.GetString(bytes);
            case JsonValueKind.Array when value.GetArrayLength() > 0:
                return Field(value[0]);
            default:
                return null;
        }
    }

    private static string? Field(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Array => Encoding.UTF8.GetString(value.EnumerateArray().Select(b => b.TryGetByte(out var x) ? x : (byte)'?').ToArray()),
        _ => null,
    };
}
