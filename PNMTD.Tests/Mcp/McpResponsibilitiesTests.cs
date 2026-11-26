using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PNMTD.Audit;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;
using PNMTD.Tests.Auth;
using PNMTD.Tools;

namespace PNMTD.Tests.Mcp;

/// <summary>
/// Responsibilities in full over MCP (docs/responsibilities-mcp-and-map-plan.md §6): the colour as hex, a description on create,
/// restoring from the history, the escalation order, the Links tab's list and changes, and the Overview's notes. Over MCP with real
/// tokens, as a person's AI uses them.
/// </summary>
[TestClass]
public sealed class McpResponsibilitiesTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) =>
        app = new TestApp(testSignIn: false, new Dictionary<string, string?> { ["Mcp:Enabled"] = "true", ["Development:DoNotSendNotifications"] = "true" });

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    /// <summary>A fake clock, which enrolling 2FA steps on, from a whole second now.</summary>
    [TestInitialize]
    public void Freeze()
    {
        var now = DateTime.UtcNow;
        Clock.Provider = new FakeTimeProvider(new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero));
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    private static async Task<(Guid Id, Browser Browser, McpClient Client)> WriterAsync()
    {
        var (id, _, _, browser) = await McpServerTests.PersonAsync(app, Roles.Admin);
        await TwoFactorLoginTests.EnrollAsync(browser);
        var token = await McpServerTests.TurnOnAsync(browser, "write");
        return (id, browser, await McpServerTests.ClientAsync(app, token.Value!));
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, object args)
    {
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        return McpServerTests.Json(await client.CallToolAsync(tool, dictionary));
    }

    private static async Task<JsonElement> OkAsync(McpClient client, string tool, object args)
    {
        var dictionary = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        var result = await client.CallToolAsync(tool, dictionary);
        Assert.IsFalse(result.IsError ?? false, $"{tool}: {((TextContentBlock)result.Content[0]).Text}");
        return McpServerTests.Json(result);
    }

    private static async Task<string> ErrorAsync(McpClient client, string tool, object args)
    {
        var result = await CallAsync(client, tool, args);
        Assert.IsTrue(result.TryGetProperty("error", out var error), $"{tool} should have failed: {result.GetRawText()}");
        return error.GetString()!;
    }

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    private static readonly string[] Reads = ["responsibility_history", "responsibility_links_list", "responsibility_notes"];
    private static readonly string[] Writes = ["responsibility_links_change", "responsibility_note", "responsibility_note_delete"];

    [TestMethod]
    public async Task TheReadOnlyTokenGetsTheNewReadsAndCookieNone()
    {
        var (_, browser, writer) = await WriterAsync();
        await using (writer)
        {
            var read = await McpServerTests.ClientAsync(app, (await McpServerTests.TurnOnAsync(browser, "read")).Value!);
            await using (read)
            {
                var readOnly = (await read.ListToolsAsync()).Select(t => t.Name).ToList();
                CollectionAssert.IsSubsetOf(Reads, readOnly);
                Assert.IsFalse(Writes.Any(readOnly.Contains), "nothing that changes");
            }
            var all = (await writer.ListToolsAsync()).Select(t => t.Name).ToList();
            CollectionAssert.IsSubsetOf(Reads.Concat(Writes).ToList(), all);
        }
        Assert.IsFalse(ToolCatalog.All.Any(t => t.ForCookie && (Reads.Contains(t.Name) || Writes.Contains(t.Name))), "Cookie gets none of them");
        Assert.AreEqual(ToolKind.Delete, ToolCatalog.All.Single(t => t.Name == "responsibility_note_delete").Kind);
    }

    [TestMethod]
    public async Task AColourIsSetAsHexKeptWhenLeftOutAndClearedWithAnEmptyText()
    {
        var (_, _, client) = await WriterAsync();
        await using (client)
        {
            var options = await OkAsync(client, "responsibility_options", new { });
            Assert.AreEqual(12, options.GetProperty("palette").GetArrayLength());
            StringAssert.Matches(options.GetProperty("suggestedColor").GetString(), new System.Text.RegularExpressions.Regex("^#[0-9a-f]{6}$"));

            var name = Unique("Colour");
            var created = await OkAsync(client, "responsibility_create", new { name, color = "#2F8FD8", description = "# Colour\n\nThe first text." });
            var id = created.GetProperty("id").GetGuid();
            Assert.AreEqual(("#2f8fd8", "Blue"), (created.GetProperty("color").GetString(), created.GetProperty("colorName").GetString()));
            StringAssert.Contains((await OkAsync(client, "responsibility_get", new { id })).GetProperty("description").GetString(), "The first text.");

            // Without the #, in capitals: stored as #rrggbb; a custom colour has no name.
            var changed = await OkAsync(client, "responsibility_update", new { id, color = "D94F4F" });
            Assert.AreEqual(("#d94f4f", "Red"), (changed.GetProperty("color").GetString(), changed.GetProperty("colorName").GetString()));
            Assert.IsFalse((await OkAsync(client, "responsibility_update", new { id, color = "#123456" })).TryGetProperty("colorName", out _));
            await using (var db = app.NewDb())
            {
                var rows = await db.ResponsibilityHistory.Where(h => h.ResponsibilityId == id && h.Kind == ResponsibilityHistoryKind.ColorChanged).ToListAsync();
                Assert.AreEqual(2, rows.Count, "one row a change");
                Assert.IsTrue(rows.All(r => r.Via == "mcp"));
            }

            // Another field alone keeps it; names and short forms are refused; an empty text clears it.
            var summary = await OkAsync(client, "responsibility_update", new { id, summary = "Only the summary" });
            Assert.AreEqual("#123456", summary.GetProperty("color").GetString());
            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "responsibility_update", new { id, color = "blue" }));
            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "responsibility_update", new { id, color = "#28d" }));
            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "responsibility_create", new { name = Unique("Bad"), color = "red" }));
            Assert.IsFalse((await OkAsync(client, "responsibility_update", new { id, color = "" })).TryGetProperty("color", out _));

            // The list and the page say it, with whom to ask and whether it needs attention: nobody is set.
            var list = await OkAsync(client, "responsibilities_list", new { query = name, attention = true });
            var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);
            Assert.IsTrue(item.GetProperty("attention").GetBoolean());
            Assert.IsTrue(item.TryGetProperty("escalation", out _));
            await OkAsync(client, "responsibility_update", new { id, color = "#2e9d5b" });
            var got = await OkAsync(client, "responsibility_get", new { id });
            Assert.AreEqual(("#2e9d5b", "Green"), (got.GetProperty("color").GetString(), got.GetProperty("colorName").GetString()));
            Assert.IsTrue(got.TryGetProperty("createdAt", out _));

            // A credential in a new description is refused before anything is made.
            var refused = Unique("Refused");
            Assert.AreEqual("looks_like_credential", await ErrorAsync(client, "responsibility_create", new { name = refused, description = "password: Sup3rS3cret!x9" }));
            await using (var db = app.NewDb())
            {
                Assert.IsFalse(await db.Responsibilities.AnyAsync(r => r.Name == refused));
            }
        }
    }

    [TestMethod]
    public async Task TheHistoryIsReadAndADescriptionRestoredFromIt()
    {
        var (id, _, client) = await WriterAsync();
        await using (client)
        {
            var created = await OkAsync(client, "responsibility_create", new { name = Unique("History"), responsibleId = id, description = "Version one." });
            var rid = created.GetProperty("id").GetGuid();
            // The clock stands still: a second later, so that "newest first" has one newest row.
            ((FakeTimeProvider)Clock.Provider).Advance(TimeSpan.FromSeconds(1));
            await OkAsync(client, "responsibility_update", new { id = rid, description = "Version two.", baseRevision = created.GetProperty("revision").GetInt32() });

            var history = await OkAsync(client, "responsibility_history", new { id = rid });
            var newest = history.GetProperty("items").EnumerateArray().First();
            Assert.AreEqual(("description changed", true, true), (newest.GetProperty("kind").GetString(), newest.GetProperty("restorable").GetBoolean(),
                newest.GetProperty("viaMcp").GetBoolean()));
            var row = newest.GetProperty("id").GetGuid();
            var detail = await OkAsync(client, "responsibility_history", new { id = rid, rowId = row });
            StringAssert.Contains(detail.GetProperty("before").GetString(), "Version one.");
            StringAssert.Contains(detail.GetProperty("after").GetString(), "Version two.");

            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "responsibility_update", new { id = rid, restoreFrom = row, description = "x", baseRevision = 1 }));
            await OkAsync(client, "responsibility_update", new { id = rid, restoreFrom = row });
            StringAssert.Contains((await OkAsync(client, "responsibility_get", new { id = rid })).GetProperty("description").GetString(), "Version one.");
            Assert.AreEqual("not_found", await ErrorAsync(client, "responsibility_history", new { id = rid, rowId = Guid.NewGuid() }));

            // Its escalation order names the responsible first.
            var first = created.GetProperty("escalation").EnumerateArray().First();
            Assert.AreEqual((1, "responsible"), (first.GetProperty("step").GetInt32(), first.GetProperty("role").GetString()));
        }
    }

    [TestMethod]
    public async Task TheLinksTabIsListedAndChangedManyAtOnce()
    {
        var (_, _, client) = await WriterAsync();
        await using (client)
        {
            var one = (await OkAsync(client, "responsibility_create", new { name = Unique("Links one") })).GetProperty("id").GetGuid();
            var two = (await OkAsync(client, "responsibility_create", new { name = Unique("Links two") })).GetProperty("id").GetGuid();
            var hostName = "lnk-" + Guid.NewGuid().ToString("N")[..10];
            var host = (await OkAsync(client, "host_create", new { name = hostName })).GetProperty("id").GetGuid();
            var spare = "lnk-" + Guid.NewGuid().ToString("N")[..10];
            await OkAsync(client, "host_create", new { name = spare });

            var linked = await OkAsync(client, "responsibility_links_change", new { action = "link", responsibilityId = one, hostIds = new[] { host } });
            Assert.AreEqual(1, linked.GetProperty("linked").GetInt32());
            var again = await OkAsync(client, "responsibility_links_change", new { action = "link", responsibilityId = one, hostIds = new[] { host } });
            Assert.AreEqual((0, "already_linked"), (again.GetProperty("linked").GetInt32(), again.GetProperty("skipped")[0].GetProperty("reason").GetString()));

            var rows = await OkAsync(client, "responsibility_links_list", new { responsibilityIds = new[] { one } });
            var row = rows.GetProperty("items").EnumerateArray().Single();
            Assert.AreEqual(("Host", host), (row.GetProperty("kind").GetString(), row.GetProperty("targetId").GetGuid()));
            var link = row.GetProperty("linkId").GetGuid();

            // Also linked to the second, then unlinked from the first.
            var copied = await OkAsync(client, "responsibility_links_change", new { action = "move", linkIds = new[] { link }, responsibilityId = two, keep = true });
            Assert.AreEqual((1, 0), (copied.GetProperty("linked").GetInt32(), copied.GetProperty("unlinked").GetInt32()));
            Assert.AreEqual(2, (await OkAsync(client, "responsibility_links_list", new { responsibilityIds = new[] { one, two } })).GetProperty("items").GetArrayLength());
            var unlinked = await OkAsync(client, "responsibility_links_change", new { action = "unlink", linkIds = new[] { link } });
            Assert.AreEqual(1, unlinked.GetProperty("unlinked").GetInt32());

            // What nobody links: the spare host, not the one the second links.
            var nobody = await OkAsync(client, "responsibility_links_list", new { view = "unlinkedHosts", query = "lnk-", limit = 100 });
            var names = nobody.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToList();
            Assert.IsTrue(names.Any(n => n.Contains(spare)));
            Assert.IsFalse(names.Any(n => n.Contains(hostName)));

            Assert.AreEqual("invalid_argument", await ErrorAsync(client, "responsibility_links_change", new { action = "link", responsibilityId = one }));
            await using var db = app.NewDb();
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.ResponsibilityUnlinked && l.Actor == AuditActor.Mcp
                && l.TargetId == one.ToString()));
        }
    }

    [TestMethod]
    public async Task NotesAreWrittenReadAndDeletedOnlyInTheWindow()
    {
        var (_, browser, client) = await WriterAsync();
        await using (client)
        {
            var text = Unique("Ask Anna first");
            var note = await OkAsync(client, "responsibility_note", new { action = "create", text, colour = "Green" });
            var id = note.GetProperty("id").GetGuid();
            Assert.AreEqual(("Green", "Medium"), (note.GetProperty("colour").GetString(), note.GetProperty("width").GetString()));
            Assert.AreEqual(JsonValueKind.Number, note.GetProperty("x").ValueKind);

            var changed = await OkAsync(client, "responsibility_note", new { action = "update", noteId = id, text = text + " or Ben", width = "Large" });
            Assert.AreEqual(("Green", "Large"), (changed.GetProperty("colour").GetString(), changed.GetProperty("width").GetString()));
            StringAssert.Contains(changed.GetProperty("text").GetString(), "or Ben");
            Assert.AreEqual("looks_like_credential", await ErrorAsync(client, "responsibility_note", new { action = "create", text = "password: Sup3rS3cret!x9" }));
            Assert.AreEqual("not_found", await ErrorAsync(client, "responsibility_note", new { action = "update", noteId = Guid.NewGuid(), text = "x" }));

            var notes = await OkAsync(client, "responsibility_notes", new { });
            Assert.IsTrue(notes.GetProperty("notes").EnumerateArray().Any(n => n.GetProperty("id").GetGuid() == id));

            Assert.AreEqual("delete_not_allowed", await ErrorAsync(client, "responsibility_note_delete", new { id }));
            using (var open = await browser.PostAsync("/api/v1/account/mcp/write/delete-window"))
            {
                Assert.AreEqual(System.Net.HttpStatusCode.OK, open.StatusCode, await open.Content.ReadAsStringAsync());
            }
            var deleted = await OkAsync(client, "responsibility_note_delete", new { id });
            StringAssert.Contains(deleted.GetProperty("deleted").GetProperty("text").GetString(), "or Ben");
            using (var stop = await browser.SendAsync(HttpMethod.Delete, "/api/v1/account/mcp/write/delete-window"))
            {
                Assert.AreEqual(System.Net.HttpStatusCode.OK, stop.StatusCode);
            }
            await using var db = app.NewDb();
            Assert.IsFalse(await db.ResponsibilityMapNotes.AnyAsync(n => n.Id == id));
            var entry = await db.AuditLog.SingleAsync(l => l.EventType == AuditEvents.ResponsibilityNoteDeleted && l.TargetId == id.ToString());
            Assert.AreEqual(AuditActor.Mcp, entry.Actor);
            StringAssert.Contains(entry.Changes, "or Ben", "the note as it was");
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.ResponsibilityNoteCreated && l.TargetId == id.ToString() && l.Actor == AuditActor.Mcp));
        }
    }
}
