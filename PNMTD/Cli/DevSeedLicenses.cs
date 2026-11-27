using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Licenses;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Cli;

/// <summary>
/// The seed's licences &amp; services (docs/licences-plan.md §16, L7), only while <c>Licenses__Enabled</c> is on (dev.env turns it on): eight
/// templates, among them a product past its end of life and one within 90 days; about twenty-five items of every type, state, kind and
/// status, on the seed's hosts and on none, with owners and without, one that nobody hears about; a year of charges, a variable service
/// with estimates; an internet line linked to a ping sensor; renewals. Their states are evaluated here, so the first start sends nobody
/// anything; failing item sensors are ignored, so the dashboard's problems stay the seed's six. On an empty table only.
/// </summary>
internal static class DevSeedLicenses
{
    private sealed record TemplatePlan(string Name, LicenseType Type, string Category, string? Manufacturer, string Product, string? Edition, string? Vendor,
        LicenseKind Kind, DateOnly? EndOfLife = null, string? EndOfLifeNote = null);

    private sealed record ItemPlan(string Name, LicenseType Type, LicenseKind Kind, string Category, string? Template, string? Manufacturer, string? Product,
        string? Vendor, decimal? Price, string Currency, LicenseBilling Billing, int? BillingMonths, int StartsDaysAgo, int? ExpiresInDays, int? TermMonths,
        LicenseRenewal Renewal, string[] Hosts, string? Owner = null, int? Seats = null, bool SeatsFromHosts = false, LicensePriceBasis Basis = LicensePriceBasis.Total,
        int? NoticeDays = null, bool WarnBeforeRenewal = false, string? Location = null, string? CostCenter = null, LicenseStatus Status = LicenseStatus.Active,
        int? ReviewedDaysAgo = null, int? ReviewMonths = null, string? SupportContact = null, string? CustomerNumber = null, string? Sla = null,
        string? LinkSensorHost = null, string? Key = null);

    private static readonly TemplatePlan[] Templates =
    [
        new("Windows Server 2016 Standard (16 cores)", LicenseType.License, "Software", "Microsoft", "Windows Server", "2016 Standard", null, LicenseKind.Perpetual,
            new DateOnly(2027, 1, 12), "end of extended support"),
        new("Windows Server 2012 R2 Standard", LicenseType.License, "Software", "Microsoft", "Windows Server", "2012 R2 Standard", null, LicenseKind.Perpetual,
            new DateOnly(2023, 10, 10), "end of extended support"),
        new("Microsoft 365 Business Standard", LicenseType.License, "Cloud & SaaS", "Microsoft", "Microsoft 365", "Business Standard", "Contoso", LicenseKind.Subscription),
        new("Veeam Backup & Replication", LicenseType.License, "Software", "Veeam", "Backup & Replication", "v12", "Comp. AG", LicenseKind.Subscription),
        new("Adobe Acrobat Pro", LicenseType.License, "Software", "Adobe", "Acrobat Pro", null, "Adobe", LicenseKind.Subscription),
        new("Business Internet 1 Gbit/s", LicenseType.Service, "Connectivity", null, "Business Internet", "1 Gbit/s", "Swisscom", LicenseKind.Contract),
        new("Dell PowerEdge R650", LicenseType.Hardware, "Hardware", "Dell", "PowerEdge", "R650", "Dell", LicenseKind.Purchase),
        new("Hetzner AX102", LicenseType.Service, "Hosting & infrastructure", null, "Dedicated server", "AX102", "Hetzner", LicenseKind.Subscription),
    ];

