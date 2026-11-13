using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Enums;
using TaskStatus = PNMTD.Api.V1.Contracts.TaskStatus;

namespace PNMTD.Services.Domain;

/// <summary>Server information, the uplink, the background tasks' health and the reference data for the docs.</summary>
public sealed class SystemService(PnmtdDbContext db, IConfiguration configuration, PublicUrls urls)
{
    /// <summary>A background task, the key it writes after each run, and how often it runs.</summary>
    private sealed record TaskDefinition(string Name, KeyValueKeyEnums Key, int IntervalSeconds, Func<IConfiguration, bool> Configured);

    private static readonly TaskDefinition[] Tasks =
    [
        new("Heartbeat check", KeyValueKeyEnums.LAST_HEARTBEAT_TASK_RUN, 10, _ => true),
        new("Notifications", KeyValueKeyEnums.LAST_NOTIFICATION_TASK_RUN, 10, _ => true),
        new("Ping check", KeyValueKeyEnums.LAST_PING_TASK_RUN, 10, _ => true),
        new("Timespan conditions", KeyValueKeyEnums.LAST_TIMESPAN_CONDITIONS_CHECK_TASK_RUN, 15, _ => true),
        new("DNS check", KeyValueKeyEnums.LAST_DNS_CHECK, 15, _ => true),
        new("Mail processing", KeyValueKeyEnums.LAST_MAIL_PROCESSING, 60, _ => true),
        new("Mailbox check", KeyValueKeyEnums.LAST_MAILBOX_CHECK, 60, MailboxConfigured),
        new("Uplink check", KeyValueKeyEnums.LAST_UPLINK_CHECK, 120, UplinkConfigured),
        // docs/mikrotik-plan.md §5.2, §5.4: every 10 seconds, and every 6 hours by default.
        new("MikroTik poller", KeyValueKeyEnums.LAST_MIKROTIK_POLL_RUN, 10, c => c.GetValue("Mikrotik:Enabled", true)),
        new("MikroTik version check", KeyValueKeyEnums.LAST_MIKROTIK_VERSION_CHECK, 6 * 3600,
            c => c.GetValue("Mikrotik:Enabled", true) && c.GetValue("Mikrotik:VersionCheck:Enabled", true)),
    ];

    public static bool MailboxConfigured(IConfiguration c) =>
        !string.IsNullOrEmpty(c["Mailbox:Host"]) && !string.IsNullOrEmpty(c["Mailbox:Username"]) && !string.IsNullOrEmpty(c["Mailbox:Password"]);

    public static bool UplinkConfigured(IConfiguration c) =>
        !string.IsNullOrEmpty(c["UplinkCheck:SharedKey"])
        && Enumerable.Range(1, 3).Count(i => c[$"UplinkCheck:Url{i}"] is { } url && (url.StartsWith("http://") || url.StartsWith("https://"))) >= 2;

    public SystemInfo Info(string? role)
    {
        if (role is null)
        {
            return new SystemInfo(ServerInfo.Identity);
        }
        return new SystemInfo(ServerInfo.Identity, ServerInfo.Version, urls.Configured, Clock.TimeZone.Id,
            configuration["Auth:TotpPolicy"] ?? "Optional", role, configuration.GetValue("WireGuard:Enabled", false));
    }

    public async Task<UplinkStatus> UplinkAsync(CancellationToken ct)
    {
        var values = await KeyValuesAsync(ct);
        var configured = UplinkConfigured(configuration);
        // UPLINK_OK starts true: without the settings the task never checks, and the uplink counts as up.
        var isUp = !configured || !values.TryGetValue(Key(KeyValueKeyEnums.UPLINK_OK), out var ok) || !bool.TryParse(ok, out var parsed) || parsed;
        return new UplinkStatus(configured, isUp, Timestamp(values, KeyValueKeyEnums.LAST_UPLINK_CHECK),
            Timestamp(values, KeyValueKeyEnums.LAST_UPLINK_CHECK_SUCCESSFULL));
    }

    public async Task<IReadOnlyList<TaskStatus>> TasksAsync(CancellationToken ct)
    {
        var values = await KeyValuesAsync(ct);
        var enabled = configuration.GetValue("BackgroundTasks:Enabled", true);
        var now = Clock.UtcNow;
        return Tasks.Select(t =>
        {
            var lastRun = Timestamp(values, t.Key);
            var state = !enabled || !t.Configured(configuration) ? "off"
                : lastRun is null ? "notRun"
                : now - lastRun.Value > TimeSpan.FromSeconds(3 * t.IntervalSeconds + 60) ? "late"
                : "ok";
            return new TaskStatus(t.Name, Key(t.Key), t.IntervalSeconds, lastRun, state);
        }).ToList();
    }

