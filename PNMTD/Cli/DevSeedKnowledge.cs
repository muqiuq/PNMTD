using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Cli;

/// <summary>
/// The seed's locations and knowledge base (docs/locations-knowledge-plan.md §8.3, K7), written through PNMTD's own services as
/// the API would, each change in the name of a demo user and at a time in the past, so the histories read as they would:
/// <list type="bullet">
/// <item><b>Locations:</b> the site <i>Zurich DC</i> with <i>Cage B</i> (and its <i>Rack B4</i>) and the <i>Meet-me room</i>, and the
///   site <i>Zurich office</i>, made from its hosts' texts. Their hosts are assigned; the cloud's, the other offices' and some
///   Zurich DC hosts keep only their texts, for <i>From the hosts' texts…</i>.</item>
/// <item><b>Fields:</b> the typical ones, a link and a number, with values: Markdown (a diagram and a floor plan among them),
///   inherited and overridden ones, and sensitive ones.</item>
/// <item><b>Pages:</b> about twenty, with tags, statuses, links to locations, hosts and sensors and to each other, revisions with
///   notes, one sensitive and one archived page, views over the last 30 days, an image, an attachment and Mermaid diagrams.</item>
/// </list>
/// The image and the attachment go through <see cref="FileStore"/> into the files folder, so they are on disk as uploads are;
/// without a writable folder they are left out, with a warning.
/// </summary>
internal static class DevSeedKnowledge
{
    public sealed record Counts(int Locations, int Pages, int Files);

    /// <summary>Moves only when the seed moves it: the services take their times from <see cref="Clock"/>.</summary>
    private sealed class SeedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SeedDatabases(Func<PnmtdDbContext> create) : IDbContextFactory<PnmtdDbContext>
    {
        public PnmtdDbContext CreateDbContext() => create();
    }

    private sealed record Edit(double DaysAgo, string By, string? Note, Func<string, string>? Content = null, string[]? Tags = null,
        KbPageStatus? Status = null, string? Summary = null);

    private sealed record PageSpec(string Key, string Title, string? Summary, string[] Tags, KbPageStatus Status, string By, double DaysAgo,
        string Content, string[] Links, Edit[] Edits, bool Sensitive = false, double? ArchivedDaysAgo = null, int Popularity = 1);

