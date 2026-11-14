using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;

namespace PNMTD.Tests.Locations;

/// <summary>
/// Locations through the API (docs/locations-knowledge-plan.md §3, §9, §12): sub-locations and their rules, deleting,
/// hosts and state with sub-locations, values with conflicts and inheritance, <i>From the hosts' texts…</i>, the hosts'
/// location, and the history.
/// </summary>
[TestClass]
public sealed class LocationsApiTests
{
    private static TestApp app = null!;
    private static HttpClient user = null!;
    private static HttpClient admin = null!;
    private static HttpClient viewer = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        user = app.Client(Roles.User);
        admin = app.Client(Roles.Admin);
        viewer = app.Client(Roles.Viewer);
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    internal static async Task<LocationDetail> CreateAsync(HttpClient client, string? name = null, Guid? parentId = null, string? description = null)
    {
        using var response = await client.PostJsonAsync("/api/v1/locations", new { name = name ?? V1.Unique("site"), parentId, description });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<LocationDetail>();
    }

    private static async Task<HostDetail> HostAsync(string? text = null, Guid? locationId = null, bool enabled = true)
    {
        using var response = await user.PostJsonAsync("/api/v1/hosts", new { name = V1.Unique("host"), location = text, locationId, enabled });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<HostDetail>();
    }

    private static async Task<LocationFieldItem> FieldAsync(object body)
    {
        using var response = await admin.PostJsonAsync("/api/v1/location-fields", body);
        return await response.ReadAsync<LocationFieldItem>();
    }

    private static Task<HttpResponseMessage> SetAsync(HttpClient client, Guid location, Guid field, object? value, int revision) =>
        client.PutJsonAsync($"/api/v1/locations/{location}/values/{field}", new { value, revision });