    private static readonly ItemPlan[] Items =
    [
        new("Microsoft 365 Business Standard", LicenseType.License, LicenseKind.Subscription, "Cloud & SaaS", "Microsoft 365 Business Standard", "Microsoft",
            "Microsoft 365", "Contoso", 12.50m, "EUR", LicenseBilling.Recurring, 12, 330, 35, 12, LicenseRenewal.Manual, [], "anna", 25, Basis: LicensePriceBasis.PerSeat,
            ReviewedDaysAgo: 270, ReviewMonths: 12),
        new("Windows Server 2016 Standard", LicenseType.License, LicenseKind.Perpetual, "Software", "Windows Server 2016 Standard (16 cores)", "Microsoft",
            "Windows Server", null, 890m, "EUR", LicenseBilling.OneTime, null, 900, null, null, LicenseRenewal.None, ["db-01", "db-02", "db-03"], "ben",
            ReviewedDaysAgo: 420, ReviewMonths: 12, Key: "NJ4MX-VQQ7Q-FKK44-W8FV9-CC9DR"),
        new("Windows Server 2012 R2", LicenseType.License, LicenseKind.Perpetual, "Software", "Windows Server 2012 R2 Standard", "Microsoft", "Windows Server",
            null, 790m, "EUR", LicenseBilling.OneTime, null, 2400, null, null, LicenseRenewal.None, ["app-01"], "ben"),
        new("Veeam Backup & Replication", LicenseType.License, LicenseKind.Subscription, "Software", "Veeam Backup & Replication", "Veeam", "Backup & Replication",
            "Comp. AG", 1450m, "EUR", LicenseBilling.Recurring, 12, 270, 95, 12, LicenseRenewal.Automatic, ["backup-srv", "nas-01", "nas-02", "web-01", "web-02",
                "web-03", "web-04"], "ben", 5, true, NoticeDays: 30, WarnBeforeRenewal: true),
        new("Adobe Acrobat Pro", LicenseType.License, LicenseKind.Subscription, "Software", "Adobe Acrobat Pro", "Adobe", "Acrobat Pro", "Adobe", 239.88m, "EUR",
            LicenseBilling.Recurring, 12, 380, -13, 12, LicenseRenewal.Manual, ["wiki"], null, 2),
        new("Windows Server CALs", LicenseType.License, LicenseKind.Perpetual, "Software", null, "Microsoft", "Windows Server CAL", "Contoso", 39m, "EUR",
            LicenseBilling.OneTime, null, 600, null, null, LicenseRenewal.None, [], null, 50, Basis: LicensePriceBasis.PerSeat),
        new("Grafana Enterprise trial", LicenseType.License, LicenseKind.Trial, "Software", null, "Grafana Labs", "Grafana Enterprise", null, null, "EUR",
            LicenseBilling.OneTime, null, 20, 10, null, LicenseRenewal.None, ["monitoring"], "anna"),
        new("Kubernetes dashboards (open source)", LicenseType.License, LicenseKind.Free, "Software", null, "CNCF", "Headlamp", null, null, "EUR",
            LicenseBilling.OneTime, null, 200, null, null, LicenseRenewal.None, ["k8s-node-01"], null),
        new("Business Internet 1 Gbit/s", LicenseType.Service, LicenseKind.Contract, "Connectivity", "Business Internet 1 Gbit/s", null, "Business Internet",
            "Swisscom", 990m, "CHF", LicenseBilling.Recurring, 1, 500, 216, 36, LicenseRenewal.Automatic, [], "anna", NoticeDays: 90, WarnBeforeRenewal: true,
            Location: "Zurich office", SupportContact: "0800 800 800 (24/7)", CustomerNumber: "1234567", Sla: "24/7, 4 h response", LinkSensorHost: "office-basel-gw"),
        new("LTE backup line", LicenseType.Service, LicenseKind.Contract, "Connectivity", null, null, "LTE backup", "Telekom", 360m, "EUR", LicenseBilling.Recurring,
            1, 400, 75, 24, LicenseRenewal.Automatic, [], null, NoticeDays: 90, WarnBeforeRenewal: true),
        new("Dedicated server AX102", LicenseType.Service, LicenseKind.Subscription, "Hosting & infrastructure", "Hetzner AX102", null, "Dedicated server", "Hetzner",
            145m, "EUR", LicenseBilling.Recurring, 1, 300, 20, 1, LicenseRenewal.Automatic, ["proxy-01"], "ben"),
        new("Azure", LicenseType.Service, LicenseKind.Usage, "Cloud & SaaS", null, "Microsoft", "Azure", "Microsoft", 1100m, "EUR", LicenseBilling.Variable, 1, 365,
            null, null, LicenseRenewal.None, [], "ben", CostCenter: "Platform"),
        new("Mobile phones Zurich", LicenseType.Service, LicenseKind.Contract, "Telephony & mobile", null, null, "Mobile subscriptions", "Swisscom", 420m, "CHF",
            LicenseBilling.Recurring, 1, 700, 33, 24, LicenseRenewal.Automatic, [], null, 12, Basis: LicensePriceBasis.Total, NoticeDays: 60, WarnBeforeRenewal: true,
            Location: "Zurich office"),
        new("pnmt.example.org", LicenseType.Service, LicenseKind.Subscription, "Domains & certificates", null, null, "Domain", "Gandi", 18m, "EUR",
            LicenseBilling.Recurring, 12, 200, 165, 12, LicenseRenewal.Automatic, [], null),
        new("Firewall support", LicenseType.Service, LicenseKind.Support, "Support & maintenance", null, "Fortinet", "FortiCare", "Comp. AG", 2400m, "EUR",
            LicenseBilling.Recurring, 12, 360, 5, 12, LicenseRenewal.Manual, ["fw-01", "fw-02"], "anna", SupportContact: "+41 44 000 00 00, support@comp.example",
            Sla: "8×5, next business day"),
        new("Network audit 2026", LicenseType.Service, LicenseKind.OneOff, "Consulting & training", null, null, "Network audit", "Comp. AG", 6400m, "EUR",
            LicenseBilling.OneTime, null, 90, null, null, LicenseRenewal.None, [], null),
        new("Legacy fax line", LicenseType.Service, LicenseKind.Contract, "Telephony & mobile", null, null, "Fax line", "Swisscom", 25m, "CHF", LicenseBilling.Recurring,
            1, 900, 60, 12, LicenseRenewal.Manual, [], null, Status: LicenseStatus.Cancelled),
        new("Old file server hosting", LicenseType.Service, LicenseKind.Subscription, "Hosting & infrastructure", null, null, "Hosting", "Hetzner", 39m, "EUR",
            LicenseBilling.Recurring, 1, 1100, null, null, LicenseRenewal.None, [], null, Status: LicenseStatus.Retired),
        new("Dell PowerEdge R650", LicenseType.Hardware, LicenseKind.Purchase, "Hardware", "Dell PowerEdge R650", "Dell", "PowerEdge", "Dell", 8900m, "EUR",
            LicenseBilling.OneTime, null, 400, 700, null, LicenseRenewal.None, ["db-01"], "ben"),
        new("Dell PowerEdge R640", LicenseType.Hardware, LicenseKind.Purchase, "Hardware", null, "Dell", "PowerEdge", "Dell", 7400m, "EUR", LicenseBilling.OneTime,
            null, 1500, 20, null, LicenseRenewal.None, ["db-02"], "ben"),
        new("Laptops (ten)", LicenseType.Hardware, LicenseKind.Lease, "Hardware", null, "Lenovo", "ThinkPad T14", "Leasing AG", 450m, "EUR", LicenseBilling.Recurring, 1,
            500, 230, null, LicenseRenewal.None, [], "anna", 10),
        new("UPS battery support", LicenseType.Hardware, LicenseKind.Support, "Support & maintenance", null, "APC", "Smart-UPS", "APC", 320m, "EUR", LicenseBilling.Recurring,
            12, 300, 65, 12, LicenseRenewal.Manual, ["ups-01", "ups-02"], null),
        new("Printer service contract", LicenseType.Service, LicenseKind.Contract, "Support & maintenance", null, "Canon", "imageRUNNER service", "Canon", 95m, "EUR",
            LicenseBilling.Recurring, 1, 250, 115, 12, LicenseRenewal.Manual, ["printer-hub"], null),
        new("Password manager", LicenseType.License, LicenseKind.Subscription, "Security", null, "Bitwarden", "Teams", "Bitwarden", 4m, "EUR", LicenseBilling.Recurring,
            1, 150, 15, 1, LicenseRenewal.Automatic, [], "anna", 20, Basis: LicensePriceBasis.PerSeat),
        new("Monitoring workshop", LicenseType.Service, LicenseKind.OneOff, "Consulting & training", null, null, "Workshop", "PNMT Academy", null, "EUR",
            LicenseBilling.OneTime, null, 40, null, null, LicenseRenewal.None, [], null),
    ];

