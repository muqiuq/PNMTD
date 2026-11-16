using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// Cookie's notes on the pages (docs/cookie-object-notes-plan.md §5, §6): a reader sees those within their rights, the case's
/// title only with <i>Cookie: Use</i>, a person with <i>Monitoring: Edit</i> removes one (404 for one they may not see), audited;
/// a host's notes go with the host.
/// </summary>
[TestClass]
public sealed class CookieObjectNotesTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        admin = app.Client(Roles.Admin);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<long> NoteAsync(Guid? hostId, Guid? sensorId, string text, Guid? caseId = null, params string[] areas)
    {
        await using var db = app.NewDb();
        var note = new CookieObjectNoteEntity { HostId = hostId, SensorId = sensorId, At = Clock.UtcNow, Text = text, CaseId = caseId, Areas = [.. areas] };
        db.CookieObjectNotes.Add(note);
        await db.SaveChangesAsync();
        return note.Id;
    }

    [TestMethod]
    public async Task A_reader_sees_the_notes_within_their_rights_and_the_case_only_with_Cookie_Use()
    {
        var host = await V1.CreateHostAsync(admin, V1.Unique("noted"));
        var sensor = await V1.CreateSensorAsync(admin, host.Id, name: "ping");
        Guid caseId;
        await using (var db = app.NewDb())
        {
            var c = new CookieCaseEntity
            {
                Id = Guid.NewGuid(), Kind = CookieCaseKind.Problem, Key = $"test:{Guid.NewGuid():N}", Title = "The noted case", Open = true, HostId = host.Id,
                OpenedAt = Clock.UtcNow, ChangedAt = Clock.UtcNow, ProgressAt = Clock.UtcNow, Areas = ["Monitoring"],
            };
            db.CookieCases.Add(c);
            await db.SaveChangesAsync();
            caseId = c.Id;
        }
        var plain = await NoteAsync(host.Id, null, "The backup runs at 02:00.", caseId, "Monitoring");
        var fromMail = await NoteAsync(host.Id, null, "A mail said the ISP works on it.", caseId, "Monitoring", "Mail");
        var onSensor = await NoteAsync(null, sensor.Id, "Flaps at night.", null, "Monitoring");

        var viewer = await (await app.Client(Roles.Viewer).GetAsync($"/api/v1/hosts/{host.Id}/cookie-notes")).ReadAsync<List<CookieObjectNoteItem>>();
        Assert.AreEqual(1, viewer.Count, "without Mail: Read, the mail-sourced note is left out");
        Assert.AreEqual(plain, viewer[0].Id);
        Assert.IsNull(viewer[0].CaseTitle, "the case's title needs Cookie: Use");

        var all = await (await admin.GetAsync($"/api/v1/hosts/{host.Id}/cookie-notes")).ReadAsync<List<CookieObjectNoteItem>>();
        CollectionAssert.AreEquivalent(new[] { plain, fromMail }, all.Select(n => n.Id).ToArray());
        Assert.AreEqual("The noted case", all.Single(n => n.Id == plain).CaseTitle);
        CollectionAssert.AreEquivalent(new[] { "Mail", "Monitoring" }, all.Single(n => n.Id == fromMail).Areas.ToArray());

        var sensors = await (await app.Client(Roles.Viewer).GetAsync($"/api/v1/sensors/{sensor.Id}/cookie-notes")).ReadAsync<List<CookieObjectNoteItem>>();
        Assert.AreEqual(onSensor, sensors.Single().Id);
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/hosts/{Guid.NewGuid()}/cookie-notes")).StatusCode);

        // Removing: Edit, and only what one may see (§3.6).
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.Viewer).DeleteAsync($"/api/v1/cookie/object-notes/{plain}")).StatusCode);
        var editor = app.ClientWithRights("Monitoring:Edit");
        Assert.AreEqual(HttpStatusCode.NotFound, (await editor.DeleteAsync($"/api/v1/cookie/object-notes/{fromMail}")).StatusCode, "not visible: as if it weren't there");
        Assert.AreEqual(HttpStatusCode.NoContent, (await editor.DeleteAsync($"/api/v1/cookie/object-notes/{plain}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await editor.DeleteAsync($"/api/v1/cookie/object-notes/{plain}")).StatusCode);
        var left = await (await admin.GetAsync($"/api/v1/hosts/{host.Id}/cookie-notes")).ReadAsync<List<CookieObjectNoteItem>>();
        Assert.AreEqual(fromMail, left.Single().Id);
        var log = await (await admin.GetAsync($"/api/v1/audit-log?type={AuditEvents.CookieNoteRemoved}")).ReadAsync<Paged<AuditLogItem>>();
        Assert.IsTrue(log.Items.Any(l => l.Detail is { } d && d.EndsWith("removed: The backup runs at 02:00.") && l.TargetId == host.Id.ToString()),
            string.Join(" | ", log.Items.Select(l => l.Detail)));

        // The host's notes go with the host (§3.1).
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/hosts/{host.Id}")).StatusCode);
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(0, await db.CookieObjectNotes.CountAsync(n => n.HostId == host.Id || n.SensorId == sensor.Id));
        }
    }
}
