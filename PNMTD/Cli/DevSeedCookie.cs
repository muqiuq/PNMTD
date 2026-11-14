using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;
using PNMTD.Data;
using PNMTD.Models.Db;

namespace PNMTD.Cli;

/// <summary>
/// The seed's Cookie (docs/cookie-plan.md §19.6, B4): a group <i>Cookie</i> to read with (Monitoring, Zabbix, Redmine, the
/// knowledge base, locations, the availability plan and the mails, all at their lowest level), the first admin's contact and column in the plan, and with Redmine the project IT
/// Support watched, with Operators as its escalation group and Network for Cookie's tickets. The duties stay off, as on a
/// fresh installation, and Cookie stays stopped.
/// </summary>
internal static class DevSeedCookie
{
    public static string Seed(PnmtdDbContext db, IConfiguration configuration, DateTime now)
    {
        if (db.CookieSettings.Any())
        {
            return "Cookie's settings exist already: left as they are.";
        }
        var group = db.Groups.FirstOrDefault(g => g.NormalizedName == "cookie");
        if (group is null)
        {
            group = new GroupEntity
            {
                Id = Guid.NewGuid(), Name = "Cookie", NormalizedName = "cookie", CreatedAt = now, UpdatedAt = now,
                Description = "What Cookie, the assistant, reads with (Admin → Cookie → Rights).",
            };
            foreach (var (area, level) in new[] { (Area.Monitoring, Level.View), (Area.Zabbix, Level.View), (Area.Redmine, Level.View),
                (Area.KnowledgeBase, Level.View), (Area.Locations, Level.View), (Area.Availability, Level.View), (Area.Mail, Level.Read) })
            {
                group.Rights.Add(new GroupRightEntity { GroupId = group.Id, Area = area, Level = level });
            }
            db.Groups.Add(group);
        }
        var admin = db.Users.AsNoTracking().Where(u => u.IsAdmin).OrderBy(u => u.Username).FirstOrDefault();
        void Save<T>(T value) where T : ICookieSection => db.CookieSettings.Add(new CookieSettingEntity
        {
            Section = T.Section, Version = 1, Json = JsonSerializer.Serialize(value, CookieSettings.Json), ChangedAt = now, ChangedByName = "seed",
            Note = "The seed",
        });
        Save(new CookieRightsSettings { GroupId = group.Id });
        if (admin is not null)
        {
            // The first admin's contact details and column in the availability plan (§8.1, §8.2), responsible for the next two
            // weeks' working days; the plan's nightly fill writes their cells.
            var person = db.Users.Single(u => u.Id == admin.Id);
            person.Email ??= "admin@example.com";
            person.RedmineName ??= "Redmine Admin";
            person.InPlan = true;
            person.ShortCode ??= "ADM";
            person.Color ??= "#fde68a";
            var today = DateOnly.FromDateTime(PNMTD.Lib.Helper.Clock.LocalNow);
            for (var day = today; day < today.AddDays(14); day = day.AddDays(1))
            {
                if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !db.AvailabilityDays.Any(d => d.Date == day))
                {
                    db.AvailabilityDays.Add(new AvailabilityDayEntity { Date = day, ResponsibleId = admin.Id, ChangedAt = now, ChangedBy = "seed" });
                }
            }
        }
        var redmine = Redmine.RedmineSetup.Configured(configuration);
        if (redmine)
        {
            Save(new CookieRedmineSettings
            {
                Projects = [WatchedProject.Default(1) with { EscalationGroupId = GroupEntity.Operators }], TicketProjectId = 3,
            });
        }
        db.SaveChanges();
        return $"Seeded Cookie's group and settings{(redmine ? ", with IT Support watched" : "")}; Cookie stays stopped and its duties off.";
    }
}