    [TestMethod]
    public async Task SubLocationsHavePathsAndNamesAreUniqueAmongSiblingsOnly()
    {
        var site = await CreateAsync(user, description: "  The data centre  ");
        var building = await CreateAsync(user, "Building A", site.Id);
        var room = await CreateAsync(user, "  Room   1 ", building.Id);
        var other = await CreateAsync(user, "Building B", site.Id);

        Assert.AreEqual("The data centre", site.Description);
        Assert.AreEqual("Room 1", room.Name);
        Assert.AreEqual($"{site.Name} › Building A › Room 1", room.Path);
        CollectionAssert.AreEqual(new[] { site.Id, building.Id }, room.Ancestors.Select(a => a.Id).ToArray());
        Assert.AreEqual(3, room.Depth);

        // Room 1 again under another building is fine; under the same one it isn't, regardless of case.
        await CreateAsync(user, "Room 1", other.Id);
        using var twice = await user.PostJsonAsync("/api/v1/locations", new { name = "ROOM 1", parentId = building.Id });
        Assert.AreEqual(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.AreEqual("duplicate_key", await V1.CodeOf(twice));
        // At the top too (NULLS NOT DISTINCT).
        using var top = await user.PostJsonAsync("/api/v1/locations", new { name = site.Name.ToUpperInvariant() });
        Assert.AreEqual(HttpStatusCode.Conflict, top.StatusCode);

        var detail = await (await viewer.GetAsync($"/api/v1/locations/{site.Id}")).ReadAsync<LocationDetail>();
        CollectionAssert.AreEqual(new[] { "Building A", "Building B" }, detail.SubLocations.Select(s => s.Name).ToArray());
        Assert.AreEqual(1, detail.SubLocations[0].SubLocations);

        using var invalid = await user.PostJsonAsync("/api/v1/locations", new { name = "x", parentId = Guid.NewGuid() });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.IsTrue((await V1.ErrorsOf(invalid)).TryGetProperty("name", out _));
    }

    [TestMethod]
    public async Task ACycleAndASeventhLevelAreRefused()
    {
        var levels = new List<LocationDetail> { await CreateAsync(user) };
        for (var i = 2; i <= 6; i++)
        {
            levels.Add(await CreateAsync(user, $"Level {i}", levels[^1].Id));
        }
        Assert.AreEqual(6, levels[^1].Depth);

        using var seventh = await user.PostJsonAsync("/api/v1/locations", new { name = "Level 7", parentId = levels[^1].Id });
        Assert.AreEqual(HttpStatusCode.Conflict, seventh.StatusCode);
        Assert.AreEqual("too_deep", await V1.CodeOf(seventh));

        // Moving a location with a sub-location under level 5 would make 7 levels.
        var pair = await CreateAsync(user);
        await CreateAsync(user, "Below", pair.Id);
        using var deep = await user.PostJsonAsync($"/api/v1/locations/{pair.Id}/move", new { parentId = levels[4].Id });
        Assert.AreEqual("too_deep", await V1.CodeOf(deep));

        using var cycle = await user.PostJsonAsync($"/api/v1/locations/{levels[1].Id}/move", new { parentId = levels[3].Id });
        Assert.AreEqual(HttpStatusCode.Conflict, cycle.StatusCode);
        Assert.AreEqual("cycle", await V1.CodeOf(cycle));
        using var self = await user.PutJsonAsync($"/api/v1/locations/{levels[1].Id}", new { name = "Level 2", parentId = levels[1].Id });
        Assert.AreEqual("cycle", await V1.CodeOf(self));

        // A move to the top is a history row with both paths.
        var moved = await (await user.PostJsonAsync($"/api/v1/locations/{levels[3].Id}/move", new { parentId = (Guid?)null })).ReadAsync<LocationDetail>();
        Assert.AreEqual("Level 4", moved.Path);
        var history = await (await viewer.GetAsync($"/api/v1/locations/{levels[3].Id}/history")).ReadAsync<Paged<LocationHistoryItem>>();
        Assert.AreEqual(LocationHistoryKind.Moved, history.Items[0].Kind);
        Assert.AreEqual($"{levels[0].Name} › Level 2 › Level 3 › Level 4", history.Items[0].OldValue!.Value.GetString());
        Assert.AreEqual("Level 4", history.Items[0].NewValue!.Value.GetString());
        Assert.AreEqual(LocationHistoryKind.Created, history.Items[^1].Kind);
        var child = await (await viewer.GetAsync($"/api/v1/locations/{levels[4].Id}")).ReadAsync<LocationDetail>();
        Assert.AreEqual("Level 4 › Level 5", child.Path);
    }

    [TestMethod]
    public async Task DeletingIsForAdminsAndOnlyWithoutSubLocations()
    {
        var site = await CreateAsync(user);
        var cage = await CreateAsync(user, "Cage B", site.Id);
        var host = await HostAsync("rack 4", cage.Id);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await user.DeleteAsync($"/api/v1/locations/{cage.Id}")).StatusCode);
        using var withChildren = await admin.DeleteAsync($"/api/v1/locations/{site.Id}");
        Assert.AreEqual(HttpStatusCode.Conflict, withChildren.StatusCode);
        var problem = await withChildren.ProblemAsync();
        Assert.AreEqual("has_sub_locations", problem.GetProperty("code").GetString());
        StringAssert.Contains(problem.GetProperty("detail").GetString(), "Cage B");
        Assert.AreEqual(cage.Id, problem.GetProperty("subLocations")[0].GetProperty("id").GetGuid());

        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/locations/{cage.Id}")).StatusCode);
        var after = await (await viewer.GetAsync($"/api/v1/hosts/{host.Id}")).ReadAsync<HostDetail>();
        Assert.IsNull(after.AssignedLocation);
        Assert.AreEqual("rack 4", after.Location, "the text is untouched");
        Assert.AreEqual(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/locations/{cage.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/locations/{site.Id}")).StatusCode);

        using var db = app.NewDb();
        var logged = await db.AuthLog.AsNoTracking().Where(l => l.EventType == AuthEvents.LocationDeleted && l.Detail!.Contains(site.Name)).ToListAsync();
        Assert.AreEqual(2, logged.Count);
        Assert.IsTrue(logged.Any(l => l.Detail!.Contains("1 host")));
    }

    [TestMethod]
    public async Task HostsAndStateIncludeSubLocations()
    {
        var site = await CreateAsync(user);
        var cage = await CreateAsync(user, "Cage B", site.Id);
        var office = await CreateAsync(user, "Office", site.Id);
        var failing = await HostAsync(locationId: cage.Id);
        var ok = await HostAsync(locationId: office.Id);
        var own = await HostAsync(locationId: site.Id);
        await HostAsync(locationId: office.Id, enabled: false);
        var f = await V1.CreateSensorAsync(user, failing.Id);
        var o = await V1.CreateSensorAsync(user, ok.Id);
        V1.AddEvents(app, f.Id, (Clock.UtcNow.AddMinutes(-5), 500, "down"));
        V1.AddEvents(app, o.Id, (Clock.UtcNow.AddMinutes(-5), 200, "up"));

        var detail = await (await viewer.GetAsync($"/api/v1/locations/{site.Id}")).ReadAsync<LocationDetail>();
        Assert.AreEqual(HostState.Failing, detail.State);
        Assert.AreEqual(new LocationHostCounts(4, 1, 1), detail.HostCounts);
        Assert.AreEqual(4, detail.Hosts.Count);
        Assert.AreEqual(failing.Id, detail.Hosts[0].Id, "failing first, as on the dashboard");
        Assert.AreEqual($"{site.Name} › Cage B", detail.Hosts[0].AssignedLocation!.Path);
        Assert.AreEqual(90, detail.Hosts[0].Days.Values.Count);
        var cageRow = detail.SubLocations.Single(s => s.Id == cage.Id);
        Assert.AreEqual(HostState.Failing, cageRow.State);
        var officeRow = detail.SubLocations.Single(s => s.Id == office.Id);
        Assert.AreEqual(HostState.Ok, officeRow.State, "the disabled host doesn't count");
        Assert.AreEqual(2, officeRow.Hosts.Total);

        var ownOnly = await (await viewer.GetAsync($"/api/v1/locations/{site.Id}?own=true")).ReadAsync<LocationDetail>();
        Assert.AreEqual(own.Id, ownOnly.Hosts.Single().Id);
        Assert.AreEqual(4, ownOnly.HostCounts.Total);

        // The tree, from the dashboard's hosts.
        app.ClearDashboardCache();
        var list = await (await viewer.GetAsync($"/api/v1/locations?search={site.Name}")).ReadAsync<LocationList>();
        var siteRow = list.Items.Single(i => i.Id == site.Id);
        Assert.AreEqual(HostState.Failing, siteRow.State);
        Assert.AreEqual(4, siteRow.Hosts.Total);
        Assert.AreEqual(2, siteRow.SubLocations);
        var tree = await (await viewer.GetAsync("/api/v1/locations")).ReadAsync<LocationList>();
        var index = tree.Items.ToList().FindIndex(i => i.Id == site.Id);
        CollectionAssert.AreEqual(new[] { cage.Id, office.Id }, tree.Items.Skip(index + 1).Take(2).Select(i => i.Id).ToArray(), "depth-first, by name");

        // The hosts list and the dashboard.
        var hosts = await (await viewer.GetAsync($"/api/v1/hosts?locationId={site.Id}&pageSize=500")).ReadAsync<Paged<HostListItem>>();
        Assert.AreEqual(4, hosts.TotalCount);
        var inCage = await (await viewer.GetAsync($"/api/v1/hosts?locationId={cage.Id}")).ReadAsync<Paged<HostListItem>>();
        Assert.AreEqual(failing.Id, inCage.Items.Single().Id);
        Assert.AreEqual(cage.Id, inCage.Items[0].AssignedLocation!.Id);
        var bySearch = await (await viewer.GetAsync($"/api/v1/hosts?search={site.Name}")).ReadAsync<Paged<HostListItem>>();
        Assert.AreEqual(4, bySearch.TotalCount, "the search matches the location's path");
        var dashboard = await (await viewer.GetAsync("/api/v1/dashboard")).ReadAsync<Dashboard>();
        Assert.AreEqual($"{site.Name} › Office", dashboard.Hosts.Single(h => h.Id == ok.Id).AssignedLocation!.Path);
    }

    [TestMethod]
    public async Task ValuesConflictInheritOverrideAndClear()
    {
        var hours = await FieldAsync(new { label = V1.Unique("Hours"), type = "Text", inherited = true });
        var site = await CreateAsync(user);
        var room = await CreateAsync(user, "Room", site.Id);

        var set = await (await SetAsync(user, site.Id, hours.Id, "8–17", 0)).ReadAsync<LocationValueItem>();
        Assert.AreEqual("8–17", set.Value!.Value.GetString());
        Assert.AreEqual(1, set.Revision);
        Assert.AreEqual("test-user", set.UpdatedByName);

        // A save from revision 0 after someone saved revision 1.
        using var stale = await SetAsync(user, site.Id, hours.Id, "9–18", 0);
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await stale.ProblemAsync();
        Assert.AreEqual("edit_conflict", conflict.GetProperty("code").GetString());
        Assert.AreEqual(1, conflict.GetProperty("revision").GetInt32());
        Assert.AreEqual("test-user", conflict.GetProperty("by").GetString());
        // Saving what is stored already is no conflict.
        Assert.AreEqual(HttpStatusCode.OK, (await SetAsync(user, site.Id, hours.Id, "8–17", 0)).StatusCode);

        var inherited = (await (await viewer.GetAsync($"/api/v1/locations/{room.Id}")).ReadAsync<LocationDetail>()).Values.Single(v => v.FieldId == hours.Id);
        Assert.AreEqual("8–17", inherited.Value!.Value.GetString());
        Assert.AreEqual(site.Id, inherited.InheritedFrom!.Id);
        Assert.AreEqual(0, inherited.Revision);

        var overridden = await (await SetAsync(user, room.Id, hours.Id, "7–12", 0)).ReadAsync<LocationValueItem>();
        Assert.AreEqual("7–12", overridden.Value!.Value.GetString());
        Assert.IsNull(overridden.InheritedFrom);

        var cleared = await (await SetAsync(user, room.Id, hours.Id, null, 1)).ReadAsync<LocationValueItem>();
        Assert.AreEqual("8–17", cleared.Value!.Value.GetString(), "back to the ancestor's");
        Assert.AreEqual(site.Id, cleared.InheritedFrom!.Id);
        Assert.AreEqual(2, cleared.Revision);

        var history = await (await viewer.GetAsync($"/api/v1/locations/{room.Id}/history")).ReadAsync<Paged<LocationHistoryItem>>();
        var change = history.Items[0];
        Assert.AreEqual(LocationHistoryKind.ValueChanged, change.Kind);
        Assert.AreEqual(hours.Label, change.FieldLabel);
        Assert.AreEqual("7–12", change.OldValue!.Value.GetString());
        Assert.IsNull(change.NewValue);

        using var invalid = await SetAsync(user, site.Id, hours.Id, "two\nlines", 1);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await SetAsync(viewer, site.Id, hours.Id, "x", 1)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await SetAsync(user, site.Id, Guid.NewGuid(), "x", 0)).StatusCode);
    }

    [TestMethod]
    public async Task MarkdownValuesRecordTheirFilesAndThePageNamesThem()
    {
        var site = await CreateAsync(user);
        var room = await CreateAsync(user, "Room", site.Id);
        var plan = await FieldAsync(new { label = V1.Unique("Plan"), type = "Markdown", inherited = true });
        var file = new FileEntity
        {
            Id = Guid.NewGuid(), Kind = FileKind.Image, Name = "cage b.png", ContentType = "image/png", Size = 1234, Width = 10, Height = 20,
            Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), Path = "2026/11/x.png", CreatedAt = Clock.UtcNow,
        };
        using (var db = app.NewDb())
        {
            db.Files.Add(file);
            await db.SaveChangesAsync();
        }

        var markdown = $"# Cage B\n\n![The cage](/api/v1/files/{file.Id}/cage-b.png)\n";
        var saved = await (await SetAsync(user, site.Id, plan.Id, markdown, 0)).ReadAsync<LocationValueItem>();
        Assert.AreEqual(markdown, saved.Value!.Value.GetString(), "stored exactly as written");

        using (var db = app.NewDb())
        {
            Assert.IsTrue(await db.FileUses.AnyAsync(u => u.FileId == file.Id && u.LocationId == site.Id));
        }
        var detail = await (await viewer.GetAsync($"/api/v1/locations/{room.Id}")).ReadAsync<LocationDetail>();
        var shown = detail.Files.Single();
        Assert.AreEqual(file.Id, shown.Id);
        Assert.AreEqual(1234, shown.Size);
        Assert.AreEqual(site.Id, detail.Values.Single(v => v.FieldId == plan.Id).InheritedFrom!.Id);
    }

    [TestMethod]
    public async Task UserAndHostValuesNameTheirTargetsAndADeletedHostClearsItsValues()
    {
        var contact = await FieldAsync(new { label = V1.Unique("Contact"), type = "User" });
        var firewall = await FieldAsync(new { label = V1.Unique("Firewall"), type = "Host" });
        var site = await CreateAsync(user);
        var host = await HostAsync();

        var person = await (await SetAsync(user, site.Id, contact.Id, TestUsers.Admin.ToString(), 0)).ReadAsync<LocationValueItem>();
        Assert.AreEqual("Test Admin", person.Target!.Name);
        using var nobody = await SetAsync(user, site.Id, contact.Id, Guid.NewGuid().ToString(), 1);
        Assert.AreEqual(HttpStatusCode.BadRequest, nobody.StatusCode);
        var fw = await (await SetAsync(user, site.Id, firewall.Id, host.Id.ToString(), 0)).ReadAsync<LocationValueItem>();
        Assert.AreEqual(host.Name, fw.Target!.Name);

        Assert.AreEqual(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/hosts/{host.Id}")).StatusCode);
        var detail = await (await viewer.GetAsync($"/api/v1/locations/{site.Id}")).ReadAsync<LocationDetail>();
        var gone = detail.Values.Single(v => v.FieldId == firewall.Id);
        Assert.IsNull(gone.Value);
        Assert.AreEqual(2, gone.Revision);
    }

    [TestMethod]
    public async Task FromTheHostsTextsMatchesIgnoringCaseAndWhitespace()
    {
        var text = V1.Unique("Zurich DC");
        var a = await HostAsync(text);
        var b = await HostAsync($"  {text.ToUpperInvariant()} ");
        var c = await HostAsync(text.Replace(" ", "   "));
        var assignedAlready = await HostAsync(text, (await CreateAsync(user)).Id);
        var elsewhere = await HostAsync(text + "x");

        var suggestions = await (await user.GetAsync("/api/v1/locations/suggestions")).ReadAsync<List<LocationSuggestion>>();
        var suggestion = suggestions.Single(s => s.Text.Equals(text, StringComparison.OrdinalIgnoreCase) || s.Text.Replace("   ", " ") == text);
        Assert.AreEqual(3, suggestion.Hosts);
        Assert.AreEqual(0, suggestion.Matches.Count);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/locations/suggestions")).StatusCode);

        var parent = await CreateAsync(user);
        var result = await (await user.PostJsonAsync("/api/v1/locations/from-text", new
        {
            items = new[] { new { text = suggestion.Text, parentId = (Guid?)parent.Id } },
            clearText = true,
        })).ReadAsync<LocationFromTextResult>();
        Assert.AreEqual(new LocationFromTextResult(1, 3), result);

        foreach (var host in new[] { a, b, c })
        {
            var detail = await (await viewer.GetAsync($"/api/v1/hosts/{host.Id}")).ReadAsync<HostDetail>();
            Assert.AreEqual(parent.Id, (await (await viewer.GetAsync($"/api/v1/locations/{detail.AssignedLocation!.Id}")).ReadAsync<LocationDetail>()).ParentId);
            Assert.IsNull(detail.Location, "the text was cleared");
        }
        Assert.AreEqual(text, (await (await viewer.GetAsync($"/api/v1/hosts/{assignedAlready.Id}")).ReadAsync<HostDetail>()).Location);
        Assert.IsNull((await (await viewer.GetAsync($"/api/v1/hosts/{elsewhere.Id}")).ReadAsync<HostDetail>()).AssignedLocation);

        // The same text again picks the location created, now an existing sibling.
        var again = await HostAsync(text);
        var second = await (await user.PostJsonAsync("/api/v1/locations/from-text", new
        {
            items = new[] { new { text, parentId = (Guid?)parent.Id } },
        })).ReadAsync<LocationFromTextResult>();
        Assert.AreEqual(new LocationFromTextResult(0, 1), second);
        var assigned = await (await viewer.GetAsync($"/api/v1/hosts/{again.Id}")).ReadAsync<HostDetail>();
        Assert.AreEqual(text, assigned.Location, "kept: not cleared this time");
        var history = await (await viewer.GetAsync($"/api/v1/locations/{assigned.AssignedLocation!.Id}/history")).ReadAsync<Paged<LocationHistoryItem>>();
        Assert.AreEqual(LocationHistoryKind.HostsAdded, history.Items[0].Kind);
        Assert.AreEqual(again.Name, history.Items[0].Detail);
    }

    [TestMethod]
    public async Task ManyHostsAtOnceAndTheHostsForm()
    {
        var site = await CreateAsync(user);
        var other = await CreateAsync(user);
        var a = await HostAsync();
        var b = await HostAsync();

        using var put = await user.PutJsonAsync("/api/v1/hosts/location", new { ids = new[] { a.Id, b.Id }, locationId = site.Id });
        Assert.AreEqual(2, await put.ReadAsync<int>());
        using var again = await user.PutJsonAsync("/api/v1/hosts/location", new { ids = new[] { a.Id, b.Id }, locationId = site.Id });
        Assert.AreEqual(0, await again.ReadAsync<int>());
        using var unknown = await user.PutJsonAsync("/api/v1/hosts/location", new { ids = new[] { a.Id }, locationId = Guid.NewGuid() });
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

        // The form moves one; null in the form is none.
        var moved = await (await user.PutJsonAsync($"/api/v1/hosts/{a.Id}", new { name = a.Name, location = "rack 4", locationId = other.Id, enabled = true }))
            .ReadAsync<HostDetail>();
        Assert.AreEqual(other.Id, moved.AssignedLocation!.Id);
        Assert.AreEqual(other.Name, moved.AssignedLocation.Path);
        Assert.AreEqual("rack 4", moved.Location);

        var none = await (await viewer.GetAsync($"/api/v1/hosts?noLocation=true&search={b.Name}")).ReadAsync<Paged<HostListItem>>();
        Assert.AreEqual(0, none.TotalCount);
        await user.PutJsonAsync("/api/v1/hosts/location", new { ids = new[] { b.Id }, locationId = (Guid?)null });
        none = await (await viewer.GetAsync($"/api/v1/hosts?noLocation=true&search={b.Name}")).ReadAsync<Paged<HostListItem>>();
        Assert.AreEqual(b.Id, none.Items.Single().Id);

        var history = await (await viewer.GetAsync($"/api/v1/locations/{site.Id}/history")).ReadAsync<Paged<LocationHistoryItem>>();
        CollectionAssert.AreEqual(new[] { LocationHistoryKind.HostsRemoved, LocationHistoryKind.HostsRemoved, LocationHistoryKind.HostsAdded, LocationHistoryKind.Created },
            history.Items.Select(h => h.Kind).ToArray());
        Assert.AreEqual(string.Join(", ", new[] { a.Name, b.Name }.Order(StringComparer.OrdinalIgnoreCase)), history.Items[2].Detail);
    }

    [TestMethod]
    public async Task RenamesAndDescriptionsAreHistory()
    {
        var site = await CreateAsync(user);
        var renamed = V1.Unique("renamed");
        var updated = await (await user.PutJsonAsync($"/api/v1/locations/{site.Id}", new { name = renamed, description = "Two floors" })).ReadAsync<LocationDetail>();
        Assert.AreEqual(renamed, updated.Name);
        Assert.AreEqual("Two floors", updated.Description);
        Assert.AreEqual("test-user", updated.UpdatedByName);

        var history = await (await viewer.GetAsync($"/api/v1/locations/{site.Id}/history")).ReadAsync<Paged<LocationHistoryItem>>();
        var kinds = history.Items.Select(h => h.Kind).ToList();
        CollectionAssert.AreEquivalent(new[] { LocationHistoryKind.Renamed, LocationHistoryKind.DescriptionChanged, LocationHistoryKind.Created }, kinds);
        var rename = history.Items.Single(h => h.Kind == LocationHistoryKind.Renamed);
        Assert.AreEqual(site.Name, rename.OldValue!.Value.GetString());
        Assert.AreEqual(renamed, rename.NewValue!.Value.GetString());
        Assert.AreEqual(JsonValueKind.Null, history.Items.Single(h => h.Kind == LocationHistoryKind.DescriptionChanged).OldValue?.ValueKind ?? JsonValueKind.Null);
    }
}