    public async Task<Dictionary<string, string>> KeyValuesAsync(CancellationToken ct) =>
        await db.KeyValues.AsNoTracking().Where(k => k.Enabled).ToDictionaryAsync(k => k.Key, k => k.Value, ct);

    private static string Key(KeyValueKeyEnums key) => KeyValueDbExtensions.KeyOf(key);

    /// <summary>The tasks write ISO 8601 UTC; v0.12 wrote local time, which the importer converted.</summary>
    internal static DateTime? Timestamp(IReadOnlyDictionary<string, string> values, KeyValueKeyEnums key) =>
        values.TryGetValue(Key(key), out var value)
        && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : null;

    public static IReadOnlyList<StatusCodeInfo> StatusCodes() =>
    [
        new(PNMTStatusCodes.OK, "OK", true, "The default success code. Any code from 0 to 399 counts as success."),
        new(PNMTStatusCodes.DNS_OK, "DNS_OK", true, "Every record of the DNS zone matches what the resolvers answer."),
        new(PNMTStatusCodes.PING_SUCCESSFULL, "PING_SUCCESSFUL", true, "The ping target answered."),
        new(PNMTStatusCodes.VALUECHECK_OK, "VALUECHECK_OK", true, "The message matched the sensor's regular expression."),
        new(PNMTStatusCodes.ONE_WITHIN_TIMESPAN_OK, "ONE_WITHIN_TIMESPAN_OK", true, "At least one successful report arrived within the timespan."),
        new(PNMTStatusCodes.ALL_WITHIN_TIMESPAN_OK, "ALL_WITHIN_TIMESPAN_OK", true, "Every report within the timespan was successful."),
        new(PNMTStatusCodes.UDP_ALIVE, "UDP_ALIVE", true, "A UDP node sent a heartbeat, or another packet that shows it is alive."),
        new(PNMTStatusCodes.UDP_BOOT, "UDP_BOOT", true, "A UDP node restarted; the message names the reason and its INFO (often the firmware version)."),
        new(PNMTStatusCodes.UDP_DATA, "UDP_DATA", true, "A UDP node sent DATA; the message is the payload, as text or as hex."),
        new(PNMTStatusCodes.MIKROTIK_OK, "MIKROTIK_OK", true, "A MikroTik router answered its poll: the message names its RouterOS version and model, and an available update."),
        new(PNMTStatusCodes.MIKROTIK_RESTARTED, "MIKROTIK_RESTARTED", true, "A MikroTik router restarted since the last poll (its boot time moved)."),
        new(PNMTStatusCodes.MIKROTIK_UPDATED, "MIKROTIK_UPDATED", true, "A MikroTik router runs another RouterOS version than at the last poll."),
        new(PNMTStatusCodes.MIKROTIK_CONFIG_CHANGED, "MIKROTIK_CONFIG_CHANGED", true, "A MikroTik router's configuration changed: a backup found a new version."),
        new(PNMTStatusCodes.MIKROTIK_UPDATE_AVAILABLE, "MIKROTIK_UPDATE_AVAILABLE", true, "A newer RouterOS version is available on the router's channel (once per version)."),
        new(PNMTStatusCodes.SYSTEM_OK, "SYSTEM_OK", true, "A system sensor's condition is fine: the uplink answers, the AI is available, no critical RouterOS fix is pending."),
        new(PNMTStatusCodes.AI_FINDING_INFO, "AI_FINDING_INFO", true, "The AI analysis found something worth knowing, but no problem."),
        new(PNMTStatusCodes.DNS_FAILED, "DNS_FAILED", false, "At least one record of the DNS zone differs from what the resolvers answer."),
        new(PNMTStatusCodes.HEARTBEAT_MISSING, "HEARTBEAT_MISSING", false, "No report within the interval and the grace period."),
        new(PNMTStatusCodes.PING_FAILED, "PING_FAILED", false, "The ping target did not answer."),
        new(PNMTStatusCodes.VALUECHECK_FAILED, "VALUECHECK_FAILED", false, "The message did not match the sensor's regular expression."),
        new(PNMTStatusCodes.ONE_WITHIN_TIMESPAN_FAILED, "ONE_WITHIN_TIMESPAN_FAILED", false, "No successful report within the timespan."),
        new(PNMTStatusCodes.ALL_WITHIN_TIMESPAN_FAILED, "ALL_WITHIN_TIMESPAN_FAILED", false, "Not every report within the timespan was successful."),
        new(PNMTStatusCodes.MIKROTIK_UNREACHABLE, "MIKROTIK_UNREACHABLE", false, "A MikroTik router did not answer its polls for longer than the grace period; the message says why."),
        new(PNMTStatusCodes.MIKROTIK_ACCESS_DENIED, "MIKROTIK_ACCESS_DENIED", false, "PNMTD reached a MikroTik router but cannot use it: login refused, host key changed, a missing policy, or RouterOS below 7.13."),
        new(PNMTStatusCodes.MIKROTIK_BACKUP_FAILED, "MIKROTIK_BACKUP_FAILED", false, "The last two configuration backups of a MikroTik router failed."),
        new(PNMTStatusCodes.MIKROTIK_UPDATE_FAILED, "MIKROTIK_UPDATE_FAILED", false, "An update job of a MikroTik router failed; the message names the step and the reason."),
        new(PNMTStatusCodes.UPLINK_DOWN, "UPLINK_DOWN", false, "PNMTD's uplink check failed three times in a row; monitoring that needs the uplink pauses."),
        new(PNMTStatusCodes.AI_FAILING, "AI_FAILING", false, "AI requests keep failing (five retries a minute apart), or the key was refused."),
        new(PNMTStatusCodes.ROUTEROS_SECURITY_UPDATE, "ROUTEROS_SECURITY_UPDATE", false, "A RouterOS release fixes a very bad issue, and at least one router is behind it."),
        new(PNMTStatusCodes.AI_FINDING_CRITICAL, "AI_FINDING_CRITICAL", false, "The AI analysis found a critical problem, and the verification model confirmed it."),
        new(PNMTStatusCodes.AI_FINDING_WARNING, "AI_FINDING_WARNING", false, "The AI analysis found a problem or pattern worth a look."),
        new(PNMTStatusCodes.AI_BUDGET_80, "AI_BUDGET_80", false, "AI spending reached 80 % of the budget."),
        new(PNMTStatusCodes.AI_BUDGET_90, "AI_BUDGET_90", false, "AI spending reached 90 % of the budget."),
        new(PNMTStatusCodes.AI_BUDGET_REACHED, "AI_BUDGET_REACHED", false, "AI spending reached the budget: AI work waits until the next period."),
        new(PNMTStatusCodes.ERROR, "ERROR", false, "The default failure code. Any code above 399 counts as a failure."),
    ];

