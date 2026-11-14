using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Audit;

/// <summary>
/// The audit log's pipeline (docs/audit-redmine-zabbix-plan.md §3.3–3.5, §3.8): one entry per write request, with the
/// changes field by field, the target named after a bulk delete, failures with their code, refusals as Denied, no entry
/// for a save that rolled back, and the retention.
/// </summary>
[TestClass]
public sealed class AuditLogTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static HttpClient viewer = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        user = app.Client(Roles.User);
        viewer = app.Client(Roles.Viewer);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<List<AuditLogEntity>> EntriesAsync(Guid target)
    {
        await using var db = app.NewDb();
        var id = target.ToString();
        return await db.AuditLog.AsNoTracking().Where(l => l.TargetId == id).OrderBy(l => l.CreatedAt).ToListAsync();
    }

    private static async Task<HostDetail> CreateHostAsync(string name, string? notes = null)
    {
        var response = await user.PostAsJsonAsync("/api/v1/hosts", new { name, notes });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<HostDetail>();
    }

    [TestMethod]
    public async Task AWriteMakesOneEntryWithItsChangesFieldByField()
    {
        var name = $"audit-host-{Guid.NewGuid():N}"[..24];
        var host = await CreateHostAsync(name, "first");
        var update = await user.PutAsJsonAsync($"/api/v1/hosts/{host.Id}", new { name, notes = "second", location = "Rack 4" });
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());

        var entries = await EntriesAsync(host.Id);
        CollectionAssert.AreEqual(new[] { AuditEvents.HostCreated, AuditEvents.HostUpdated }, entries.Select(e => e.EventType).ToArray(),
            "one entry per request");
        var created = entries[0];
        Assert.AreEqual(AuditOutcome.Succeeded, created.Outcome);
        Assert.AreEqual(AuditActor.User, created.Actor);
        Assert.AreEqual(TestUsers.User, created.UserId);
        Assert.AreEqual(AuditCategory.Hosts, created.Category);
        Assert.AreEqual(name, created.TargetName);
        Assert.AreEqual($"{name} created", created.Detail);
        Assert.AreEqual("POST /api/v1/hosts", created.Request);
        CollectionAssert.Contains(created.Subjects, host.Id);

        var updated = entries[1];
        Assert.AreEqual($"PUT /api/v1/hosts/{{id:guid}}", updated.Request);
        StringAssert.Contains(updated.Detail, "Notes first → second");
        StringAssert.Contains(updated.Detail, "Location – → Rack 4");
        var changes = System.Text.Json.JsonSerializer.Deserialize<List<AuditChange>>(updated.Changes!, AuditLog.Json)!;
        var notes = changes.Single(c => c.Field == "Notes");
        Assert.AreEqual(("first", "second"), (notes.Old, notes.New));
        Assert.AreEqual($"Host {name}", notes.Object);
    }

    [TestMethod]
    public async Task ABulkDeleteStillNamesItsTarget()
    {
        var name = $"audit-gone-{Guid.NewGuid():N}"[..24];
        var host = await CreateHostAsync(name);
        Assert.AreEqual(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/hosts/{host.Id}")).StatusCode);

        var deleted = (await EntriesAsync(host.Id)).Single(e => e.EventType == AuditEvents.HostDeleted);
        Assert.AreEqual(name, deleted.TargetName);
        Assert.AreEqual($"{name} deleted", deleted.Detail);
        Assert.AreEqual(AuditOutcome.Succeeded, deleted.Outcome);
    }

    [TestMethod]
    public async Task AFailureIsAnEntryWithItsCode()
    {
        var missing = Guid.NewGuid();
        var response = await user.PutAsJsonAsync($"/api/v1/hosts/{missing}", new { name = "nobody" });
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);

        var entry = (await EntriesAsync(missing)).Single();
        Assert.AreEqual(AuditEvents.HostUpdated, entry.EventType);
        Assert.AreEqual(AuditOutcome.Failed, entry.Outcome);
        Assert.AreEqual("404 not_found", entry.Error);
        Assert.IsNull(entry.Changes);
    }

    [TestMethod]
    public async Task ARefusalIsDenied()
    {
        var host = await CreateHostAsync($"audit-refused-{Guid.NewGuid():N}"[..24]);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/v1/hosts/{host.Id}", new { name = "x" })).StatusCode);

        var denied = (await EntriesAsync(host.Id)).Single(e => e.Outcome == AuditOutcome.Denied);
        Assert.AreEqual(AuditEvents.HostUpdated, denied.EventType);
        Assert.AreEqual(TestUsers.Viewer, denied.UserId);
        Assert.AreEqual("403", denied.Error);
        Assert.AreEqual("missing: Monitoring:Edit", denied.Detail, "the right it lacked (docs/audit-redmine-zabbix-plan.md §5.4)");
    }

    [TestMethod]
    public async Task ASaveThatRollsBackTakesItsEntryAlong()
    {
        var correlation = $"rollback-{Guid.NewGuid():N}";
        var scope = AuditScope.Begin([AuditEvents.HostCreated], AuditWho.Cli, "test", correlation, null);
        try
        {
            await using (var db = app.NewDb())
            {
                db.Hosts.Add(new HostEntity { Id = Guid.NewGuid(), Name = null! });
                await Assert.ThrowsExactlyAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }
            await using (var db = app.NewDb())
            {
                Assert.IsFalse(await db.AuditLog.AnyAsync(l => l.CorrelationId == correlation), "no entry without its change");
                db.Hosts.Add(new HostEntity { Id = Guid.NewGuid(), Name = "audit-after-rollback", Created = Clock.UtcNow, Enabled = true });
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            scope.End(AuditOutcome.Succeeded, null);
        }
        await using var check = app.NewDb();
        var entry = await check.AuditLog.SingleAsync(l => l.CorrelationId == correlation);
        Assert.AreEqual("audit-after-rollback", entry.TargetName);
        Assert.AreEqual(AuditActor.Cli, entry.Actor);
    }

    [TestMethod]
    public async Task TheCatalogKnowsEveryTypeAndLabelsIt()
    {
        var types = await (await app.Client(Roles.Admin).GetAsync("/api/v1/audit-log/types")).ReadAsync<List<AuditEventTypeItem>>();
        Assert.AreEqual(types.Count, types.Select(t => t.Type).Distinct().Count());
        Assert.AreEqual("MikroTik join script viewed", types.Single(t => t.Type == AuditEvents.MikrotikJoinScriptViewed).Label);
        Assert.AreEqual("2FA failed", types.Single(t => t.Type == AuditEvents.TotpFailed).Label);
        Assert.AreEqual(AuditTone.Danger, types.Single(t => t.Type == AuditEvents.LoginFailed).Tone);
        var constants = typeof(AuditEvents).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        CollectionAssert.AreEquivalent(constants, types.Select(t => t.Type).ToList(), "every event of AuditEvents is in the catalog");
    }

    [TestMethod]
    public async Task RetentionRemovesTheOldestByAgeThenByCount()
    {
        await using (var db = app.NewDb())
        {
            var who = new AuditWho(AuditActor.User, null, "retention-test", null, null, null);
            db.AuditLog.Add(AuditLog.Entry(AuditEvents.Logout, who, "too old", at: Clock.UtcNow.AddDays(-401)));
            db.AuditLog.AddRange(Enumerable.Range(0, 1100).Select(i => AuditLog.Entry(AuditEvents.Logout, who, $"bulk {i}",
                at: Clock.UtcNow.AddDays(-300).AddMinutes(i))));
            await db.SaveChangesAsync();
        }
        await app.Services.GetRequiredService<AuditRetentionTask>().RunAsync(CancellationToken.None);

        await using var check = app.NewDb();
        Assert.IsFalse(await check.AuditLog.AnyAsync(l => l.Detail == "too old"), "older than Audit:KeepDays (400)");
        Assert.IsTrue(await check.AuditLog.CountAsync() <= 500_000);
        Assert.IsTrue(await check.AuditLog.AnyAsync(l => l.Detail == "bulk 1099"), "within both limits");
    }

    [TestMethod]
    public async Task RetentionKeepsAtMostMaxEntries()
    {
        using var small = new TestApp(settings: new Dictionary<string, string?> { ["Audit:MaxEntries"] = "1000" });
        await using (var db = small.NewDb())
        {
            var who = new AuditWho(AuditActor.User, null, "retention-count", null, null, null);
            db.AuditLog.AddRange(Enumerable.Range(0, 1200).Select(i => AuditLog.Entry(AuditEvents.Logout, who, $"count {i}",
                at: Clock.UtcNow.AddDays(-350).AddMinutes(i))));
            db.AuditLog.Add(AuditLog.Entry(AuditEvents.Logout, who, "count newest"));
            await db.SaveChangesAsync();
        }
        Assert.IsTrue(await small.Services.GetRequiredService<AuditRetentionTask>().RunAsync(CancellationToken.None) >= 200);

        await using var check = small.NewDb();
        Assert.IsTrue(await check.AuditLog.CountAsync() <= 1000);
        Assert.IsFalse(await check.AuditLog.AnyAsync(l => l.Detail == "count 0"), "the oldest go first");
        Assert.IsTrue(await check.AuditLog.AnyAsync(l => l.Detail == "count newest"));
    }
}
