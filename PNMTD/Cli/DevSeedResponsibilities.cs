using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Models.Db;
using PNMTD.Responsibilities;

namespace PNMTD.Cli;

/// <summary>
/// The seed's responsibilities (docs/responsibilities-plan.md §14): <i>Network</i> with Anna Muster and Ben Keller, <i>Backup</i>
/// with Ben Keller alone, and <i>Printers</i> with nobody, so that the global people stand in; their links to hosts, locations and
/// a page; the global responsible (the first admin) and deputy (Anna Muster). On an empty table only.
/// </summary>
internal static class DevSeedResponsibilities
{
    private static readonly (string Name, string Summary, string? Responsible, string? Deputy, string[] Hosts, string[] Locations, string[] Pages,
        string Description)[] Plan =
    [
        ("Network", "Switches, routers, the firewalls and the WAN links", "anna", "ben",
            ["fw-01", "fw-02", "router-core", "switch-core-01", "switch-core-02"], ["Rack B4", "Meet-me room"], ["Rack B4 layout"],
            """
            ## What it covers

            The core switches and the firewalls in **Rack B4**, the core router in the meet-me room, and both uplinks.

            ## What it doesn't

            The office Wi-Fi and the printers: see *Printers*.

            ## Escalation

            The providers' numbers and circuit IDs are on the Zurich DC's page, under *Uplink and circuit ID*.
            """),
        ("Backup", "The nightly backups, the NAS and the offsite copies", "ben", null, ["backup-srv", "nas-01", "nas-02"], ["Cage B"], [],
            """
            Every server is backed up at night to **backup-srv**, which copies to the NAS and once a week offsite.

            A failed backup is retried at 06:00; if that fails too, it is this responsibility's first job of the day.
            """),
        ("Printers", "The printers in the offices and their service contracts", null, null, ["printer-hub"], ["Zurich office"], [],
            "Nobody owns the printers right now, so the global responsible stands in."),
    ];

    public static string Seed(PnmtdDbContext db, DateTime now)
    {
        if (db.Responsibilities.Any())
        {
            return "Responsibilities exist already: left as they are.";
        }
        var users = db.Users.AsNoTracking().Where(u => u.IsActive).ToList();
        Guid? User(string? username) => username is null ? null : users.FirstOrDefault(u => u.NormalizedUsername == username)?.Id;
        var admin = users.Where(u => u.IsAdmin && u.NormalizedUsername != "philipp").OrderBy(u => u.CreatedAt).FirstOrDefault();
        var hosts = db.Hosts.AsNoTracking().Select(h => new { h.Id, h.Name }).ToList();
        var locations = db.Locations.AsNoTracking().Select(l => new { l.Id, l.Name }).ToList();
        var pages = db.KbPages.AsNoTracking().Select(p => new { p.Id, p.Title }).ToList();
        var by = admin?.DisplayName ?? "PNMT";
        var links = 0;
        foreach (var (name, summary, responsible, deputy, hostNames, locationNames, pageTitles, description) in Plan)
        {
            var at = now.AddDays(-20);
            var r = new ResponsibilityEntity
            {
                Id = Guid.NewGuid(), Name = name, NormalizedName = ResponsibilityService.Normalize(name), Summary = summary, Description = description,
                PlainText = Knowledge.MarkdownText.Read(description).PlainText, Revision = 1, ResponsibleId = User(responsible), DeputyId = User(deputy),
                CreatedAt = at, CreatedBy = admin?.Id, CreatedByName = by, UpdatedAt = at, UpdatedBy = admin?.Id, UpdatedByName = by,
            };
            db.Responsibilities.Add(r);
            db.ResponsibilityHistory.Add(new ResponsibilityHistoryEntity
            {
                Id = Guid.NewGuid(), ResponsibilityId = r.Id, At = at, ByUserId = admin?.Id, ByName = by, Kind = ResponsibilityHistoryKind.Created,
                Detail = name,
            });
            db.ResponsibilityHistory.Add(new ResponsibilityHistoryEntity
            {
                Id = Guid.NewGuid(), ResponsibilityId = r.Id, At = at.AddMinutes(5), ByUserId = admin?.Id, ByName = by,
                Kind = ResponsibilityHistoryKind.DescriptionChanged, OldValue = "", NewValue = description,
            });
            void Link(Guid? page, Guid? location, Guid? host)
            {
                db.ResponsibilityLinks.Add(new ResponsibilityLinkEntity
                {
                    Id = Guid.NewGuid(), ResponsibilityId = r.Id, PageId = page, LocationId = location, HostId = host, CreatedAt = at,
                    CreatedBy = admin?.Id, CreatedByName = by,
                });
                links++;
            }
            foreach (var host in hosts.Where(h => hostNames.Contains(h.Name)))
            {
                Link(null, null, host.Id);
            }
            foreach (var location in locations.Where(l => locationNames.Contains(l.Name)).DistinctBy(l => l.Name))
            {
                Link(null, location.Id, null);
            }
            foreach (var page in pages.Where(p => pageTitles.Contains(p.Title)))
            {
                Link(page.Id, null, null);
            }
        }
        if (db.ResponsibilitySettings.SingleOrDefault(s => s.Id == ResponsibilitySettingsEntity.SingletonId) is { } settings)
        {
            settings.GlobalResponsibleId = admin?.Id;
            settings.GlobalDeputyId = User("anna") is { } anna && anna != admin?.Id ? anna : null;
            settings.UpdatedAt = now.AddDays(-20);
            settings.UpdatedBy = admin?.Id;
            settings.UpdatedByName = by;
        }
        db.SaveChanges();
        return $"Seeded {Plan.Length} responsibilities with {links} links, and the global responsible and deputy.";
    }
}