    public static IReadOnlyList<SensorTypeInfo> SensorTypes() =>
    [
        new(SensorType.SIMPLE, "Reports through the sensor API; the last report is the state.", null, true, false),
        new(SensorType.HEARTBEAT, "Must report every Interval seconds; after Interval + GracePeriod without a report it gets 460.", null, true, true),
        new(SensorType.HEARTBEAT_VALUECHECK, "A heartbeat whose message is checked like VALUECHECK.", "A regular expression.", true, true),
        new(SensorType.PING, "PNMTD pings the target every Interval seconds: 270 or 470.", "The host name or address to ping.", true, true),
        new(SensorType.VALUECHECK, "The message is matched against a regular expression: 275 when it matches, 475 when not.", "A regular expression.", true, false),
        new(SensorType.ENCAPSULADED, "Takes a JSON array of { Name, Code, Message } by POST; each Name becomes a child sensor (at most 65).", null, true, false),
        new(SensorType.ONE_WITHIN_TIMESPAN, "At the times the descriptor names, OK (280) when at least one report since the last check succeeded, else 480.", "An interval descriptor: \"daily 8\", \"weekly monday\", \"monthly 15\".", true, false),
        new(SensorType.ALL_WITHIN_TIMESPAN, "Like ONE_WITHIN_TIMESPAN, but every report must have succeeded (281 or 481).", "An interval descriptor.", true, false),
        new(SensorType.DNS, "Created by a DNS zone with a host: 211 while its records match, 411 when not.", null, false, false),
        new(SensorType.UDP_NODE, "A device reporting over PNMT-UDP (port 5028/udp): 230 when alive, 231 after a restart, 232 for data; 460 after Interval + GracePeriod without a packet.", null, true, true),
        new(SensorType.SYSTEM, "One of PNMT's own conditions, written by PNMT: the uplink (220, 420), the AI (220, 421), the AI budget (220, 425–427), the AI analysis (220, 221, 423, 424), the RouterOS advisories (220, 422).", null, false, false),
        new(SensorType.MIKROTIK, "A MikroTik router (RouterOS 7.13 or later) that PNMTD polls every Interval seconds over SSH, as the read-only user pnmt: 240 when it answers, 241 after a restart, 242 after an update, 244 when an update is available; 440 when unreachable for longer than the GracePeriod, 441 when access is refused.", null, true, true),
    ];

    public static IReadOnlyList<NotificationRuleTypeInfo> NotificationRuleTypes() =>
    [
        new(NotificationRuleType.ALWAYS, "Every event."),
        new(NotificationRuleType.ONLY_ON_CHANGE, "Events whose code differs from the previous event's."),
        new(NotificationRuleType.ONLY_FAILURES, "Every failure."),
        new(NotificationRuleType.ONLY_FAILURES_ON_CHANGE, "Failures whose code differs from the previous event's."),
        new(NotificationRuleType.ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK, "Failures with a new code, and the first success after a failure."),
    ];
}
