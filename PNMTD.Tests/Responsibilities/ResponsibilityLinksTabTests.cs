using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Models.Db;
using PNMTD.Tests.Api.V1;
using PNMTD.Tests.Locations;

namespace PNMTD.Tests.Responsibilities;

/// <summary>
/// The Links tab (docs/responsibilities-extensions-plan.md §5): every link with its filters, paging and sorting; the hosts and locations
/// no responsibility links, and who may list them; linking, unlinking, moving and copying many at once, with the limit, the history
/// and one audit entry per responsibility.
/// </summary>
[TestClass]
public sealed class ResponsibilityLinksTabTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static HttpClient editor = null!;
    private static HttpClient reader = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        app = new TestApp();
        admin = app.Client(Roles.Admin);
        editor = app.ClientWithRights("Responsibilities:Edit");
        reader = app.ClientWithRights("Responsibilities:View");
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<ResponsibilityDetail> CreateAsync(string name, Guid? responsibleId = null, Guid? deputyId = null)
    {
        using var response = await editor.PostJsonAsync("/api/v1/responsibilities", new { name = V1.Unique(name), responsibleId, deputyId });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<ResponsibilityDetail>();
    }

    private static async Task<Guid> UserAsync(string name)
    {
        await using var db = app.NewDb();
        var username = V1.Unique("person").ToLowerInvariant();
        var user = new UserEntity
        {
            Id = Guid.NewGuid(), Username = username, NormalizedUsername = username, DisplayName = name, PasswordHash = "x", SecurityStamp = "x",
            IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>A host in a location, named uniquely.</summary>
    private static async Task<HostDetail> HostInAsync(string name, Guid? locationId)
    {
        var host = await V1.CreateHostAsync(admin, V1.Unique(name));
        await using var db = app.NewDb();
        await db.Hosts.Where(h => h.Id == host.Id).ExecuteUpdateAsync(h => h.SetProperty(x => x.LocationId, locationId));
        return host;
    }

    private static async Task<KbPageDetail> PageAsync(string title) =>
        (await (await admin.PostJsonAsync("/api/v1/kb/pages", new KbPageRequest { Title = V1.Unique(title), Content = "x" })).ReadAsync<KbSaveResult>()).Page;

    private static async Task<ResponsibilityBulkResult> LinkAsync(Guid responsibility, params (string Kind, Guid Id)[] targets)
    {
        using var response = await editor.PostJsonAsync("/api/v1/responsibilities/links",
            new { responsibilityId = responsibility, targets = targets.Select(t => new { kind = t.Kind, id = t.Id }) });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<ResponsibilityBulkResult>();
    }

    private static async Task<Paged<ResponsibilityLinkRow>> RowsAsync(string query, HttpClient? client = null)
    {
        using var response = await (client ?? reader).GetAsync($"/api/v1/responsibilities/links?pageSize=500&{query}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<Paged<ResponsibilityLinkRow>>();
    }

    /// <summary>"hosts?location=…" or "locations?…".</summary>
    private static async Task<Paged<ResponsibilityUnlinkedRow>> UnlinkedAsync(string query, HttpClient? client = null)
    {
        using var response = await (client ?? admin).GetAsync($"/api/v1/responsibilities/unlinked/{query}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<Paged<ResponsibilityUnlinkedRow>>();
    }

    [TestMethod]
    public async Task EveryLinkIsARowFilteredByResponsibilityPersonKindAndLocation()
    {
        var anna = await UserAsync("Anna Links");
        var network = await CreateAsync("Network", responsibleId: anna);
        var backup = await CreateAsync("Backup");
        var site = await LocationsApiTests.CreateAsync(admin, V1.Unique("Zurich"));
        var room = await LocationsApiTests.CreateAsync(admin, "Server room", site.Id);
        var other = await LocationsApiTests.CreateAsync(admin, V1.Unique("Geneva"));
        var core = await HostInAsync("sw-core", room.Id);
        var far = await HostInAsync("sw-far", other.Id);
        var page = await PageAsync("WAN providers");
        await LinkAsync(network.Id, ("Host", core.Id), ("Location", room.Id), ("Page", page.Id));
        await LinkAsync(backup.Id, ("Host", core.Id), ("Host", far.Id));

        var mine = await RowsAsync($"responsibility={network.Id}&responsibility={backup.Id}");
        Assert.AreEqual(5, mine.TotalCount);
        var hostRow = mine.Items.First(i => i.TargetId == core.Id && i.Responsibility.Id == network.Id);
        Assert.AreEqual($"{site.Name} › Server room", hostRow.Detail, "a host is where its location is");
        Assert.AreEqual(site.Name, mine.Items.Single(i => i.Kind == KbLinkKind.Location).Detail, "a location is where its parent is");
        var pageRow = mine.Items.Single(i => i.Kind == KbLinkKind.Page);
        Assert.AreEqual(page.Number, pageRow.Number);
        Assert.AreEqual(page.Title, pageRow.Name);

        Assert.AreEqual(3, (await RowsAsync($"person={anna}")).TotalCount, "Anna is responsible for Network");
        Assert.AreEqual(3, (await RowsAsync($"responsibility={network.Id}&responsibility={backup.Id}&kind=Host")).TotalCount);
        Assert.AreEqual(4, (await RowsAsync($"responsibility={network.Id}&responsibility={backup.Id}&kind=Host&kind=Page")).TotalCount);
        // The site and below: both links of sw-core and the room; not Geneva's host, and pages have no location.
        Assert.AreEqual(3, (await RowsAsync($"location={site.Id}")).TotalCount);
        Assert.AreEqual(0, (await RowsAsync($"location={site.Id}&below=false")).TotalCount, "nothing is linked at the site itself");

        // The search: an object's name, its Where, the responsibility's name, a page's number.
        Assert.AreEqual(2, (await RowsAsync($"search={core.Name}")).TotalCount);
        Assert.AreEqual(3, (await RowsAsync($"search={Uri.EscapeDataString(site.Name)}")).TotalCount, "the path of sw-core and of the room");
        Assert.AreEqual(3, (await RowsAsync($"search={Uri.EscapeDataString(network.Name)}")).TotalCount);
        Assert.AreEqual(1, (await RowsAsync($"responsibility={network.Id}&search=KB-{page.Number}")).TotalCount);
    }

    [TestMethod]
    public async Task RowsArePagedAndSorted()
    {
        var r = await CreateAsync("Sorted");
        var hosts = new List<HostDetail>();
        foreach (var name in new[] { "c-host", "a-host", "b-host" })
        {
            hosts.Add(await HostInAsync(name, null));
        }
        await LinkAsync(r.Id, [.. hosts.Select(h => ("Host", h.Id))]);

        var byObject = await RowsAsync($"responsibility={r.Id}&sort=object");
        CollectionAssert.AreEqual(hosts.Select(h => h.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(), byObject.Items.Select(i => i.Name).ToList());
        var descending = await RowsAsync($"responsibility={r.Id}&sort=-object");
        Assert.AreEqual(byObject.Items[0].Name, descending.Items[^1].Name);
        using (var page = await reader.GetAsync($"/api/v1/responsibilities/links?responsibility={r.Id}&sort=object&page=2&pageSize=2"))
        {
            var second = await page.ReadAsync<Paged<ResponsibilityLinkRow>>();
            Assert.AreEqual(3, second.TotalCount);
            Assert.AreEqual(byObject.Items[2].Id, second.Items.Single().Id);
        }
        using var wrong = await reader.GetAsync("/api/v1/responsibilities/links?sort=colour");
        Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [TestMethod]
    public async Task WhatNobodyLinksListsHostsAndLocationsWithTheirLocationsResponsibilities()
    {
        var r = await CreateAsync("Site");
        var site = await LocationsApiTests.CreateAsync(admin, V1.Unique("Basel"));
        var room = await LocationsApiTests.CreateAsync(admin, "Rack room", site.Id);
        var lonely = await LocationsApiTests.CreateAsync(admin, V1.Unique("Bern"));
        var inRoom = await HostInAsync("in-room", room.Id);
        var linked = await HostInAsync("linked", room.Id);
        var outside = await HostInAsync("outside", lonely.Id);
        await LinkAsync(r.Id, ("Location", site.Id), ("Host", linked.Id));

        // In Basel and below, only in-room: a host in a linked location is not linked itself.
        var row = (await UnlinkedAsync($"hosts?pageSize=500&location={site.Id}")).Items.Single();
        Assert.AreEqual(inRoom.Id, row.Id);
        Assert.AreEqual(r.Id, row.ThroughLocation.Single().Id, "its location's parent is linked");
        Assert.AreEqual($"{site.Name} › Rack room", row.Detail);
        Assert.AreEqual(0, (await UnlinkedAsync($"hosts?pageSize=500&location={lonely.Id}")).Items.Single(h => h.Id == outside.Id).ThroughLocation.Count);
        Assert.IsTrue((await UnlinkedAsync($"hosts?pageSize=500&search={inRoom.Name}")).Items.Any(h => h.Id == inRoom.Id), "without a location filter too");
        Assert.IsFalse((await UnlinkedAsync($"hosts?pageSize=500&search={linked.Name}")).Items.Any(), "a linked host isn't listed");

        Assert.AreEqual(0, (await UnlinkedAsync($"hosts?pageSize=500&location={site.Id}&covered=true")).TotalCount, "covered: through its location");
        Assert.AreEqual(1, (await UnlinkedAsync($"hosts?pageSize=500&location={lonely.Id}&covered=true")).TotalCount);

        var locations = await UnlinkedAsync($"locations?pageSize=500&location={site.Id}");
        Assert.AreEqual(room.Id, locations.Items.Single().Id, "Basel is linked itself; its room isn't");
        Assert.AreEqual(r.Id, locations.Items.Single().ThroughLocation.Single().Id);
        Assert.AreEqual(1, (await UnlinkedAsync($"locations?pageSize=500&location={lonely.Id}")).TotalCount);
    }

    [TestMethod]
    public async Task ListingWhatNobodyLinksNeedsTheRightToListIt()
    {
        foreach (var kind in new[] { "hosts", "locations" })
        {
            using var viewOnly = await editor.GetAsync($"/api/v1/responsibilities/unlinked/{kind}");
            Assert.AreEqual(HttpStatusCode.Forbidden, viewOnly.StatusCode, $"{kind}: Responsibilities: Edit alone doesn't list them");
        }
        var monitoring = app.ClientWithRights("Responsibilities:View,Monitoring:View");
        await UnlinkedAsync("hosts?pageSize=5", monitoring);
        using (var locations = await monitoring.GetAsync("/api/v1/responsibilities/unlinked/locations"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, locations.StatusCode, "Monitoring: View lists hosts, not locations");
        }
        var places = app.ClientWithRights("Responsibilities:View,Locations:View");
        await UnlinkedAsync("locations?pageSize=5", places);
        using (var hosts = await places.GetAsync("/api/v1/responsibilities/unlinked/hosts"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, hosts.StatusCode);
        }
        using (var without = await app.ClientWithRights("Monitoring:View").GetAsync("/api/v1/responsibilities/unlinked/hosts"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, without.StatusCode, "Responsibilities: View is needed too");
        }
    }

    [TestMethod]
    public async Task LinkingManySkipsWhatIsLinkedOrGoneAndWritesOneAuditEntry()
    {
        var r = await CreateAsync("Many");
        var a = await HostInAsync("many-a", null);
        var b = await HostInAsync("many-b", null);
        await LinkAsync(r.Id, ("Host", a.Id));
        using (var forbidden = await reader.PostJsonAsync("/api/v1/responsibilities/links", new { responsibilityId = r.Id, targets = new[] { new { kind = "Host", id = b.Id } } }))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        var gone = Guid.NewGuid();
        var result = await LinkAsync(r.Id, ("Host", a.Id), ("Host", b.Id), ("Host", gone));
        Assert.AreEqual(1, result.Linked);
        Assert.AreEqual(ResponsibilityErrors.AlreadyLinked, result.Skipped.Single(s => s.Id == a.Id).Reason);
        Assert.AreEqual(ResponsibilityErrors.NotFound, result.Skipped.Single(s => s.Id == gone).Reason);

        using (var sensor = await editor.PostJsonAsync("/api/v1/responsibilities/links", new { responsibilityId = r.Id, targets = new[] { new { kind = "Sensor", id = b.Id } } }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, sensor.StatusCode);
        }
        using (var none = await editor.PostJsonAsync("/api/v1/responsibilities/links", new { responsibilityId = r.Id, targets = Array.Empty<object>() }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, none.StatusCode);
        }

        await using var db = app.NewDb();
        var history = await db.ResponsibilityHistory.AsNoTracking().Where(h => h.ResponsibilityId == r.Id && h.Kind == ResponsibilityHistoryKind.LinkAdded).ToListAsync();
        Assert.AreEqual(2, history.Count, "one row per link");
        var entries = await db.AuditLog.AsNoTracking()
            .Where(e => e.EventType == AuditEvents.ResponsibilityLinked && e.TargetId == r.Id.ToString() && e.Outcome == AuditOutcome.Succeeded).ToListAsync();
        var bulk = entries.Single(e => e.Subjects.Contains(b.Id));
        StringAssert.Contains(bulk.Detail, $"host {b.Name}");
    }

    [TestMethod]
    public async Task TheLimitCountsBeforeAnythingIsLinked()
    {
        var r = await CreateAsync("Full");
        await using (var db = app.NewDb())
        {
            for (var i = 0; i < 199; i++)
            {
                var host = new HostEntity { Id = Guid.NewGuid(), Name = V1.Unique("filler"), Location = "lab", Created = DateTime.UtcNow };
                db.Hosts.Add(host);
                db.ResponsibilityLinks.Add(new ResponsibilityLinkEntity { Id = Guid.NewGuid(), ResponsibilityId = r.Id, HostId = host.Id, CreatedAt = DateTime.UtcNow });
            }
            await db.SaveChangesAsync();
        }
        var a = await HostInAsync("over-a", null);
        var b = await HostInAsync("over-b", null);
        using var over = await editor.PostJsonAsync("/api/v1/responsibilities/links",
            new { responsibilityId = r.Id, targets = new[] { new { kind = "Host", id = a.Id }, new { kind = "Host", id = b.Id } } });
        Assert.AreEqual(ResponsibilityErrors.TooManyLinks, await V1.CodeOf(over));
        StringAssert.Contains(await over.Content.ReadAsStringAsync(), "1 more fit");
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(199, await db.ResponsibilityLinks.CountAsync(l => l.ResponsibilityId == r.Id), "nothing was linked");
        }
        Assert.AreEqual(1, (await LinkAsync(r.Id, ("Host", a.Id))).Linked);
    }

    [TestMethod]
    public async Task UnlinkingManyWritesAnEntryPerResponsibility()
    {
        var one = await CreateAsync("One");
        var two = await CreateAsync("Two");
        var host = await HostInAsync("shared", null);
        await LinkAsync(one.Id, ("Host", host.Id));
        await LinkAsync(two.Id, ("Host", host.Id));
        var rows = await RowsAsync($"responsibility={one.Id}&responsibility={two.Id}");
        using var response = await editor.PostJsonAsync("/api/v1/responsibilities/links/unlink", new { linkIds = rows.Items.Select(i => i.Id).Append(Guid.NewGuid()) });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(2, (await response.ReadAsync<ResponsibilityBulkResult>()).Unlinked, "a link gone already is left out");
        Assert.AreEqual(0, (await RowsAsync($"responsibility={one.Id}&responsibility={two.Id}")).TotalCount);

        await using var db = app.NewDb();
        foreach (var r in new[] { one, two })
        {
            var entry = await db.AuditLog.AsNoTracking()
                .SingleAsync(e => e.EventType == AuditEvents.ResponsibilityUnlinked && e.TargetId == r.Id.ToString() && e.Outcome == AuditOutcome.Succeeded);
            CollectionAssert.Contains(entry.Subjects, host.Id, "the host's own History shows it");
            Assert.IsTrue(await db.ResponsibilityHistory.AnyAsync(h => h.ResponsibilityId == r.Id && h.Kind == ResponsibilityHistoryKind.LinkRemoved));
        }
    }

    [TestMethod]
    public async Task MovingLinksLeavesTheirSourceAndCopyingKeepsIt()
    {
        var from = await CreateAsync("From");
        var to = await CreateAsync("To");
        var a = await HostInAsync("move-a", null);
        var b = await HostInAsync("move-b", null);
        var c = await HostInAsync("move-c", null);
        await LinkAsync(from.Id, ("Host", a.Id), ("Host", b.Id), ("Host", c.Id));
        await LinkAsync(to.Id, ("Host", b.Id));
        var rows = (await RowsAsync($"responsibility={from.Id}")).Items;

        // Also link to: a is copied, b is on the target already, and From keeps both.
        using (var copy = await editor.PostJsonAsync("/api/v1/responsibilities/links/move",
            new { linkIds = rows.Where(r => r.TargetId != c.Id).Select(r => r.Id), responsibilityId = to.Id, keep = true }))
        {
            var result = await copy.ReadAsync<ResponsibilityBulkResult>();
            Assert.AreEqual((1, 0), (result.Linked, result.Unlinked));
            Assert.AreEqual(b.Id, result.Skipped.Single().Id);
        }
        Assert.AreEqual(3, (await RowsAsync($"responsibility={from.Id}")).TotalCount);

        // Move to: c goes over; b, on the target already, only leaves From.
        using (var move = await editor.PostJsonAsync("/api/v1/responsibilities/links/move",
            new { linkIds = rows.Where(r => r.TargetId != a.Id).Select(r => r.Id), responsibilityId = to.Id, keep = false }))
        {
            Assert.AreEqual(HttpStatusCode.OK, move.StatusCode, await move.Content.ReadAsStringAsync());
            var result = await move.ReadAsync<ResponsibilityBulkResult>();
            Assert.AreEqual((1, 2), (result.Linked, result.Unlinked));
        }
        CollectionAssert.AreEquivalent(new[] { a.Id }, (await RowsAsync($"responsibility={from.Id}")).Items.Select(i => i.TargetId).ToArray());
        CollectionAssert.AreEquivalent(new[] { a.Id, b.Id, c.Id }, (await RowsAsync($"responsibility={to.Id}")).Items.Select(i => i.TargetId).ToArray());

        await using var db = app.NewDb();
        var moved = await db.ResponsibilityHistory.AsNoTracking().Where(h => h.ResponsibilityId == to.Id && h.Kind == ResponsibilityHistoryKind.LinkAdded)
            .Select(h => h.Detail).ToListAsync();
        Assert.IsTrue(moved.Any(d => d!.Contains($"host {c.Name}, moved from {from.Name}")), string.Join(" | ", moved));
        Assert.IsTrue(await db.AuditLog.AnyAsync(e => e.EventType == AuditEvents.ResponsibilityUnlinked && e.TargetId == from.Id.ToString()
            && e.Detail!.Contains($"moved to {to.Name}")));
    }
}
