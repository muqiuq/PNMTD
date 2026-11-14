// The parsers are pure and run anywhere; the Linux agent's assembly is marked Linux-only for its native calls.
#pragma warning disable CA1416

using PNMTD.Lib.Agent;
using PNMTD.Sensor.Linux.Access;

namespace PNMTD.Tests.Linux;

/// <summary>
/// The Linux agent's journal-line parsers (docs/linux-sensor-plan.md §5.6, §14), table-driven over the lines L0 captured from Debian 12 and 13
/// and Ubuntu 22.04, 24.04 and 26.04 (<c>Fixtures/Linux/journal/</c>, the same scenario on each): every line of a login source is matched, and
/// the scenario's events are read: key logins with their fingerprints, password logins, failures, invalid users, SFTP where sshd logs at
/// VERBOSE, sudo's and sudo-rs' successes and failures, su, logind. Then the pairing and aggregation the stream builds on them, and console
/// logins, written by hand from login(1)'s and pam_unix's formats (a container has no getty).
/// </summary>
[TestClass]
public sealed class JournalLinesTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static List<JournalRecord> Records(string file) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Linux", "journal", file))
            .Select(JournalLines.ParseJson).Where(r => r is not null).Select(r => r!).ToList();

    private static List<JournalEvent> Events(string file) =>
        Records(file).Select(r => JournalLines.Parse(r.Identifier, r.Message, r.At, r.Pid)).Where(e => e is not null).Select(e => e!).ToList();

    private static JournalEvent? Line(string identifier, string message, int pid = 100) => JournalLines.Parse(identifier, message, At, pid);

    [TestMethod]
    [DataRow("debian-12.jsonl")]
    [DataRow("debian-13.jsonl")]
    [DataRow("debian-13-info.jsonl")]
    [DataRow("ubuntu-22.04.jsonl")]
    [DataRow("ubuntu-24.04.jsonl")]
    [DataRow("ubuntu-26.04.jsonl")]
    [DataRow("ubuntu-26.04-sudo-rs.jsonl")]
    public void Every_line_of_a_login_source_is_matched(string file)
    {
        var records = Records(file);
        Assert.IsTrue(records.Count > 0, $"{file} has entries");
        var unmatched = records.Where(r => JournalLines.Parse(r.Identifier, r.Message, r.At, r.Pid) is null).Select(r => $"{r.Identifier}: {r.Message}").ToList();

        Assert.AreEqual(0, unmatched.Count, string.Join("\n", unmatched));
    }

    [TestMethod]
    [DataRow("debian-12.jsonl", "SHA256:20DQsXE1FxGLKxG9dAAGQqeyfect76rkV6tXMIele+s", "SHA256:V/1oYAPw5Rkj5L0MVHUUPcP9wfbL+45Dtf2eeNgtrc0", "sshd", true)]
    [DataRow("debian-13.jsonl", "SHA256:EkSAkEyiW3WmQCGIpAllmZRm+TtUnXAxgBkGlY1ICMs", "SHA256:nEODoZ2aWaW/HquTAmgBBxS8IA87iONsU4lbE/eVwgc", "sshd-session", true)]
    [DataRow("debian-13-info.jsonl", "SHA256:R1tlb5nE9fSa1FeNc0j5MsTY7GKCT0QpswwApwaQnyA", null, "sshd-session", false)]
    [DataRow("ubuntu-22.04.jsonl", "SHA256:fV08N0GIsOeZJLRd6shqu4dopW4Mj1cpx1SjzaUm/sw", "SHA256:lq91blRm5DX5GdO8788gFOjo8IxCtj+dNnEtvY/0Mxw", "sshd", true)]
    [DataRow("ubuntu-24.04.jsonl", "SHA256:HKaKj1K1nI0mPdgmJQs5sE9hhSDvrr5OYCqAFgPSKn4", "SHA256:RsLRlKulfVBFpP70QtvGYZdGfeuMEb4r+maGWPr+PNQ", "sshd", true)]
    [DataRow("ubuntu-26.04.jsonl", "SHA256:UyFwG816LIpoM0x4L3oRLsXVCfVjxQ3FnZDWQ48tgDo", "SHA256:oBim20rOiRlDs81MxG5ZJP659uUwneNlspRMQ37rVkk", "sshd-session", true)]
    public void The_ssh_scenario_is_read(string file, string acceptedKey, string? refusedKey, string identifier, bool verbose)
    {
        var records = Records(file);
        var events = Events(file);

        Assert.IsTrue(records.Any(r => r.Identifier == identifier && r.Message.StartsWith("Accepted", StringComparison.Ordinal)), $"logins logged as {identifier}");
        foreach (var user in new[] { "deploy", "root" })
        {
            var keys = events.Where(e => e.Kind == JournalEventKind.SshAccepted && e.Method == "publickey" && e.User == user).ToList();
            Assert.AreEqual(user == "deploy" ? 2 : 1, keys.Count, $"{user}'s key logins (deploy's second is the SFTP session)");
            Assert.IsTrue(keys.All(k => k.Fingerprint == acceptedKey && k.KeyType == "ED25519" && k.Address == "127.0.0.1" && k.Port is not null));
            Assert.IsTrue(events.Any(e => e.Kind == JournalEventKind.SshAccepted && e.Method == "password" && e.User == user), $"{user}'s password login");
            Assert.IsTrue(events.Any(e => e.Kind == JournalEventKind.SshDisconnected && e.User == user), $"{user}'s session end");
        }
        var wrong = events.Single(e => e.Kind == JournalEventKind.SshFailed && e.Method == "password" && !e.InvalidUser);
        Assert.AreEqual("deploy", wrong.User);
        var invalid = events.Where(e => e.Kind == JournalEventKind.SshInvalidUser).Select(e => e.User).ToList();
        CollectionAssert.AreEqual(new[] { "admin", "oracle" }, invalid);
        Assert.IsTrue(events.Any(e => e.Kind == JournalEventKind.SshFailed && e.InvalidUser && e.User == "admin"));
        Assert.IsTrue(events.Any(e => e.Kind == JournalEventKind.SshClosed && e.Preauth && e.InvalidUser && e.User == "oracle"));
        Assert.IsTrue(events.Any(e => e.Kind == JournalEventKind.SessionClosed && e.Service == "sshd"));

        var refused = events.Where(e => e.Kind == JournalEventKind.SshFailed && e.Method == "publickey").ToList();
        var sftp = events.Where(e => e.Kind == JournalEventKind.SshSession && e.Session == "sftp").ToList();
        if (verbose)
        {
            Assert.AreEqual(refusedKey, refused.Single().Fingerprint);
            Assert.AreEqual("RSA", refused.Single().KeyType);
            Assert.AreEqual("deploy", sftp.Single().User);
            Assert.IsTrue(events.Any(e => e.Kind == JournalEventKind.SshSession && e.Session == "command"));
            Assert.IsTrue(events.Any(e => e.Kind == JournalEventKind.SshChild && e.ChildPid is not null));
        }
        else
        {
            // At INFO an SFTP session is a session like any other, and refused keys aren't logged.
            Assert.AreEqual(0, refused.Count);
            Assert.AreEqual(0, sftp.Count);
        }
    }

    [TestMethod]
    [DataRow("debian-12.jsonl", true)]
    [DataRow("debian-13.jsonl", true)]
    [DataRow("ubuntu-22.04.jsonl", true)]
    [DataRow("ubuntu-24.04.jsonl", true)]
    [DataRow("ubuntu-26.04.jsonl", false)]
    public void Sudo_and_su_are_read(string file, bool classicSudo)
    {
        var events = Events(file);

        var sudo = events.Where(e => e.Kind == JournalEventKind.SudoAccepted).ToList();
        CollectionAssert.AreEqual(new[] { "/usr/bin/true", "/usr/bin/ls /root" }, sudo.Select(e => e.Command).ToList());
        Assert.IsTrue(sudo.All(e => e.User == "deploy" && e.Target == "root" && e.Tty is null));
        Assert.AreEqual("deploy", events.Single(e => e.Kind == JournalEventKind.SudoAuthFailure).User);
        var failed = events.Where(e => e.Kind == JournalEventKind.SudoFailed).ToList();
        if (classicSudo)
        {
            var password = failed.Single(e => e.Reason == LinuxLogins.WrongPassword);
            Assert.AreEqual(("deploy", "root", 1, "/usr/bin/true"), (password.User, password.Target, password.Attempts, password.Command));
            Assert.AreEqual("guest", failed.Single(e => e.Reason == LinuxLogins.NotInSudoers).User);
        }
        else
        {
            // sudo-rs (Ubuntu 26.04's sudo) logs failures only through PAM, and "not in sudoers" not at all.
            Assert.AreEqual(0, failed.Count);
        }

        var su = events.Where(e => e.Kind == JournalEventKind.SuAccepted).ToList();
        Assert.IsTrue(su.Count >= 5);
        Assert.IsTrue(su.All(e => e.User == "root" && e.Target is "deploy" or "guest"));
        Assert.IsTrue(su.Any(e => e.Tty == "pts/0") && su.Any(e => e.Tty is null), "on a terminal and without one (\"on none\")");
        var suFailed = events.Where(e => e.Kind == JournalEventKind.SuFailed).ToList();
        Assert.AreEqual(2, suFailed.Count);
        Assert.IsTrue(suFailed.All(e => e.User == "deploy" && e.Target == "root" && e.Tty == "pts/1" && e.Reason == LinuxLogins.WrongPassword));
        Assert.IsTrue(events.Any(e => e.Kind == JournalEventKind.SessionClosed && e.Service == "su-l"));

        Assert.IsTrue(events.Any(e => e.Kind == JournalEventKind.LogindNew && e.User == "deploy"));
        Assert.IsTrue(events.Any(e => e.Kind == JournalEventKind.LogindRemoved));
    }

    [TestMethod]
    public void Sudo_rs_is_read()
    {
        var events = Events("ubuntu-26.04-sudo-rs.jsonl");

        var accepted = events.Single(e => e.Kind == JournalEventKind.SudoAccepted);
        Assert.AreEqual(("deploy", "root", "/usr/bin/true"), (accepted.User, accepted.Target, accepted.Command));
        Assert.AreEqual("deploy", events.Single(e => e.Kind == JournalEventKind.SudoAuthFailure).User);
        Assert.AreEqual(2, events.Count(e => e.Kind is JournalEventKind.SessionOpened or JournalEventKind.SessionClosed));
    }

    [TestMethod]
    public void Ubuntu_26_04_logind_names_its_class()
    {
        var sessions = Events("ubuntu-26.04.jsonl").Where(e => e.Kind == JournalEventKind.LogindNew).ToList();

        Assert.IsTrue(sessions.Any(e => e.Class == "manager" && e.User == "deploy" && e.Session!.StartsWith('c')));
        Assert.IsTrue(sessions.Any(e => e.Class == "user" && e.User == "root"));
        Assert.AreEqual(("3", "root", null), (Line("systemd-logind", "New session 3 of user root.")!.Session, Line("systemd-logind", "New session 3 of user root.")!.User,
            Line("systemd-logind", "New session 3 of user root.")!.Class));
        Assert.AreEqual("first.last", Line("systemd-logind", "New session c7 of user first.last.")!.User);
    }

    // --- lines written by hand: console logins and what the scenario didn't produce ---------------------------------

    [TestMethod]
    public void Console_logins_are_read()
    {
        var opened = Line("login", "pam_unix(login:session): session opened for user root(uid=0) by LOGIN(uid=0)");
        Assert.AreEqual((JournalEventKind.SessionOpened, "login", "root"), (opened!.Kind, opened.Service, opened.User));
        var root = Line("login", "ROOT LOGIN ON tty1");
        Assert.AreEqual((JournalEventKind.ConsoleTty, "root", "tty1"), (root!.Kind, root.User, root.Tty));
        var user = Line("login", "LOGIN ON tty2 BY deploy");
        Assert.AreEqual(("deploy", "tty2"), (user!.User, user.Tty));
        var failed = Line("login", "FAILED LOGIN (1) on '/dev/tty1' FOR 'root', Authentication failure");
        Assert.AreEqual((JournalEventKind.LoginFailed, "root", "tty1", LinuxLogins.WrongPassword, false), (failed!.Kind, failed.User, failed.Tty, failed.Reason, failed.InvalidUser));
        var unknown = Line("login", "FAILED LOGIN (2) on '/dev/tty1' FOR 'UNKNOWN', User not known to the underlying authentication module");
        Assert.IsTrue(unknown!.InvalidUser);
        Assert.IsNull(unknown.User);
        var remote = Line("login", "FAILED LOGIN (1) on '/dev/pts/3' FROM '10.0.0.9' FOR 'deploy', Authentication failure");
        Assert.AreEqual(("10.0.0.9", "pts/3"), (remote!.Address, remote.Tty));
        Assert.AreEqual(JournalEventKind.Lockout, Line("login", "TOO MANY LOGIN TRIES (5) on '/dev/tty1' FOR 'root', Authentication failure")!.Kind);
        Assert.AreEqual(JournalEventKind.SessionClosed, Line("login", "pam_unix(login:session): session closed for user root")!.Kind);
        Assert.AreEqual(JournalEventKind.Ignored, Line("login", "pam_unix(login:auth): authentication failure; logname=LOGIN uid=0 euid=0 tty=/dev/tty1 ruser= rhost=  user=root")!.Kind);
    }

    [TestMethod]
    public void Certificates_attempts_and_lockouts_are_read()
    {
        var certificate = Line("sshd", "Accepted publickey for root from 10.0.4.17 port 50122 ssh2: ED25519-CERT SHA256:Kq1f9hJ0x0mRk8xPZ3oWJ6gW5p0rPj5n8l2pHq0rS2A ID jdoe@corp (serial 42) CA ED25519 SHA256:abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ");
        Assert.AreEqual(("ED25519-CERT", "SHA256:Kq1f9hJ0x0mRk8xPZ3oWJ6gW5p0rPj5n8l2pHq0rS2A"), (certificate!.KeyType, certificate.Fingerprint));
        StringAssert.Contains(certificate.Certificate, "jdoe@corp");
        StringAssert.Contains(certificate.Certificate, "SHA256:abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ");
        var keyboard = Line("sshd", "Failed keyboard-interactive/pam for invalid user oracle from 203.0.113.9 port 4242 ssh2");
        Assert.AreEqual(("keyboard-interactive/pam", true, "oracle", "203.0.113.9"), (keyboard!.Method, keyboard.InvalidUser, keyboard.User, keyboard.Address));
        var max = Line("sshd-session", "error: maximum authentication attempts exceeded for root from 203.0.113.9 port 4243 ssh2 [preauth]");
        Assert.AreEqual((JournalEventKind.SshMaxTries, "root", false), (max!.Kind, max.User, max.InvalidUser));
        var faillock = Line("sshd", "pam_faillock(sshd:auth): Consecutive login failures for user deploy account temporarily locked");
        Assert.AreEqual((JournalEventKind.Lockout, "deploy", "sshd"), (faillock!.Kind, faillock.User, faillock.Service));
        var subsystem = Line("sshd", "subsystem request for sftp by user backup");
        Assert.AreEqual((JournalEventKind.SshSubsystem, "sftp", "backup"), (subsystem!.Kind, subsystem.Session, subsystem.User));
        var forced = Line("sshd", "Starting session: forced-command (key-option) 'rsync --server -vlogDtpre.iLsfxC . /backup' for root from 10.0.0.5 port 40000 id 0");
        Assert.AreEqual(("forced", "root"), (forced!.Session, forced.User));
        Assert.IsNull(forced.Command, "a forced command's text is never kept");
        Assert.AreEqual("pts/0", Line("sshd", "Starting session: shell on pts/0 for root from 10.0.0.5 port 40001 id 0")!.Tty);
        Assert.AreEqual(JournalEventKind.Ignored, Line("sshd", "Failed none for root from 10.0.0.5 port 40002 ssh2")!.Kind);
        Assert.IsTrue(Line("sshd", "Failed none for invalid user scanner from 198.51.100.1 port 5 ssh2")!.InvalidUser);
        Assert.AreEqual("203.0.113.7", Line("sshd", "Invalid user ubnt from 203.0.113.7")!.Address);
        var sudoTty = Line("sudo", "    root : TTY=pts/0 ; PWD=/root ; USER=deploy ; COMMAND=/bin/bash -c echo a ; echo b");
        Assert.AreEqual(("root", "deploy", "pts/0", "/bin/bash -c echo a ; echo b"), (sudoTty!.User, sudoTty.Target, sudoTty.Tty, sudoTty.Command));
        var attempts = Line("sudo", "deploy : 3 incorrect password attempts ; TTY=pts/1 ; PWD=/home/deploy ; USER=root ; COMMAND=/usr/bin/apt update");
        Assert.AreEqual((JournalEventKind.SudoFailed, 3, LinuxLogins.WrongPassword), (attempts!.Kind, attempts.Attempts, attempts.Reason));
        Assert.AreEqual("command not allowed", Line("sudo", "deploy : command not allowed ; TTY=pts/1 ; PWD=/ ; USER=root ; COMMAND=/bin/sh")!.Reason);
        Assert.IsNull(Line("sshd", "Something sshd never said before"), "an unknown line is unmatched, so the stream counts it");
        Assert.IsNull(Line("cron", "pam_unix(cron:session): session opened for user root(uid=0) by (uid=0)"), "other identifiers are not login sources");
    }

    [TestMethod]
    public void Journalctls_json_is_read_with_bytes_and_several_values()
    {
        var bytes = JournalLines.ParseJson("""{"__CURSOR":"s=1;i=2","__REALTIME_TIMESTAMP":"1794620157800675","_PID":"42","SYSLOG_IDENTIFIER":"sshd","PRIORITY":"6","MESSAGE":[73,110,118,97,108,105,100,32,117,115,101,114,32,106,246,114,103,32,102,114,111,109,32,49,46,50,46,51,46,52,32,112,111,114,116,32,53]}""");
        Assert.IsNotNull(bytes);
        Assert.AreEqual(("s=1;i=2", 42, "sshd", 6), (bytes.Cursor, bytes.Pid, bytes.Identifier, bytes.Priority));
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1794620157800), bytes.At);
        StringAssert.StartsWith(bytes.Message, "Invalid user j");
        var invalid = JournalLines.Parse(bytes.Identifier, bytes.Message, bytes.At, bytes.Pid);
        Assert.AreEqual(("1.2.3.4", 5), (invalid!.Address, invalid.Port));
        var several = JournalLines.ParseJson("""{"__CURSOR":"c","MESSAGE":"m","SYSLOG_IDENTIFIER":["sudo","other"]}""");
        Assert.AreEqual("sudo", several!.Identifier);
        Assert.IsNull(JournalLines.ParseJson("-- No entries --"));
    }

    // --- the records the stream makes of them (JournalLogins) ------------------------------------------------------

    private static List<LoginRecord> Logins(IEnumerable<JournalRecord> records, bool sudoCommands = false)
    {
        var logins = new JournalLogins(user => user is "root" or "deploy", () => sudoCommands);
        foreach (var record in records)
        {
            if (JournalLines.Parse(record.Identifier, record.Message, record.At, record.Pid) is { } e)
            {
                logins.Handle(e, record.Cursor);
            }
        }
        logins.Flush(DateTimeOffset.MaxValue);
        return logins.Take(int.MaxValue);
    }

    [TestMethod]
    [DataRow("debian-12.jsonl", true)]
    [DataRow("debian-13.jsonl", true)]
    [DataRow("debian-13-info.jsonl", false)]
    [DataRow("ubuntu-22.04.jsonl", true)]
    [DataRow("ubuntu-24.04.jsonl", true)]
    [DataRow("ubuntu-26.04.jsonl", true)]
    public void The_scenario_becomes_records(string file, bool verbose)
    {
        var records = Logins(Records(file));

        Assert.IsTrue(records.All(r => r.Channel == LinuxLogins.Channel));
        Assert.AreEqual(records.Count, records.Select(r => r.RecordId).Distinct().Count(), "one record id each");
        var sessions = records.Where(r => r.Kind == LogonKind.Ssh && r.Outcome == LogonOutcome.Success).ToList();
        var keyLogins = sessions.Where(r => r.AuthPackage == "publickey").ToList();
        Assert.IsTrue(keyLogins.All(r => r.KeyFingerprint is not null && r.LogonId is not null && r.SourceAddress == "127.0.0.1"));
        Assert.IsTrue(keyLogins.Any(r => r.User == "root" && r.IsAdmin));
        Assert.IsTrue(sessions.Any(r => r.AuthPackage == "password" && r.User == "deploy"));
        Assert.IsTrue(sessions.All(r => r.LoggedOffAt is not null && r.LoggedOffAt >= r.At), "every session's end is paired");
        var sftp = records.Where(r => r.Kind == LogonKind.Sftp).ToList();
        Assert.AreEqual(verbose ? 1 : 0, sftp.Count);
        Assert.AreEqual(verbose ? 2 : 3, keyLogins.Count, "the SFTP session is a file transfer where sshd says so");

        var invalid = records.Single(r => r.User == LinuxLogins.InvalidUser);
        Assert.AreEqual((LogonOutcome.Failure, LinuxLogins.InvalidUser, "127.0.0.1", false), (invalid.Outcome, invalid.FailureReason, invalid.SourceAddress, invalid.IsAdmin));
        CollectionAssert.AreEqual(new[] { "admin", "oracle" }, invalid.TriedUsers!.ToList());
        Assert.AreEqual(2, invalid.Count, "two connections; admin's password attempt is its connection's first");

        var wrong = records.Single(r => r.Kind == LogonKind.Ssh && r.FailureReason == LinuxLogins.WrongPassword);
        Assert.AreEqual(("deploy", 1), (wrong.User, wrong.Count));
        var refused = records.Where(r => r.FailureReason == LinuxLogins.KeyRefused).ToList();
        Assert.AreEqual(verbose ? 1 : 0, refused.Count, "the refused key's connection never logged in");
        if (verbose)
        {
            Assert.IsNotNull(refused[0].KeyFingerprint);
        }

        var sudoFailures = records.Where(r => r.Kind == LogonKind.Sudo && r.FailureReason == LinuxLogins.WrongPassword).ToList();
        Assert.AreEqual(1, sudoFailures.Sum(r => r.Count), "sudo's PAM line and its summary are one wrong password");
        var elevations = records.Where(r => r.Kind is LogonKind.Sudo or LogonKind.Su && r.Outcome == LogonOutcome.Success).ToList();
        Assert.IsTrue(elevations.Any(r => r.Kind == LogonKind.Sudo && r.User == "deploy" && r.TargetUser == "root" && r.Elevated == true && r.Command is null),
            "sudo's command only with collect.sudo_commands");
        Assert.IsTrue(elevations.Where(r => r.Kind == LogonKind.Su).All(r => r.User == "root" && r.LoggedOffAt is not null));
        Assert.AreEqual(2, records.Where(r => r.Kind == LogonKind.Su && r.Outcome == LogonOutcome.Failure).Sum(r => r.Count));
    }

    [TestMethod]
    public void Sudo_commands_come_with_the_right()
    {
        var records = Logins(Records("debian-12.jsonl"), sudoCommands: true);

        CollectionAssert.AreEquivalent(new[] { "/usr/bin/true", "/usr/bin/ls /root" },
            records.Where(r => r.Kind == LogonKind.Sudo && r.Outcome == LogonOutcome.Success).Select(r => r.Command).ToList());
    }

    [TestMethod]
    public void Sudo_rs_failures_count_once()
    {
        var records = Logins(Records("ubuntu-26.04-sudo-rs.jsonl"));

        Assert.AreEqual(("deploy", "root"), records.Where(r => r.Outcome == LogonOutcome.Success).Select(r => (r.User, r.TargetUser!)).Single());
        Assert.AreEqual(1, records.Where(r => r.Outcome == LogonOutcome.Failure).Sum(r => r.Count));
    }

    [TestMethod]
    public void Three_wrong_sudo_passwords_are_three()
    {
        var logins = new JournalLogins(_ => false, () => false);
        foreach (var i in Enumerable.Range(0, 3))
        {
            logins.Handle(Line("sudo", "pam_unix(sudo:auth): authentication failure; logname= uid=1000 euid=0 tty=/dev/pts/1 ruser=deploy rhost=  user=deploy", 77)!, $"c{i}");
        }
        logins.Handle(Line("sudo", "deploy : 3 incorrect password attempts ; TTY=pts/1 ; PWD=/home/deploy ; USER=root ; COMMAND=/usr/bin/id", 77)!, "c3");
        // Without PAM's lines (sudo configured another way): the summary counts them all.
        logins.Handle(Line("sudo", "bob : 2 incorrect password attempts ; TTY=pts/2 ; PWD=/ ; USER=root ; COMMAND=/usr/bin/id", 78)!, "c4");
        logins.Flush(DateTimeOffset.MaxValue);
        var records = logins.Take(100);

        Assert.AreEqual(3, records.Single(r => r.User == "deploy").Count);
        Assert.AreEqual(2, records.Single(r => r.User == "bob").Count);
    }

    [TestMethod]
    public void A_console_login_is_paired_with_its_end()
    {
        var logins = new JournalLogins(user => user == "root", () => false);
        logins.Handle(Line("login", "pam_unix(login:session): session opened for user root(uid=0) by LOGIN(uid=0)", 900)!, "a");
        logins.Handle(Line("login", "ROOT LOGIN ON tty1", 900)!, "b");
        logins.Handle(Line("login", "FAILED LOGIN (1) on '/dev/tty2' FOR 'deploy', Authentication failure", 901)!, "c");
        logins.Handle(Line("login", "FAILED LOGIN (2) on '/dev/tty2' FOR 'deploy', Authentication failure", 901)!, "d");
        logins.Handle(Line("login", "FAILED LOGIN (1) on '/dev/tty3' FOR 'UNKNOWN', User not known to the underlying authentication module", 902)!, "e");
        logins.Handle(JournalLines.Parse("login", "pam_unix(login:session): session closed for user root", At.AddMinutes(30), 900)!, "f");
        logins.Flush(DateTimeOffset.MaxValue);
        var records = logins.Take(100);

        var console = records.Single(r => r.Outcome == LogonOutcome.Success);
        Assert.AreEqual((LogonKind.Console, "root", true, "tty1", "900"), (console.Kind, console.User, console.IsAdmin, console.Workstation, console.LogonId));
        Assert.AreEqual(At.AddMinutes(30), console.LoggedOffAt);
        Assert.AreEqual(2, records.Single(r => r.User == "deploy").Count);
        Assert.AreEqual((LinuxLogins.InvalidUser, LogonKind.Console), records.Where(r => r.User == LinuxLogins.InvalidUser).Select(r => (r.FailureReason!, r.Kind)).Single());
    }

    [TestMethod]
    public void Invalid_users_are_aggregated_per_source_with_the_first_ten_names()
    {
        var logins = new JournalLogins(_ => false, () => false);
        var port = 1000;
        foreach (var name in Enumerable.Range(0, 30).Select(i => $"user{i}"))
        {
            port++;
            logins.Handle(Line("sshd", $"Invalid user {name} from 203.0.113.9 port {port}")!, $"i{port}");
            logins.Handle(Line("sshd", $"Failed password for invalid user {name} from 203.0.113.9 port {port} ssh2")!, $"f{port}");
            logins.Handle(Line("sshd", $"Failed password for invalid user {name} from 203.0.113.9 port {port} ssh2")!, $"g{port}");
        }
        logins.Handle(Line("sshd", "Invalid user admin from 198.51.100.4 port 22222")!, "other");
        logins.Flush(DateTimeOffset.MaxValue);
        var records = logins.Take(100);

        var scanner = records.Single(r => r.SourceAddress == "203.0.113.9");
        Assert.AreEqual(60, scanner.Count, "each connection once, and its second attempt");
        Assert.AreEqual(10, scanner.TriedUsers!.Count);
        Assert.AreEqual("user0", scanner.TriedUsers[0]);
        Assert.AreEqual(1, records.Single(r => r.SourceAddress == "198.51.100.4").Count);
        Assert.IsTrue(records.All(r => r.User == LinuxLogins.InvalidUser && !r.IsAdmin));
    }

    [TestMethod]
    public void A_key_offered_before_the_right_one_is_no_failure()
    {
        var logins = new JournalLogins(_ => true, () => false);
        logins.Handle(Line("sshd", "Failed publickey for deploy from 10.0.0.5 port 5000 ssh2: RSA SHA256:V/1oYAPw5Rkj5L0MVHUUPcP9wfbL+45Dtf2eeNgtrc0", 10)!, "a");
        logins.Handle(Line("sshd", "Accepted publickey for deploy from 10.0.0.5 port 5000 ssh2: ED25519 SHA256:20DQsXE1FxGLKxG9dAAGQqeyfect76rkV6tXMIele+s", 10)!, "b");
        logins.Handle(Line("sshd", "Failed publickey for deploy from 10.0.0.5 port 5001 ssh2: RSA SHA256:V/1oYAPw5Rkj5L0MVHUUPcP9wfbL+45Dtf2eeNgtrc0", 11)!, "c");
        logins.Handle(Line("sshd", "Connection closed by authenticating user deploy 10.0.0.5 port 5001 [preauth]", 11)!, "d");
        logins.Flush(DateTimeOffset.MaxValue);
        var records = logins.Take(100);

        Assert.AreEqual(1, records.Count(r => r.Outcome == LogonOutcome.Success));
        var refused = records.Single(r => r.Outcome == LogonOutcome.Failure);
        Assert.AreEqual((LinuxLogins.KeyRefused, "SHA256:V/1oYAPw5Rkj5L0MVHUUPcP9wfbL+45Dtf2eeNgtrc0"), (refused.FailureReason, refused.KeyFingerprint));
    }

    [TestMethod]
    public void A_cluster_nodes_ssh_is_a_file_transfer()
    {
        var logins = new JournalLogins(_ => true, () => false, address => address == "10.0.0.2");
        for (var i = 0; i < 5; i++)
        {
            logins.Handle(Line("sshd", $"Accepted publickey for root from 10.0.0.2 port {6000 + i} ssh2: RSA SHA256:V/1oYAPw5Rkj5L0MVHUUPcP9wfbL+45Dtf2eeNgtrc0", 20 + i)!, $"n{i}");
        }
        logins.Flush(DateTimeOffset.MaxValue);
        var records = logins.Take(100);

        var transfer = records.Single();
        Assert.AreEqual((LogonKind.Sftp, 5, "root"), (transfer.Kind, transfer.Count, transfer.User));
    }

    [TestMethod]
    public void Aggregates_end_with_their_window()
    {
        var logins = new JournalLogins(_ => false, () => false) { Window = TimeSpan.FromMinutes(10) };
        logins.Handle(JournalLines.Parse("sshd", "Failed password for deploy from 10.0.0.5 port 1 ssh2", At, 1)!, "a");
        logins.Handle(JournalLines.Parse("sshd", "Failed password for deploy from 10.0.0.5 port 2 ssh2", At.AddMinutes(9), 2)!, "b");
        logins.Handle(JournalLines.Parse("sshd", "Failed password for deploy from 10.0.0.5 port 3 ssh2", At.AddMinutes(11), 3)!, "c");
        logins.Flush(DateTimeOffset.MaxValue);
        var records = logins.Take(100).OrderBy(r => r.At).ToList();

        Assert.AreEqual(2, records.Count);
        Assert.AreEqual((2, At.AddMinutes(9)), (records[0].Count, records[0].LastAt!.Value));
        Assert.AreEqual(1, records[1].Count);
        Assert.AreEqual(JournalLogins.RecordId("a"), records[0].RecordId);
    }

    [TestMethod]
    public void The_last_24_hours_are_counted()
    {
        var logins = new JournalLogins(user => user is "root" or "deploy", () => false);
        foreach (var record in Records("debian-12.jsonl"))
        {
            if (JournalLines.Parse(record.Identifier, record.Message, record.At, record.Pid) is { } e)
            {
                logins.Handle(e, record.Cursor);
            }
        }
        logins.Flush(DateTimeOffset.MaxValue);
        var last = Records("debian-12.jsonl").Max(r => r.At);
        var counts = logins.Counts.Last24Hours(last.AddMinutes(1));

        Assert.AreEqual(5, counts[LoginCounts.Successes], "four SSH sessions and the SFTP one");
        Assert.AreEqual(5, counts[LoginCounts.AdminLogins]);
        Assert.AreEqual(2, counts[LoginCounts.InvalidUsers]);
        Assert.IsTrue(counts[LoginCounts.Elevations] >= 7, "sudo twice, su each time");
        Assert.AreEqual(6, counts[LoginCounts.Failures], "a password, a refused key, a sudo password, not in sudoers, two su");
        var restored = LoginCounts.FromState(logins.Counts.ToState());
        CollectionAssert.AreEqual(counts, restored.Last24Hours(last.AddMinutes(1)));
        Assert.AreEqual(0, logins.Counts.Last24Hours(last.AddHours(26)).Sum());
    }
}
