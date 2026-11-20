using Microsoft.EntityFrameworkCore;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Auth;
using PNMTD.Auth.Rights;

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
        // Was this you? (docs/cookie-access-plan.md §6.6, §9): a one-time token answers one question; rate-limited.
        "GET /api/v1/access/was-this-you/{token}",
        "POST /api/v1/access/was-this-you/{token}",
        // The advanced sensors' reports (docs/advanced-sensor-plan.md §4.1): authenticated by the sensor's write token.
        "POST /report",
        "POST /report/{token}",
        // 3rd Party Connectors (docs/connectors-plan.md §4.1): authenticated by the caller's key.
        "POST /connector/v1/{slug}",
        "GET /connector/v1/{slug}",
        "GET /connector/v1/{slug}/tickets/{ticket}",
        "GET /connector/v1",
    ];

    /// <summary>
    /// What each v1 endpoint needs (docs/audit-redmine-zabbix-plan.md §5.2, Appendix A): a level in an area ("Monitoring:Edit"),
    /// either of two ("KnowledgeBase:Edit|Locations:Edit"), the admin flag ("admin"), or only a session ("signed-in": one's
    /// own account). And who could call it before 1.4, the lowest role: Admin, User and Viewer became the admin flag and the
    /// groups Operators and Viewers, which must still answer the same (§5.3). (The anonymous login steps are in the allowlist.)
    /// </summary>
    private static readonly Dictionary<string, (string Right, string Before)> Matrix = new()
    {
        ["GET /api/v1/dashboard"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/search"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/system/uplink"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/system/tasks"] = ("System:Manage", Roles.Admin),
        // The two outbound logs and the Redmine queue (docs/redmine-queue-outbound-plan.md §7.4, §8): System health's right.
        ["GET /api/v1/system/outbound/notifications"] = ("System:Manage", Roles.Admin),
        ["GET /api/v1/system/outbound/notifications/{id:long}"] = ("System:Manage", Roles.Admin),
        ["GET /api/v1/system/outbound/redmine"] = ("System:Manage", Roles.Admin),
        ["GET /api/v1/system/outbound/redmine/{id:long}"] = ("System:Manage", Roles.Admin),
        ["GET /api/v1/system/outbound/live"] = ("System:Manage", Roles.Admin),
        ["GET /api/v1/system/status-codes"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/system/sensor-types"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/system/notification-rule-types"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/hosts"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/hosts/{id:guid}"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/hosts/{id:guid}/cookie-notes"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/hosts"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/hosts/{id:guid}"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/hosts/{id:guid}"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/sensors"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/cookie-notes"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/sensors"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/sensors/{id:guid}"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/sensors/{id:guid}"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/sensors/{id:guid}/tokens/rotate"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/sensors/lookup"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/sensors/{id:guid}/events"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/notification-rules"] = ("Monitoring:View", Roles.Viewer),
        ["PUT /api/v1/sensors/{id:guid}/notification-rules/{ruleId:guid}"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/sensors/{id:guid}/notification-rules/{ruleId:guid}"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/events"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/events/{id:guid}"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/events/state-changes"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/notification-rules"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/notification-rules/{id:guid}"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/notification-rules"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/notification-rules/{id:guid}"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/notification-rules/{id:guid}"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/notification-rules/people"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/notification-rules/{id:guid}/sensors"] = ("Monitoring:View", Roles.Viewer),
        ["PUT /api/v1/notification-rules/{id:guid}/sensors/{sensorId:guid}"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/notification-rules/{id:guid}/sensors/{sensorId:guid}"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/dns-zones"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/dns-zones/{id:guid}"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/dns-zones"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/dns-zones/{id:guid}"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/dns-zones/{id:guid}"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/dns-zones/{id:guid}/force-update"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/dns-zones/{id:guid}/records/{recordId:guid}"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/dns-zones/{id:guid}/log"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/dns-zones/log"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/mail-input-rules"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/mail-input-rules/{id:guid}"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/mail-input-rules"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/mail-input-rules/{id:guid}"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/mail-input-rules/{id:guid}"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/mail-log"] = ("Mail:Read", Roles.User),
        ["GET /api/v1/mail-log/{id:guid}"] = ("Mail:Read", Roles.User),
        ["GET /api/v1/auth/me"] = ("signed-in", Roles.Viewer),
        ["POST /api/v1/auth/logout"] = ("signed-in", Roles.Viewer),
        // The day's 2FA code (docs/mcp-plan.md §3.3).
        ["POST /api/v1/auth/totp/daily"] = ("signed-in", Roles.Viewer),
        // One's own MCP tokens (docs/mcp-plan.md §4.5): the dashboard's card.
        ["GET /api/v1/account/mcp"] = ("signed-in", Roles.Viewer),
        ["POST /api/v1/account/mcp/{kind:regex(^(read|write)$)}/on"] = ("signed-in", Roles.Viewer),
        ["POST /api/v1/account/mcp/{kind:regex(^(read|write)$)}/off"] = ("signed-in", Roles.Viewer),
        ["POST /api/v1/account/mcp/{kind:regex(^(read|write)$)}/regenerate"] = ("signed-in", Roles.Viewer),
        ["POST /api/v1/account/mcp/write/delete-window"] = ("signed-in", Roles.Viewer),
        ["DELETE /api/v1/account/mcp/write/delete-window"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/account/mcp/log"] = ("signed-in", Roles.Viewer),
        // Admin → MCP (docs/mcp-plan.md §10): the documentation for everyone, an entry of one's own too.
        ["GET /api/v1/mcp/catalog"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/mcp/log"] = ("Mcp:View", Roles.Admin),
        ["GET /api/v1/mcp/log/{id:long}"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/mcp/tokens"] = ("Mcp:View", Roles.Admin),
        ["GET /api/v1/mcp/status"] = ("Mcp:View", Roles.Admin),
        ["POST /api/v1/mcp/tokens/revoke-all"] = ("Mcp:Manage", Roles.Admin),
        ["POST /api/v1/mcp/tokens/{id:guid}/off"] = ("Mcp:Manage", Roles.Admin),
        ["POST /api/v1/account/password"] = ("signed-in", Roles.Viewer),
        ["POST /api/v1/account/sessions/revoke"] = ("signed-in", Roles.Viewer),
        ["POST /api/v1/account/totp/setup"] = ("signed-in", Roles.Viewer),
        ["POST /api/v1/account/totp/confirm"] = ("signed-in", Roles.Viewer),
        ["POST /api/v1/account/totp/disable"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/users"] = ("admin", Roles.Admin),
        ["GET /api/v1/users/{id:guid}"] = ("admin", Roles.Admin),
        ["POST /api/v1/users"] = ("admin", Roles.Admin),
        ["PUT /api/v1/users/{id:guid}"] = ("admin", Roles.Admin),
        ["DELETE /api/v1/users/{id:guid}"] = ("admin", Roles.Admin),
        ["POST /api/v1/users/{id:guid}/reset-password"] = ("admin", Roles.Admin),
        ["POST /api/v1/users/{id:guid}/reset-totp"] = ("admin", Roles.Admin),
        ["POST /api/v1/users/{id:guid}/unlock"] = ("admin", Roles.Admin),
        // Groups and rights (docs/audit-redmine-zabbix-plan.md §5): only admins; the catalog every signed-in user's.
        ["GET /api/v1/groups"] = ("admin", Roles.Admin),
        ["GET /api/v1/groups/{id:guid}"] = ("admin", Roles.Admin),
        ["POST /api/v1/groups"] = ("admin", Roles.Admin),
        ["PUT /api/v1/groups/{id:guid}"] = ("admin", Roles.Admin),
        ["DELETE /api/v1/groups/{id:guid}"] = ("admin", Roles.Admin),
        ["PUT /api/v1/groups/{id:guid}/members"] = ("admin", Roles.Admin),
        ["GET /api/v1/rights"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/users/{id:guid}/access"] = ("admin", Roles.Admin),
        ["GET /api/v1/account/access"] = ("signed-in", Roles.Viewer),
        // Redmine (docs/audit-redmine-zabbix-plan.md §9): tickets for Redmine's View, links for its Link, the test for System's Manage.
        ["GET /api/v1/redmine/status"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/redmine/lookups"] = ("Redmine:View", Roles.User),
        ["GET /api/v1/redmine/overview"] = ("Redmine:View", Roles.User),
        ["GET /api/v1/redmine/issues"] = ("Redmine:View", Roles.User),
        ["GET /api/v1/redmine/issues/{id:int}"] = ("Redmine:View", Roles.User),
        // The refresh button (docs/redmine-queue-outbound-plan.md §5.3): a read, as the page.
        ["POST /api/v1/redmine/refresh"] = ("Redmine:View", Roles.User),
        ["GET /api/v1/redmine/related"] = ("Redmine:View", Roles.User),
        ["GET /api/v1/redmine/problems"] = ("Redmine:View", Roles.User),
        ["POST /api/v1/redmine/links"] = ("Redmine:Link", Roles.User),
        ["PUT /api/v1/redmine/links/{id:guid}"] = ("Redmine:Link", Roles.User),
        ["DELETE /api/v1/redmine/links/{id:guid}"] = ("Redmine:Link", Roles.User),
        ["POST /api/v1/redmine/test"] = ("System:Manage", Roles.Admin),
        // Zabbix (docs/audit-redmine-zabbix-plan.md §9): the pages for Zabbix's View (Viewers too), the test for System's Manage.
        ["GET /api/v1/zabbix/status"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/zabbix/summary"] = ("Zabbix:View", Roles.Viewer),
        ["GET /api/v1/zabbix/problems"] = ("Zabbix:View", Roles.Viewer),
        ["GET /api/v1/zabbix/problems/{eventId}"] = ("Zabbix:View", Roles.Viewer),
        ["GET /api/v1/zabbix/hosts"] = ("Zabbix:View", Roles.Viewer),
        ["GET /api/v1/zabbix/hosts/{hostId:long}"] = ("Zabbix:View", Roles.Viewer),
        ["GET /api/v1/zabbix/hosts/{hostId:long}/items"] = ("Zabbix:View", Roles.Viewer),
        ["POST /api/v1/zabbix/test"] = ("System:Manage", Roles.Admin),
        // Advanced sensors (docs/advanced-sensor-plan.md §8.3): Monitoring's, with Details checked by the service for data that names people.
        ["GET /api/v1/sensors/{id:guid}/advanced"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/advanced/reports"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/advanced/reports/latest"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/advanced/reports/{reportId:guid}"] = ("Monitoring:View", Roles.Viewer),
        ["DELETE /api/v1/sensors/{id:guid}/advanced/reports/{reportId:guid}"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/sensors/{id:guid}/advanced/reports"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/sensors/{id:guid}/advanced/paths"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/advanced/samples"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/sensors/{id:guid}/advanced/test"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/sensors/{id:guid}/advanced/rules"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/sensors/{id:guid}/advanced/rules/order"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/sensors/{id:guid}/advanced/rules/copy"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/sensors/{id:guid}/advanced/rules/{ruleId:guid}"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/sensors/{id:guid}/advanced/rules/{ruleId:guid}"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/sensors/{id:guid}/advanced/fields"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/sensors/{id:guid}/advanced/fields/order"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/sensors/{id:guid}/advanced/fields/{fieldId:guid}"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/sensors/{id:guid}/advanced/fields/{fieldId:guid}"] = ("Monitoring:Edit", Roles.User),
        // SSL certificates (docs/ssl-certificate-plan.md §6.3, §6.4): an area of their own, granted to no built-in group.
        ["GET /api/v1/sensors/{id:guid}/certificate"] = ("Certificates:View", Roles.Admin),
        ["POST /api/v1/sensors/{id:guid}/certificate/check"] = ("Certificates:Edit", Roles.Admin),
        ["POST /api/v1/sensors/{id:guid}/certificate/accept"] = ("Certificates:Edit", Roles.Admin),
        ["GET /api/v1/sensors/{id:guid}/certificate/versions"] = ("Certificates:View", Roles.Admin),
        ["GET /api/v1/sensors/{id:guid}/certificate/versions/{versionId:guid}"] = ("Certificates:View", Roles.Admin),
        ["GET /api/v1/sensors/{id:guid}/certificate/versions/{versionId:guid}/pem"] = ("Certificates:View", Roles.Admin),
        ["GET /api/v1/certificate-authorities"] = ("Certificates:View", Roles.Admin),
        ["POST /api/v1/certificate-authorities/preview"] = ("Certificates:Manage", Roles.Admin),
        ["POST /api/v1/certificate-authorities"] = ("Certificates:Manage", Roles.Admin),
        ["DELETE /api/v1/certificate-authorities/{id:guid}"] = ("Certificates:Manage", Roles.Admin),
        ["GET /api/v1/certificate-authorities/{id:guid}/pem"] = ("Certificates:View", Roles.Admin),
        ["GET /api/v1/zabbix/suggestions"] = ("Monitoring:Edit&Zabbix:View", Roles.User),
        ["POST /api/v1/zabbix/attach"] = ("Monitoring:Edit&Zabbix:View", Roles.User),
        // Cookie (docs/cookie-plan.md §19.4): the status for everyone, the cases for Use, Admin → Cookie for Manage. Only admins had it before.
        ["GET /api/v1/cookie/status"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/cookie/summary"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/cookie/messages"] = ("Cookie:Use", Roles.Admin),
        ["POST /api/v1/cookie/messages"] = ("Cookie:Use", Roles.Admin),
        ["POST /api/v1/cookie/messages/empowered"] = ("admin", Roles.Admin),
        ["GET /api/v1/cookie/cases"] = ("Cookie:Use", Roles.Admin),
        ["GET /api/v1/cookie/cases/{id:guid}"] = ("Cookie:Use", Roles.Admin),
        ["GET /api/v1/cookie/trace"] = ("Cookie:Use", Roles.Admin),
        ["POST /api/v1/cookie/cases/{id:guid}/comments"] = ("Cookie:Use", Roles.Admin),
        ["POST /api/v1/cookie/cases/{id:guid}/stop"] = ("Cookie:Instruct", Roles.Admin),
        ["POST /api/v1/cookie/cases/{id:guid}/go-on"] = ("Cookie:Instruct", Roles.Admin),
        // Package nine point five (docs/cookie-plan.md §21.4): the big brother and instructions for Instruct, the log and ratings for Use.
        ["POST /api/v1/cookie/cases/{id:guid}/big-brother"] = ("Cookie:Instruct", Roles.Admin),
        ["GET /api/v1/cookie/decisions"] = ("Cookie:Use", Roles.Admin),
        ["PUT /api/v1/cookie/decisions/{id:long}/rating"] = ("Cookie:Use", Roles.Admin),
        ["DELETE /api/v1/cookie/object-notes/{id:long}"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/cookie/instructions/{id:guid}/confirm"] = ("Cookie:Instruct", Roles.Admin),
        ["POST /api/v1/cookie/instructions/{id:guid}/end"] = ("Cookie:Instruct", Roles.Admin),
        ["GET /api/v1/cookie/admin/instructions"] = ("Cookie:Manage", Roles.Admin),
        ["POST /api/v1/cookie/admin/instructions"] = ("Cookie:Manage", Roles.Admin),
        ["PUT /api/v1/cookie/admin/settings/lessons"] = ("Cookie:Manage", Roles.Admin),
        // The access watch (docs/cookie-access-plan.md §10.3): an access case for Use, its answer for a contact or Instruct (checked
        // in code), a ticket for Instruct and Link, "This wasn't expected" for Monitoring Details; the tab, known access and the
        // routers' switches for Manage; a router's logins for Monitoring Details (docs/cookie-object-notes-plan.md §4).
        ["GET /api/v1/cookie/access/cases/{id:guid}"] = ("Cookie:Use", Roles.Admin),
        ["POST /api/v1/cookie/access/cases/{id:guid}/answer"] = ("Cookie:Use", Roles.Admin),
        ["POST /api/v1/cookie/access/cases/{id:guid}/ticket"] = ("Cookie:Instruct&Redmine:Link", Roles.Admin),
        ["POST /api/v1/cookie/access/logins/{id:guid}/not-expected"] = ("Monitoring:Details", Roles.User),
        ["GET /api/v1/cookie/admin/access/state"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/access/known"] = ("Cookie:Manage", Roles.Admin),
        ["POST /api/v1/cookie/admin/access/known"] = ("Cookie:Manage", Roles.Admin),
        ["POST /api/v1/cookie/admin/access/known/{id:guid}/end"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/access/routers"] = ("Cookie:Manage", Roles.Admin),
        ["PUT /api/v1/cookie/admin/access/routers"] = ("Cookie:Manage", Roles.Admin),
        ["PUT /api/v1/cookie/admin/settings/access"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/sensors/{id:guid}/mikrotik/logins"] = ("Monitoring:Details", Roles.User),
        ["PUT /api/v1/sensors/{id:guid}/mikrotik/access-watch"] = ("Cookie:Manage", Roles.Admin),
        // The person and the availability plan (docs/cookie-plan.md §8.1, §8.2): one's own contact details and column for
        // everyone (the plan's code checks the column), the rest by the area Availability; a user's profile for admins.
        ["GET /api/v1/account/contact"] = ("signed-in", Roles.Viewer),
        ["PUT /api/v1/account/contact"] = ("signed-in", Roles.Viewer),
        ["POST /api/v1/account/contact/test"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/users/{id:guid}/profile"] = ("admin", Roles.Admin),
        ["PUT /api/v1/users/{id:guid}/profile"] = ("admin", Roles.Admin),
        ["GET /api/v1/availability/plan"] = ("signed-in", Roles.Viewer),
        ["PUT /api/v1/availability/entries"] = ("signed-in", Roles.Viewer),
        ["POST /api/v1/availability/fill/mine"] = ("signed-in", Roles.Viewer),
        ["PUT /api/v1/availability/days"] = ("Availability:Edit", Roles.Admin),
        ["POST /api/v1/availability/fill"] = ("Availability:Edit", Roles.Admin),
        ["GET /api/v1/availability/people"] = ("Availability:Manage", Roles.Admin),
        ["PUT /api/v1/availability/people/{id:guid}"] = ("Availability:Manage", Roles.Admin),
        ["GET /api/v1/availability/settings"] = ("Availability:Manage", Roles.Admin),
        ["PUT /api/v1/availability/settings"] = ("Availability:Manage", Roles.Admin),
        ["POST /api/v1/cookie/admin/start"] = ("Cookie:Manage", Roles.Admin),
        ["POST /api/v1/cookie/admin/stop"] = ("Cookie:Manage", Roles.Admin),
        ["POST /api/v1/cookie/admin/run"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/rounds"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/prompts/preview"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/overview"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/settings"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/outbox"] = ("Cookie:Manage", Roles.Admin),
        ["POST /api/v1/cookie/admin/outbox/{id:long}/send"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/defaults"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/people"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/redmine"] = ("Cookie:Manage", Roles.Admin),
        ["PUT /api/v1/cookie/admin/settings/identity"] = ("Cookie:Manage", Roles.Admin),
        ["PUT /api/v1/cookie/admin/settings/prompts"] = ("Cookie:Manage", Roles.Admin),
        ["PUT /api/v1/cookie/admin/settings/cases"] = ("Cookie:Manage", Roles.Admin),
        ["PUT /api/v1/cookie/admin/settings/redmine"] = ("Cookie:Manage", Roles.Admin),
        ["PUT /api/v1/cookie/admin/settings/actions"] = ("Cookie:Manage", Roles.Admin),
        ["PUT /api/v1/cookie/admin/settings/models"] = ("Cookie:Manage", Roles.Admin),
        ["PUT /api/v1/cookie/admin/settings/rights"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/settings/{section}/versions"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/cookie/admin/settings/{section}/versions/{version:int}"] = ("Cookie:Manage", Roles.Admin),
        ["POST /api/v1/cookie/admin/settings/{section}/rollback/{version:int}"] = ("Cookie:Manage", Roles.Admin),
        ["GET /api/v1/audit-log"] = ("AuditLog:View", Roles.Admin),
        ["GET /api/v1/audit-log/types"] = ("AuditLog:View", Roles.Admin),
        ["GET /api/v1/audit-log/export"] = ("AuditLog:View", Roles.Admin),
        ["GET /api/v1/audit-log/subjects/{id:guid}"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/key-values"] = ("System:Manage", Roles.Admin),
        ["GET /api/v1/key-values/{id:guid}"] = ("System:Manage", Roles.Admin),
        ["POST /api/v1/key-values"] = ("System:Manage", Roles.Admin),
        ["PUT /api/v1/key-values/{id:guid}"] = ("System:Manage", Roles.Admin),
        ["DELETE /api/v1/key-values/{id:guid}"] = ("System:Manage", Roles.Admin),
        // UDP nodes (docs/udp-iot-plan.md §5): a node's own endpoints as the sensor's, the rest Admin.
        ["PUT /api/v1/sensors/{id:guid}/udp/key"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/sensors/{id:guid}/udp/regenerate-key"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/sensors/{id:guid}/udp/reset-counter"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/sensors/{id:guid}/udp/reset-session"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/sensors/{id:guid}/udp/last-packets"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/udp/rejections"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/udp/commands"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/udp/commands/{commandId:guid}/log"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/sensors/{id:guid}/udp/commands"] = ("Commands:Run", Roles.User),
        ["DELETE /api/v1/sensors/{id:guid}/udp/commands/{commandId:guid}"] = ("Commands:Run", Roles.User),
        ["GET /api/v1/sensors/{id:guid}/udp/ota-jobs"] = ("Monitoring:View", Roles.Viewer),
        // MikroTik routers (docs/mikrotik-plan.md §10): the join script holds keys and exports reveal the network (CanWrite); the backup list is the sensor's.
        ["GET /api/v1/sensors/{id:guid}/mikrotik/join-script"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/sensors/{id:guid}/mikrotik/removal-script"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/sensors/{id:guid}/mikrotik/poll"] = ("Commands:Run", Roles.User),
        ["POST /api/v1/sensors/{id:guid}/mikrotik/host-key/accept"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/sensors/{id:guid}/mikrotik/backups"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/sensors/{id:guid}/mikrotik/backups"] = ("Commands:Run", Roles.User),
        ["GET /api/v1/sensors/{id:guid}/mikrotik/backups/{backupId:guid}"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/sensors/{id:guid}/mikrotik/backups/{backupId:guid}/diff"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/sensors/{id:guid}/mikrotik/backups/{backupId:guid}/download"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/sensors/{id:guid}/mikrotik/backups/{backupId:guid}"] = ("Mikrotik:Manage", Roles.Admin),
        ["POST /api/v1/sensors/{id:guid}/mikrotik/regenerate-keys"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/wireguard/status"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/wireguard/network"] = ("Mikrotik:Manage", Roles.Admin),
        ["PUT /api/v1/wireguard/network"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/wireguard/peers"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/wireguard/tunnels"] = ("Mikrotik:Manage", Roles.Admin),
        ["POST /api/v1/wireguard/tunnels"] = ("Mikrotik:Manage", Roles.Admin),
        ["PUT /api/v1/wireguard/tunnels/{id:guid}"] = ("Mikrotik:Manage", Roles.Admin),
        ["DELETE /api/v1/wireguard/tunnels/{id:guid}"] = ("Mikrotik:Manage", Roles.Admin),
        ["POST /api/v1/wireguard/tunnels/{id:guid}/regenerate-keys"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/wireguard/tunnels/{id:guid}/config"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/account/tunnels"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/account/tunnels/{id:guid}/config"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/mikrotik/jobs"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/mikrotik/jobs/{jobId:guid}"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/sensors/{id:guid}/mikrotik/changelog"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/mikrotik/jobs"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/mikrotik/jobs"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/mikrotik/jobs/{id:guid}"] = ("Mikrotik:Manage", Roles.Admin),
        ["DELETE /api/v1/mikrotik/jobs/{id:guid}"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/mikrotik/status"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/mikrotik/routers"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/mikrotik/ssh-keys"] = ("Mikrotik:Manage", Roles.Admin),
        ["POST /api/v1/mikrotik/ssh-keys/rotate"] = ("Mikrotik:Manage", Roles.Admin),
        ["POST /api/v1/mikrotik/ssh-keys/retire"] = ("Mikrotik:Manage", Roles.Admin),
        // Backups (docs/mikrotik-backup-plan.md §2.2): the new-router form reads the default.
        ["GET /api/v1/mikrotik/backup-settings"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/mikrotik/backup-settings"] = ("Mikrotik:Manage", Roles.Admin),
        ["POST /api/v1/mikrotik/routers/backup-settings"] = ("Mikrotik:Manage", Roles.Admin),
        ["POST /api/v1/mikrotik/backup-keys"] = ("Mikrotik:Manage", Roles.Admin),
        ["POST /api/v1/mikrotik/backup-keys/retire"] = ("Mikrotik:Manage", Roles.Admin),
        // Remote commands (§8): as pnmt for Users; as an admin for Admins, checked in the service.
        ["POST /api/v1/mikrotik/commands"] = ("Commands:Run", Roles.User),
        ["GET /api/v1/mikrotik/commands"] = ("Commands:Run", Roles.User),
        ["GET /api/v1/mikrotik/commands/{id:guid}"] = ("Commands:Run", Roles.User),
        ["GET /api/v1/mikrotik/commands/batches/{batchId:guid}"] = ("Commands:Run", Roles.User),
        ["DELETE /api/v1/mikrotik/commands/{id:guid}"] = ("Commands:Run", Roles.User),
        ["POST /api/v1/mikrotik/commands/{id:guid}/redact"] = ("Mikrotik:Manage", Roles.Admin),
        ["GET /api/v1/udp/status"] = ("UdpNodes:Manage", Roles.Admin),
        ["GET /api/v1/udp/nodes"] = ("UdpNodes:Manage", Roles.Admin),
        ["GET /api/v1/udp/command-log"] = ("UdpNodes:Manage", Roles.Admin),
        ["GET /api/v1/udp/unknown-senders"] = ("UdpNodes:Manage", Roles.Admin),
        ["GET /api/v1/udp/firmware"] = ("UdpNodes:Manage", Roles.Admin),
        ["POST /api/v1/udp/firmware"] = ("UdpNodes:Manage", Roles.Admin),
        ["PUT /api/v1/udp/firmware/{id:guid}"] = ("UdpNodes:Manage", Roles.Admin),
        ["DELETE /api/v1/udp/firmware/{id:guid}"] = ("UdpNodes:Manage", Roles.Admin),
        ["GET /api/v1/udp/firmware/{id:guid}/content"] = ("UdpNodes:Manage", Roles.Admin),
        ["GET /api/v1/udp/ota-jobs"] = ("UdpNodes:Manage", Roles.Admin),
        ["POST /api/v1/udp/ota-jobs"] = ("UdpNodes:Manage", Roles.Admin),
        ["DELETE /api/v1/udp/ota-jobs/{id:guid}"] = ("UdpNodes:Manage", Roles.Admin),
        ["GET /api/v1/udp/protocol/examples"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/udp/protocol/build"] = ("Monitoring:View", Roles.Viewer),
        // The debug page (docs/ai-operations-plan.md §8).
        ["GET /api/v1/debug"] = ("System:Manage", Roles.Admin),
        ["POST /api/v1/debug/pushover"] = ("System:Manage", Roles.Admin),
        ["POST /api/v1/debug/mail"] = ("System:Manage", Roles.Admin),
        // The uplink page (docs/ai-operations-plan.md §7.3).
        ["GET /api/v1/uplink"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/uplink/checks"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/uplink/outages"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/uplink/check"] = ("System:Manage", Roles.Admin),
        // Importance and the Notification Manager (docs/ai-operations-plan.md §11).
        ["PUT /api/v1/hosts/importance"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/sensors/importance"] = ("Monitoring:Edit", Roles.User),
        ["GET /api/v1/notifications/matrix"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/notifications/coverage"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/notifications/settings"] = ("Monitoring:View", Roles.Viewer),
        ["PUT /api/v1/notifications/settings"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/notifications/assign"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/notification-rules/{id:guid}/scopes"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/notification-rules/{id:guid}/scopes/preview"] = ("Monitoring:Edit", Roles.User),
        ["PUT /api/v1/notification-rules/{id:guid}/exclusions/{sensorId:guid}"] = ("Monitoring:Edit", Roles.User),
        ["DELETE /api/v1/notification-rules/{id:guid}/exclusions/{sensorId:guid}"] = ("Monitoring:Edit", Roles.User),
        // The AI (docs/ai-operations-plan.md §14): Admin, because requests hold mail contents.
        ["GET /api/v1/ai/status"] = ("Ai:Manage", Roles.Admin),
        ["GET /api/v1/ai/requests"] = ("Ai:Manage", Roles.Admin),
        ["GET /api/v1/ai/requests/{id:long}"] = ("Ai:Manage", Roles.Admin),
        ["GET /api/v1/ai/work"] = ("Ai:Manage", Roles.Admin),
        ["POST /api/v1/ai/work/{id:guid}/retry"] = ("Ai:Manage", Roles.Admin),
        ["POST /api/v1/ai/work/{id:guid}/cancel"] = ("Ai:Manage", Roles.Admin),
        ["POST /api/v1/debug/ai"] = ("System:Manage", Roles.Admin),
        ["POST /api/v1/ai/mail/backfill"] = ("Ai:Manage", Roles.Admin),
        ["GET /api/v1/mikrotik/releases"] = ("Mikrotik:Manage", Roles.Admin),
        // Decisions (docs/ai-operations-plan.md §5.3): the User role, as the mail log.
        ["GET /api/v1/decisions"] = ("Decisions:Decide", Roles.User),
        ["GET /api/v1/decisions/count"] = ("Decisions:Decide", Roles.User),
        ["GET /api/v1/decisions/{id:guid}"] = ("Decisions:Decide", Roles.User),
        ["POST /api/v1/decisions/{id:guid}/decide"] = ("Decisions:Decide", Roles.User),
        // Servers with an agent, of both platforms (docs/windows-sensor-plan.md §9.2; docs/linux-sensor-plan.md §10, under
        // /api/v1/windows/ before L1): the agent log, revoking and tokens are Admin, requests CanWrite.
        ["GET /api/v1/agents/servers"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/agents/servers/{id:guid}"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/agents/servers/{id:guid}/logins"] = ("Monitoring:Details", Roles.User),
        ["GET /api/v1/agents/servers/{id:guid}/changes"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/agents/servers/{id:guid}/log"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/agents/servers/{id:guid}/collect"] = ("Commands:Run", Roles.User),
        ["POST /api/v1/agents/servers/{id:guid}/send-log"] = ("Servers:Manage", Roles.Admin),
        ["PUT /api/v1/agents/servers/{id:guid}/settings"] = ("Monitoring:Edit", Roles.User),
        ["POST /api/v1/agents/servers/{id:guid}/revoke"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/agents/servers/{id:guid}/rejoin-token"] = ("Servers:Manage", Roles.Admin),
        // Linux (docs/linux-sensor-plan.md §6.3, §8.4): a server's SSH keys (key comments and accounts: CanWrite), its accounts for scripts.
        ["GET /api/v1/agents/servers/{id:guid}/ssh-keys"] = ("Monitoring:Details", Roles.User),
        ["GET /api/v1/agents/servers/{id:guid}/run-as-accounts"] = ("Servers:Manage", Roles.Admin),
        ["GET /api/v1/agents/join-tokens"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/agents/join-tokens"] = ("Servers:Manage", Roles.Admin),
        ["DELETE /api/v1/agents/join-tokens/{id:guid}"] = ("Servers:Manage", Roles.Admin),
        ["GET /api/v1/agents/agent-log"] = ("Servers:Manage", Roles.Admin),
        ["GET /api/v1/agents/downloads"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/agents/downloads/fetch"] = ("Servers:Manage", Roles.Admin),
        ["GET /api/v1/agents/versions"] = ("Servers:Manage", Roles.Admin),
        ["PUT /api/v1/agents/versions/chosen"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/agents/versions/{version}/fetch"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/agents/versions/{version}/files"] = ("admin", Roles.Admin),
        ["GET /api/v1/agents/versions/{version}/files/{file}"] = ("Servers:Manage", Roles.Admin),
        ["DELETE /api/v1/agents/versions/{version}"] = ("Servers:Manage", Roles.Admin),
        // Scripts (docs/windows-sensor-plan.md §7; docs/linux-sensor-plan.md §8): Admin only.
        ["GET /api/v1/agents/scripts"] = ("Servers:Manage", Roles.Admin),
        ["GET /api/v1/agents/scripts/{id:guid}"] = ("Servers:Manage", Roles.Admin),
        ["GET /api/v1/agents/scripts/template"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/agents/scripts"] = ("Servers:Manage", Roles.Admin),
        ["PUT /api/v1/agents/scripts/{id:guid}"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/agents/scripts/{id:guid}/parse"] = ("Servers:Manage", Roles.Admin),
        ["DELETE /api/v1/agents/scripts/{id:guid}"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/agents/scripts/{id:guid}/run"] = ("Servers:Manage", Roles.Admin),
        ["GET /api/v1/agents/script-runs"] = ("Servers:Manage", Roles.Admin),
        ["GET /api/v1/agents/script-runs/{id:guid}"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/agents/script-runs/{id:guid}/redact"] = ("Servers:Manage", Roles.Admin),
        // What stays Windows' own (docs/linux-sensor-plan.md §3.3): the typed sections, WMI, the scripts registered on a server
        // and the run-as accounts.
        ["GET /api/v1/windows/servers/{id:guid}/sections/{section}"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/windows/servers/{id:guid}/wmi"] = ("Servers:Manage", Roles.Admin),
        ["PUT /api/v1/windows/servers/{id:guid}/wmi"] = ("Servers:Manage", Roles.Admin),
        ["GET /api/v1/windows/servers/{id:guid}/local-scripts"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/windows/servers/{id:guid}/local-scripts/run"] = ("Servers:Manage", Roles.Admin),
        ["GET /api/v1/windows/run-as"] = ("Servers:Manage", Roles.Admin),
        ["POST /api/v1/windows/run-as"] = ("Servers:Manage", Roles.Admin),
        ["PUT /api/v1/windows/run-as/{id:guid}"] = ("Servers:Manage", Roles.Admin),
        ["DELETE /api/v1/windows/run-as/{id:guid}"] = ("Servers:Manage", Roles.Admin),
        // Linux's own (docs/linux-sensor-plan.md §10): the typed sections; access, journal and ssh-keys check CanWrite inside.
        ["GET /api/v1/linux/servers/{id:guid}/sections/{section}"] = ("Monitoring:View", Roles.Viewer),
        // The SSH key manager (docs/linux-sensor-plan.md §6.4, §6.5, §10): Admin, so nobody vouches for a key by registering it; a
        // user's own keys for every role.
        ["GET /api/v1/ssh-keys"] = ("SshKeys:Manage", Roles.Admin),
        ["GET /api/v1/ssh-keys/summary"] = ("SshKeys:Manage", Roles.Admin),
        ["GET /api/v1/ssh-keys/unregistered"] = ("SshKeys:Manage", Roles.Admin),
        ["POST /api/v1/ssh-keys/parse"] = ("SshKeys:Manage", Roles.Admin),
        ["POST /api/v1/ssh-keys"] = ("SshKeys:Manage", Roles.Admin),
        ["GET /api/v1/ssh-keys/{id:guid}"] = ("SshKeys:Manage", Roles.Admin),
        ["PUT /api/v1/ssh-keys/{id:guid}"] = ("SshKeys:Manage", Roles.Admin),
        ["POST /api/v1/ssh-keys/{id:guid}/retire"] = ("SshKeys:Manage", Roles.Admin),
        ["POST /api/v1/ssh-keys/{id:guid}/reactivate"] = ("SshKeys:Manage", Roles.Admin),
        ["DELETE /api/v1/ssh-keys/{id:guid}"] = ("SshKeys:Manage", Roles.Admin),
        ["POST /api/v1/ssh-keys/{id:guid}/removal-items"] = ("SshKeys:Manage", Roles.Admin),
        ["GET /api/v1/ssh-keys/users/{userId:guid}"] = ("SshKeys:Manage", Roles.Admin),
        ["POST /api/v1/ssh-keys/users/{userId:guid}/removal-items"] = ("SshKeys:Manage", Roles.Admin),
        ["POST /api/v1/ssh-keys/users/{userId:guid}/offboard"] = ("SshKeys:Manage", Roles.Admin),
        ["POST /api/v1/ssh-keys/remove/preview"] = ("SshKeys:Manage", Roles.Admin),
        ["POST /api/v1/ssh-keys/remove"] = ("SshKeys:Manage", Roles.Admin),
        ["GET /api/v1/ssh-keys/mine"] = ("signed-in", Roles.Viewer),
        ["GET /api/v1/ssh-key-removals"] = ("SshKeys:Manage", Roles.Admin),
        // Diagnostics (docs/windows-sensor-plan.md §9.2): reading CanRead, changing Admin, Run now CanWrite.
        ["GET /api/v1/diagnostics/sets"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/diagnostics/sets/{id:guid}"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/diagnostics/sets"] = ("Diagnostics:Manage", Roles.Admin),
        ["PUT /api/v1/diagnostics/sets/{id:guid}"] = ("Diagnostics:Manage", Roles.Admin),
        ["DELETE /api/v1/diagnostics/sets/{id:guid}"] = ("Diagnostics:Manage", Roles.Admin),
        ["GET /api/v1/diagnostics/assignments"] = ("Monitoring:View", Roles.Viewer),
        ["POST /api/v1/diagnostics/assignments"] = ("Diagnostics:Manage", Roles.Admin),
        ["PUT /api/v1/diagnostics/assignments/{id:guid}"] = ("Diagnostics:Manage", Roles.Admin),
        ["DELETE /api/v1/diagnostics/assignments/{id:guid}"] = ("Diagnostics:Manage", Roles.Admin),
        ["POST /api/v1/diagnostics/assignments/{id:guid}/run"] = ("Commands:Run", Roles.User),
        ["GET /api/v1/diagnostics/runs"] = ("Monitoring:View", Roles.Viewer),
        ["GET /api/v1/diagnostics/runs/{id:guid}"] = ("Monitoring:View", Roles.Viewer),
        // Locations (docs/locations-knowledge-plan.md §8.1, §9): every role reads, Users write, Admins delete and define fields.
        ["GET /api/v1/locations"] = ("Locations:View", Roles.Viewer),
        ["GET /api/v1/locations/{id:guid}"] = ("Locations:View", Roles.Viewer),
        ["POST /api/v1/locations"] = ("Locations:Edit", Roles.User),
        ["PUT /api/v1/locations/{id:guid}"] = ("Locations:Edit", Roles.User),
        ["POST /api/v1/locations/{id:guid}/move"] = ("Locations:Edit", Roles.User),
        ["DELETE /api/v1/locations/{id:guid}"] = ("Locations:Manage", Roles.Admin),
        ["PUT /api/v1/locations/{id:guid}/values/{fieldId:guid}"] = ("Locations:Edit", Roles.User),
        ["GET /api/v1/locations/{id:guid}/history"] = ("Locations:View", Roles.Viewer),
        ["GET /api/v1/locations/suggestions"] = ("Locations:Edit", Roles.User),
        ["POST /api/v1/locations/from-text"] = ("Locations:Edit", Roles.User),
        ["PUT /api/v1/hosts/location"] = ("Locations:Edit", Roles.User),
        ["GET /api/v1/location-fields"] = ("Locations:View", Roles.Viewer),
        ["POST /api/v1/location-fields"] = ("Locations:Manage", Roles.Admin),
        ["PUT /api/v1/location-fields/{id:guid}"] = ("Locations:Manage", Roles.Admin),
        ["PUT /api/v1/location-fields/order"] = ("Locations:Manage", Roles.Admin),
        ["POST /api/v1/location-fields/{id:guid}/archive"] = ("Locations:Manage", Roles.Admin),
        ["POST /api/v1/location-fields/{id:guid}/restore"] = ("Locations:Manage", Roles.Admin),
        ["DELETE /api/v1/location-fields/{id:guid}"] = ("Locations:Manage", Roles.Admin),
        // The knowledge base (docs/locations-knowledge-plan.md §8.1, §9): every role reads and counts a view; Users write, mark
        // sensitive and run AI tags; Admins delete archived pages, redact, and rename and delete tags.
        ["GET /api/v1/kb/pages"] = ("KnowledgeBase:View", Roles.Viewer),
        ["POST /api/v1/kb/pages"] = ("KnowledgeBase:Edit", Roles.User),
        ["GET /api/v1/kb/pages/{number:int}"] = ("KnowledgeBase:View", Roles.Viewer),
        ["PUT /api/v1/kb/pages/{id:guid}"] = ("KnowledgeBase:Edit", Roles.User),
        ["POST /api/v1/kb/pages/{id:guid}/archive"] = ("KnowledgeBase:Edit", Roles.User),
        ["POST /api/v1/kb/pages/{id:guid}/restore"] = ("KnowledgeBase:Edit", Roles.User),
        ["DELETE /api/v1/kb/pages/{id:guid}"] = ("KnowledgeBase:Manage", Roles.Admin),
        ["POST /api/v1/kb/pages/{id:guid}/views"] = ("KnowledgeBase:View", Roles.Viewer),
        ["POST /api/v1/kb/pages/{id:guid}/editing"] = ("KnowledgeBase:Edit", Roles.User),
        ["GET /api/v1/kb/pages/{id:guid}/revisions"] = ("KnowledgeBase:View", Roles.Viewer),
        ["GET /api/v1/kb/pages/{id:guid}/revisions/{number:int}"] = ("KnowledgeBase:View", Roles.Viewer),
        ["GET /api/v1/kb/pages/{id:guid}/diff"] = ("KnowledgeBase:View", Roles.Viewer),
        ["POST /api/v1/kb/pages/{id:guid}/revisions/{number:int}/restore"] = ("KnowledgeBase:Edit", Roles.User),
        ["POST /api/v1/kb/pages/{id:guid}/redact"] = ("KnowledgeBase:Manage", Roles.Admin),
        ["PUT /api/v1/kb/pages/{id:guid}/sensitive"] = ("KnowledgeBase:Edit", Roles.User),
        ["POST /api/v1/kb/pages/{id:guid}/ai-tags"] = ("KnowledgeBase:Edit", Roles.User),
        ["GET /api/v1/kb/pages/{id:guid}/ai-tags/{workId:guid}"] = ("KnowledgeBase:Edit", Roles.User),
        ["GET /api/v1/kb/related"] = ("KnowledgeBase:View", Roles.Viewer),
        ["GET /api/v1/kb/tags"] = ("KnowledgeBase:View", Roles.Viewer),
        ["PUT /api/v1/kb/tags/{id:guid}"] = ("KnowledgeBase:Manage", Roles.Admin),
        ["DELETE /api/v1/kb/tags/{id:guid}"] = ("KnowledgeBase:Manage", Roles.Admin),
        // Uploaded files (docs/locations-knowledge-plan.md §9): uploading is a write, reading every role's, the overview and deleting the Admin's.
        ["POST /api/v1/files"] = ("KnowledgeBase:Edit|Locations:Edit", Roles.User),
        ["GET /api/v1/files/{id:guid}/{name}"] = ("KnowledgeBase:View|Locations:View", Roles.Viewer),
        ["GET /api/v1/files"] = ("KnowledgeBase:Manage", Roles.Admin),
        ["DELETE /api/v1/files/{id:guid}"] = ("KnowledgeBase:Manage", Roles.Admin),
        // 3rd Party Connectors (docs/connectors-plan.md §11): a rights area of its own, granted to no group by the update.
        ["GET /api/v1/connector-calls"] = ("Connectors:View", Roles.Admin),
        ["GET /api/v1/connector-calls/figures"] = ("Connectors:View", Roles.Admin),
        ["GET /api/v1/connector-calls/{id:guid}"] = ("Connectors:View", Roles.Admin),
        ["POST /api/v1/connector-calls/{id:guid}/redact"] = ("Connectors:Manage", Roles.Admin),
        ["POST /api/v1/connector-calls/{id:guid}/cancel"] = ("Connectors:Manage", Roles.Admin),
        ["GET /api/v1/connectors"] = ("Connectors:View", Roles.Admin),
        ["GET /api/v1/connectors/{id:guid}"] = ("Connectors:View", Roles.Admin),
        ["GET /api/v1/connectors/docs"] = ("Connectors:View", Roles.Admin),
        ["GET /api/v1/connectors/{id:guid}/compare"] = ("Connectors:View", Roles.Admin),
        ["POST /api/v1/connectors"] = ("Connectors:Manage", Roles.Admin),
        ["PUT /api/v1/connectors/{id:guid}"] = ("Connectors:Manage", Roles.Admin),
        ["DELETE /api/v1/connectors/{id:guid}"] = ("Connectors:Manage", Roles.Admin),
        ["POST /api/v1/connectors/{id:guid}/check"] = ("Connectors:Manage", Roles.Admin),
        ["POST /api/v1/connectors/parse"] = ("Connectors:Manage", Roles.Admin),
        ["POST /api/v1/connectors/patterns/test"] = ("Connectors:View", Roles.Admin),
        ["GET /api/v1/connectors/lookups/servers"] = ("Connectors:Manage", Roles.Admin),
        ["GET /api/v1/connectors/lookups/library"] = ("Connectors:Manage", Roles.Admin),
        ["GET /api/v1/connectors/lookups/library/{scriptId:guid}/versions/{number:int}"] = ("Connectors:Manage", Roles.Admin),
        ["GET /api/v1/connectors/lookups/registered"] = ("Connectors:Manage", Roles.Admin),
        ["GET /api/v1/connectors/lookups/accounts"] = ("Connectors:Manage", Roles.Admin),
        ["GET /api/v1/connector-callers"] = ("Connectors:View", Roles.Admin),
        ["GET /api/v1/connector-callers/{id:guid}"] = ("Connectors:View", Roles.Admin),
        ["POST /api/v1/connector-callers"] = ("Connectors:Manage", Roles.Admin),
        ["PUT /api/v1/connector-callers/{id:guid}"] = ("Connectors:Manage", Roles.Admin),
        ["DELETE /api/v1/connector-callers/{id:guid}"] = ("Connectors:Manage", Roles.Admin),
        ["GET /api/v1/connector-callers/{id:guid}/key"] = ("Connectors:Manage", Roles.Admin),
        ["POST /api/v1/connector-callers/{id:guid}/key/rotate"] = ("Connectors:Manage", Roles.Admin),
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

    /// <summary>The right an endpoint's attributes ask for, in the matrix's notation: the action's own, else the controller's.</summary>
    private static string RightOf(RouteEndpoint endpoint)
    {
        // Rights of two areas on one action are both needed: "Monitoring:Edit&Zabbix:View".
        var rights = endpoint.Metadata.GetOrderedMetadata<RequireRightAttribute>().ToList();
        if (rights.Select(r => r.Area).Distinct().Count() > 1)
        {
            return string.Join("&", rights.Select(r => RightsCatalog.Name(r.Area, r.Level)));
        }
        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).Where(p => p is not null).ToList();
        if (policies.LastOrDefault() is not { } last)
        {
            return "signed-in";
        }
        return last == "admin" ? "admin" : last["right:".Length..];
    }

    [TestMethod]
    public void EveryV1EndpointNamesTheRightOfTheMatrix()
    {
        var v1 = Endpoints().Where(e => e.Pattern.StartsWith("/api/v1/", StringComparison.Ordinal) && !e.Pattern.Contains("{**rest}")
            && Matrix.ContainsKey(e.Key)).ToList();
        Assert.IsGreaterThan(250, v1.Count);
        var wrong = v1.Where(e => RightOf(e.Endpoint) != Matrix[e.Key].Right).Select(e => $"{e.Key}: {RightOf(e.Endpoint)}, the matrix says {Matrix[e.Key].Right}");
        Assert.AreEqual("", string.Join("\n", wrong));
        // A write needs a right, except one's own session and account (§5.1).
        var selfService = v1.Where(e => e.Method != "GET" && Matrix[e.Key].Right == "signed-in").Select(e => e.Key).ToList();
        // The availability plan's own column (docs/cookie-plan.md §8.2): its code lets each person edit only their own.
        Assert.IsTrue(selfService.All(k => k.Contains("/api/v1/auth/", StringComparison.Ordinal) || k.Contains("/api/v1/account/", StringComparison.Ordinal)
            || k is "PUT /api/v1/availability/entries" or "POST /api/v1/availability/fill/mine"),
            "writes with no right: " + string.Join(", ", selfService));
    }

    [TestMethod]
    public void TheMatrixCoversEveryV1Endpoint()
    {
        var v1 = Endpoints()
            .Where(e => e.Pattern.StartsWith("/api/v1/", StringComparison.Ordinal) && !e.Pattern.Contains("{**rest}")
                && e.Key is not ("GET /api/v1/system/info" or "POST /api/v1/auth/login" or "POST /api/v1/auth/login/totp"
                    // Was this you? (docs/cookie-access-plan.md §6.6): anonymous, in the allowlist.
                    or "GET /api/v1/access/was-this-you/{token}" or "POST /api/v1/access/was-this-you/{token}"))
            .Select(e => e.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        CollectionAssert.AreEqual(Matrix.Keys.Order(StringComparer.Ordinal).ToList(), v1,
            "Not in the matrix: " + string.Join(", ", v1.Except(Matrix.Keys)) + "; gone: " + string.Join(", ", Matrix.Keys.Except(v1)));
    }

    /// <summary>
    /// Every endpoint × who (§5.4): signed out 401; a user with every level but the one it needs 403; one with exactly that
    /// level, and an admin, through. Ids are random and bodies empty, so the allowed calls end in 404 or 400 and change
    /// nothing.
    /// </summary>
    [TestMethod]
    public async Task EveryV1EndpointAnswersAsItsRightSays()
    {
        var failures = new List<string>();
        foreach (var (key, (right, _)) in Matrix)
        {
            var cases = new List<(string Who, string? Rights, bool Anonymous, string Expected)> { ("anonymous", null, true, "401"), ("an admin", "admin", false, "allowed") };
            if (right == "signed-in")
            {
                cases.Add(("no rights", "", false, "allowed"));
            }
            else if (right == "admin")
            {
                cases.Add(("every right but the flag", Everything(), false, "403"));
            }
            else if (right.Contains('&'))
            {
                var all = right.Split('&');
                cases.AddRange(all.Select(r => ($"every right but {r}", (string?)Everything(except: r), false, "403")));
                cases.AddRange(all.Select(r => ($"only {r}", (string?)r, false, "403")));
                cases.Add(("all of them", string.Join(",", all), false, "allowed"));
            }
            else
            {
                var options = right.Split('|');
                cases.Add(("every right but this", Everything(except: options), false, "403"));
                cases.AddRange(options.Select(o => ($"exactly {o}", o, false, "allowed")));
            }
            foreach (var (who, rights, anonymous, expected) in cases)
            {
                var status = await CallAsync(key, anonymous ? null : app.ClientWithRights(rights!));
                if (!Meets(status, expected))
                {
                    failures.Add($"{key} with {who}: {(int)status}, expected {expected}");
                }
            }
        }
        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
    }

    /// <summary>
    /// The update changes nobody's access (§5.3): the admin flag, Operators and Viewers answer every endpoint as Admin, User
    /// and Viewer did before 1.4.
    /// </summary>
    [TestMethod]
    public async Task TheFormerRolesStillGetWhatTheyGot()
    {
        var rank = new Dictionary<string, int> { [Roles.Viewer] = 1, [Roles.User] = 2, [Roles.Admin] = 3 };
        var failures = new List<string>();
        foreach (var (key, (_, before)) in Matrix)
        {
            foreach (var role in Roles.All)
            {
                var status = await CallAsync(key, app.Client(role));
                var expected = rank[role] >= rank[before] ? "allowed" : "403";
                if (!Meets(status, expected))
                {
                    failures.Add($"{key} as {role}: {(int)status}, expected {expected}");
                }
            }
        }
        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
    }

    /// <summary>
    /// Nothing slips in unlogged (docs/audit-redmine-zabbix-plan.md §3.7): every write endpoint names its event, or why it
    /// has none, and every one writes a Denied entry with its event and the missing right when called without it.
    /// </summary>
    [TestMethod]
    public void EveryWriteEndpointNamesItsAuditEvent()
    {
        var writes = Endpoints().Where(e => Matrix.ContainsKey(e.Key) && e.Method != "GET").ToList();
        Assert.IsGreaterThan(150, writes.Count);
        var missing = writes.Where(e => e.Endpoint.Metadata.GetMetadata<AuditAttribute>() is null && e.Endpoint.Metadata.GetMetadata<NotAuditedAttribute>() is null)
            .Select(e => e.Key).ToList();
        Assert.AreEqual("", string.Join(", ", missing), "write endpoints with neither [Audit] nor [NotAudited]");
        var unknown = writes.SelectMany(e => e.Endpoint.Metadata.GetMetadata<AuditAttribute>()?.EventTypes ?? []).Where(t => !AuditCatalog.Contains(t)).ToList();
        Assert.AreEqual("", string.Join(", ", unknown), "events that are not in the catalog");
    }

    [TestMethod]
    public async Task EveryAuditedEndpointWritesItsDeniedEntry()
    {
        var failures = new List<string>();
        foreach (var e in Endpoints().Where(e => Matrix.ContainsKey(e.Key) && e.Method != "GET"))
        {
            var (right, _) = Matrix[e.Key];
            if (e.Endpoint.Metadata.GetMetadata<AuditAttribute>() is not { } audit || right == "signed-in")
            {
                continue;
            }
            var name = $"denied-{Guid.NewGuid():N}";
            var client = app.ClientWithRights(right == "admin" ? Everything() : Everything(except: right.Split('|')));
            client.DefaultRequestHeaders.Remove(TestSignIn.NameHeader);
            client.DefaultRequestHeaders.Add(TestSignIn.NameHeader, name);
            var status = await CallAsync(e.Key, client);
            await using var db = app.NewDb();
            var entry = await db.AuditLog.AsNoTracking().Where(l => l.Username == name).SingleOrDefaultAsync();
            if (status != HttpStatusCode.Forbidden || entry is null || entry.EventType != audit.EventTypes[0] || entry.Outcome != AuditOutcome.Denied
                || entry.Request != e.Key)
            {
                failures.Add($"{e.Key}: {(int)status}, entry {entry?.EventType ?? "none"} {entry?.Outcome} {entry?.Request}");
            }
        }
        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
    }

    /// <summary>Every area at its highest level, but those named, which get none ("Monitoring:Edit" withholds Monitoring).</summary>
    private static string Everything(params string[] except)
    {
        var withheld = except.Select(e => e.Split(':')[0]).ToHashSet();
        return string.Join(",", RightsCatalog.Areas.Where(a => !withheld.Contains(a.Area.ToString()))
            .Select(a => RightsCatalog.Name(a.Area, a.Levels[^1].Level)));
    }

    private static async Task<HttpStatusCode> CallAsync(string key, HttpClient? client)
    {
        var method = key[..key.IndexOf(' ')];
        // A long id (an AI request's) and a page's number need a number to match their route.
        var path = System.Text.RegularExpressions.Regex.Replace(key[(key.IndexOf(' ') + 1)..], @"\{[^}]+\}",
            m => m.Value.EndsWith(":long}", StringComparison.Ordinal) ? Random.Shared.NextInt64(1L << 40, 1L << 50).ToString()
                : m.Value.EndsWith(":int}", StringComparison.Ordinal) ? Random.Shared.Next(1 << 24, int.MaxValue).ToString()
                : Guid.NewGuid().ToString());
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }
        using var response = await (client ?? app.Client()).SendAsync(request);
        return response.StatusCode;
    }

    private static bool Meets(HttpStatusCode status, string expected) => expected switch
    {
        "401" => status == HttpStatusCode.Unauthorized,
        "403" => status == HttpStatusCode.Forbidden,
        _ => status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && (int)status < 500,
    };
}
