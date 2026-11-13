using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Collectors;
using PNMTD.Sensor.Windows.Resilience;
using PNMTD.Sensor.Windows.Service;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Access;

/// <summary>
/// Logins and connections (docs/windows-sensor-plan.md §4.7), as they happen: the Security log and the RDP, SSH, WinRM and SMB
/// logs, each read on from its bookmark every 15 seconds (after a restart or time offline too, as long as the log holds the
/// records). Console, RDP, SSH and WinRM for everyone; network logons, explicit credentials and batch only for admins;
/// failures of every kind aggregated per user, source and kind over 10 minutes, as are repeated identical successes. Never
/// machine accounts, anonymous or built-in service logons. Buffered up to 5,000 while offline.
/// </summary>
internal sealed partial class LoginStream(ProblemReporter problems, SectionScheduler sections, ILogger<LoginStream> logger) : IAgentFeature
{
    public sealed record Channel(string Name, string Purpose, string Query);

    public static readonly IReadOnlyList<Channel> Channels =
    [
        new("Security", "logons, failures, logoffs, explicit credentials, special privileges, lockouts",
            "*[System[(EventID=4624 or EventID=4625 or EventID=4634 or EventID=4647 or EventID=4648 or EventID=4672 or EventID=4740)]]"),
        new("Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational", "RDP connections (1149)", "*[System[(EventID=1149)]]"),
        new("Microsoft-Windows-TerminalServices-LocalSessionManager/Operational", "RDP sessions (21–25)", "*[System[(EventID>=21 and EventID<=25)]]"),
        new("OpenSSH/Operational", "SSH logins", "*[System[(EventID=4)]]"),
        new("Microsoft-Windows-WinRM/Operational", "PowerShell remoting (91)", "*[System[(EventID=91)]]"),
        new("Microsoft-Windows-SMBServer/Security", "SMB authentication failures (551)", "*[System[(EventID=551)]]"),
    ];

    public const int MaxBuffered = 5000;
    /// <summary>How often the logs are read: a login reaches PNMT within this time, plus the send.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>Records per log and poll; a backlog beyond it is read on at the next poll.</summary>
    private const int MaxPerPoll = 20_000;
    private const string BookmarksFile = "bookmarks.json";

    private readonly Lock gate = new();
    private readonly Dictionary<(string Channel, long RecordId), LoginRecord> pending = [];
    private readonly Dictionary<string, Aggregate> aggregates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (LoginRecord Record, DateTimeOffset At)> openLogons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> privileged = new(StringComparer.Ordinal);
    private readonly List<(DateTimeOffset At, LogonOutcome Outcome, bool Admin, LogonKind Kind)> recent = [];
    private Dictionary<string, string> bookmarks = new(StringComparer.Ordinal);
    private HashSet<string> localAdmins = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset localAdminsReadAt = DateTimeOffset.MinValue;
    private TimeSpan window = TimeSpan.FromMinutes(10);
    private int dropped;

    private sealed class Aggregate(LoginRecord record, DateTimeOffset start)
    {
        public LoginRecord Record { get; set; } = record;

        public DateTimeOffset Start { get; } = start;
    }

    public string Name => "logins";

    public IReadOnlyList<string> Capabilities => [AgentCapabilities.Access];

    public void Configure(AgentConfig config) => window = TimeSpan.FromMinutes(Math.Clamp(config.Access.AggregateMinutes, 1, 60));

