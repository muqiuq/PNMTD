using System.Diagnostics.Eventing.Reader;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Access;
using PNMTD.Sensor.Windows.Native;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>
/// <c>access</c> (docs/windows-sensor-plan.md §4.7): the sessions now, the effective audit policy for what logins need, the
/// operational logs, the Security log's span, and the last 24 hours in numbers. Whatever is missing comes with the command
/// that fixes it: the agent changes nothing (§6). Every 5 minutes.
/// </summary>
internal sealed class AccessCollector(LoginStream stream) : Collector
{
    public override string Name => SectionNames.Access;

    public override TimeSpan Interval => TimeSpan.FromMinutes(5);

    private static readonly IReadOnlySet<string> VolatileNames = new HashSet<string> { "sessions", "last24Hours", "oldestAt", "spanHours" };

    public override IReadOnlySet<string> Volatile => VolatileNames;

    /// <summary>The subcategories logins need, and whether success, failure or both.</summary>
    public static readonly (string Name, Guid Guid, bool Success, bool Failure)[] Required =
    [
        ("Logon", new Guid("0CCE9215-69AE-11D9-BED3-505054503030"), true, true),
        ("Logoff", new Guid("0CCE9216-69AE-11D9-BED3-505054503030"), true, false),
        ("Account Lockout", new Guid("0CCE9217-69AE-11D9-BED3-505054503030"), false, true),
        ("Special Logon", new Guid("0CCE921B-69AE-11D9-BED3-505054503030"), true, false),
        ("Other Logon/Logoff Events", new Guid("0CCE921C-69AE-11D9-BED3-505054503030"), true, true),
        ("Credential Validation", new Guid("0CCE923F-69AE-11D9-BED3-505054503030"), true, true),
    ];

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var sessions = Isolated.Part(errors, "sessions", SystemCollector.Sessions) ?? [];
        var policy = Isolated.Part(errors, "audit policy", () =>
        {
            var effective = AuditPolicy.Query(Required.Select(r => r.Guid).ToList());
            return Required.Select(r =>
            {
                var (success, failure) = effective.GetValueOrDefault(r.Guid);
                var missing = new List<string>();
                if (r.Success && !success)
                {
                    missing.Add("/success:enable");
                }
                if (r.Failure && !failure)
                {
                    missing.Add("/failure:enable");
                }
                return new AuditSetting(r.Name, r.Guid.ToString("B").ToUpperInvariant(), success, failure, r.Success, r.Failure,
                    // Quoted: in PowerShell, {…} would be a script block.
                    missing.Count == 0 ? null : $"auditpol /set /subcategory:\"{r.Guid.ToString("B").ToUpperInvariant()}\" {string.Join(' ', missing)}");
            }).ToList();
        }) ?? [];
        var logs = Isolated.Part(errors, "operational logs", () => LoginStream.Channels.Where(c => c.Name != "Security").Select(c =>
        {
            try
            {
                using var config = new EventLogConfiguration(c.Name);
                return new OperationalLog(c.Name, c.Purpose, true, config.IsEnabled, config.IsEnabled ? null : $"wevtutil sl \"{c.Name}\" /e:true");
            }
            catch (EventLogNotFoundException)
            {
                return new OperationalLog(c.Name, c.Purpose, false, false, null);
            }
        }).ToList()) ?? [];
        var security = Isolated.Part(errors, "Security log", () =>
        {
            using var config = new EventLogConfiguration("Security");
            using var reader = new EventLogReader(new EventLogQuery("Security", PathType.LogName) { ReverseDirection = false });
            using var oldest = reader.ReadEvent();
            DateTimeOffset? at = oldest?.TimeCreated is { } t ? new DateTimeOffset(t) : null;
            return new SecurityLogInfo(config.MaximumSizeInBytes, at, at is { } a ? Math.Round((DateTimeOffset.UtcNow - a).TotalHours, 1) : null);
        });
        var gaps = policy.Where(p => p.Fix is not null).Select(p => p.Fix!)
            .Concat(logs.Where(l => l.Fix is not null).Select(l => l.Fix!))
            .ToList();
        if (security is { SpanHours: < 24 })
        {
            gaps.Add($"wevtutil sl Security /ms:{Math.Max(security.MaxBytes * 4, 512L * 1024 * 1024)}");
        }
        return new AccessSection(sessions, policy, logs, security, stream.Counts(), gaps);
    }
}

/// <summary>
/// <c>event-log</c> (docs/windows-sensor-plan.md §4.8): the newest 100 entries of System and Application at Critical, Error or
/// Warning, the message rendered and capped at 2 KB. Every 5 minutes, and within about 15 s of a new entry (the login stream's
/// poll asks for it).
/// </summary>
internal sealed class EventLogCollector : Collector
{
    public override string Name => SectionNames.EventLog;

    public override TimeSpan Interval => TimeSpan.FromMinutes(5);

    public const string Query = "*[System[(Level=1 or Level=2 or Level=3)]]";

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var entries = new List<EventEntry>();
        foreach (var log in new[] { "System", "Application" })
        {
            Isolated.Part(errors, log, () =>
            {
                using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, Query) { ReverseDirection = true });
                for (var i = 0; i < 100; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var record = reader.ReadEvent();
                    if (record is null)
                    {
                        break;
                    }
                    entries.Add(Entry(log, record));
                }
                return true;
            });
        }
        return new EventLogSection(entries.OrderByDescending(e => e.At).Take(100).ToList());
    }

    public static EventEntry Entry(string log, EventRecord record)
    {
        string message;
        try
        {
            message = record.FormatDescription() ?? $"(no description for event {record.Id} of {record.ProviderName})";
        }
        catch (EventLogException)
        {
            message = $"(no description for event {record.Id} of {record.ProviderName})";
        }
        if (message.Length > 2048)
        {
            message = message[..2047] + "…";
        }
        string? user = null;
        try
        {
            user = record.UserId?.Translate(typeof(System.Security.Principal.NTAccount)).Value;
        }
        catch (Exception)
        {
            user = record.UserId?.Value;
        }
        string? category = null;
        try
        {
            category = record.TaskDisplayName;
        }
        catch (EventLogException)
        {
        }
        var level = record.Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", _ => "Information" };
        return new EventEntry(record.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.UtcNow, log, level, record.ProviderName ?? "?", record.Id,
            category, message, user, record.RecordId ?? 0);
    }
}
