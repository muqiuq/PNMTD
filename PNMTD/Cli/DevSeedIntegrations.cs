using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Dev;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Zabbix;

namespace PNMTD.Cli;

/// <summary>
/// The seed's integrations and audit log (docs/audit-redmine-zabbix-plan.md §11, F). With Redmine configured: tickets of the
/// recorded set linked to a failing sensor (its chip on the dashboard), the core switch, the VPN, the old printer and a
/// knowledge base page, with PNMT's copy, which the sync refreshes from the real Redmine if it is one. With the fake Zabbix
/// (<c>pnmtd dev fake-integrations</c>): nas-01's Zabbix sensor, attached to the recorded nas01. Always: an entry of every
/// audit category in the last week, a refused one and a failed sign-in among them.
/// </summary>
internal static class DevSeedIntegrations
{
    public static (int Links, int ZabbixSensors, int AuditEntries) Seed(PnmtdDbContext db, IConfiguration configuration, DateTime now)
    {
        var hosts = db.Hosts.AsNoTracking().ToDictionary(h => h.Name, h => h.Id);
        var admin = db.Users.AsNoTracking().Where(u => u.IsAdmin).OrderBy(u => u.Username).FirstOrDefault();
        var links = 0;
        if (Redmine.RedmineSetup.Configured(configuration))
        {
            links = SeedRedmine(db, hosts, admin, now);
        }
        var zabbixSensors = 0;
        if (ZabbixSetup.Configured(configuration) && (configuration["Zabbix:Url"] ?? "").TrimEnd('/').EndsWith("/zabbix", StringComparison.Ordinal)
            && hosts.TryGetValue("nas-01", out var nas))
        {
            var fake = new FakeZabbix();
            var sensor = new SensorEntity
            {
                Id = Guid.NewGuid(), ParentId = nas, Type = SensorType.ZABBIX, Name = "nas01 (Zabbix)", Enabled = true, Created = now.AddDays(-2),
            };
            sensor.SetNewSecretToken();
            db.Sensors.Add(sensor);
            db.ZabbixSensors.Add(new ZabbixSensorEntity
            {
                SensorId = sensor.Id, ZabbixHostId = long.Parse(fake.HostId("nas01"), CultureInfo.InvariantCulture), Created = now.AddDays(-2),
            });
            zabbixSensors = 1;
        }
        var audit = SeedAudit(db, hosts, admin, now);
        db.SaveChanges();
        return (links, zabbixSensors, audit);
    }

    private static int SeedRedmine(PnmtdDbContext db, Dictionary<string, Guid> hosts, UserEntity? admin, DateTime now)
    {
        var issues = new FakeRedmine().Issues;
        var disk = db.Sensors.AsNoTracking().Where(s => s.Parent.Name == "backup-srv" && s.Name == "disk /").Select(s => (Guid?)s.Id).FirstOrDefault();
        var page = db.KbPages.AsNoTracking().OrderBy(p => p.Number).Select(p => (Guid?)p.Id).FirstOrDefault();
        var plan = new List<(int Issue, Guid? Page, Guid? Host, Guid? Sensor, string Note)>
        {
            (9, null, null, disk, "the repository's disk fills up while the printer spools"),
            (11, null, hosts.GetValueOrDefault("switch-core-01"), null, "port 12 flaps"),
            (12, null, hosts.GetValueOrDefault("vpn"), null, "Zurich office users"),
            (13, null, hosts.GetValueOrDefault("printer-hub"), null, ""),
            (10, page, null, null, "the setup steps"),
        };
        var count = 0;
        foreach (var (issue, pageId, hostId, sensorId, note) in plan)
        {
            var host = hostId is { } h && h != Guid.Empty ? h : (Guid?)null;
            if (pageId is null && host is null && sensorId is null)
            {
                continue;
            }
            db.RedmineLinks.Add(new RedmineLinkEntity
            {
                Id = Guid.NewGuid(), IssueId = issue, PageId = pageId, HostId = host, SensorId = sensorId,
                Note = note.Length == 0 ? null : note, CreatedAt = now.AddDays(-1).AddMinutes(-count * 37), CreatedById = admin?.Id,
                CreatedByName = admin?.DisplayName,
            });
            if (issues.TryGetValue(issue, out var i) && !db.RedmineIssues.Local.Any(x => x.IssueId == issue))
            {
                db.RedmineIssues.Add(Copy(i, now));
            }
            count++;
        }
        return count;
    }

    /// <summary>PNMT's copy of a recorded ticket (§6.7).</summary>
    private static RedmineIssueEntity Copy(JsonObject i, DateTime now) => new()
    {
        IssueId = i["id"]!.GetValue<int>(),
        ProjectId = i["project"]!["id"]!.GetValue<int>(),
        ProjectName = i["project"]!["name"]!.GetValue<string>(),
        TrackerName = i["tracker"]?["name"]?.GetValue<string>(),
        StatusName = i["status"]!["name"]!.GetValue<string>(),
        IsClosed = i["status"]!["is_closed"]?.GetValue<bool>() == true,
        PriorityId = i["priority"]?["id"]?.GetValue<int>(),
        PriorityName = i["priority"]?["name"]?.GetValue<string>(),
        Subject = i["subject"]!.GetValue<string>(),
        AssignedToName = i["assigned_to"]?["name"]?.GetValue<string>(),
        UpdatedOn = DateTime.Parse(i["updated_on"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        DueDate = i["due_date"]?.GetValue<string>(),
        FetchedAt = now,
    };

    /// <summary>An entry of every category (§3.3), the catalog's first event of each, spread over the last week.</summary>
    private static int SeedAudit(PnmtdDbContext db, Dictionary<string, Guid> hosts, UserEntity? admin, DateTime now)
    {
        var who = admin is null
            ? Audit.AuditWho.Cli
            : new Audit.AuditWho(Audit.AuditActor.User, admin.Id, admin.Username, null, "192.0.2.15", "Mozilla/5.0 (seed)");
        var web = hosts.TryGetValue("web-03", out var w) ? Audit.AuditTarget.Host(w, "web-03") : null;
        var count = 0;
        foreach (var info in Audit.AuditCatalog.All.GroupBy(e => e.Category).Select(g => g.First()))
        {
            var target = info.TargetType == Audit.AuditTargets.Host ? web : null;
            var actor = info.Category == Audit.AuditCategory.System ? Audit.AuditWho.System : who;
            db.AuditLog.Add(Audit.AuditLog.Entry(info.Type, actor, $"{info.Label} (demo data)", target, at: now.AddHours(-3 - count * 7)));
            count++;
        }
        db.AuditLog.Add(Audit.AuditLog.Entry(Audit.AuditEvents.HostDeleted, who with { Username = "vera", UserId = null },
            "Monitoring:Edit missing (demo data)", web, Audit.AuditOutcome.Denied, at: now.AddHours(-30)));
        db.AuditLog.Add(Audit.AuditLog.Entry(Audit.AuditEvents.LoginFailed, new Audit.AuditWho(Audit.AuditActor.User, null, "admln", null, "198.51.100.7", null),
            "wrong password (demo data)", at: now.AddHours(-50)));
        return count + 2;
    }
}