    public async Task RunAsync(CancellationToken ct)
    {
        bookmarks = AgentFiles.ReadState<Dictionary<string, string>>(BookmarksFile) ?? new(StringComparer.Ordinal);
        var savedAt = DateTimeOffset.UtcNow;
        var eventLogMark = new Dictionary<string, long>(StringComparer.Ordinal);
        var eventLogCheckedAt = DateTimeOffset.MinValue;
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        while (!ct.IsCancellationRequested)
        {
            RefreshLocalAdmins();
            foreach (var channel in Channels.Where(c => !unavailable.Contains(c.Name)))
            {
                try
                {
                    Poll(channel);
                }
                catch (EventLogNotFoundException)
                {
                    unavailable.Add(channel.Name);
                    logger.LogInformation("{Log} does not exist here: its logins are not recorded.", channel.Name);
                }
                catch (EventLogException e)
                {
                    problems.Report("logins", $"reading {channel.Name}", e);
                }
            }
            if (DateTimeOffset.UtcNow - eventLogCheckedAt >= PollInterval)
            {
                eventLogCheckedAt = DateTimeOffset.UtcNow;
                Isolated.Part([], "event log", () => CheckEventLog(eventLogMark));
            }
            if (DateTimeOffset.UtcNow - savedAt > TimeSpan.FromSeconds(30))
            {
                savedAt = DateTimeOffset.UtcNow;
                lock (gate)
                {
                    AgentFiles.WriteState(BookmarksFile, bookmarks);
                }
            }
            await Task.Delay(PollInterval, ct);
        }
    }