    public static Counts Seed(Func<PnmtdDbContext> database, IConfiguration configuration, IReadOnlyDictionary<string, HostEntity> hosts, DateTime now)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IDbContextFactory<PnmtdDbContext>>(new SeedDatabases(database));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext());
        services.AddDomainServices();
        services.AddFiles(configuration);
        using var provider = services.BuildServiceProvider();

        var clock = new SeedClock(now);
        var before = Clock.Provider;
        Clock.Provider = clock;
        try
        {
            return new Run(provider, clock, hosts, now).All();
        }
        finally
        {
            Clock.Provider = before;
        }
    }

    private sealed class Run(ServiceProvider provider, SeedClock clock, IReadOnlyDictionary<string, HostEntity> hosts, DateTime now)
    {
        private readonly Dictionary<string, UserEntity> users = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid> locations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Guid> fields = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FileItem> files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, KbPageDetail> pages = new(StringComparer.Ordinal);
        private Dictionary<(string Host, string Sensor), Guid> sensors = [];

        public Counts All()
        {
            At(80);
            Users();
            Locations();
            Fields();
            Files();
            Pages();
            Values();
            Views();
            return new Counts(locations.Count, pages.Count, files.Count);
        }

        private void At(double daysAgo) => clock.Now = new DateTimeOffset(now.AddDays(-daysAgo));

        /// <summary>A request of <paramref name="by"/>: a scope of its own, with that user signed in.</summary>
        private T As<T>(string by, Func<IServiceProvider, Task<T>> action)
        {
            using var scope = provider.CreateScope();
            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            accessor.HttpContext = new DefaultHttpContext { User = SessionClaims.Create(users[by]), RequestServices = scope.ServiceProvider };
            try
            {
                return action(scope.ServiceProvider).GetAwaiter().GetResult();
            }
            finally
            {
                accessor.HttpContext = null;
            }
        }

        private PnmtdDbContext Db() => provider.GetRequiredService<IDbContextFactory<PnmtdDbContext>>().CreateDbContext();

        // --- users -------------------------------------------------------------------------------------------------------

        /// <summary>The authors: the first admin, philipp (DevSeedAgents) and two Users, Anna Muster and Ben Keller. Unusable passwords.</summary>
        private void Users()
        {
            using var db = Db();
            foreach (var (username, name) in new[] { ("anna", "Anna Muster"), ("ben", "Ben Keller") })
            {
                if (!db.Users.Any(u => u.NormalizedUsername == username))
                {
                    db.Users.Add(new UserEntity
                    {
                        Id = Guid.NewGuid(), Username = username, NormalizedUsername = username, DisplayName = name,
                        PasswordHash = Lib.Security.PasswordHasher.Hash(Lib.Security.SecurePasswordGenerator.Generate()), IsActive = true,
                        MustChangePassword = true, SecurityStamp = UserAccounts.NewStamp(), CreatedAt = now.AddDays(-100),
                        Groups = { new UserGroupEntity { GroupId = GroupEntity.Operators } },
                    });
                }
            }
            db.SaveChanges();
            foreach (var user in db.Users.AsNoTracking().Where(u => u.IsActive).ToList())
            {
                users[user.NormalizedUsername] = user;
            }
            // "admin": the first admin, whatever Auth:InitialAdmin:Username named it.
            users["admin"] = db.Users.AsNoTracking().Where(u => u.IsAdmin && u.NormalizedUsername != "philipp").OrderBy(u => u.CreatedAt).First();
            sensors = db.Sensors.AsNoTracking().Include(s => s.Parent).ToList()
                .GroupBy(s => (s.Parent.Name, s.Name)).ToDictionary(g => g.Key, g => g.First().Id);
        }

        // --- locations (§3) ----------------------------------------------------------------------------------------------

        private static readonly (string Name, string? Parent, string Description, string[] Hosts)[] Tree =
        [
            ("Zurich DC", null, "Interxion ZUR1: our primary data centre", []),
            ("Cage B", "Zurich DC", "Servers, rows 2 and 3", ["web-01", "web-02", "web-03", "web-04", "db-01", "db-02", "db-03", "db-04", "app-01", "app-02", "app-03",
                "nas-01", "nas-02", "backup-srv"]),
            ("Rack B4", "Cage B", "The network rack", ["fw-01", "fw-02", "switch-core-01", "switch-core-02"]),
            ("Meet-me room", "Zurich DC", "Uplinks and cross-connects", ["router-core"]),
        ];

        /// <summary>What the assigned hosts' texts say now: their place inside the location (§3.2).</summary>
        private static readonly Dictionary<string, string> Places = new(StringComparer.Ordinal)
        {
            ["fw-01"] = "U40", ["fw-02"] = "U39", ["switch-core-01"] = "U38", ["switch-core-02"] = "U37", ["router-core"] = "MMR rack 1",
            ["web-01"] = "Rack B2", ["web-02"] = "Rack B2", ["web-03"] = "Rack B2", ["web-04"] = "Rack B2", ["db-01"] = "Rack B3", ["db-02"] = "Rack B3",
            ["db-03"] = "Rack B3", ["db-04"] = "Rack B3", ["app-01"] = "Rack B5", ["app-02"] = "Rack B5", ["app-03"] = "Rack B5",
            ["nas-01"] = "Rack B6", ["nas-02"] = "Rack B6", ["backup-srv"] = "Rack B6",
        };

        private void Locations()
        {
            foreach (var (name, parent, description, _) in Tree)
            {
                var detail = As("philipp", sp => sp.GetRequiredService<LocationService>().CreateAsync(new LocationRequest
                {
                    Name = name, Description = description, ParentId = parent is null ? null : locations[parent],
                }, default));
                locations[name] = detail.Id;
                clock.Now = clock.Now.AddMinutes(3);
            }
            At(79);
            foreach (var (name, _, _, members) in Tree.Where(t => t.Hosts.Length > 0))
            {
                As("philipp", sp => sp.GetRequiredService<LocationService>().SetHostsAsync(new HostLocationRequest
                {
                    Ids = members.Select(h => hosts[h].Id).ToList(), LocationId = locations[name],
                }, default));
                clock.Now = clock.Now.AddMinutes(2);
            }
            using (var db = Db())
            {
                foreach (var host in db.Hosts.Where(h => h.LocationId != null).ToList())
                {
                    host.Location = Places.TryGetValue(host.Name, out var place) ? place : host.Location;
                }
                db.SaveChanges();
            }
            // The second site the way §3.2 makes it: from its hosts' text, which is cleared.
            At(60);
            As("anna", sp => sp.GetRequiredService<LocationService>().FromTextAsync(new LocationFromTextRequest
            {
                Items = [new LocationFromTextItem { Text = "Zurich office" }], ClearText = true,
            }, default));
            using (var db = Db())
            {
                locations["Zurich office"] = db.Locations.Single(l => l.Name == "Zurich office" && l.ParentId == null).Id;
            }
            clock.Now = clock.Now.AddMinutes(5);
            As("anna", sp => sp.GetRequiredService<LocationService>().UpdateAsync(locations["Zurich office"], new LocationRequest
            {
                Name = "Zurich office", Description = "Bahnhofstrasse 10, 3rd floor",
            }, default));
        }

        // --- fields (§4) -------------------------------------------------------------------------------------------------

        private void Fields()
        {
            At(78);
            using (var db = Db())
            {
                foreach (var field in db.LocationFields.AsNoTracking())
                {
                    fields[field.Label] = field.Id;
                }
            }
            var extra = new LocationFieldRequest[]
            {
                new() { Label = "Password manager", Type = LocationFieldType.Link, Group = "Access", Help = "The entry with the door codes: PNMT is not a password manager." },
                new() { Label = "Power budget", Type = LocationFieldType.Number, Group = "Network", Options = new LocationFieldOptions { Unit = "kW", Min = 0, Decimals = 1 } },
            };
            foreach (var request in LocationFieldService.Typical.Concat(extra).Where(r => !fields.ContainsKey(r.Label!)))
            {
                var field = As("admin", sp => sp.GetRequiredService<LocationFieldService>().CreateAsync(request, default));
                fields[field.Label] = field.Id;
            }
        }

        private void Value(string location, string field, object value, string by, double daysAgo)
        {
            At(daysAgo);
            var json = JsonSerializer.SerializeToElement(value);
            As(by, async sp =>
            {
                var own = await sp.GetRequiredService<PnmtdDbContext>().LocationValues.AsNoTracking()
                    .Where(v => v.LocationId == locations[location] && v.FieldId == fields[field]).Select(v => (int?)v.Revision).SingleOrDefaultAsync();
                return await sp.GetRequiredService<LocationValues>().SetAsync(locations[location], fields[field], new LocationValueRequest
                {
                    Value = json, Revision = own ?? 0, AcknowledgeCredentials = true,
                }, default);
            });
        }

        private void Values()
        {
            // Zurich DC: most fields, inherited by its sub-locations where the field says so.
            Value("Zurich DC", "Overview", $"""
                Our primary data centre, **Interxion ZUR1**. Servers stand in cage B (racks B1–B6), the network in rack B4, and the uplinks
                arrive in the meet-me room.

                - **Getting in:** the front desk hands out badges against an ID; see *Access* for the cage.
                - **Remote hands:** a ticket in the provider's portal, 24/7, usually within 30 minutes.
                - **Power:** two feeds per rack, A and B. When one fails: [{Kb("power")}]({Link("power")}).
                """, "philipp", 58);
            Value("Zurich DC", "Address", "Interxion ZUR1  \nHardturmstrasse 131  \n8005 Zürich\n\nDeliveries: loading dock at the back, gate 3.", "philipp", 58);
            Value("Zurich DC", "Access", "Badges at the front desk, against an ID. Our cage is **B**, row 2; its code is in the password manager.\n\n"
                + "Visitors are escorted and signed in by one of us. Photos in the data hall only of our own racks.", "philipp", 58);
            Value("Zurich DC", "Opening hours", "24/7; escorted visits Mon–Fri 08:00–18:00", "philipp", 58);
            Value("Zurich DC", "On-site contact", users["anna"].Id.ToString(), "philipp", 58);
            Value("Zurich DC", "Contacts", """
                | Role | Who | Phone |
                |---|---|---|
                | Interxion NOC | 24/7 | +41 44 555 01 00 |
                | Remote hands | ticket first | +41 44 555 01 10 |
                | Init7 support | circuit ZRH-12-3456 | +41 44 555 02 00 |
                """, "anna", 57);
            Value("Zurich DC", "Network", """
                Two uplinks into the meet-me room, then the firewall pair in rack B4 and the core switches.

                ```mermaid
                flowchart LR
                  init7[Init7 10G] --> core[router-core]
                  swisscom[Swisscom 1G backup] --> core
                  core --> fw1[fw-01]
                  core --> fw2[fw-02]
                  fw1 --> sw1[switch-core-01]
                  fw2 --> sw2[switch-core-02]
                  sw1 --- sw2
                ```
                """, "philipp", 57);
            Value("Zurich DC", "Uplink and circuit ID", "Init7 10G · ZRH-12-3456; Swisscom 1G backup · 4711-0815", "philipp", 57);
            Value("Zurich DC", "Main firewall", hosts["fw-01"].Id.ToString(), "philipp", 57);
            Value("Zurich DC", "Emergency procedure", $"""
                1. Call the Interxion NOC (*Contacts*) and open a ticket.
                2. Power: follow [{Kb("power")}]({Link("power")}).
                3. Tell the on-call channel what is down, and when you expect it back.
                """, "anna", 56);
            Value("Zurich DC", "Password manager", new { url = "https://vault.example.org/#/vaults/ops/zrh-dc", label = "Ops vault: ZRH DC" }, "philipp", 56);
            Value("Zurich DC", "Power budget", 24.0m, "philipp", 56);
            // A first text, edited later: the history shows both.
            Value("Zurich DC", "Opening hours", "24/7; escorted visits Mon–Fri 08:00–17:00", "anna", 30);
            Value("Zurich DC", "Opening hours", "24/7; escorted visits Mon–Fri 08:00–18:00", "anna", 12);

            Value("Cage B", "Overview", """
                Rows 2 and 3: racks **B1–B6**. B4 is the network rack; the servers are in B2, B3, B5 and B6.

                Keep the cold aisle closed: the doors at both ends shut by themselves, don't wedge them open.
                """, "philipp", 55);
            Value("Cage B", "Power budget", 16.0m, "philipp", 55);
            if (files.TryGetValue("floor-plan", out var plan))
            {
                Value("Cage B", "Floor plan", $"![Cage B, floor plan]({plan.Url})\n\nThe cage door is at the bottom left; the PDUs are on the right.", "anna", 40);
            }
            Value("Rack B4", "Overview", "Firewalls (fw-01, fw-02) and the core switches, each on both PDUs. The patch panel at U42 goes to rows 2 and 3.", "philipp", 54);
            Value("Rack B4", "Main firewall", hosts["fw-02"].Id.ToString(), "philipp", 54);
            Value("Meet-me room", "Overview", "The cross-connects to Init7 and Swisscom, in Interxion's meet-me room. **Escorted only.**", "philipp", 54);
            // Overridden here, inherited everywhere else under Zurich DC.
            Value("Meet-me room", "Opening hours", "Escorted only; book 24 hours ahead", "philipp", 54);

            Value("Zurich office", "Overview", $"""
                Our office: 40 desks on the 3rd floor, the printers and the server cupboard behind reception.

                - Guest Wi-Fi: [{Kb("wifi")}]({Link("wifi")})
                - Printers: [{Kb("printer")}]({Link("printer")})
                """, "ben", 50);
            Value("Zurich office", "Address", "Bahnhofstrasse 10, 3rd floor  \n8001 Zürich", "ben", 50);
            Value("Zurich office", "Opening hours", "Mon–Fri 07:00–19:00", "ben", 50);
            Value("Zurich office", "On-site contact", users["ben"].Id.ToString(), "ben", 50);
            Value("Zurich office", "Emergency procedure", "Fire alarm: leave by the stairs, meet at the fountain across the street. The UPS keeps the cupboard up for 20 minutes.",
                "ben", 49);
        }

        // --- files (§5.5) ------------------------------------------------------------------------------------------------

        private void Files()
        {
            var store = provider.GetRequiredService<FileStore>();
            if (store.Problem() is { } problem)
            {
                Console.Error.WriteLine($"{problem} The seed's image and attachment are left out.");
                return;
            }
            Store("floor-plan", FileKind.Image, "cage-b-floor-plan.png", "image/png", FloorPlan(), "anna", 41);
            Store("rack", FileKind.Image, "rack-b4-front.png", "image/png", RackFront(), "philipp", 52);
            Store("ups-manual", FileKind.Attachment, "SRT3000 battery replacement.pdf", "application/pdf", Pdf(
                "APC Smart-UPS SRT 3000: replacing the battery pack",
                ["1. Check the UPS display: Replace battery.", "2. Order SRT96BP (two packs).", "3. Pull the front bezel, unplug the pack, slide it out.",
                    "4. Slide the new pack in, plug it, wait for the self test.", "5. Send the old packs back with the prepaid label."]), "ben", 33);
        }

        private void Store(string key, FileKind kind, string name, string type, byte[] bytes, string by, double daysAgo)
        {
            At(daysAgo);
            var user = users[by];
            var actor = new FileActor(user.Id, user.Username, user.DisplayName, new AuditContext(null, "pnmtd dev seed", null));
            using var body = new MemoryStream(bytes);
            files[key] = provider.GetRequiredService<FileStore>().StoreAsync(body, kind, name, type, actor, default).GetAwaiter().GetResult();
        }

        // --- pages (§6) --------------------------------------------------------------------------------------------------

        private string Kb(string key) => $"KB-{pages[key].Number} {pages[key].Title}";

        private string Link(string key) => $"/pnmt-admin/kb/{pages[key].Number}";

        /// <summary><c>{{kb:key}}</c> as a link to that page, <c>{{file:key}}</c> as its URL.</summary>
        private string Expand(string text)
        {
            foreach (var (key, page) in pages)
            {
                text = text.Replace($"{{{{kb:{key}}}}}", $"[KB-{page.Number} {page.Title}](/pnmt-admin/kb/{page.Number})");
            }
            foreach (var (key, file) in files)
            {
                text = text.Replace($"{{{{file:{key}}}}}", file.Url);
            }
            // Without the files folder: the lines that show a file go.
            return string.Join('\n', text.Split('\n').Where(line => !line.Contains("{{file:", StringComparison.Ordinal)));
        }

        private List<KbLinkRequest> Links(string[] links) => links.Select(l => l.Split(':', 2) switch
        {
            ["location", var name] => new KbLinkRequest { Kind = KbLinkKind.Location, Id = locations[name] },
            ["host", var name] => new KbLinkRequest { Kind = KbLinkKind.Host, Id = hosts[name].Id },
            ["sensor", var path] => new KbLinkRequest { Kind = KbLinkKind.Sensor, Id = sensors[(path.Split('/')[0], path.Split('/', 2)[1])] },
            _ => throw new InvalidOperationException($"Unknown link {l}"),
        }).ToList();

        private void Pages()
        {
            foreach (var spec in PageSpecs().OrderByDescending(s => s.DaysAgo))
            {
                At(spec.DaysAgo);
                var created = As(spec.By, sp => sp.GetRequiredService<KbPageService>().CreateAsync(new KbPageRequest
                {
                    Title = spec.Title, Summary = spec.Summary, Content = Expand(spec.Content), Tags = [.. spec.Tags], Status = spec.Status,
                    Links = Links(spec.Links), AcknowledgeCredentials = true, MarkSensitive = spec.Sensitive,
                }, default));
                pages[spec.Key] = created.Page;
            }
            // Edits after every page exists, in the order they happened, so a page can link to one written after it.
            var edits = PageSpecs().SelectMany(s => s.Edits.Select(e => (Spec: s, Edit: e))).OrderByDescending(x => x.Edit.DaysAgo);
            foreach (var (spec, edit) in edits)
            {
                At(edit.DaysAgo);
                var page = pages[spec.Key];
                var saved = As(edit.By, sp => sp.GetRequiredService<KbPageService>().UpdateAsync(page.Id, new KbPageRequest
                {
                    Title = page.Title, Summary = edit.Summary ?? page.Summary, Content = edit.Content is null ? page.Content : Expand(edit.Content(page.Content)),
                    Tags = edit.Tags is null ? [.. page.Tags.Select(t => t.Name)] : [.. edit.Tags], Status = edit.Status ?? page.Status,
                    Links = page.Links.Select(l => new KbLinkRequest { Kind = l.Kind, Id = l.Id }).ToList(), Revision = page.Revision, Note = edit.Note,
                    AcknowledgeCredentials = true,
                }, default));
                pages[spec.Key] = saved.Page;
            }
            foreach (var spec in PageSpecs().Where(s => s.ArchivedDaysAgo is not null))
            {
                At(spec.ArchivedDaysAgo!.Value);
                pages[spec.Key] = As("philipp", sp => sp.GetRequiredService<KbPageService>().ArchiveAsync(pages[spec.Key].Id, true, default));
            }
        }

        /// <summary>Views (§6.6) per day over the last 30 days, and some older ones, so the 30 days and all time differ.</summary>
        private void Views()
        {
            var random = new Random(5);
            using var db = Db();
            var specs = PageSpecs().ToDictionary(s => s.Key);
            foreach (var (key, page) in pages)
            {
                var spec = specs[key];
                var total = 0;
                for (var day = 0; day < 90 && day < spec.DaysAgo; day++)
                {
                    // Popular pages most days, the others now and then; fewer views further back.
                    var chance = Math.Min(0.9, 0.15 * spec.Popularity) * (day < 30 ? 1 : 0.5);
                    if (spec.ArchivedDaysAgo is { } archived && day < archived || random.NextDouble() >= chance)
                    {
                        continue;
                    }
                    var views = 1 + random.Next(0, spec.Popularity + 1);
                    // Days back from today's date, not 24 hours back from now: across a DST change two of those land on one date
                    // when it is late in the evening.
                    var date = DateOnly.FromDateTime(Clock.ToLocal(now)).AddDays(-day);
                    db.KbPageViews.Add(new KbPageViewEntity { PageId = page.Id, Day = date, Views = views });
                    total += views;
                }
                db.SaveChanges();
                db.KbPages.Where(p => p.Id == page.Id).ExecuteUpdate(u => u.SetProperty(p => p.Views, total));
            }
        }

        /// <summary>An edit's change: the text replaced, which must be there.</summary>
        private static Func<string, string> Swap(string from, string to) => c =>
            c.Contains(from, StringComparison.Ordinal) ? c.Replace(from, to, StringComparison.Ordinal) : throw new InvalidOperationException($"The seed's edit finds no “{from}”.");

        /// <summary>An edit's change: a paragraph added at the end.</summary>
        private static Func<string, string> Append(string text) => c => c.TrimEnd('\n') + "\n\n" + text.Trim('\n') + "\n";

        private static IEnumerable<PageSpec> PageSpecs() =>
        [
            new("power", "Power outage procedure", "What to do when a feed or the whole data centre loses power.", ["power", "ups", "zurich-dc", "emergency"],
                KbPageStatus.Published, "philipp", 75, """
                ## When one feed fails

                Every rack in cage B has two feeds, **A** and **B**. Losing one is not an outage, but the other must not be overloaded.

                1. Check the PDUs' display in rack B4: each feed carries at most 50 % of its fuse.
                2. Open a ticket with the NOC (see the location's *Contacts*).
                3. Watch the *UPS* sensors in PNMT until the feed is back.

                ## When both fail

                The generator takes over within 15 seconds; the UPSs bridge the gap.

                - [ ] Tell the on-call channel.
                - [ ] Shut down the test systems (`app-04` to `app-06`) to save battery.
                - [ ] Note the times for the provider's report.
                """, ["location:Zurich DC", "host:ups-01", "host:ups-02"],
                [
                    new(60, "anna", "The generator's start time, from the provider's test", Swap("within 15 seconds", "within 12 seconds")),
                    new(20, "philipp", "Linked the battery page", Append("When a UPS reports a weak battery afterwards: {{kb:ups-battery}}.")),
                ], Popularity: 6),
            new("ups-battery", "Replacing a UPS battery", "The SRT 3000's battery packs, step by step, with the vendor's sheet.", ["ups", "hardware", "maintenance"],
                KbPageStatus.Published, "ben", 34, """
                The UPSs in the office cupboard (*ups-01*, *ups-02*) are APC Smart-UPS SRT 3000. Their packs last about four years.

                1. Order two **SRT96BP** packs per UPS.
                2. Replace them during office hours: the UPS keeps running on mains meanwhile.
                3. Run a self test from the display, then check the *battery monitor* sensor.

                The vendor's sheet: [SRT3000 battery replacement.pdf]({{file:ups-manual}})
                """, ["host:ups-01", "host:ups-02", "sensor:ups-01/battery monitor", "location:Zurich office"],
                [new(30, "ben", "One pack per string", Swap("two **SRT96BP** packs per UPS", "two **SRT96BP** packs per UPS, one per string"))], Popularity: 3),
            new("network", "Network overview, Zurich DC", "The uplinks, the firewall pair and the core, and how full the uplink is.",
                ["network", "zurich-dc", "firewall", "diagram"], KbPageStatus.Published, "philipp", 70, """
                ## The path

                ```mermaid
                flowchart LR
                  init7[Init7 10G] --> core[router-core]
                  swisscom[Swisscom 1G backup] --> core
                  core --> fw1[fw-01]
                  core --> fw2[fw-02]
                  fw1 --> sw1[switch-core-01]
                  fw2 --> sw2[switch-core-02]
                  sw1 --- sw2
                  sw1 --> servers[Cage B servers]
                  sw2 --> servers
                ```

                The backup uplink carries only the VPN and mail when Init7 is down.

                ## How full the uplink is

                ```mermaid
                xychart
                  title "Init7 uplink, 95th percentile (Mbit/s)"
                  x-axis [Jun, Jul, Aug, Sep, Oct]
                  y-axis "Mbit/s" 0 --> 10000
                  bar [3100, 3350, 2900, 4200, 4800]
                  line [3100, 3350, 2900, 4200, 4800]
                ```

                At 7,000 Mbit/s we order the second 10G port.
                """, ["location:Zurich DC", "host:fw-01", "host:fw-02", "host:router-core"],
                [
                    new(12, "philipp", "November's numbers", c => Swap("[Jun, Jul, Aug, Sep, Oct]", "[Jun, Jul, Aug, Sep, Oct, Nov]")(
                        Swap("bar [3100, 3350, 2900, 4200, 4800]", "bar [3100, 3350, 2900, 4200, 4800, 5100]")(
                        Swap("line [3100, 3350, 2900, 4200, 4800]", "line [3100, 3350, 2900, 4200, 4800, 5100]")(c)))),
                    new(8, "anna", "The backup uplink's role", Swap("carries only the VPN and mail", "carries only the VPN, mail and DNS")),
                ], Popularity: 5),
            new("rack", "Rack B4 layout", "What is where in the network rack, front view.", ["rack", "zurich-dc", "network"], KbPageStatus.Published, "philipp", 52, """
                ![Rack B4, front view]({{file:rack}})

                | Units | What |
                |---|---|
                | U42 | Patch panel to rows 2 and 3 |
                | U40, U39 | fw-01, fw-02 |
                | U38, U37 | switch-core-01, switch-core-02 |
                | U1–U4 | the PDUs' controllers |

                Cables: blue for servers, yellow for the uplinks, red for management.
                """, ["location:Rack B4"], [new(51, "philipp", null, Swap("red for management", "red for management, green for the PDUs"))], Popularity: 2),
            new("veeam-restore", "Restoring a VM from Veeam", "A whole VM or single files, from the nightly backup on backup-srv.", ["backup", "veeam", "restore"],
                KbPageStatus.Published, "anna", 66, """
                Backups run nightly at 01:00 on **backup-srv** and report to PNMT by mail (the *Veeam job report* input).

                ## A whole VM

                1. In the Veeam console: *Home → Backups → Disk*, pick the VM, *Restore entire VM*.
                2. Restore to the original location only after the broken VM is switched off.
                """, ["host:backup-srv", "sensor:backup-srv/backup"],
                [
                    new(50, "anna", "Single files with PowerShell", Append("""
                        ## Single files

                        ```powershell
                        Start-VBRWindowsFileRestore -RestorePoint (Get-VBRRestorePoint -Name "app-02" | Select-Object -Last 1)
                        ```

                        Restored files land in `C:\VeeamFLR\`, and are gone when the session ends.
                        """)),
                    new(25, "ben", "Where restored files land", Swap("are gone when the session ends", "are gone when the session ends: copy them out first")),
                    new(4, "anna", null, Status: KbPageStatus.NeedsReview, Summary: "A whole VM or single files, from the nightly backup on backup-srv. To check after the Veeam 13 update."),
                ], Popularity: 4),
            new("heartbeat", "Nightly backup reports “Heartbeat missing”", "Why the backup sensor turns red, and what to check first.", ["backup", "troubleshooting", "heartbeat"],
                KbPageStatus.NeedsReview, "ben", 28, """
                The *backup* sensor expects a report every 24 hours, with 2 hours' grace. **Heartbeat missing** means nothing arrived.

                1. Did the job run? The Veeam console's history, or the mail log in PNMT.
                2. Did the mail arrive? *Mail log* shows whether a rule matched.
                """, ["sensor:backup-srv/backup", "host:backup-srv"], [new(27, "ben", "Linked the disk page", Append("3. Is the disk full? See {{kb:disk-full}}."))],
                Popularity: 3),
            new("disk-full", "Disk full on backup-srv", "Making room on the repository without losing restore points.", ["backup", "storage", "troubleshooting"],
                KbPageStatus.Published, "anna", 29, """
                The repository is a RAID 1 of two 16 TB disks. Above 90 % the *disk /* sensor fails.

                - Remove restore points of deleted VMs first (*Backups → Disk (Orphaned)*).
                - Never delete files in the repository by hand: Veeam loses track of the chain.
                """, ["host:backup-srv", "sensor:backup-srv/disk /"], [], Popularity: 2),
            new("firewall-ha", "Firewall failover, fw-01 and fw-02", "How the pair fails over, and how to test it.", ["firewall", "network", "ha"],
                KbPageStatus.Published, "philipp", 64, """
                fw-01 is active, fw-02 standby; they share the virtual address `10.0.0.1` and sync sessions over a direct cable.

                ## Testing

                Announce it first: a failover drops nothing, but people notice the warning.
                """, ["host:fw-01", "host:fw-02", "location:Rack B4"],
                [
                    new(40, "philipp", "The tested commands", Append("""
                        1. On fw-01: `execute ha failover set 1`.
                        2. Check that fw-02 is active and PNMT's *ping* sensors stay green.
                        3. `execute ha failover unset 1` to go back.
                        """)),
                    new(10, "anna", null, Append("Last tested: the provider's maintenance window in October.")),
                ], Popularity: 3),
            new("certs", "Certificate renewal for the web servers", "Let's Encrypt on web-01 to web-04, and what the cert sensor checks.", ["certificates", "web", "letsencrypt"],
                KbPageStatus.Published, "ben", 47, """
                The web servers renew with certbot twice a day; a certificate is renewed 30 days before it expires.

                ```bash
                sudo systemctl list-timers certbot.timer
                ```

                The *cert* sensor reports the days left. Below 7 it fails: then the timer or the DNS challenge is broken.
                """, ["sensor:web-03/cert", "host:web-03"],
                [new(15, "ben", "The dry run first", Swap("sudo systemctl list-timers certbot.timer", "sudo certbot renew --dry-run\nsudo systemctl list-timers certbot.timer"))],
                Popularity: 2),
            new("onboarding", "Onboarding: access to the Zurich DC", "Badges, the escort rule and the first visit.", ["onboarding", "access", "zurich-dc"],
                KbPageStatus.Published, "anna", 62, """
                A new colleague needs:

                - [x] a badge request at Interxion (two working days);
                - [x] the cage code from the password manager;
                - [ ] a first visit with someone who knows the cage.

                See the location's *Access* for the rules.
                """, ["location:Zurich DC"], [], Popularity: 1),
            new("printer", "Printer setup, 3rd floor", "Adding the office printers on Windows and macOS.", ["printer", "office"], KbPageStatus.Published, "ben", 58, """
                The printer is a queue on **printer-hub**: `\\printer-hub\ricoh-3og`.

                - **Windows:** *Settings → Printers → Add*, then the queue's path.
                - **macOS:** *System Settings → Printers*, IP `10.20.3.15`, driver *Generic PostScript*.
                """, ["location:Zurich office", "host:printer-hub"],
                [
                    new(36, "ben", "The colour queue", Swap(@"The printer is a queue on **printer-hub**: `\\printer-hub\ricoh-3og`.",
                        @"Both printers are queues on **printer-hub**: `\\printer-hub\ricoh-3og` and `\\printer-hub\ricoh-3og-colour`.")),
                    new(18, "ben", "printer-hub is retiring: Windows prints by IP too", Swap("*Settings → Printers → Add*, then the queue's path.",
                        "*Settings → Printers → Add*, *The printer that I want isn't listed*, IP `10.20.3.15`.")),
                ], Popularity: 4),
            new("wifi", "Wi-Fi for guests", "The guest network, its weekly code, and what it reaches.", ["wifi", "office", "guests"], KbPageStatus.Published, "ben", 55, """
                The guest SSID is **PNMT-Guest**. It reaches the internet only, at most 20 Mbit/s per device.

                The weekly code is on the screen at reception; it changes every Monday at 06:00.
                """, ["location:Zurich office"], [], Popularity: 2),
            new("vpn", "VPN for remote staff", "WireGuard on the vpn host: adding a person, and what to do when it fails.", ["vpn", "remote", "wireguard"],
                KbPageStatus.Published, "philipp", 44, """
                Each person gets one WireGuard peer per device, created on **vpn** with the script in `/etc/wireguard/add-peer.sh`.

                When nobody can connect: check the *ping* sensor of *vpn* first.
                """, ["host:vpn", "sensor:vpn/ping"],
                [new(22, "philipp", "Linked the network page", Swap("of *vpn* first.", "of *vpn* first, then whether the backup uplink is active ({{kb:network}})."))],
                Popularity: 3),
            new("dns-changes", "DNS changes: who and how", "Zone files in Git, reviewed, and how PNMT checks them afterwards.", ["dns", "change-management"],
                KbPageStatus.Published, "anna", 39, """
                The zones *example.org* and *example.net* live in the `dns-zones` repository.

                1. A merge request, reviewed by a second person.
                2. CI deploys to **dns-01** and **dns-02**.
                3. PNMT's DNS zone monitoring compares every record with what the resolvers answer; a mismatch shows within a day.
                """, ["host:dns-01", "host:dns-02"], [new(19, "anna", "The check runs hourly", Swap("within a day", "within the hour"))], Popularity: 2),
            new("patch-day", "Patch day checklist", "The second Tuesday of the month, in order.", ["patching", "checklist", "linux", "windows"], KbPageStatus.Published, "anna", 72, """
                - [ ] Announce the window in the team channel.
                - [ ] Snapshot the VMs (Veeam quick backup).
                - [ ] Linux: `sudo apt update && sudo apt full-upgrade`, then reboot the web servers.
                - [ ] Windows: WSUS approves; reboot app-04 to app-06 after 18:00.
                """, ["host:web-01", "host:app-04"],
                [
                    new(68, "anna", "One web server at a time", Swap("reboot the web servers", "reboot one web server at a time")),
                    new(41, "ben", "The WSUS step", Swap("WSUS approves", "WSUS approves the month's updates")),
                    new(13, "anna", "The dashboard check at the end", Append("- [ ] Check the dashboard: nothing red that wasn't red before.")),
                    new(2, "philipp", null, Tags: ["patching", "checklist", "linux", "windows", "maintenance"]),
                ], Popularity: 4),
            new("k8s-drain", "Draining a Kubernetes node", "Before maintenance on a k8s node in fra1.", ["kubernetes", "maintenance", "cloud"], KbPageStatus.Published, "ben", 26, """
                ```bash
                kubectl cordon k8s-node-03
                kubectl drain k8s-node-03 --ignore-daemonsets --delete-emptydir-data
                # maintenance …
                kubectl uncordon k8s-node-03
                ```

                ```mermaid
                gantt
                  title Node maintenance, one at a time
                  dateFormat HH:mm
                  axisFormat %H:%M
                  section k8s-node-03
                  drain     :a1, 20:00, 15m
                  patch     :a2, after a1, 30m
                  uncordon  :a3, after a2, 5m
                ```
                """, ["host:k8s-node-03"], [], Popularity: 1),
            new("mail-relay", "Mail relay and SPF", "What may send as example.org, and why mail from PNMT arrives.", ["mail", "dns"], KbPageStatus.Draft, "philipp", 9, """
                Only **mail** sends for example.org: the SPF record is `v=spf1 mx -all`.
                """, ["host:mail"], [new(7, "philipp", "The to-dos", Append("*To do:* DKIM keys for the relay, and a DMARC record in `p=quarantine`."))],
                Popularity: 1),
            new("switch-firmware", "Core switch firmware update", "Updating switch-core-01 and -02 one after the other.", ["network", "firmware", "maintenance"],
                KbPageStatus.Draft, "philipp", 6, """
                1. Back up both configurations.
                2. Update **switch-core-02** first; wait until it forwards again (the *uplink* sensor).
                3. Then **switch-core-01**.
                """, ["host:switch-core-01", "host:switch-core-02", "location:Rack B4"], [], Popularity: 1),
            new("break-glass", "Break-glass accounts", "Where the emergency accounts are kept, and who may open the envelope.", ["security", "access", "emergency"],
                KbPageStatus.Published, "philipp", 38, """
                The emergency accounts for the firewalls and the hypervisors are **not** in the password manager, so that it isn't a single point of failure.

                - Sealed envelope in the office safe; the team leads hold the key.
                - Opening it is logged in the safe's book, and every password in it is changed afterwards.
                """, ["host:fw-01", "location:Zurich office"],
                [new(16, "philipp", "Who holds the key", Swap("the team leads hold the key", "Ben Keller and Philipp hold the key"))], Sensitive: true, Popularity: 1),
            new("tapes", "Tape library (decommissioned)", "The old LTO-6 library: kept for the history, no longer in use.", ["backup", "legacy"], KbPageStatus.Published, "anna", 85, """
                Until 2025 the weekly full backups went to an LTO-6 library in rack B6.
                """, ["host:backup-srv"], [new(84, "anna", "Retention of the tapes", Append("Its tapes are with the archive company until 2032."))],
                ArchivedDaysAgo: 35, Popularity: 1),
            new("monitoring", "How PNMT watches us", "Which sensors exist, who gets notified, and where to look first.", ["monitoring", "onboarding", "pnmt"],
                KbPageStatus.Published, "philipp", 3, """
                - **Dashboard first:** red means a sensor failed; the bars show the last 90 days.
                - **Notifications:** *Ops on-call* by Pushover for failures and their recovery; the storage team by mail.
                - **Knowledge base:** a sensor's or a host's page lists the pages linked to it. Start with {{kb:heartbeat}} for the backup sensor.
                """, ["host:monitoring"], [new(1, "anna", "Where to start", Append("New here? {{kb:onboarding}} first."))], Popularity: 2),
        ];

        // --- the files' bytes --------------------------------------------------------------------------------------------

        private static byte[] FloorPlan()
        {
            var image = new Canvas(800, 500, (247, 247, 245));
            image.Fill(40, 40, 720, 420, (255, 255, 255));
            image.Frame(40, 40, 720, 420, 6, (60, 64, 72));
            // Cold aisle between the rows, the racks of rows 2 and 3, rack B4 marked.
            image.Fill(120, 215, 560, 70, (222, 236, 250));
            for (var i = 0; i < 6; i++)
            {
                (byte, byte, byte) rack = (205, 210, 220), rackFrame = (90, 96, 108), b4 = (255, 214, 153), b4Frame = (214, 120, 0);
                var (fill, frame) = i == 3 ? (b4, b4Frame) : (rack, rackFrame);
                image.Fill(120 + i * 95, 130, 80, 80, fill);
                image.Frame(120 + i * 95, 130, 80, 80, 3, frame);
                image.Fill(120 + i * 95, 290, 80, 80, (205, 210, 220));
                image.Frame(120 + i * 95, 290, 80, 80, 3, (90, 96, 108));
            }
            // The PDUs on the right, the door at the bottom left.
            image.Fill(705, 120, 30, 260, (196, 225, 196));
            image.Frame(705, 120, 30, 260, 3, (60, 120, 60));
            image.Fill(40, 400, 6, 60, (255, 255, 255));
            image.Fill(46, 452, 70, 8, (214, 120, 0));
            return image.Png();
        }

        private static byte[] RackFront()
        {
            var image = new Canvas(360, 640, (247, 247, 245));
            image.Fill(60, 20, 240, 600, (40, 42, 48));
            for (var u = 0; u < 42; u++)
            {
                image.Fill(70, 26 + u * 14, 220, 12, (58, 61, 70));
            }
            void Unit(int fromTop, int units, (byte, byte, byte) colour) => image.Fill(70, 26 + fromTop * 14, 220, units * 14 - 2, colour);
            Unit(0, 1, (230, 230, 230));     // U42 patch panel
            Unit(2, 1, (214, 80, 60));       // U40 fw-01
            Unit(3, 1, (214, 80, 60));       // U39 fw-02
            Unit(4, 1, (70, 130, 200));      // U38 switch-core-01
            Unit(5, 1, (70, 130, 200));      // U37 switch-core-02
            Unit(38, 4, (90, 160, 90));      // U1–U4 PDUs
            return image.Png();
        }

        /// <summary>A one-page PDF with a title and lines, written by hand: the offsets in the xref table are counted.</summary>
        private static byte[] Pdf(string title, IReadOnlyList<string> lines)
        {
            static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            var text = new StringBuilder($"BT /F1 18 Tf 72 770 Td ({Escape(title)}) Tj ET\n");
            for (var i = 0; i < lines.Count; i++)
            {
                text.Append(CultureInvariant($"BT /F1 12 Tf 72 {730 - i * 22} Td ({Escape(lines[i])}) Tj ET\n"));
            }
            var stream = text.ToString();
            var objects = new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
                $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            };
            var pdf = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (var i = 0; i < objects.Length; i++)
            {
                offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
                pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
            pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                pdf.Append(CultureInvariant($"{offset:D10} 00000 n \n"));
            }
            pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return Encoding.ASCII.GetBytes(pdf.ToString());
        }

        private static string CultureInvariant(FormattableString s) => FormattableString.Invariant(s);
    }

    /// <summary>Rectangles in RGB, written as a PNG: enough for a floor plan, without an image library.</summary>
    private sealed class Canvas(int width, int height, (byte R, byte G, byte B) background)
    {
        private readonly byte[] pixels = Enumerable.Range(0, width * height).SelectMany(_ => new[] { background.R, background.G, background.B }).ToArray();

        public void Fill(int x, int y, int w, int h, (byte R, byte G, byte B) colour)
        {
            for (var row = Math.Max(0, y); row < Math.Min(height, y + h); row++)
            {
                for (var column = Math.Max(0, x); column < Math.Min(width, x + w); column++)
                {
                    var i = (row * width + column) * 3;
                    (pixels[i], pixels[i + 1], pixels[i + 2]) = colour;
                }
            }
        }

        public void Frame(int x, int y, int w, int h, int thickness, (byte R, byte G, byte B) colour)
        {
            Fill(x, y, w, thickness, colour);
            Fill(x, y + h - thickness, w, thickness, colour);
            Fill(x, y, thickness, h, colour);
            Fill(x + w - thickness, y, thickness, h, colour);
        }

        public byte[] Png()
        {
            var raw = new MemoryStream();
            using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (var row = 0; row < height; row++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(pixels, row * width * 3, width * 3);
                }
            }
            var png = new MemoryStream();
            png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
            var header = new byte[13];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8; // bit depth
            header[9] = 2; // RGB
            Chunk(png, "IHDR", header);
            Chunk(png, "IDAT", raw.ToArray());
            Chunk(png, "IEND", []);
            return png.ToArray();
        }

        private static void Chunk(Stream png, string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            png.Write(length);
            var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            png.Write(typed);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
            png.Write(crc);
        }

        private static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }
            return ~crc;
        }
    }
}
