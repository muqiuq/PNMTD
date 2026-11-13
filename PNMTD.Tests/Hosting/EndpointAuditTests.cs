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
        ["GET /api/v1/auth-log"] = Roles.Admin,
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
            // Writes need CanWrite, except one's own session and account, which every role manages.
            var selfService = e.Pattern.StartsWith("/api/v1/auth/", StringComparison.Ordinal)
                || e.Pattern.StartsWith("/api/v1/account/", StringComparison.Ordinal);
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
            var path = System.Text.RegularExpressions.Regex.Replace(key[(key.IndexOf(' ') + 1)..], @"\{[^}]+\}", _ => Guid.NewGuid().ToString());
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