    /// <summary>New entries in System or Application at Critical, Error or Warning: the event-log section is collected again (§4.8).</summary>
    private bool CheckEventLog(Dictionary<string, long> marks)
    {
        foreach (var log in new[] { "System", "Application" })
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, EventLogCollector.Query) { ReverseDirection = true });
            using var newest = reader.ReadEvent();
            var id = newest?.RecordId ?? 0;
            if (marks.TryGetValue(log, out var mark) && id > mark)
            {
                sections.Request([SectionNames.EventLog]);
            }
            marks[log] = id;
        }
        return true;
    }

    private void Poll(Channel channel)
    {
        string? bookmark;
        lock (gate)
        {
            bookmark = bookmarks.GetValueOrDefault(channel.Name);
        }
        var query = new EventLogQuery(channel.Name, PathType.LogName, bookmark is null
            // The first time: the last hour, not the whole log.
            ? channel.Query.Replace("[System[", "[System[TimeCreated[timediff(@SystemTime) <= 3600000] and ", StringComparison.Ordinal)
            : channel.Query);
        using var reader = bookmark is null ? new EventLogReader(query) : new EventLogReader(query, new EventBookmark(bookmark));
        for (var i = 0; i < MaxPerPoll; i++)
        {
            using var record = reader.ReadEvent();
            if (record is null)
            {
                break;
            }
            try
            {
                Handle(channel.Name, record);
            }
            catch (Exception e)
            {
                problems.Report("logins", $"parsing {channel.Name} {record.Id}", e);
            }
            lock (gate)
            {
                bookmarks[channel.Name] = record.Bookmark.BookmarkXml;
            }
        }
    }

    // --- parsing (§4.7) ---------------------------------------------------------------------------------------

    private void Handle(string channel, EventRecord record)
    {
        var at = record.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.UtcNow;
        var recordId = record.RecordId ?? 0;
        var data = Data(record);
        switch (channel, record.Id)
        {
            case ("Security", 4672):
                if (data.GetValueOrDefault("SubjectLogonId") is { } privilegedLogon)
                {
                    lock (gate)
                    {
                        privileged[privilegedLogon] = at;
                    }
                }
                break;
            case ("Security", 4624):
                Logon(channel, recordId, at, data);
                break;
            case ("Security", 4625):
                Failure(channel, recordId, at, data);
                break;
            case ("Security", 4634) or ("Security", 4647):
                Logoff(data.GetValueOrDefault("TargetLogonId"), at);
                break;
            case ("Security", 4648):
                Explicit(channel, recordId, at, data);
                break;
            case ("Security", 4740):
                Add(new LoginRecord(channel, recordId, at, null, 1, LogonOutcome.Failure, "locked out", data.GetValueOrDefault("TargetUserName") ?? "?",
                    null, IsLocalAdmin(null, data.GetValueOrDefault("TargetUserName")), LogonKind.Lockout, null, null, null,
                    data.GetValueOrDefault("TargetDomainName"), null, null, null), aggregate: false);
                break;
            case ("Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational", 1149):
                var user = data.GetValueOrDefault("Param1");
                if (!string.IsNullOrEmpty(user))
                {
                    var domain = data.GetValueOrDefault("Param2");
                    Add(new LoginRecord(channel, recordId, at, null, 1, LogonOutcome.Success, null, user, Blank(domain), IsLocalAdmin(domain, user),
                        LogonKind.RdpConnection, "NLA", Blank(data.GetValueOrDefault("Param3")), null, null, null, null, null), aggregate: true);
                }
                break;
            case ("OpenSSH/Operational", 4):
                Ssh(channel, recordId, at, data.GetValueOrDefault("payload") ?? Safe(record.FormatDescription));
                break;
            case ("Microsoft-Windows-WinRM/Operational", 91):
                var account = Account(record);
                if (account is not null && !IsIgnored(account.Value.Domain, account.Value.User))
                {
                    Add(new LoginRecord(channel, recordId, at, null, 1, LogonOutcome.Success, null, account.Value.User, account.Value.Domain,
                        IsLocalAdmin(account.Value.Domain, account.Value.User), LogonKind.WinRm, null, null, null, null, null, null, null), aggregate: true);
                }
                break;
            case ("Microsoft-Windows-SMBServer/Security", 551):
                var smbUser = data.GetValueOrDefault("UserName");
                if (!string.IsNullOrEmpty(smbUser))
                {
                    Add(new LoginRecord(channel, recordId, at, null, 1, LogonOutcome.Failure, Reason(data.GetValueOrDefault("Status")), smbUser, null,
                        IsLocalAdmin(null, smbUser), LogonKind.Network, null, Blank(data.GetValueOrDefault("ClientName")), null, null, null, null, null), aggregate: true);
                }
                break;
        }
    }

    private void Logon(string channel, long recordId, DateTimeOffset at, IReadOnlyDictionary<string, string> data)
    {
        var user = data.GetValueOrDefault("TargetUserName") ?? "";
        var domain = data.GetValueOrDefault("TargetDomainName");
        var type = int.TryParse(data.GetValueOrDefault("LogonType"), out var lt) ? lt : 0;
        if (IsIgnored(domain, user) || type is 0 or 5)
        {
            return;
        }
        var logonId = data.GetValueOrDefault("TargetLogonId");
        bool admin;
        lock (gate)
        {
            admin = logonId is not null && privileged.ContainsKey(logonId);
        }
        admin = admin || IsLocalAdmin(domain, user);
        var kind = Kind(type);
        // Ordinary users' network logons (file shares) stay out (R19); batch and explicit credentials are recorded for admins.
        if (kind is LogonKind.Network or LogonKind.Batch or LogonKind.Explicit && !admin)
        {
            return;
        }
        var elevated = data.GetValueOrDefault("ElevatedToken") switch { "%%1842" => true, "%%1843" => false, _ => (bool?)null };
        var package = data.GetValueOrDefault("AuthenticationPackageName");
        var lm = data.GetValueOrDefault("LmPackageName");
        if (!string.IsNullOrEmpty(lm) && lm != "-")
        {
            package = lm;
        }
        var login = new LoginRecord(channel, recordId, at, null, 1, LogonOutcome.Success, null, user, Blank(domain), admin, kind, Blank(package),
            Address(data.GetValueOrDefault("IpAddress")), int.TryParse(data.GetValueOrDefault("IpPort"), out var port) && port > 0 ? port : null,
            Blank(data.GetValueOrDefault("WorkstationName")), elevated, logonId, null);
        var added = Add(login, aggregate: true);
        if (added.Count == 1 && logonId is not null)
        {
            lock (gate)
            {
                openLogons[logonId] = (added, at);
            }
        }
    }

    private void Failure(string channel, long recordId, DateTimeOffset at, IReadOnlyDictionary<string, string> data)
    {
        var user = data.GetValueOrDefault("TargetUserName") ?? "";
        var domain = data.GetValueOrDefault("TargetDomainName");
        if (IsIgnored(domain, user) && !string.IsNullOrEmpty(user))
        {
            return;
        }
        var type = int.TryParse(data.GetValueOrDefault("LogonType"), out var lt) ? lt : 3;
        var status = data.GetValueOrDefault("SubStatus") is { } sub && sub != "0x0" ? sub : data.GetValueOrDefault("Status");
        Add(new LoginRecord(channel, recordId, at, null, 1, LogonOutcome.Failure, Reason(status), string.IsNullOrEmpty(user) ? "?" : user, Blank(domain),
            IsLocalAdmin(domain, user), Kind(type), Blank(data.GetValueOrDefault("AuthenticationPackageName")), Address(data.GetValueOrDefault("IpAddress")),
            null, Blank(data.GetValueOrDefault("WorkstationName")), null, null, null), aggregate: true);
    }

    private void Explicit(string channel, long recordId, DateTimeOffset at, IReadOnlyDictionary<string, string> data)
    {
        var user = data.GetValueOrDefault("TargetUserName") ?? "";
        var domain = data.GetValueOrDefault("TargetDomainName");
        var subject = data.GetValueOrDefault("SubjectLogonId");
        bool admin;
        lock (gate)
        {
            admin = subject is not null && privileged.ContainsKey(subject);
        }
        if (IsIgnored(domain, user) || !(admin || IsLocalAdmin(domain, user)))
        {
            return;
        }
        Add(new LoginRecord(channel, recordId, at, null, 1, LogonOutcome.Success, null, user, Blank(domain), true, LogonKind.Explicit, null,
            Address(data.GetValueOrDefault("IpAddress")), null, Blank(data.GetValueOrDefault("TargetServerName")), null, null, null), aggregate: true);
    }

    private void Ssh(string channel, long recordId, DateTimeOffset at, string? payload)
    {
        if (payload is null)
        {
            return;
        }
        if (SshAccepted().Match(payload) is { Success: true } ok)
        {
            var user = ok.Groups["user"].Value;
            Add(new LoginRecord(channel, recordId, at, null, 1, LogonOutcome.Success, null, user, null, IsLocalAdmin(null, user), LogonKind.Ssh,
                ok.Groups["method"].Value, ok.Groups["from"].Value, int.TryParse(ok.Groups["port"].Value, out var p) ? p : null, null, null, null, null), aggregate: true);
        }
        else if (SshFailed().Match(payload) is { Success: true } failed)
        {
            var user = failed.Groups["user"].Value;
            Add(new LoginRecord(channel, recordId, at, null, 1, LogonOutcome.Failure, failed.Groups["invalid"].Success ? "unknown user" : $"bad {failed.Groups["method"].Value}",
                user, null, IsLocalAdmin(null, user), LogonKind.Ssh, failed.Groups["method"].Value, failed.Groups["from"].Value, null, null, null, null, null), aggregate: true);
        }
    }

    [GeneratedRegex(@"Accepted (?<method>\S+) for (?<user>\S+) from (?<from>\S+) port (?<port>\d+)")]
    private static partial Regex SshAccepted();

    [GeneratedRegex(@"Failed (?<method>\S+) for (?<invalid>invalid user )?(?<user>\S+) from (?<from>\S+) port (?<port>\d+)")]
    private static partial Regex SshFailed();

    private void Logoff(string? logonId, DateTimeOffset at)
    {
        if (logonId is null)
        {
            return;
        }
        lock (gate)
        {
            if (openLogons.Remove(logonId, out var open) && open.Record.Count == 1)
            {
                var closed = open.Record with { LoggedOffAt = at };
                pending[(closed.Channel, closed.RecordId)] = closed;
            }
        }
    }

    /// <summary>Adds a record, folding it into an aggregate of the same outcome, user, source and kind within the window.</summary>
    private LoginRecord Add(LoginRecord record, bool aggregate)
    {
        lock (gate)
        {
            recent.Add((record.At, record.Outcome, record.IsAdmin && record.Outcome == LogonOutcome.Success, record.Kind));
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            if (recent.Count > 20_000 || recent.Count > 0 && recent[0].At < cutoff)
            {
                recent.RemoveAll(r => r.At < cutoff);
                if (recent.Count > 20_000)
                {
                    recent.RemoveRange(0, recent.Count - 20_000);
                }
            }
            var result = record;
            if (aggregate)
            {
                var key = $"{record.Outcome}\n{record.Domain}\\{record.User}\n{record.SourceAddress}\n{record.Kind}".ToLowerInvariant();
                if (aggregates.TryGetValue(key, out var current) && record.At - current.Start <= window)
                {
                    current.Record = current.Record with
                    {
                        Count = current.Record.Count + 1,
                        LastAt = record.At,
                        IsAdmin = current.Record.IsAdmin || record.IsAdmin,
                        LogonId = null,
                    };
                    result = current.Record;
                }
                else
                {
                    aggregates[key] = new Aggregate(record, record.At);
                }
                foreach (var stale in aggregates.Where(a => DateTimeOffset.UtcNow - a.Value.Start > window * 2).Select(a => a.Key).ToList())
                {
                    aggregates.Remove(stale);
                }
            }
            pending[(result.Channel, result.RecordId)] = result;
            if (pending.Count > MaxBuffered)
            {
                pending.Remove(pending.OrderBy(p => p.Value.At).First().Key);
                dropped++;
            }
            foreach (var old in privileged.Where(p => DateTimeOffset.UtcNow - p.Value > TimeSpan.FromMinutes(10)).Select(p => p.Key).ToList())
            {
                privileged.Remove(old);
            }
            if (openLogons.Count > 10_000)
            {
                foreach (var old in openLogons.OrderBy(o => o.Value.At).Take(1000).Select(o => o.Key).ToList())
                {
                    openLogons.Remove(old);
                }
            }
            return result;
        }
    }

    public async Task ReportAsync(AgentSession session, CancellationToken ct)
    {
        List<LoginRecord> batch;
        int lost;
        lock (gate)
        {
            if (pending.Count == 0 && dropped == 0)
            {
                return;
            }
            batch = pending.Values.OrderBy(r => r.At).Take(Math.Clamp(session.Config.Access.MaxBatch, 50, 1000)).ToList();
            foreach (var record in batch)
            {
                pending.Remove((record.Channel, record.RecordId));
            }
            lost = dropped;
            dropped = 0;
        }
        if (lost > 0)
        {
            problems.Report("logins", "buffer", $"{lost} logins were dropped while offline (at most {MaxBuffered} are kept); the bookmarks read missed ones while the logs hold them.");
        }
        if (batch.Count == 0)
        {
            return;
        }
        try
        {
            await session.SendAsync(AgentMessageTypes.Access, new AccessBatch(batch), ct);
        }
        catch
        {
            lock (gate)
            {
                foreach (var record in batch)
                {
                    pending.TryAdd((record.Channel, record.RecordId), record);
                }
            }
            throw;
        }
    }

    public Task<Ack?> HandleAsync(AgentSession session, SignedContent request, CancellationToken ct) => Task.FromResult<Ack?>(null);

    /// <summary>The last 24 hours in numbers, for the access section.</summary>
    public AccessCounts Counts()
    {
        lock (gate)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            var day = recent.Where(r => r.At >= cutoff).ToList();
            return new AccessCounts(day.Count(r => r.Outcome == LogonOutcome.Success), day.Count(r => r.Outcome == LogonOutcome.Failure),
                day.Count(r => r.Admin), day.Count(r => r.Kind == LogonKind.Lockout));
        }
    }

    // --- helpers -------------------------------------------------------------------------------------------------

    private static LogonKind Kind(int logonType) => logonType switch
    {
        2 => LogonKind.Console,
        10 => LogonKind.Rdp,
        11 => LogonKind.Cached,
        7 => LogonKind.Unlock,
        4 => LogonKind.Batch,
        9 => LogonKind.Explicit,
        _ => LogonKind.Network,
    };

    /// <summary>Machine accounts, anonymous logons and built-in service logons are never recorded (§4.7).</summary>
    private static bool IsIgnored(string? domain, string? user)
    {
        if (string.IsNullOrEmpty(user) || user == "-" || user.EndsWith('$'))
        {
            return true;
        }
        if (user.Equals("ANONYMOUS LOGON", StringComparison.OrdinalIgnoreCase) || user.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
            || user.Equals("LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) || user.Equals("NETWORK SERVICE", StringComparison.OrdinalIgnoreCase)
            || user.StartsWith("DWM-", StringComparison.OrdinalIgnoreCase) || user.StartsWith("UMFD-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return domain is not null && (domain.Equals("NT SERVICE", StringComparison.OrdinalIgnoreCase) || domain.Equals("Window Manager", StringComparison.OrdinalIgnoreCase)
            || domain.Equals("Font Driver Host", StringComparison.OrdinalIgnoreCase) || domain.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase)
            || domain.Equals("NT VIRTUAL MACHINE", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsLocalAdmin(string? domain, string? user) =>
        !string.IsNullOrEmpty(user) && (localAdmins.Contains(domain is null ? user : $"{domain}\\{user}") || localAdmins.Contains($"{Environment.MachineName}\\{user}")
            || domain is null && localAdmins.Any(a => a.EndsWith($"\\{user}", StringComparison.OrdinalIgnoreCase)));

    private void RefreshLocalAdmins()
    {
        if (DateTimeOffset.UtcNow - localAdminsReadAt < TimeSpan.FromMinutes(15))
        {
            return;
        }
        localAdminsReadAt = DateTimeOffset.UtcNow;
        try
        {
            localAdmins = SystemCollector.LocalAdministrators().Where(a => a.Kind == "User").Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            problems.Report("logins", "reading the local administrators", e);
        }
    }

    /// <summary>The 4625 status codes, as words.</summary>
    private static string? Reason(string? status) => status?.ToUpperInvariant() switch
    {
        null or "" or "0X0" => null,
        "0XC000006A" => "bad password",
        "0XC0000064" => "unknown user",
        "0XC0000234" => "locked out",
        "0XC0000072" => "account disabled",
        "0XC0000193" => "account expired",
        "0XC0000071" => "password expired",
        "0XC0000224" => "password must change",
        "0XC000006F" => "outside logon hours",
        "0XC0000070" => "workstation not allowed",
        "0XC000015B" => "logon type not granted",
        "0XC000006D" => "bad user name or password",
        "0XC000005E" => "no logon servers",
        var other => other.ToLowerInvariant(),
    };

    /// <summary>A source address: none for the local machine, IPv4-mapped IPv6 as IPv4.</summary>
    private static string? Address(string? value)
    {
        var v = Blank(value);
        if (v is null or "::1" or "127.0.0.1")
        {
            return null;
        }
        return v.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) ? v[7..] : v;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) || value == "-" ? null : value.Trim();

    private static string? Safe(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (EventLogException)
        {
            return null;
        }
    }

    private static (string? Domain, string User)? Account(EventRecord record)
    {
        try
        {
            var name = record.UserId?.Translate(typeof(System.Security.Principal.NTAccount)).Value;
            if (name is null)
            {
                return null;
            }
            var parts = name.Split('\\', 2);
            return parts.Length == 2 ? (parts[0], parts[1]) : (null, parts[0]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The event's named data: EventData's Data elements, or UserData's (the RDP and SMB logs), by name.</summary>
    private static Dictionary<string, string> Data(EventRecord record)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var xml = XDocument.Parse(record.ToXml());
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        foreach (var data in xml.Descendants(ns + "Data"))
        {
            if (data.Attribute("Name")?.Value is { } name)
            {
                result[name] = data.Value;
            }
        }
        foreach (var element in xml.Descendants().Where(e => e.Parent?.Name.LocalName is "EventXML" or "EventInfo" && !e.HasElements))
        {
            result.TryAdd(element.Name.LocalName, element.Value);
        }
        if (!result.ContainsKey("payload") && xml.Descendants(ns + "Data").FirstOrDefault(d => d.Attribute("Name") is null) is { } unnamed)
        {
            result["payload"] = unnamed.Value;
        }
        return result;
    }
}
