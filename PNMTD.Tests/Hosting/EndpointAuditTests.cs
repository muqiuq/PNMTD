using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Auth;

namespace PNMTD.Tests.Hosting;

/// <summary>
/// No endpoint slips in unprotected (docs/webui-migration-plan.md §3.6): the anonymous endpoints are
/// exactly the allowlist, every v1 endpoint names its policy, and every v1 endpoint answers each
/// role as the matrix of §4.10 says. A new endpoint fails these tests until it is added below.
/// </summary>
[TestClass]
public sealed class EndpointAuditTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    /// <summary>§3.6. Static files under /pnmt-admin are served before routing and are anonymous too.</summary>
    private static readonly string[] AnonymousAllowlist =
    [
        "GET /event/{secretToken}/{code}/{message?}",
        "POST /event/{secretToken}/{code}",
        "GET /read/{secretToken}/{option?}",
        "GET /healthz",
        "GET /api/v1/system/info",
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/login/totp",
        "GET /api/v1/auth/me",
        "GET /pnmt-admin/{**path:nonfile}",
        "HEAD /pnmt-admin/{**path:nonfile}",
        // The agents of every platform (docs/windows-sensor-plan.md §9.1): authenticated by the join token or the agent's key.
        "GET /agent/v1/join",
        "POST /agent/v1/join",
        "GET /agent/v1/ws",
        // The agents' files (docs/linux-sensor-plan.md §4.4, §9.7): public as on GitHub, fixed names only, limited per address.
        "GET /agent/v1/download",
        "GET /agent/v1/download/{asset}",
        "HEAD /agent/v1/download/{asset}",
    ];

    /// <summary>
    /// Who may call each v1 endpoint: the lowest role (Viewer, User, Admin). Writes need User;
    /// mail contents need User; users, the audit log, key values and task health need Admin. One's own
    /// session and account are every role's. (The anonymous login steps are in the allowlist above.)
    /// </summary>
    private static readonly Dictionary<string, string> Matrix = new()
    {
        ["GET /api/v1/dashboard"] = Roles.Viewer,
        ["GET /api/v1/search"] = Roles.Viewer,
        ["GET /api/v1/system/uplink"] = Roles.Viewer,
        ["GET /api/v1/system/tasks"] = Roles.Admin,
        ["GET /api/v1/system/status-codes"] = Roles.Viewer,
        ["GET /api/v1/system/sensor-types"] = Roles.Viewer,
        ["GET /api/v1/system/notification-rule-types"] = Roles.Viewer,
        ["GET /api/v1/hosts"] = Roles.Viewer,
        ["GET /api/v1/hosts/{id:guid}"] = Roles.Viewer,
        ["POST /api/v1/hosts"] = Roles.User,
        ["PUT /api/v1/hosts/{id:guid}"] = Roles.User,
        ["DELETE /api/v1/hosts/{id:guid}"] = Roles.User,
        ["GET /api/v1/sensors"] = Roles.Viewer,
        ["GET /api/v1/sensors/{id:guid}"] = Roles.Viewer,
        ["POST /api/v1/sensors"] = Roles.User,
        ["PUT /api/v1/sensors/{id:guid}"] = Roles.User,
        ["DELETE /api/v1/sensors/{id:guid}"] = Roles.User,
        ["POST /api/v1/sensors/{id:guid}/tokens/rotate"] = Roles.User,
        ["POST /api/v1/sensors/lookup"] = Roles.User,
        ["GET /api/v1/sensors/{id:guid}/events"] = Roles.Viewer,
        ["GET /api/v1/sensors/{id:guid}/notification-rules"] = Roles.Viewer,
        ["PUT /api/v1/sensors/{id:guid}/notification-rules/{ruleId:guid}"] = Roles.User,
        ["DELETE /api/v1/sensors/{id:guid}/notification-rules/{ruleId:guid}"] = Roles.User,
        ["GET /api/v1/events"] = Roles.Viewer,
        ["GET /api/v1/events/{id:guid}"] = Roles.Viewer,
        ["GET /api/v1/events/state-changes"] = Roles.Viewer,
        ["GET /api/v1/notification-rules"] = Roles.Viewer,
        ["GET /api/v1/notification-rules/{id:guid}"] = Roles.Viewer,
        ["POST /api/v1/notification-rules"] = Roles.User,
        ["PUT /api/v1/notification-rules/{id:guid}"] = Roles.User,
        ["DELETE /api/v1/notification-rules/{id:guid}"] = Roles.User,
        ["GET /api/v1/notification-rules/{id:guid}/sensors"] = Roles.Viewer,
        ["PUT /api/v1/notification-rules/{id:guid}/sensors/{sensorId:guid}"] = Roles.User,
        ["DELETE /api/v1/notification-rules/{id:guid}/sensors/{sensorId:guid}"] = Roles.User,
        ["GET /api/v1/dns-zones"] = Roles.Viewer,
        ["GET /api/v1/dns-zones/{id:guid}"] = Roles.Viewer,
        ["POST /api/v1/dns-zones"] = Roles.User,
        ["PUT /api/v1/dns-zones/{id:guid}"] = Roles.User,
        ["DELETE /api/v1/dns-zones/{id:guid}"] = Roles.User,
        ["POST /api/v1/dns-zones/{id:guid}/force-update"] = Roles.User,
        ["PUT /api/v1/dns-zones/{id:guid}/records/{recordId:guid}"] = Roles.User,
        ["GET /api/v1/dns-zones/{id:guid}/log"] = Roles.Viewer,
        ["GET /api/v1/dns-zones/log"] = Roles.Viewer,
        ["GET /api/v1/mail-input-rules"] = Roles.Viewer,
        ["GET /api/v1/mail-input-rules/{id:guid}"] = Roles.Viewer,
        ["POST /api/v1/mail-input-rules"] = Roles.User,
        ["PUT /api/v1/mail-input-rules/{id:guid}"] = Roles.User,
        ["DELETE /api/v1/mail-input-rules/{id:guid}"] = Roles.User,
        ["GET /api/v1/mail-log"] = Roles.User,
        ["GET /api/v1/mail-log/{id:guid}"] = Roles.User,
        ["GET /api/v1/auth/me"] = Roles.Viewer,
        ["POST /api/v1/auth/logout"] = Roles.Viewer,
        ["POST /api/v1/account/password"] = Roles.Viewer,
        ["POST /api/v1/account/sessions/revoke"] = Roles.Viewer,
        ["POST /api/v1/account/totp/setup"] = Roles.Viewer,
        ["POST /api/v1/account/totp/confirm"] = Roles.Viewer,
        ["POST /api/v1/account/totp/disable"] = Roles.Viewer,
        ["GET /api/v1/users"] = Roles.Admin,
        ["GET /api/v1/users/{id:guid}"] = Roles.Admin,
        ["POST /api/v1/users"] = Roles.Admin,
        ["PUT /api/v1/users/{id:guid}"] = Roles.Admin,
        ["DELETE /api/v1/users/{id:guid}"] = Roles.Admin,
        ["POST /api/v1/users/{id:guid}/reset-password"] = Roles.Admin,
        ["POST /api/v1/users/{id:guid}/reset-totp"] = Roles.Admin,
        ["POST /api/v1/users/{id:guid}/unlock"] = Roles.Admin,
        ["GET /api/v1/audit-log"] = Roles.Admin,
        ["GET /api/v1/audit-log/types"] = Roles.Admin,
        ["GET /api/v1/key-values"] = Roles.Admin,
        ["GET /api/v1/key-values/{id:guid}"] = Roles.Admin,
        ["POST /api/v1/key-values"] = Roles.Admin,
        ["PUT /api/v1/key-values/{id:guid}"] = Roles.Admin,
        ["DELETE /api/v1/key-values/{id:guid}"] = Roles.Admin,
        // UDP nodes (docs/udp-iot-plan.md §5): a node's own endpoints as the sensor's, the rest Admin.
        ["PUT /api/v1/sensors/{id:guid}/udp/key"] = Roles.User,
        ["POST /api/v1/sensors/{id:guid}/udp/regenerate-key"] = Roles.User,
        ["POST /api/v1/sensors/{id:guid}/udp/reset-counter"] = Roles.User,
        ["POST /api/v1/sensors/{id:guid}/udp/reset-session"] = Roles.User,
        ["GET /api/v1/sensors/{id:guid}/udp/last-packets"] = Roles.Viewer,
        ["GET /api/v1/sensors/{id:guid}/udp/rejections"] = Roles.Viewer,
        ["GET /api/v1/sensors/{id:guid}/udp/commands"] = Roles.Viewer,
        ["GET /api/v1/sensors/{id:guid}/udp/commands/{commandId:guid}/log"] = Roles.Viewer,
        ["POST /api/v1/sensors/{id:guid}/udp/commands"] = Roles.User,
        ["DELETE /api/v1/sensors/{id:guid}/udp/commands/{commandId:guid}"] = Roles.User,
        ["GET /api/v1/sensors/{id:guid}/udp/ota-jobs"] = Roles.Viewer,
        // MikroTik routers (docs/mikrotik-plan.md §10): the join script holds keys and exports reveal the network (CanWrite); the backup list is the sensor's.
        ["GET /api/v1/sensors/{id:guid}/mikrotik/join-script"] = Roles.User,
        ["GET /api/v1/sensors/{id:guid}/mikrotik/removal-script"] = Roles.User,
        ["POST /api/v1/sensors/{id:guid}/mikrotik/poll"] = Roles.User,
        ["POST /api/v1/sensors/{id:guid}/mikrotik/host-key/accept"] = Roles.Admin,
        ["GET /api/v1/sensors/{id:guid}/mikrotik/backups"] = Roles.Viewer,
        ["POST /api/v1/sensors/{id:guid}/mikrotik/backups"] = Roles.User,
        ["GET /api/v1/sensors/{id:guid}/mikrotik/backups/{backupId:guid}"] = Roles.User,
        ["GET /api/v1/sensors/{id:guid}/mikrotik/backups/{backupId:guid}/diff"] = Roles.User,
        ["GET /api/v1/sensors/{id:guid}/mikrotik/backups/{backupId:guid}/download"] = Roles.User,
        ["DELETE /api/v1/sensors/{id:guid}/mikrotik/backups/{backupId:guid}"] = Roles.Admin,
        ["POST /api/v1/sensors/{id:guid}/mikrotik/regenerate-keys"] = Roles.User,
        ["GET /api/v1/wireguard/status"] = Roles.Admin,
        ["GET /api/v1/wireguard/network"] = Roles.Admin,
        ["PUT /api/v1/wireguard/network"] = Roles.Admin,
        ["GET /api/v1/wireguard/peers"] = Roles.Admin,
        ["GET /api/v1/wireguard/tunnels"] = Roles.Admin,
        ["POST /api/v1/wireguard/tunnels"] = Roles.Admin,
        ["PUT /api/v1/wireguard/tunnels/{id:guid}"] = Roles.Admin,
        ["DELETE /api/v1/wireguard/tunnels/{id:guid}"] = Roles.Admin,
        ["POST /api/v1/wireguard/tunnels/{id:guid}/regenerate-keys"] = Roles.Admin,
        ["GET /api/v1/wireguard/tunnels/{id:guid}/config"] = Roles.Admin,
        ["GET /api/v1/account/tunnels"] = Roles.Viewer,
        ["GET /api/v1/account/tunnels/{id:guid}/config"] = Roles.Viewer,
        ["GET /api/v1/sensors/{id:guid}/mikrotik/jobs"] = Roles.Viewer,
        ["GET /api/v1/sensors/{id:guid}/mikrotik/jobs/{jobId:guid}"] = Roles.Viewer,
        ["GET /api/v1/sensors/{id:guid}/mikrotik/changelog"] = Roles.Viewer,
        ["POST /api/v1/mikrotik/jobs"] = Roles.Admin,
        ["GET /api/v1/mikrotik/jobs"] = Roles.Admin,
        ["GET /api/v1/mikrotik/jobs/{id:guid}"] = Roles.Admin,
        ["DELETE /api/v1/mikrotik/jobs/{id:guid}"] = Roles.Admin,
        ["GET /api/v1/mikrotik/status"] = Roles.Admin,
        ["GET /api/v1/mikrotik/routers"] = Roles.Admin,
        ["GET /api/v1/mikrotik/ssh-keys"] = Roles.Admin,
        ["POST /api/v1/mikrotik/ssh-keys/rotate"] = Roles.Admin,
        ["POST /api/v1/mikrotik/ssh-keys/retire"] = Roles.Admin,
        // Backups (docs/mikrotik-backup-plan.md §2.2): the new-router form reads the default.
        ["GET /api/v1/mikrotik/backup-settings"] = Roles.User,
        ["PUT /api/v1/mikrotik/backup-settings"] = Roles.Admin,
        ["POST /api/v1/mikrotik/routers/backup-settings"] = Roles.Admin,
        ["POST /api/v1/mikrotik/backup-keys"] = Roles.Admin,
        ["POST /api/v1/mikrotik/backup-keys/retire"] = Roles.Admin,
        // Remote commands (§8): as pnmt for Users; as an admin for Admins, checked in the service.
        ["POST /api/v1/mikrotik/commands"] = Roles.User,
        ["GET /api/v1/mikrotik/commands"] = Roles.User,
        ["GET /api/v1/mikrotik/commands/{id:guid}"] = Roles.User,
        ["GET /api/v1/mikrotik/commands/batches/{batchId:guid}"] = Roles.User,
        ["DELETE /api/v1/mikrotik/commands/{id:guid}"] = Roles.User,
        ["POST /api/v1/mikrotik/commands/{id:guid}/redact"] = Roles.Admin,
        ["GET /api/v1/udp/status"] = Roles.Admin,
        ["GET /api/v1/udp/nodes"] = Roles.Admin,
        ["GET /api/v1/udp/command-log"] = Roles.Admin,
        ["GET /api/v1/udp/unknown-senders"] = Roles.Admin,
        ["GET /api/v1/udp/firmware"] = Roles.Admin,
        ["POST /api/v1/udp/firmware"] = Roles.Admin,
        ["PUT /api/v1/udp/firmware/{id:guid}"] = Roles.Admin,
        ["DELETE /api/v1/udp/firmware/{id:guid}"] = Roles.Admin,
        ["GET /api/v1/udp/firmware/{id:guid}/content"] = Roles.Admin,
        ["GET /api/v1/udp/ota-jobs"] = Roles.Admin,
        ["POST /api/v1/udp/ota-jobs"] = Roles.Admin,
        ["DELETE /api/v1/udp/ota-jobs/{id:guid}"] = Roles.Admin,
        ["GET /api/v1/udp/protocol/examples"] = Roles.Viewer,
        ["GET /api/v1/udp/protocol/build"] = Roles.Viewer,
        // The debug page (docs/ai-operations-plan.md §8).
        ["GET /api/v1/debug"] = Roles.Admin,
        ["POST /api/v1/debug/pushover"] = Roles.Admin,
        ["POST /api/v1/debug/mail"] = Roles.Admin,
        // The uplink page (docs/ai-operations-plan.md §7.3).
        ["GET /api/v1/uplink"] = Roles.Viewer,
        ["GET /api/v1/uplink/checks"] = Roles.Viewer,
        ["GET /api/v1/uplink/outages"] = Roles.Viewer,
        ["POST /api/v1/uplink/check"] = Roles.Admin,
        // Importance and the Notification Manager (docs/ai-operations-plan.md §11).
        ["PUT /api/v1/hosts/importance"] = Roles.User,
        ["PUT /api/v1/sensors/importance"] = Roles.User,
        ["GET /api/v1/notifications/matrix"] = Roles.Viewer,
        ["GET /api/v1/notifications/coverage"] = Roles.Viewer,
        ["POST /api/v1/notifications/assign"] = Roles.User,
        ["PUT /api/v1/notification-rules/{id:guid}/scopes"] = Roles.User,
        ["POST /api/v1/notification-rules/{id:guid}/scopes/preview"] = Roles.User,
        ["PUT /api/v1/notification-rules/{id:guid}/exclusions/{sensorId:guid}"] = Roles.User,
        ["DELETE /api/v1/notification-rules/{id:guid}/exclusions/{sensorId:guid}"] = Roles.User,
        // The AI (docs/ai-operations-plan.md §14): Admin, because requests hold mail contents.
        ["GET /api/v1/ai/status"] = Roles.Admin,
        ["GET /api/v1/ai/requests"] = Roles.Admin,
        ["GET /api/v1/ai/requests/{id:long}"] = Roles.Admin,
        ["GET /api/v1/ai/work"] = Roles.Admin,
        ["POST /api/v1/ai/work/{id:guid}/retry"] = Roles.Admin,
        ["POST /api/v1/ai/work/{id:guid}/cancel"] = Roles.Admin,
        ["POST /api/v1/debug/ai"] = Roles.Admin,
        ["POST /api/v1/ai/mail/backfill"] = Roles.Admin,
        ["GET /api/v1/mikrotik/releases"] = Roles.Admin,
        ["GET /api/v1/ai/analysis"] = Roles.Viewer,
        ["GET /api/v1/ai/analysis/{id:guid}"] = Roles.Viewer,
        ["POST /api/v1/ai/analysis/run"] = Roles.Admin,
        // Decisions (docs/ai-operations-plan.md §5.3): the User role, as the mail log.
        ["GET /api/v1/decisions"] = Roles.User,
        ["GET /api/v1/decisions/count"] = Roles.User,
        ["GET /api/v1/decisions/{id:guid}"] = Roles.User,
        ["POST /api/v1/decisions/{id:guid}/decide"] = Roles.User,
        // Servers with an agent, of both platforms (docs/windows-sensor-plan.md §9.2; docs/linux-sensor-plan.md §10, under
        // /api/v1/windows/ before L1): the agent log, revoking and tokens are Admin, requests CanWrite.
        ["GET /api/v1/agents/servers"] = Roles.Viewer,
        ["GET /api/v1/agents/servers/{id:guid}"] = Roles.Viewer,
        ["GET /api/v1/agents/servers/{id:guid}/logins"] = Roles.User,
        ["GET /api/v1/agents/servers/{id:guid}/changes"] = Roles.Viewer,
        ["GET /api/v1/agents/servers/{id:guid}/log"] = Roles.Admin,
        ["POST /api/v1/agents/servers/{id:guid}/collect"] = Roles.User,
        ["POST /api/v1/agents/servers/{id:guid}/send-log"] = Roles.Admin,
        ["PUT /api/v1/agents/servers/{id:guid}/settings"] = Roles.User,
        ["POST /api/v1/agents/servers/{id:guid}/revoke"] = Roles.Admin,
        ["POST /api/v1/agents/servers/{id:guid}/rejoin-token"] = Roles.Admin,
        // Linux (docs/linux-sensor-plan.md §6.3, §8.4): a server's SSH keys (key comments and accounts: CanWrite), its accounts for scripts.
        ["GET /api/v1/agents/servers/{id:guid}/ssh-keys"] = Roles.User,
        ["GET /api/v1/agents/servers/{id:guid}/run-as-accounts"] = Roles.Admin,
        ["GET /api/v1/agents/join-tokens"] = Roles.Admin,
        ["POST /api/v1/agents/join-tokens"] = Roles.Admin,
        ["DELETE /api/v1/agents/join-tokens/{id:guid}"] = Roles.Admin,
        ["GET /api/v1/agents/agent-log"] = Roles.Admin,
        ["GET /api/v1/agents/downloads"] = Roles.Admin,
        ["POST /api/v1/agents/downloads/fetch"] = Roles.Admin,
        // Scripts (docs/windows-sensor-plan.md §7; docs/linux-sensor-plan.md §8): Admin only.
        ["GET /api/v1/agents/scripts"] = Roles.Admin,
        ["GET /api/v1/agents/scripts/{id:guid}"] = Roles.Admin,
        ["GET /api/v1/agents/scripts/template"] = Roles.Admin,
        ["POST /api/v1/agents/scripts"] = Roles.Admin,
        ["PUT /api/v1/agents/scripts/{id:guid}"] = Roles.Admin,
        ["POST /api/v1/agents/scripts/{id:guid}/parse"] = Roles.Admin,
        ["DELETE /api/v1/agents/scripts/{id:guid}"] = Roles.Admin,
        ["POST /api/v1/agents/scripts/{id:guid}/run"] = Roles.Admin,
        ["GET /api/v1/agents/script-runs"] = Roles.Admin,
        ["GET /api/v1/agents/script-runs/{id:guid}"] = Roles.Admin,
        ["POST /api/v1/agents/script-runs/{id:guid}/redact"] = Roles.Admin,
        // What stays Windows' own (docs/linux-sensor-plan.md §3.3): the typed sections, WMI, the scripts registered on a server
        // and the run-as accounts.
        ["GET /api/v1/windows/servers/{id:guid}/sections/{section}"] = Roles.Viewer,
        ["GET /api/v1/windows/servers/{id:guid}/wmi"] = Roles.Admin,
        ["PUT /api/v1/windows/servers/{id:guid}/wmi"] = Roles.Admin,
        ["GET /api/v1/windows/servers/{id:guid}/local-scripts"] = Roles.Admin,
        ["POST /api/v1/windows/servers/{id:guid}/local-scripts/run"] = Roles.Admin,
        ["GET /api/v1/windows/run-as"] = Roles.Admin,
        ["POST /api/v1/windows/run-as"] = Roles.Admin,
        ["PUT /api/v1/windows/run-as/{id:guid}"] = Roles.Admin,
        ["DELETE /api/v1/windows/run-as/{id:guid}"] = Roles.Admin,
        // Linux's own (docs/linux-sensor-plan.md §10): the typed sections; access, journal and ssh-keys check CanWrite inside.
        ["GET /api/v1/linux/servers/{id:guid}/sections/{section}"] = Roles.Viewer,
        // The SSH key manager (docs/linux-sensor-plan.md §6.4, §6.5, §10): Admin, so nobody vouches for a key by registering it; a
        // user's own keys for every role.
        ["GET /api/v1/ssh-keys"] = Roles.Admin,
        ["GET /api/v1/ssh-keys/summary"] = Roles.Admin,
        ["GET /api/v1/ssh-keys/unregistered"] = Roles.Admin,
        ["POST /api/v1/ssh-keys/parse"] = Roles.Admin,
        ["POST /api/v1/ssh-keys"] = Roles.Admin,
        ["GET /api/v1/ssh-keys/{id:guid}"] = Roles.Admin,
        ["PUT /api/v1/ssh-keys/{id:guid}"] = Roles.Admin,
        ["POST /api/v1/ssh-keys/{id:guid}/retire"] = Roles.Admin,
        ["POST /api/v1/ssh-keys/{id:guid}/reactivate"] = Roles.Admin,
        ["DELETE /api/v1/ssh-keys/{id:guid}"] = Roles.Admin,
        ["POST /api/v1/ssh-keys/{id:guid}/removal-items"] = Roles.Admin,
        ["GET /api/v1/ssh-keys/users/{userId:guid}"] = Roles.Admin,
        ["POST /api/v1/ssh-keys/users/{userId:guid}/removal-items"] = Roles.Admin,
        ["POST /api/v1/ssh-keys/users/{userId:guid}/offboard"] = Roles.Admin,
        ["POST /api/v1/ssh-keys/remove/preview"] = Roles.Admin,
        ["POST /api/v1/ssh-keys/remove"] = Roles.Admin,
        ["GET /api/v1/ssh-keys/mine"] = Roles.Viewer,
        ["GET /api/v1/ssh-key-removals"] = Roles.Admin,
        // Diagnostics (docs/windows-sensor-plan.md §9.2): reading CanRead, changing Admin, Run now CanWrite.
        ["GET /api/v1/diagnostics/sets"] = Roles.Viewer,
        ["GET /api/v1/diagnostics/sets/{id:guid}"] = Roles.Viewer,
        ["POST /api/v1/diagnostics/sets"] = Roles.Admin,
        ["PUT /api/v1/diagnostics/sets/{id:guid}"] = Roles.Admin,
        ["DELETE /api/v1/diagnostics/sets/{id:guid}"] = Roles.Admin,
        ["GET /api/v1/diagnostics/assignments"] = Roles.Viewer,
        ["POST /api/v1/diagnostics/assignments"] = Roles.Admin,
        ["PUT /api/v1/diagnostics/assignments/{id:guid}"] = Roles.Admin,
        ["DELETE /api/v1/diagnostics/assignments/{id:guid}"] = Roles.Admin,
        ["POST /api/v1/diagnostics/assignments/{id:guid}/run"] = Roles.User,
        ["GET /api/v1/diagnostics/runs"] = Roles.Viewer,
        ["GET /api/v1/diagnostics/runs/{id:guid}"] = Roles.Viewer,
        // Locations (docs/locations-knowledge-plan.md §8.1, §9): every role reads, Users write, Admins delete and define fields.
        ["GET /api/v1/locations"] = Roles.Viewer,
        ["GET /api/v1/locations/{id:guid}"] = Roles.Viewer,
        ["POST /api/v1/locations"] = Roles.User,
        ["PUT /api/v1/locations/{id:guid}"] = Roles.User,
        ["POST /api/v1/locations/{id:guid}/move"] = Roles.User,
        ["DELETE /api/v1/locations/{id:guid}"] = Roles.Admin,
        ["PUT /api/v1/locations/{id:guid}/values/{fieldId:guid}"] = Roles.User,
        ["GET /api/v1/locations/{id:guid}/history"] = Roles.Viewer,
        ["GET /api/v1/locations/suggestions"] = Roles.User,
        ["POST /api/v1/locations/from-text"] = Roles.User,
        ["PUT /api/v1/hosts/location"] = Roles.User,
        ["GET /api/v1/location-fields"] = Roles.Viewer,
        ["POST /api/v1/location-fields"] = Roles.Admin,
        ["PUT /api/v1/location-fields/{id:guid}"] = Roles.Admin,
        ["PUT /api/v1/location-fields/order"] = Roles.Admin,
        ["POST /api/v1/location-fields/{id:guid}/archive"] = Roles.Admin,
        ["POST /api/v1/location-fields/{id:guid}/restore"] = Roles.Admin,
        ["DELETE /api/v1/location-fields/{id:guid}"] = Roles.Admin,
        // The knowledge base (docs/locations-knowledge-plan.md §8.1, §9): every role reads and counts a view; Users write, mark
        // sensitive and run AI tags; Admins delete archived pages, redact, and rename and delete tags.
        ["GET /api/v1/kb/pages"] = Roles.Viewer,
        ["POST /api/v1/kb/pages"] = Roles.User,
        ["GET /api/v1/kb/pages/{number:int}"] = Roles.Viewer,
        ["PUT /api/v1/kb/pages/{id:guid}"] = Roles.User,
        ["POST /api/v1/kb/pages/{id:guid}/archive"] = Roles.User,
        ["POST /api/v1/kb/pages/{id:guid}/restore"] = Roles.User,
        ["DELETE /api/v1/kb/pages/{id:guid}"] = Roles.Admin,
        ["POST /api/v1/kb/pages/{id:guid}/views"] = Roles.Viewer,
        ["POST /api/v1/kb/pages/{id:guid}/editing"] = Roles.User,
        ["GET /api/v1/kb/pages/{id:guid}/revisions"] = Roles.Viewer,
        ["GET /api/v1/kb/pages/{id:guid}/revisions/{number:int}"] = Roles.Viewer,
        ["GET /api/v1/kb/pages/{id:guid}/diff"] = Roles.Viewer,
        ["POST /api/v1/kb/pages/{id:guid}/revisions/{number:int}/restore"] = Roles.User,
        ["POST /api/v1/kb/pages/{id:guid}/redact"] = Roles.Admin,
        ["PUT /api/v1/kb/pages/{id:guid}/sensitive"] = Roles.User,
        ["POST /api/v1/kb/pages/{id:guid}/ai-tags"] = Roles.User,
        ["GET /api/v1/kb/pages/{id:guid}/ai-tags/{workId:guid}"] = Roles.User,
        ["GET /api/v1/kb/related"] = Roles.Viewer,
        ["GET /api/v1/kb/tags"] = Roles.Viewer,
        ["PUT /api/v1/kb/tags/{id:guid}"] = Roles.Admin,
        ["DELETE /api/v1/kb/tags/{id:guid}"] = Roles.Admin,
        // Uploaded files (docs/locations-knowledge-plan.md §9): uploading is a write, reading every role's, the overview and deleting the Admin's.
        ["POST /api/v1/files"] = Roles.User,
        ["GET /api/v1/files/{id:guid}/{name}"] = Roles.Viewer,
        ["GET /api/v1/files"] = Roles.Admin,
        ["DELETE /api/v1/files/{id:guid}"] = Roles.Admin,
    };

    private sealed record EndpointInfo(string Key, string Method, string Pattern, RouteEndpoint Endpoint);

    private static List<EndpointInfo> Endpoints()
    {
        var sources = app.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
        return sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var pattern = "/" + e.RoutePattern.RawText!.TrimStart('/');
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
                return methods.Select(m => new EndpointInfo($"{m} {pattern}", m, pattern, e));
            })
            .ToList();
    }

    [TestMethod]
    public void AnonymousEndpointsAreExactlyTheAllowlist()
    {
        var anonymous = Endpoints()
            .Where(e => e.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        CollectionAssert.AreEqual(AnonymousAllowlist.Order(StringComparer.Ordinal).ToList(), anonymous,
            "Anonymous: " + string.Join(", ", anonymous));
    }

    [TestMethod]
    public void EveryV1EndpointNamesItsPolicy()
    {
        var known = new[] { Policies.CanRead, Policies.CanWrite, Policies.Admin };
        var v1 = Endpoints().Where(e => e.Pattern.StartsWith("/api/v1/", StringComparison.Ordinal) && !e.Pattern.Contains("{**rest}")).ToList();
        Assert.IsGreaterThan(30, v1.Count);
        foreach (var e in v1.Where(e => e.Key != "GET /api/v1/system/info"))
        {
            var policies = e.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            Assert.IsTrue(policies.Count > 0 && policies.All(p => known.Contains(p)), $"{e.Key}: policies {string.Join(",", policies)}");
            // Writes need CanWrite, except one's own session and account, which every role manages, and a page's view, which
            // every role counts (docs/locations-knowledge-plan.md §6.6).
            var selfService = e.Pattern.StartsWith("/api/v1/auth/", StringComparison.Ordinal)
                || e.Pattern.StartsWith("/api/v1/account/", StringComparison.Ordinal)
                || e.Key == "POST /api/v1/kb/pages/{id:guid}/views";
            if (e.Method != "GET" && !selfService)
            {
                Assert.IsTrue(policies.Contains(Policies.CanWrite) || policies.Contains(Policies.Admin), $"{e.Key} writes without CanWrite");
            }
        }
    }

    [TestMethod]
    public void TheRoleMatrixCoversEveryV1Endpoint()
    {
        var v1 = Endpoints()
            .Where(e => e.Pattern.StartsWith("/api/v1/", StringComparison.Ordinal) && !e.Pattern.Contains("{**rest}")
                && e.Key is not ("GET /api/v1/system/info" or "POST /api/v1/auth/login" or "POST /api/v1/auth/login/totp"))
            .Select(e => e.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        CollectionAssert.AreEqual(Matrix.Keys.Order(StringComparer.Ordinal).ToList(), v1,
            "Not in the matrix: " + string.Join(", ", v1.Except(Matrix.Keys)) + "; gone: " + string.Join(", ", Matrix.Keys.Except(v1)));
    }

    /// <summary>
    /// Every endpoint × signed out, Viewer, User, Admin: 401, 403, or anything else for a role that
    /// may. Ids are random and bodies empty, so the allowed calls end in 404 or 400 and change nothing.
    /// </summary>
    [TestMethod]
    public async Task EveryV1EndpointAnswersEachRoleAsTheMatrixSays()
    {
        var rank = new Dictionary<string, int> { [Roles.Viewer] = 1, [Roles.User] = 2, [Roles.Admin] = 3 };
        var failures = new List<string>();
        foreach (var (key, minimum) in Matrix)
        {
            var method = key[..key.IndexOf(' ')];
            // A long id (an AI request's) and a page's number need a number to match their route.
            var path = System.Text.RegularExpressions.Regex.Replace(key[(key.IndexOf(' ') + 1)..], @"\{[^}]+\}",
                m => m.Value.EndsWith(":long}", StringComparison.Ordinal) ? Random.Shared.NextInt64(1L << 40, 1L << 50).ToString()
                    : m.Value.EndsWith(":int}", StringComparison.Ordinal) ? Random.Shared.Next(1 << 24, int.MaxValue).ToString()
                    : Guid.NewGuid().ToString());
            foreach (var role in new string?[] { null, Roles.Viewer, Roles.User, Roles.Admin })
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT")
                {
                    request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                }
                using var response = await app.Client(role).SendAsync(request);
                var status = response.StatusCode;
                var expected = role is null ? "401" : rank[role] >= rank[minimum] ? "allowed" : "403";
                var ok = expected switch
                {
                    "401" => status == HttpStatusCode.Unauthorized,
                    "403" => status == HttpStatusCode.Forbidden,
                    _ => status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && (int)status < 500,
                };
                if (!ok)
                {
                    failures.Add($"{key} as {role ?? "anonymous"}: {(int)status}, expected {expected}");
                }
            }
        }
        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
    }
}
