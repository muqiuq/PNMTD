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
        // docs/audit-redmine-zabbix-plan.md §3.8: once a day.
        new("Audit retention", KeyValueKeyEnums.LAST_AUDIT_RETENTION, 24 * 3600, _ => true),
        // docs/audit-redmine-zabbix-plan.md §6.7, §8.5: the copy of linked tickets, every Redmine:SyncMinutes (10).
        new("Redmine sync", KeyValueKeyEnums.LAST_REDMINE_SYNC, 600, c => Redmine.RedmineSetup.Configured(c)),
        // docs/audit-redmine-zabbix-plan.md §7.3, §8.5: the problems every Zabbix:PollSeconds (60), the hosts every Zabbix:HostSyncMinutes (10).
        new("Zabbix problems", KeyValueKeyEnums.LAST_ZABBIX_POLL, 60, c => Zabbix.ZabbixSetup.Configured(c)),
        new("Zabbix hosts", KeyValueKeyEnums.LAST_ZABBIX_HOST_SYNC, 600, c => Zabbix.ZabbixSetup.Configured(c)),
        // docs/cookie-plan.md §19.3: the case book every 30 seconds, with Cookie__Enabled=true.
        new("Cookie's case book", KeyValueKeyEnums.LAST_COOKIE_CASEBOOK, 30, c => c.GetValue("Cookie:Enabled", false)),
        // docs/mcp-plan.md §4.3: the tokens' expiry every 5 minutes, with Mcp__Enabled=true.
        new("MCP tokens", KeyValueKeyEnums.LAST_MCP_TOKEN_EXPIRY, 300, c => c.GetValue("Mcp:Enabled", false)),
        // docs/ssl-certificate-plan.md §5.2: the due checks every 15 seconds, every sensor's evaluation every minute.
        new("Certificate checks", KeyValueKeyEnums.LAST_CERTIFICATE_CHECK, 60, c => c.GetValue("Certificates:Enabled", true)),
        // docs/connectors-plan.md §9.1: the connectors' worker, every 5 seconds and when nudged.
        new("Connectors' worker", KeyValueKeyEnums.LAST_CONNECTOR_WORK, 60, c => c.GetValue("Connectors:Enabled", true)),
        // docs/advanced-sensor-plan.md §7.3: the evaluator's timer every 30 seconds, nudged by every report in between.
        new("Advanced sensors", KeyValueKeyEnums.LAST_ADVANCED_EVALUATION, 60, _ => true),
        // docs/network-map-plan.md §7.3: the schedule's check and the retention, every minute.
        // docs/knowledge-base-extensions-plan.md §10.5: the notification task's second timer, every UserNotifications:IntervalSeconds (30).
        new("User notifications", KeyValueKeyEnums.LAST_USER_NOTIFICATION_RUN, 30, c => c.GetValue("UserNotifications:Enabled", true)),
        new("Network map schedule", KeyValueKeyEnums.LAST_NETWORK_SCAN_SCHEDULE_RUN, 60,
            c => c.GetValue("Mikrotik:Enabled", true) && c.GetValue("NetworkMap:Enabled", true)),
    ];

    public static bool MailboxConfigured(IConfiguration c) =>
        !string.IsNullOrEmpty(c["Mailbox:Host"]) && !string.IsNullOrEmpty(c["Mailbox:Username"]) && !string.IsNullOrEmpty(c["Mailbox:Password"]);

    public static bool UplinkConfigured(IConfiguration c) =>
        !string.IsNullOrEmpty(c["UplinkCheck:SharedKey"])
        && Enumerable.Range(1, 3).Count(i => c[$"UplinkCheck:Url{i}"] is { } url && (url.StartsWith("http://") || url.StartsWith("https://"))) >= 2;

    public SystemInfo Info(bool signedIn)
    {
        if (!signedIn)
        {
            return new SystemInfo(ServerInfo.Identity);
        }
        return new SystemInfo(ServerInfo.Identity, ServerInfo.Version, urls.Configured, Clock.TimeZone.Id,
            configuration["Auth:TotpPolicy"] ?? "Optional", configuration.GetValue("WireGuard:Enabled", false), Redmine.RedmineSetup.Configured(configuration),
            Zabbix.ZabbixSetup.Configured(configuration), HiddenMenus(configuration));
    }

    /// <summary>The menus a deployment doesn't want in the drawer.</summary>
    public static readonly string[] Menus = ["redmine", "mail", "zabbix"];

    /// <summary>
    /// Redmine__HideMenu, Mail__HideMenu, Zabbix__HideMenu: the drawer leaves their entries out (Mail: the mail inputs and the
    /// mail log). The features keep working, and their pages stay reachable through links.
    /// </summary>
    public static IReadOnlyList<string> HiddenMenus(IConfiguration c) =>
        Menus.Where(m => c.GetValue($"{char.ToUpperInvariant(m[0])}{m[1..]}:HideMenu", false)).ToList();

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
        new(PNMTStatusCodes.SYSTEM_OK, "SYSTEM_OK", true, "A system sensor's condition is fine: the uplink answers, the AI is available, no critical RouterOS fix is pending, the files are in place."),
        new(PNMTStatusCodes.WINDOWS_OK, "WINDOWS_OK", true, "A Windows server's heartbeat: the agent is connected and its clock is right. The message names the server, its Windows and the agent."),
        new(PNMTStatusCodes.WINDOWS_RESTARTED, "WINDOWS_RESTARTED", true, "A Windows server restarted (its boot time moved); the message gives the last shutdown's reason."),
        new(PNMTStatusCodes.WINDOWS_CHANGED, "WINDOWS_CHANGED", true, "Something notable changed on a Windows server: services, remote access, local administrators, addresses, the OS build, disks, the audit policy."),
        new(PNMTStatusCodes.WINDOWS_ADMIN_LOGIN, "WINDOWS_ADMIN_LOGIN", true, "An admin logged in to a Windows server by console, RDP, SSH or WinRM (optional, on by default)."),
        new(PNMTStatusCodes.LINUX_OK, "LINUX_OK", true, "A Linux server's heartbeat: the agent is connected and its clock is right. The message names the server, its distribution, its kernel and the agent."),
        new(PNMTStatusCodes.LINUX_RESTARTED, "LINUX_RESTARTED", true, "A Linux server restarted (its boot time moved); the message says whether the last shutdown was clean."),
        new(PNMTStatusCodes.LINUX_CHANGED, "LINUX_CHANGED", true, "Something notable changed on a Linux server: services, remote access, administrators, addresses, the kernel, disks, SSH keys."),
        new(PNMTStatusCodes.LINUX_ADMIN_LOGIN, "LINUX_ADMIN_LOGIN", true, "An admin (root included) logged in to a Linux server over SSH or at the console (optional, on by default)."),
        new(PNMTStatusCodes.LINUX_UPDATES_INSTALLED, "LINUX_UPDATES_INSTALLED", true, "An upgrade PNMT ran on a Linux server succeeded; the message counts the packages and says whether a reboot is required."),
        new(PNMTStatusCodes.DIAGNOSTICS_OK, "DIAGNOSTICS_OK", true, "Every check of a diagnostics set passed."),
        new(PNMTStatusCodes.SSL_OK, "SSL_OK", true, "An SSL certificate is valid under the sensor's trust and checks, with more than its warning days left."),
        new(PNMTStatusCodes.CONNECTORS_OK, "CONNECTORS_OK", true, "No refusal of a 3rd Party Connector's call that may mean a leaked key, for Connectors__QuietMinutes after a 433."),
        new(PNMTStatusCodes.SSL_CHANGED, "SSL_CHANGED", true, "A new SSL certificate that the sensor's rules don't alarm on, usually a renewal; written once."),
        new(PNMTStatusCodes.ADVANCED_OK, "ADVANCED_OK", true, "An Advanced sensor's latest report holds every rule, or the sensor has no rules."),
        new(PNMTStatusCodes.ADVANCED_WARNING, "ADVANCED_WARNING", true, "Only rules marked warn don't hold for an Advanced sensor's latest report; the state stays OK."),
        new(PNMTStatusCodes.DNS_FAILED, "DNS_FAILED", false, "At least one record of the DNS zone differs from what the resolvers answer."),
        new(PNMTStatusCodes.HEARTBEAT_MISSING, "HEARTBEAT_MISSING", false, "No report within the interval and the grace period (an Advanced sensor without a report, too)."),
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
        new(PNMTStatusCodes.AI_BUDGET_80, "AI_BUDGET_80", false, "AI spending reached 80 % of the budget."),
        new(PNMTStatusCodes.AI_BUDGET_90, "AI_BUDGET_90", false, "AI spending reached 90 % of the budget."),
        new(PNMTStatusCodes.AI_BUDGET_REACHED, "AI_BUDGET_REACHED", false, "AI spending reached the budget: AI work waits until the next period."),
        new(PNMTStatusCodes.REDMINE_FAILING, "REDMINE_FAILING", false, "Redmine didn't answer, refused PNMT's API key, or a configured project is missing, twice in a row."),
        new(PNMTStatusCodes.ZABBIX_OK, "ZABBIX_OK", true, "The Zabbix host has no open problem from the sensor's severity up."),
        new(PNMTStatusCodes.ZABBIX_PROBLEM, "ZABBIX_PROBLEM", false, "The Zabbix host has an open problem from the sensor's severity up; the message lists them. A new problem is a new event."),
        new(PNMTStatusCodes.ZABBIX_HOST_MISSING, "ZABBIX_HOST_MISSING", false, "The Zabbix host is no longer monitored in Zabbix: deleted, disabled, or out of the token's host groups."),
        new(PNMTStatusCodes.ZABBIX_HOST_UNAVAILABLE, "ZABBIX_HOST_UNAVAILABLE", false, "An interface of the Zabbix host is unavailable (only when the sensor says so)."),
        new(PNMTStatusCodes.ZABBIX_FAILING, "ZABBIX_FAILING", false, "Zabbix didn't answer, refused PNMT's API token, or a configured host group is missing, twice in a row. Zabbix sensors keep their state meanwhile."),
        new(PNMTStatusCodes.FILE_STORAGE_PROBLEM, "FILE_STORAGE_PROBLEM", false, "The files folder is missing or not writable, its disk has less free space than KnowledgeBase:MinFreeMb, or files are missing or changed."),
        new(PNMTStatusCodes.WINDOWS_AGENT_PROBLEM, "WINDOWS_AGENT_PROBLEM", false, "A Windows server's agent has a problem: its clock is off, it reported failures in three heartbeats in a row, or its key or protocol is wrong."),
        new(PNMTStatusCodes.WINDOWS_CRITICAL_EVENT, "WINDOWS_CRITICAL_EVENT", false, "A new Critical entry in a Windows server's System or Application log (optional, off by default)."),
        new(PNMTStatusCodes.WINDOWS_LOGIN_FAILURES, "WINDOWS_LOGIN_FAILURES", false, "A burst of failed logins on a Windows server from one source or for one user, or an account locked out (optional, on by default)."),
        new(PNMTStatusCodes.LINUX_AGENT_PROBLEM, "LINUX_AGENT_PROBLEM", false, "A Linux server's agent has a problem: its clock is off, it reported failures in three heartbeats in a row, its config.yaml is unsafe, or its key or protocol is wrong."),
        new(PNMTStatusCodes.LINUX_SECURITY_UPDATES, "LINUX_SECURITY_UPDATES", false, "Security updates have been pending on a Linux server for longer than its days (optional, off by default)."),
        new(PNMTStatusCodes.LINUX_CRITICAL_ENTRY, "LINUX_CRITICAL_ENTRY", false, "A new entry at emerg, alert or crit in a Linux server's journal (optional, off by default)."),
        new(PNMTStatusCodes.LINUX_LOGIN_FAILURES, "LINUX_LOGIN_FAILURES", false, "A burst of failed logins against existing accounts on a Linux server from one source or for one user, or a lockout; invalid users never (optional, on by default)."),
        new(PNMTStatusCodes.LINUX_KEY_ADDED, "LINUX_KEY_ADDED", false, "An SSH key that isn't registered as active appeared in an authorized_keys file on a Linux server (optional, on by default)."),
        new(PNMTStatusCodes.LINUX_HIGH_RISK_UPDATE, "LINUX_HIGH_RISK_UPDATE", false, "A pending update on a Linux server fixes a high-risk vulnerability (exploited in the wild, a top EPSS score, or rated critical), and nobody acknowledged it; until it is installed (optional, on by default with the high-risk check)."),
        new(PNMTStatusCodes.LINUX_UPDATE_FAILED, "LINUX_UPDATE_FAILED", false, "On a Linux server, the last upgrade PNMT ran failed (until one succeeds or a person marks it handled), dpkg was interrupted, or the package lists weren't refreshed for days (optional, on by default)."),
        new(PNMTStatusCodes.OS_ADVISORIES_FAILING, "OS_ADVISORIES_FAILING", false, "The OS updates' high-risk check is partly blind: a source wasn't fetched for hours, or one of its roles (what an update fixes, how dangerous a CVE is) has no working source."),
        new(PNMTStatusCodes.DIAGNOSTICS_FAILED, "DIAGNOSTICS_FAILED", false, "At least one check of a diagnostics set failed; the message names them."),
        new(PNMTStatusCodes.CONNECTORS_REFUSED, "CONNECTORS_REFUSED", false, "A 3rd Party Connector's call was refused in a way that may mean a leaked key: a valid key from outside its caller's networks, a refusal by the review, or an address blocked for failed keys."),
        new(PNMTStatusCodes.SSL_ABOUT_TO_EXPIRE, "SSL_ABOUT_TO_EXPIRE", false, "An SSL certificate, or an intermediate of its chain, expires within the sensor's warning days."),
        new(PNMTStatusCodes.SSL_EXPIRED, "SSL_EXPIRED", false, "An SSL certificate, or an intermediate of its chain, has expired."),
        new(PNMTStatusCodes.SSL_CHANGED_UNEXPECTEDLY, "SSL_CHANGED_UNEXPECTEDLY", false, "An SSL certificate, its key or its issuer changed, and the sensor alarms on that; until a person accepts the new certificate, or the accepted one returns."),
        new(PNMTStatusCodes.SSL_INVALID, "SSL_INVALID", false, "An SSL certificate isn't trusted under the sensor's trust mode, doesn't match the name, isn't valid yet, or comes without its intermediate, as the sensor checks."),
        new(PNMTStatusCodes.SSL_UNREACHABLE, "SSL_UNREACHABLE", false, "Two checks of an SSL certificate in a row failed: no such name, refused, timeout, the handshake failed, or no certificate."),
        new(PNMTStatusCodes.ADVANCED_FAILED, "ADVANCED_FAILED", false, "At least one rule marked fail doesn't hold for an Advanced sensor's latest report; the message names them with their values."),
        new(PNMTStatusCodes.ADVANCED_INVALID, "ADVANCED_INVALID", false, "An Advanced sensor's latest report couldn't be read: not JSON, not an object, too large, too deep, or a key twice."),
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
        new(SensorType.SYSTEM, "One of PNMT's own conditions, written by PNMT: the uplink (220, 420), the AI (220, 421), the AI budget (220, 425–427), the AI analysis (220, 221, 423, 424), the RouterOS advisories (220, 422), the file storage (220, 428), Redmine (220, 429), Zabbix (220, 430).", null, false, false),
        new(SensorType.MIKROTIK, "A MikroTik router (RouterOS 7.13 or later) that PNMTD polls every Interval seconds over SSH, as the read-only user pnmt: 240 when it answers, 241 after a restart, 242 after an update, 244 when an update is available; 440 when unreachable for longer than the GracePeriod, 441 when access is refused.", null, true, true),
        new(SensorType.WINDOWS, "A Windows server or PC running the PNMT sensor service, added with a join token (Add server): 250 with each heartbeat, 251 after a restart, 253 for a notable change, 254 for an admin login; 451 for an agent problem, 453 for a new Critical entry, 454 for failed-login bursts; 460 after Interval + GracePeriod without a heartbeat.", null, false, true),
        new(SensorType.DIAGNOSTICS, "A diagnostics set assigned to a server's agent, created by the assignment: 252 when every check passed, 452 when one failed; scheduled ones get 460 without a result within their interval and 2 minutes.", null, false, true),
        new(SensorType.ZABBIX, "A Zabbix host attached to this host, from Zabbix's open problems (each poll, Zabbix__PollSeconds): 290 without a problem from the sensor's severity up, 490 with one, 491 when the host is gone from Zabbix, 492 when an interface is unavailable and the sensor counts that. No events while Zabbix doesn't answer.", null, true, false),
        new(SensorType.SSL_CERTIFICATE, "PNMTD reads the TLS certificate of host:port every Interval seconds (daily by default) and evaluates it every minute, trusting public CAs, private CAs or a pinned certificate: 285 while valid, 286 for a new certificate it accepts, 485 within the sensor's warning days, 486 once expired, 487 for a change it alarms on until accepted, 488 when not trusted or the name doesn't match, 489 after two failed checks in a row.", "host[:port], or an https:// URL", true, true),
        new(SensorType.ADVANCED, "An app POSTs a JSON object to /report with the write token (Authorization: Bearer); PNMT stores it, and its evaluator judges it by the sensor's rules within moments: 295 when every rule holds, 296 when only warning rules don't, 495 when a rule fails, 496 when the report can't be read; with an interval, 460 after Interval + GracePeriod without a report.", null, true, true),
        new(SensorType.LINUX, "A Linux server running the PNMT agent, added with a join token (Add server): 260 with each heartbeat, 261 after a restart, 263 for a notable change, 264 for an admin login; 461 for an agent problem, 462 for pending security updates, 463 for a new critical journal entry, 464 for failed-login bursts, 465 for an unregistered SSH key; 460 after Interval + GracePeriod without a heartbeat.", null, false, true),
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