    public static string Seed(PnmtdDbContext db, IConfiguration configuration, DateTime now)
    {
        if (!LicenseSetup.Enabled(configuration))
        {
            return "Licences & services is off (Licenses__Enabled): not seeded.";
        }
        if (db.Licenses.Any())
        {
            return "Licences & services exist already: left as they are.";
        }
        var today = DateOnly.FromDateTime(Clock.ToLocal(now));
        var categories = db.LicenseCategories.AsNoTracking().ToDictionary(c => c.Name, c => c.Id);
        var users = db.Users.AsNoTracking().Where(u => u.IsActive).ToList();
        var hosts = db.Hosts.AsNoTracking().Select(h => new { h.Id, h.Name }).ToList().GroupBy(h => h.Name).ToDictionary(g => g.Key, g => g.First().Id);
        var locations = db.Locations.AsNoTracking().Select(l => new { l.Id, l.Name }).ToList().GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.First().Id);
        var templates = new Dictionary<string, LicenseTemplateEntity>(StringComparer.Ordinal);
        foreach (var t in Templates)
        {
            var template = new LicenseTemplateEntity
            {
                Id = Guid.NewGuid(), Name = t.Name, NormalizedName = t.Name.ToUpperInvariant(), Type = t.Type, CategoryId = categories[t.Category],
                Manufacturer = t.Manufacturer, ProductName = t.Product, Edition = t.Edition, Vendor = t.Vendor, Kind = t.Kind, EndOfLifeOn = t.EndOfLife,
                EndOfLifeNote = t.EndOfLifeNote, CreatedAt = now.AddDays(-400), CreatedBy = "PNMT seed", UpdatedAt = now.AddDays(-400),
            };
            templates[t.Name] = template;
            db.LicenseTemplates.Add(template);
        }
        // The app's key ring (Auth/AuthSetup.cs), so the app can show the seed's key.
        var keys = new LicenseKeyProtector(Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
            new DirectoryInfo(configuration["DataProtection:KeysPath"] ?? Auth.AuthSetup.DefaultKeysPath),
            b => b.SetApplicationName("PNMTD")));
        var number = 0;
        var (items, sensors, charges) = (0, 0, 0);
        foreach (var p in Items)
        {
            number++;
            var start = today.AddDays(-p.StartsDaysAgo);
            var template = p.Template is { } tn ? templates[tn] : null;
            var item = new LicenseEntity
            {
                Id = Guid.NewGuid(), Number = number, Type = p.Type, Name = p.Name, TemplateId = template?.Id, CategoryId = categories[p.Category], Manufacturer = p.Manufacturer,
                ProductName = p.Product, Edition = template?.Edition, Vendor = p.Vendor, Kind = p.Kind, LicensedTo = "PNMT Demo AG",
                Reference = $"PO-{2026}-{1000 + number}", CustomerNumber = p.CustomerNumber, SupportContact = p.SupportContact, Sla = p.Sla, CostCenter = p.CostCenter ?? "IT",
                LocationId = p.Location is { } loc && locations.TryGetValue(loc, out var lid) ? lid : null,
                OwnerId = p.Owner is { } owner ? users.FirstOrDefault(u => u.NormalizedUsername == owner)?.Id : null,
                Seats = p.Seats, SeatUnit = "devices", SeatsFromHosts = p.SeatsFromHosts, PriceAmount = p.Price, Currency = p.Currency, PriceBasis = p.Basis,
                Billing = p.Billing, BillingMonths = p.BillingMonths, RecordCharges = true, ChargesFrom = start, StartsOn = start,
                ExpiresOn = p.ExpiresInDays is { } e ? today.AddDays(e) : null, TermMonths = p.TermMonths, Renewal = p.Renewal, WarnDays = 30, NoticeDays = p.NoticeDays,
                WarnBeforeRenewal = p.WarnBeforeRenewal, EndOfLifeFromTemplate = template?.EndOfLifeOn is not null, EolWarnDays = 90,
                ReviewedOn = p.ReviewedDaysAgo is { } r ? today.AddDays(-r) : null, ReviewedBy = p.ReviewedDaysAgo is null ? null : "Ben Keller", ReviewMonths = p.ReviewMonths,
                Status = p.Status, StatusChangedAt = p.Status == LicenseStatus.Active ? null : now.AddDays(-30), StatusChangedBy = p.Status == LicenseStatus.Active ? null : "Ben Keller",
                Note = p.Name == "Azure" ? "Usage is billed monthly; the amount is an estimate until the invoice is in." : null,
                CreatedAt = now.AddDays(-Math.Min(p.StartsDaysAgo, 400)), CreatedBy = "PNMT seed", UpdatedAt = now.AddDays(-10),
            };
            if (p.Key is { } key)
            {
                item.LicenseKeyProtected = keys.Protect(key);
                item.LicenseKeyHint = LicenseKeyProtector.Hint(key);
            }
            db.Licenses.Add(item);
            items++;

            // A year of charges at most (§6.2), estimated for the variable price.
            var cost = CostItem.From(item) with { CreatedOn = start };
            var since = start > today.AddYears(-1) ? start : today.AddYears(-1);
            if (cost.Periodic)
            {
                foreach (var due in CostMath.DueCharges(cost, since, item.Status == LicenseStatus.Retired ? today.AddDays(-31) : today))
                {
                    db.LicenseCharges.Add(new LicenseChargeEntity
                    {
                        Id = Guid.NewGuid(), LicenseId = item.Id, Kind = LicenseChargeKind.Period, Source = LicenseChargeSource.Automatic, On = due.Day,
                        PeriodFrom = due.Day, PeriodTo = due.PeriodTo, Amount = CostMath.Cents(p.Billing == LicenseBilling.Variable ? due.Amount * (0.85m + (due.Day.Month % 4) * 0.08m) : due.Amount),
                        Currency = item.Currency, Estimated = p.Billing == LicenseBilling.Variable && due.Day > today.AddDays(-40),
                        Reference = p.Billing == LicenseBilling.Variable && due.Day <= today.AddDays(-40) ? $"INV-{due.Day:yyyyMM}" : null, CreatedAt = now,
                    });
                    charges++;
                }
            }
            else if (CostMath.UnitPrice(cost) is { } once && start >= today.AddYears(-1))
            {
                db.LicenseCharges.Add(new LicenseChargeEntity
                {
                    Id = Guid.NewGuid(), LicenseId = item.Id, Kind = LicenseChargeKind.Purchase, Source = LicenseChargeSource.Automatic, On = start, Amount = CostMath.Cents(once),
                    Currency = item.Currency, CreatedAt = now,
                });
                charges++;
            }
            if (p.Name == "Microsoft 365 Business Standard")
            {
                db.LicenseRenewals.Add(new LicenseRenewalEntity
                {
                    Id = Guid.NewGuid(), LicenseId = item.Id, Kind = LicenseRenewalKind.Renewal, On = today.AddDays(-330), PreviousExpiresOn = today.AddDays(-330),
                    NewExpiresOn = item.ExpiresOn!.Value, Reference = "R-2025-1187", CreatedAt = now.AddDays(-330), CreatedBy = "Anna Muster",
                });
            }

            // Its state, evaluated here (§4): the first start has nothing to announce.
            var assigned = p.Status == LicenseStatus.Retired ? [] : p.Hosts.Where(hosts.ContainsKey).ToList();
            var eol = item.EndOfLifeFromTemplate ? template!.EndOfLifeOn : null;
            var verdict = LicenseVerdict.Evaluate(LicenseEvaluator.Facts(item, eol, template?.EndOfLifeNote, assigned.Count, false), today);
            if (verdict is not null)
            {
                item.VerdictCode = verdict.Code;
                item.VerdictKey = verdict.Key;
                item.VerdictMessage = verdict.Message.Length <= 1000 ? verdict.Message : verdict.Message[..999];
                item.VerdictAt = now;
                item.AnnouncedExpiresOn = item.ExpiresOn;
                item.NoticeKey = LicenseEvaluator.NoticeKey(verdict, false, today, 7, item.ExpiresOn) ?? verdict.Key;
                item.ReviewNoticeOn = LicenseEvaluator.ReviewDue(item, today);
            }
            foreach (var hostName in assigned)
            {
                var sensor = LicenseSensors.New(hosts[hostName], item.Type, item.Name, item.Number, now.AddDays(-20));
                sensor.Ignore = verdict is { Ok: false };
                db.Sensors.Add(sensor);
                db.LicenseAssignments.Add(new LicenseAssignmentEntity
                {
                    Id = Guid.NewGuid(), LicenseId = item.Id, HostId = hosts[hostName], SensorId = sensor.Id, Seats = 1, AssignedAt = now.AddDays(-20),
                    AssignedBy = "PNMT seed", SensorKey = verdict?.Key,
                });
                if (verdict is not null)
                {
                    db.Events.Add(new EventEntity
                    {
                        Id = Guid.NewGuid(), SensorId = sensor.Id, Code = verdict.Code, Message = item.VerdictMessage, Created = now.AddMinutes(-5),
                        Source = LicenseEvaluator.Source,
                    });
                }
                sensors++;
            }
            if (p.LinkSensorHost is { } linkHost && hosts.TryGetValue(linkHost, out var hostId))
            {
                var ping = db.Sensors.AsNoTracking().Where(s => s.ParentId == hostId && s.Type == SensorType.PING).Select(s => (Guid?)s.Id).FirstOrDefault();
                if (ping is { } pingId)
                {
                    db.LicenseSensorLinks.Add(new LicenseSensorLinkEntity
                    {
                        Id = Guid.NewGuid(), LicenseId = item.Id, SensorId = pingId, Note = "the primary line", CreatedAt = now.AddDays(-20), CreatedBy = "PNMT seed",
                    });
                }
            }
        }
        db.SaveChanges();
        // The identity goes on after the seed's own numbers (§3.3).
        db.Database.ExecuteSqlRaw("SELECT setval(pg_get_serial_sequence('licenses', 'Number'), (SELECT MAX(\"Number\") FROM licenses))");
        return $"Seeded {Templates.Length} licence templates, {items} licences and services, {sensors} item sensors, {charges} charges.";
    }
}
