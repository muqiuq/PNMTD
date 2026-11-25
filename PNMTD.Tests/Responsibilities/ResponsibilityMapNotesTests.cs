using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Responsibilities;

/// <summary>
/// Notes on the overview (docs/responsibilities-extensions-plan.md §7): created where the person looks, changed and deleted with Edit,
/// moved with the layout; at most 2,000 characters and 500 notes; the credentials check; each change in the audit log.
/// </summary>
[TestClass]
public sealed class ResponsibilityMapNotesTests
{
    private static TestApp app = null!;
    private static HttpClient editor = null!;
    private static HttpClient reader = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        editor = app.ClientWithRights("Responsibilities:Edit");
        reader = app.ClientWithRights("Responsibilities:View");
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<ResponsibilityMapNote> CreateAsync(string text, string colour = "Green", string width = "Large", double x = 10, double y = 20)
    {
        using var response = await editor.PostJsonAsync("/api/v1/responsibilities/map/notes", new { text, colour, width, x, y });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<ResponsibilityMapNote>();
    }

    private static async Task<ResponsibilityMap> MapAsync()
    {
        using var response = await reader.GetAsync("/api/v1/responsibilities/map");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<ResponsibilityMap>();
    }

    [TestMethod]
    public async Task ANoteIsCreatedChangedMovedAndDeleted()
    {
        var text = $"**Infra team** {V1.Unique("ask Anna")}\nbefore changing the firewall";
        var note = await CreateAsync($"  {text}  ");
        Assert.AreEqual(text, note.Text, "the ends trimmed");
        Assert.AreEqual((ResponsibilityNoteColour.Green, ResponsibilityNoteWidth.Large, 10d, 20d), (note.Colour, note.Width, note.X, note.Y));
        Assert.IsTrue((await MapAsync()).Notes.Any(n => n.Id == note.Id), "View sees the notes");

        using (var forbidden = await reader.PostJsonAsync("/api/v1/responsibilities/map/notes", new { text = "x", x = 0, y = 0 }))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        using (var changed = await editor.PutJsonAsync($"/api/v1/responsibilities/map/notes/{note.Id}", new { text = "Infra team", colour = "Pink" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            var after = await changed.ReadAsync<ResponsibilityMapNote>();
            Assert.AreEqual(("Infra team", ResponsibilityNoteColour.Pink, ResponsibilityNoteWidth.Medium, 10d), (after.Text, after.Colour, after.Width, after.X),
                "the width left out is medium; the place stays");
        }
        using (var moved = await editor.PutJsonAsync("/api/v1/responsibilities/map/positions",
            new { notes = new[] { new { id = note.Id, x = -50.5, y = 75 } } }))
        {
            Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode, await moved.Content.ReadAsStringAsync());
            Assert.AreEqual(1, (await moved.ReadAsync<ResponsibilityMapSaveResult>()).Saved);
        }
        var placed = (await MapAsync()).Notes.Single(n => n.Id == note.Id);
        Assert.AreEqual((-50.5, 75d), (placed.X, placed.Y));

        Assert.AreEqual(HttpStatusCode.NoContent, (await editor.DeleteAsync($"/api/v1/responsibilities/map/notes/{note.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await editor.DeleteAsync($"/api/v1/responsibilities/map/notes/{note.Id}")).StatusCode);
        Assert.IsFalse((await MapAsync()).Notes.Any(n => n.Id == note.Id));

        await using var db = app.NewDb();
        var events = await db.AuditLog.AsNoTracking().Where(e => e.TargetId == note.Id.ToString() && e.Outcome == AuditOutcome.Succeeded)
            .Select(e => e.EventType).ToListAsync();
        CollectionAssert.AreEquivalent(new[] { AuditEvents.ResponsibilityNoteCreated, AuditEvents.ResponsibilityNoteChanged, AuditEvents.ResponsibilityNoteDeleted },
            events);
        var created = await db.AuditLog.AsNoTracking().SingleAsync(e => e.TargetId == note.Id.ToString() && e.EventType == AuditEvents.ResponsibilityNoteCreated);
        StringAssert.StartsWith(created.Detail, "Overview note: **Infra team**");
    }

    [TestMethod]
    public async Task TextsAndPlacesAreChecked()
    {
        foreach (var body in new object[]
                 {
                     new { text = "", x = 0, y = 0 },
                     new { text = new string('x', 2001), x = 0, y = 0 },
                     new { text = "a note", x = 1e9, y = 0 },
                     new { text = "a note" },
                     new { text = "a note", colour = "Orange", x = 0, y = 0 },
                 })
        {
            using var response = await editor.PostJsonAsync("/api/v1/responsibilities/map/notes", body);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        Assert.AreEqual(2000, (await CreateAsync(new string('y', 2000))).Text.Length);
    }

    [TestMethod]
    public async Task ATextThatLooksLikeACredentialIsSavedOnlyOnceAcknowledged()
    {
        using (var refused = await editor.PostJsonAsync("/api/v1/responsibilities/map/notes", new { text = "password: Sup3rS3cret!x9", x = 0, y = 0 }))
        {
            Assert.AreEqual("looks_like_credential", await V1.CodeOf(refused));
        }
        using var acknowledged = await editor.PostJsonAsync("/api/v1/responsibilities/map/notes",
            new { text = "password: Sup3rS3cret!x9", x = 0, y = 0, acknowledgeCredentials = true });
        Assert.AreEqual(HttpStatusCode.OK, acknowledged.StatusCode, await acknowledged.Content.ReadAsStringAsync());
        var note = await acknowledged.ReadAsync<ResponsibilityMapNote>();
        Assert.AreEqual(HttpStatusCode.NoContent, (await editor.DeleteAsync($"/api/v1/responsibilities/map/notes/{note.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task TheOverviewHoldsAtMost500Notes()
    {
        await using (var db = app.NewDb())
        {
            var have = await db.ResponsibilityMapNotes.CountAsync();
            for (var i = have; i < 500; i++)
            {
                db.ResponsibilityMapNotes.Add(new ResponsibilityMapNoteEntity { Id = Guid.NewGuid(), Text = $"filler {i}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            }
            await db.SaveChangesAsync();
        }
        try
        {
            using var full = await editor.PostJsonAsync("/api/v1/responsibilities/map/notes", new { text = "one more", x = 0, y = 0 });
            Assert.AreEqual(ResponsibilityErrors.TooManyNotes, await V1.CodeOf(full));
        }
        finally
        {
            await using var db = app.NewDb();
            await db.ResponsibilityMapNotes.Where(n => n.Text.StartsWith("filler ")).ExecuteDeleteAsync();
        }
    }
}
