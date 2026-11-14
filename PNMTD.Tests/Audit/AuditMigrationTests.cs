using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PNMTD.Tests.Audit;

/// <summary>
/// auth_log becomes audit_log and its rows keep their meaning (docs/audit-redmine-zabbix-plan.md §3.2): the category from
/// the catalog, the outcome from the type, the actor from the "cli: " and "agent: " prefixes, which go, and an account's
/// events about the account.
/// </summary>
[TestClass]
public sealed class AuditMigrationTests
{
    [TestMethod]
    public async Task TheAuthLogsRowsKeepTheirMeaning()
    {
        await using var db = TestDatabases.Empty("pnmtd_test_audit_upgrade");
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261114084339_AddFiles");
        var bob = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO auth_log ("Id", "CreatedAt", "UserId", "Username", "EventType", "IpAddress", "Detail") VALUES
                ({Guid.NewGuid()}, now(), {bob}, 'bob', 'LoginFailed', '10.0.0.1', 'wrong password'),
                ({Guid.NewGuid()}, now(), {bob}, 'bob', 'PasswordReset', NULL, 'cli: password set'),
                ({Guid.NewGuid()}, now(), {bob}, 'bob', 'TotpReset', NULL, 'cli'),
                ({Guid.NewGuid()}, now(), NULL, 'agent', 'AgentJoined', '10.0.0.9', 'agent: web01 joined'),
                ({Guid.NewGuid()}, now(), NULL, 'admin', 'WireGuardTunnelCreated', '10.0.0.2', 'laptop: 10.99.0.5')
            """);
        await migrator.MigrateAsync();

        var rows = await db.AuditLog.AsNoTracking().ToDictionaryAsync(l => l.EventType);
        Assert.AreEqual(AuditOutcome.Failed, rows[AuditEvents.LoginFailed].Outcome);
        Assert.AreEqual(AuditCategory.Auth, rows[AuditEvents.LoginFailed].Category);
        Assert.AreEqual("wrong password", rows[AuditEvents.LoginFailed].Detail);

        var reset = rows[AuditEvents.PasswordReset];
        Assert.AreEqual((AuditActor.Cli, "password set", AuditCategory.Users), (reset.Actor, reset.Detail, reset.Category));
        Assert.AreEqual((AuditTargets.User, "bob"), (reset.TargetType, reset.TargetName));
        CollectionAssert.AreEqual(new[] { bob }, reset.Subjects);
        Assert.AreEqual((AuditActor.Cli, (string?)null), (rows[AuditEvents.TotpReset].Actor, rows[AuditEvents.TotpReset].Detail));

        var joined = rows[AuditEvents.AgentJoined];
        Assert.AreEqual((AuditActor.Agent, "web01 joined", AuditCategory.Agents), (joined.Actor, joined.Detail, joined.Category));

        var tunnel = rows[AuditEvents.WireGuardTunnelCreated];
        Assert.AreEqual((AuditActor.User, AuditOutcome.Succeeded, AuditCategory.WireGuard), (tunnel.Actor, tunnel.Outcome, tunnel.Category));
        Assert.AreEqual("laptop: 10.99.0.5", tunnel.Detail);
    }
}
