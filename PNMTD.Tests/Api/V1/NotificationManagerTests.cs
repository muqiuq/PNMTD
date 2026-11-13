using System.Net;
using Microsoft.Extensions.Configuration;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Notifications;

namespace PNMTD.Tests.Api.V1;

/// <summary>Importance, scopes, exclusions and the Notification Manager (docs/ai-operations-plan.md §11).</summary>
[TestClass]
public sealed class NotificationManagerTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;

    /// <summary>Scopes reach every sensor of the run's database: the rules go when the class ends.</summary>
    private static readonly List<Guid> created = [];

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        user = app.Client(Roles.User);
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        foreach (var id in created)
        {
            await user.DeleteAsync($"/api/v1/notification-rules/{id}");
        }
        app.Dispose();
    }

    private static async Task<HostDetail> HostAsync(Importance importance) =>
        await (await user.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"nm-{Guid.NewGuid():N}"[..20], Importance = importance }))
            .ReadAsync<HostDetail>();

    private static async Task<SensorDetail> SensorAsync(Guid hostId, Importance? importance = null) =>
        await (await user.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = hostId, Type = SensorType.SIMPLE, Name = $"s-{Guid.NewGuid():N}"[..10], Importance = importance,
        })).ReadAsync<SensorDetail>();

    private static async Task<NotificationRuleDetail> RuleAsync(string recipient = "ops@example.com")
    {
        var rule = await (await user.PostJsonAsync("/api/v1/notification-rules", new NotificationRuleRequest
        {
            Name = $"nm-rule-{Guid.NewGuid():N}", Recipient = recipient, Type = NotificationRuleType.ALWAYS, Enabled = true,
        })).ReadAsync<NotificationRuleDetail>();
        created.Add(rule.Id);
        return rule;
    }

    private static async Task<NotificationMatrix> MatrixAsync(Guid hostId, string extra = "") =>
        await (await user.GetAsync($"/api/v1/notifications/matrix?hostId={hostId}{extra}")).ReadAsync<NotificationMatrix>();

    [TestMethod]
    public async Task A_sensor_takes_its_hosts_importance_unless_it_has_its_own()
    {
        var host = await HostAsync(Importance.High);
        Assert.AreEqual(Importance.High, host.Importance);
        var inherits = await SensorAsync(host.Id);
        var own = await SensorAsync(host.Id, Importance.Low);
        Assert.IsNull(inherits.Importance);
        Assert.AreEqual(Importance.High, inherits.EffectiveImportance);
        Assert.AreEqual(Importance.Low, own.EffectiveImportance);

        var high = await (await user.GetAsync($"/api/v1/sensors?hostId={host.Id}&importance=High")).ReadAsync<Paged<SensorListItem>>();
        CollectionAssert.AreEqual(new[] { inherits.Id }, high.Items.Select(s => s.Id).ToArray());

        Assert.AreEqual(1, await (await user.PutJsonAsync("/api/v1/sensors/importance", new ImportanceRequest { Ids = [own.Id] })).ReadAsync<int>());
        var back = await (await user.GetAsync($"/api/v1/sensors/{own.Id}")).ReadAsync<SensorDetail>();
        Assert.AreEqual(Importance.High, back.EffectiveImportance, "null takes the host's again");
        await user.PutJsonAsync("/api/v1/hosts/importance", new ImportanceRequest { Ids = [host.Id], Importance = Importance.Low });
        Assert.AreEqual(Importance.Low, (await (await user.GetAsync($"/api/v1/sensors/{own.Id}")).ReadAsync<SensorDetail>()).EffectiveImportance);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await user.PutJsonAsync("/api/v1/hosts/importance", new ImportanceRequest { Ids = [host.Id] })).StatusCode, "a host needs one");
    }

    [TestMethod]
    public async Task Scopes_cover_sensors_of_at_least_an_importance_and_sensors_added_later()
    {
        var host = await HostAsync(Importance.Normal);
        var normal = await SensorAsync(host.Id);
        var high = await SensorAsync(host.Id, Importance.High);
        var low = await SensorAsync(host.Id, Importance.Low);
        var rule = await RuleAsync();
        var scopes = new[] { new NotificationScopeRequest { Kind = NotificationScopeKind.Importance, Importance = Importance.Normal } };
        var preview = await (await user.PostJsonAsync($"/api/v1/notification-rules/{rule.Id}/scopes/preview", scopes)).ReadAsync<NotificationScopePreview>();
        Assert.IsTrue(preview.Sensors >= 2);
        Assert.AreEqual(HttpStatusCode.NoContent, (await user.PutJsonAsync($"/api/v1/notification-rules/{rule.Id}/scopes", scopes)).StatusCode);

        var matrix = await MatrixAsync(host.Id);
        NotificationMatrixCell? Cell(Guid sensor) => matrix.Rows.Single(r => r.SensorId == sensor).Cells.SingleOrDefault(c => c.RuleId == rule.Id);
        Assert.AreEqual("scope", Cell(normal.Id)?.Via);
        Assert.AreEqual("importance ≥ Normal", Cell(high.Id)?.Scope);
        Assert.IsNull(Cell(low.Id), "at least Normal leaves Low out");

        // A sensor added later is covered; one taken out by an exclusion is not, but a link still applies.
        var later = await SensorAsync(host.Id);
        await user.PutAsync($"/api/v1/notification-rules/{rule.Id}/exclusions/{normal.Id}", null);
        matrix = await MatrixAsync(host.Id);
        Assert.AreEqual("scope", Cell(later.Id)?.Via);
        Assert.AreEqual("excluded", Cell(normal.Id)?.Via);
        var detail = await (await user.GetAsync($"/api/v1/notification-rules/{rule.Id}")).ReadAsync<NotificationRuleDetail>();
        Assert.AreEqual(Importance.Normal, detail.Scopes!.Single().Importance);
        Assert.AreEqual(normal.Id, detail.Exclusions!.Single().SensorId);

        var forSensor = await (await user.GetAsync($"/api/v1/sensors/{later.Id}/notification-rules")).ReadAsync<List<NotificationRuleItem>>();
        Assert.AreEqual("scope: importance ≥ Normal", forSensor.Single(r => r.Id == rule.Id).Via);
    }

    [TestMethod]
    public async Task Bulk_assignment_links_and_removing_a_scoped_sensor_excludes_it()
    {
        // Low: other tests' importance scopes (at least Normal, at least High) stay out of this host.
        var host = await HostAsync(Importance.Low);
        var a = await SensorAsync(host.Id);
        var b = await SensorAsync(host.Id);
        var linkRule = await RuleAsync();
        var hostRule = await RuleAsync();
        await user.PutJsonAsync($"/api/v1/notification-rules/{hostRule.Id}/scopes",
            new[] { new NotificationScopeRequest { Kind = NotificationScopeKind.Host, HostId = host.Id } });

        var added = await (await user.PostJsonAsync("/api/v1/notifications/assign", new NotificationAssignRequest
        {
            RuleIds = [linkRule.Id], SensorIds = [a.Id, b.Id], Mode = AssignMode.Add,
        })).ReadAsync<NotificationAssignResult>();
        Assert.AreEqual(2, added.Linked);

        var removed = await (await user.PostJsonAsync("/api/v1/notifications/assign", new NotificationAssignRequest
        {
            RuleIds = [linkRule.Id, hostRule.Id], SensorIds = [b.Id], Mode = AssignMode.Remove,
        })).ReadAsync<NotificationAssignResult>();
        Assert.AreEqual(1, removed.Unlinked);
        Assert.AreEqual(1, removed.Excluded, "the host scope still covered it");

        var matrix = await MatrixAsync(host.Id);
        var row = matrix.Rows.Single(r => r.SensorId == b.Id);
        Assert.IsFalse(row.Cells.Any(c => c.RuleId == linkRule.Id));
        Assert.AreEqual("excluded", row.Cells.Single(c => c.RuleId == hostRule.Id).Via);
        var uncovered = await MatrixAsync(host.Id, "&uncovered=true");
        CollectionAssert.AreEqual(new[] { b.Id }, uncovered.Rows.Select(r => r.SensorId).ToArray());

        var readded = await (await user.PostJsonAsync("/api/v1/notifications/assign", new NotificationAssignRequest
        {
            RuleIds = [hostRule.Id], SensorIds = [b.Id], Mode = AssignMode.Add,
        })).ReadAsync<NotificationAssignResult>();
        Assert.AreEqual(1, readded.Included);
        var coverage = await (await user.GetAsync("/api/v1/notifications/coverage")).ReadAsync<NotificationCoverage>();
        Assert.IsFalse(coverage.Sensors.Any(s => s.SensorId == b.Id));
    }

    [TestMethod]
    public async Task Pending_notifications_come_from_links_and_scopes_once_with_the_importance()
    {
        var host = await HostAsync(Importance.High);
        var sensor = await SensorAsync(host.Id);
        var excluded = await SensorAsync(host.Id);
        var both = await RuleAsync();
        await user.PutAsync($"/api/v1/sensors/{sensor.Id}/notification-rules/{both.Id}", null);
        await user.PutJsonAsync($"/api/v1/notification-rules/{both.Id}/scopes",
            new[] { new NotificationScopeRequest { Kind = NotificationScopeKind.Importance, Importance = Importance.High } });
        await user.PutAsync($"/api/v1/notification-rules/{both.Id}/exclusions/{excluded.Id}", null);
        await using (var db = app.NewDb())
        {
            foreach (var id in new[] { sensor.Id, excluded.Id })
            {
                db.Events.Add(new EventEntity { Id = Guid.NewGuid(), SensorId = id, Code = 500, Message = "down", Created = Clock.UtcNow, Source = "test" });
            }
            await db.SaveChangesAsync();
        }
        await using var check = app.NewDb();
        var pending = check.GetAllPendingNotificationsForLastMinutes().Where(p => p.NotitificationRule.Id == both.Id).ToList();
        Assert.AreEqual(1, pending.Count(p => p.EventEntity.SensorId == sensor.Id), "a link and a scope: one notification");
        Assert.AreEqual(0, pending.Count(p => p.EventEntity.SensorId == excluded.Id));
        var mine = pending.Single(p => p.EventEntity.SensorId == sensor.Id);
        Assert.AreEqual(Importance.High, mine.Importance,
            $"sensor {mine.EventEntity.Sensor.Importance}, host {mine.EventEntity.Sensor.Parent.Importance} ({mine.EventEntity.Sensor.Parent.Name})");
    }

    [TestMethod]
    public void Pushovers_priority_follows_the_importance()
    {
        var on = new PushoverNotification(new ConfigurationBuilder().Build(), null!);
        Assert.AreEqual(-1, on.PriorityOf(new NotificationMessage("pushover://u", "s", "m", "m", Importance.Low)));
        Assert.AreEqual(0, on.PriorityOf(new NotificationMessage("pushover://u", "s", "m", "m")));
        Assert.AreEqual(1, on.PriorityOf(new NotificationMessage("pushover://u", "s", "m", "m", Importance.High)));
        Assert.AreEqual(-1, on.PriorityOf(new NotificationMessage("pushover://u", "s", "m", "m", Importance.High, Priority: -1)), "the debug page's own");
        var off = new PushoverNotification(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:PushoverPriorityByImportance"] = "false" }).Build(), null!);
        Assert.AreEqual(0, off.PriorityOf(new NotificationMessage("pushover://u", "s", "m", "m", Importance.High)));
    }
}
