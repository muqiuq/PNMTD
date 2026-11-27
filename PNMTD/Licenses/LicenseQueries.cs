using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Locations;
using PNMTD.Models.Db;

namespace PNMTD.Licenses;

/// <summary>
/// Reading licences &amp; services (docs/licences-plan.md §8, §10): the lists, an item, the overview, the costs, the charges, what comes up,
/// the names, the panels and the search. Costs come from <see cref="CostMath"/> only, so every view gives the same numbers (decision 19).
/// </summary>
public sealed class LicenseQueries(PnmtdDbContext db, IOptionsMonitor<LicenseOptions> options, LocationTree locationTree)
{
    /// <summary>An item with what its row needs, loaded once for many.</summary>
    internal sealed record Item(LicenseEntity L, string CategoryName, DateOnly? Eol, string? EolNote, string? TemplateName,
        IReadOnlyList<Host> Hosts, string? OwnerName, bool OwnerHasEmail, string? LocationName)
    {
        public CostItem Cost { get; } = CostItem.From(L);

        public int UsedSeats => Hosts.Sum(h => h.Seats);

        public string Display => LicenseWords.Number(L.Type, L.Number);
    }

    internal sealed record Host(Guid Id, string Name, bool Enabled, Guid? LocationId, int Seats);

    internal sealed record Loaded(IReadOnlyList<Item> Items, int UnownedChoosers, LocationTreeSnapshot Tree);

    // --- loading ---------------------------------------------------------------------------------------------------------

    internal async Task<Loaded> LoadAsync(Func<IQueryable<LicenseEntity>, IQueryable<LicenseEntity>>? where, CancellationToken ct)
    {
        var query = db.Licenses.AsNoTracking();
        if (where is not null)
        {
            query = where(query);
        }
        var licenses = await query.OrderBy(l => l.Number).ToListAsync(ct);
        var ids = licenses.Select(l => l.Id).ToList();
        var categories = await db.LicenseCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var templateIds = licenses.Where(l => l.TemplateId != null).Select(l => l.TemplateId!.Value).Distinct().ToList();
        var templates = await db.LicenseTemplates.AsNoTracking().Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.EndOfLifeOn, t.EndOfLifeNote }).ToDictionaryAsync(t => t.Id, ct);
        var hosts = (await (from a in db.LicenseAssignments.AsNoTracking()
                            join h in db.Hosts.AsNoTracking() on a.HostId equals h.Id
                            where ids.Contains(a.LicenseId)
                            select new { a.LicenseId, h.Id, h.Name, h.Enabled, h.LocationId, a.Seats }).ToListAsync(ct))
            .ToLookup(x => x.LicenseId, x => new Host(x.Id, x.Name, x.Enabled, x.LocationId, x.Seats));
        var ownerIds = licenses.Where(l => l.OwnerId != null).Select(l => l.OwnerId!.Value).Distinct().ToList();
        var owners = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email }).ToDictionaryAsync(u => u.Id, ct);
        var tree = await locationTree.GetAsync(ct);
        var unowned = licenses.Any(l => l.OwnerId is null) ? (await LicenseEvaluator.UnownedChoosersAsync(db, ct)).Count : 0;
        var items = licenses.Select(l =>
        {
            var template = l.TemplateId is { } tid && templates.TryGetValue(tid, out var t) ? t : null;
            var (eol, note) = l.EndOfLifeFromTemplate && template is not null ? (template.EndOfLifeOn, template.EndOfLifeNote) : (l.EndOfLifeOn, l.EndOfLifeNote);
            var owner = l.OwnerId is { } oid && owners.TryGetValue(oid, out var o) ? o : null;
            return new Item(l, categories.GetValueOrDefault(l.CategoryId, "?"), eol, note, template?.Name,
                hosts[l.Id].OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList(), owner?.DisplayName,
                !string.IsNullOrWhiteSpace(owner?.Email), l.LocationId is { } loc && tree.Find(loc) is { } node ? tree.Path(node.Id) : null);
        }).ToList();
        return new Loaded(items, unowned, tree);
    }

    // --- the parts of a row -------------------------------------------------------------------------------------------------

    internal static IReadOnlyList<LicenseFindingItem> Findings(Item i, DateOnly today)
    {
        var verdict = LicenseVerdict.Evaluate(LicenseEvaluator.Facts(i.L, i.Eol, i.EolNote, i.UsedSeats, false), today);
        return verdict?.Findings.Select(f => new LicenseFindingItem(f.Code, f.Text, f.On)).ToList() ?? [];
    }

    internal static DateOnly? ReviewDueOn(LicenseEntity l) =>
        l.ReviewMonths is > 0 && l.Status != LicenseStatus.Retired
            ? (l.ReviewedOn ?? DateOnly.FromDateTime(Clock.ToLocal(l.CreatedAt))).AddMonths(l.ReviewMonths.Value) : null;

    /// <summary>
    /// Nobody hears about it (§5.2): an active item with a date that matters, on no host, without an owner who has an e-mail address,
    /// while nobody chose the items nobody owns. An owner who turned the e-mails off still counts as told.
    /// </summary>
    internal static bool NobodyNotified(Item i, int unownedChoosers) =>
        i.L.Status != LicenseStatus.Retired
        && (i.L.ExpiresOn is not null || i.Eol is not null || i.L.ReviewMonths is > 0)
        && i.Hosts.Count == 0
        && (i.L.OwnerId is not null ? !i.OwnerHasEmail : unownedChoosers == 0);

    internal static DateOnly? CancelBy(LicenseEntity l) =>
        l.Status == LicenseStatus.Active && l.Renewal == LicenseRenewal.Automatic && l.ExpiresOn is { } e && l.NoticeDays is { } n ? e.AddDays(-n) : null;

    private LicenseRow Row(Item i, int unowned, DateOnly today)
    {
        var l = i.L;
        var due = ReviewDueOn(l);
        return new LicenseRow(l.Id, l.Number, i.Display, l.Type, l.Name, l.Kind, l.Status, l.CategoryId, i.CategoryName, l.TemplateId, l.Manufacturer,
            l.ProductName, l.Edition, l.Vendor, l.LicensedTo, l.Reference, l.CustomerNumber, l.CostCenter, l.LocationId, i.LocationName, l.OwnerId,
            i.OwnerName, l.Seats, l.SeatUnit, l.SeatsFromHosts, i.UsedSeats, i.Hosts.Count, i.Hosts.Select(h => h.Name).ToList(), l.PriceAmount,
            l.Currency, l.PriceBasis, l.Billing, l.BillingMonths, CostMath.Cents(CostMath.PerMonth(i.Cost, today)),
            CostMath.Cents(CostMath.PerYear(i.Cost, today)), l.StartsOn, l.ExpiresOn, l.TermMonths, l.Renewal, l.NoticeDays, CancelBy(l), i.Eol,
            l.ReviewedOn, due, due is { } d && d <= today, l.Status == LicenseStatus.Retired ? null : l.VerdictCode,
            l.Status == LicenseStatus.Retired ? null : l.VerdictMessage, l.Status == LicenseStatus.Retired ? [] : Findings(i, today),
            NobodyNotified(i, unowned), l.LicenseKeyHint, l.ProductKeyHint, Clock.AsUtc(l.UpdatedAt));
    }

    // --- the lists and an item ------------------------------------------------------------------------------------------------

    /// <summary>The rows of the lists (§8.3): one type or all; active and cancelled unless asked for more.</summary>
    public async Task<IReadOnlyList<LicenseRow>> ListAsync(LicenseType? type, IReadOnlyList<LicenseStatus>? statuses, IReadOnlyList<Guid>? ids,
        CancellationToken ct)
    {
        var wanted = statuses is { Count: > 0 } ? statuses.ToList() : [LicenseStatus.Active, LicenseStatus.Cancelled];
        var loaded = await LoadAsync(q => q.Where(l => (type == null || l.Type == type) && wanted.Contains(l.Status)
            && (ids == null || ids.Contains(l.Id))), ct);
        var today = Clock.Today;
        return loaded.Items.Select(i => Row(i, loaded.UnownedChoosers, today)).ToList();
    }

    public async Task<LicenseDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(q => q.Where(l => l.Id == id), ct);
        var i = loaded.Items.SingleOrDefault() ?? throw ApiException.NotFound("Licence or service");
        var l = i.L;
        var today = Clock.Today;
        var counts = new
        {
            Links = await db.LicenseSensorLinks.CountAsync(x => x.LicenseId == id, ct),
            Renewals = await db.LicenseRenewals.CountAsync(x => x.LicenseId == id, ct),
            Charges = await db.LicenseCharges.CountAsync(x => x.LicenseId == id, ct),
            Documents = await db.LicenseDocuments.CountAsync(x => x.LicenseId == id, ct),
        };
        var yearStart = new DateOnly(today.Year, 1, 1);
        var spent = await db.LicenseCharges.AsNoTracking().Where(c => c.LicenseId == id && c.On >= yearStart && c.On <= today && c.Currency == l.Currency)
            .SumAsync(c => (decimal?)c.Amount, ct) ?? 0m;
        var due = ReviewDueOn(l);
        var nobody = NobodyNotified(i, loaded.UnownedChoosers);
        return new LicenseDetail(l.Id, l.Number, i.Display, l.Type, l.Name, l.TemplateId, i.TemplateName, l.CategoryId, i.CategoryName, l.Manufacturer,
            l.ProductName, l.Edition, l.Vendor, l.Kind, l.LicensedTo, l.Reference, l.CustomerNumber, l.PortalUrl, l.SupportContact, l.SupportUrl, l.Sla,
            l.CostCenter, l.LocationId, i.LocationName, l.OwnerId, i.OwnerName, l.LicenseKeyProtected is not null, l.LicenseKeyHint,
            l.ProductKeyProtected is not null, l.ProductKeyHint, l.Seats, l.SeatUnit, l.SeatsFromHosts, i.UsedSeats, l.PriceAmount, l.Currency,
            l.PriceBasis, l.Billing, l.BillingMonths, l.RecordCharges, l.ChargesFrom, l.StartsOn, l.ExpiresOn, l.TermMonths, l.Renewal, l.WarnDays,
            l.NoticeDays, CancelBy(l), l.WarnBeforeRenewal, l.EndOfLifeFromTemplate, i.Eol, i.EolNote, l.EndOfLifeOn, l.EndOfLifeNote, l.EolWarnDays,
            l.ReviewedOn, l.ReviewedBy, l.ReviewNote, l.ReviewMonths, due, due is { } d && d <= today, l.Status, l.StatusChangedAt, l.StatusChangedBy,
            l.Note, l.Status == LicenseStatus.Retired ? null : l.VerdictCode, l.Status == LicenseStatus.Retired ? null : l.VerdictMessage, l.VerdictAt,
            l.Status == LicenseStatus.Retired ? [] : Findings(i, today), CostMath.UnitPrice(i.Cost) is { } unit ? CostMath.Cents(unit) : null,
            CostMath.Cents(CostMath.PerMonth(i.Cost, today)), CostMath.Cents(CostMath.PerYear(i.Cost, today)), spent, i.Hosts.Count, counts.Links,
            counts.Renewals, counts.Charges, counts.Documents,
            new LicenseNotifying(l.OwnerId is not null, i.OwnerHasEmail, i.Hosts.Count, l.OwnerId is null ? loaded.UnownedChoosers : 0, nobody),
            Clock.AsUtc(l.CreatedAt), l.CreatedBy, Clock.AsUtc(l.UpdatedAt));
    }

    public async Task<LicenseDetail> GetByNumberAsync(int number, CancellationToken ct)
    {
        var id = await db.Licenses.AsNoTracking().Where(l => l.Number == number).Select(l => (Guid?)l.Id).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Licence or service");
        return await GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<LicenseAssignmentItem>> AssignmentsAsync(Guid id, CancellationToken ct)
    {
        await ExistsAsync(id, ct);
        var rows = await (from a in db.LicenseAssignments.AsNoTracking()
                          join h in db.Hosts.AsNoTracking() on a.HostId equals h.Id
                          join s in db.Sensors.AsNoTracking() on a.SensorId equals s.Id into sensors
                          from s in sensors.DefaultIfEmpty()
                          where a.LicenseId == id
                          orderby h.Name
                          select new { a, h.Name, h.Enabled, h.LocationId, SensorEnabled = (bool?)s.Enabled, SensorIgnore = (bool?)s.Ignore }).ToListAsync(ct);
        var sensorIds = rows.Where(r => r.a.SensorId != null).Select(r => r.a.SensorId!.Value).ToList();
        var last = await LastEventsAsync(sensorIds, ct);
        var tree = await locationTree.GetAsync(ct);
        return rows.Select(r => new LicenseAssignmentItem(r.a.HostId, r.Name, r.Enabled, r.LocationId, r.LocationId is { } loc ? tree.Path(loc) : null,
            r.a.SensorId, r.SensorEnabled, r.SensorIgnore, r.a.SensorId is { } sid && last.TryGetValue(sid, out var e) ? e.Code : null,
            r.a.SensorId is { } sid2 && last.TryGetValue(sid2, out var e2) ? e2.Message : null, r.a.Seats, r.a.Note, Clock.AsUtc(r.a.AssignedAt),
            r.a.AssignedBy)).ToList();
    }

    public async Task<IReadOnlyList<LicenseSensorLinkItem>> SensorLinksAsync(Guid id, CancellationToken ct)
    {
        await ExistsAsync(id, ct);
        var rows = await (from k in db.LicenseSensorLinks.AsNoTracking()
                          join s in db.Sensors.AsNoTracking() on k.SensorId equals s.Id
                          where k.LicenseId == id
                          orderby s.Parent.Name, s.Name
                          select new { k, s.Name, s.ParentId, Host = s.Parent.Name }).ToListAsync(ct);
        var last = await LastEventsAsync(rows.Select(r => r.k.SensorId).ToList(), ct);
        return rows.Select(r => new LicenseSensorLinkItem(r.k.SensorId, r.Name, r.ParentId, r.Host, last.TryGetValue(r.k.SensorId, out var e) ? e.Code : null,
            last.TryGetValue(r.k.SensorId, out var e2) ? e2.Message : null, r.k.Note, Clock.AsUtc(r.k.CreatedAt), r.k.CreatedBy)).ToList();
    }

    public async Task<IReadOnlyList<LicenseRenewalItem>> RenewalsAsync(Guid id, CancellationToken ct)
    {
        await ExistsAsync(id, ct);
        var renewals = await db.LicenseRenewals.AsNoTracking().Where(r => r.LicenseId == id).OrderByDescending(r => r.On).ThenByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        var documents = (await db.LicenseDocuments.AsNoTracking().Where(d => d.LicenseId == id && d.RenewalId != null)
            .Select(d => new { d.RenewalId, d.FileId }).ToListAsync(ct)).ToLookup(d => d.RenewalId!.Value, d => d.FileId);
        return renewals.Select(r => new LicenseRenewalItem(r.Id, r.Kind, r.On, r.PreviousExpiresOn, r.NewExpiresOn, r.PriceAmount, r.Currency, r.Reference,
            r.Note, Clock.AsUtc(r.CreatedAt), r.CreatedBy, documents[r.Id].ToList())).ToList();
    }

    public async Task<IReadOnlyList<LicenseChargeItem>> ItemChargesAsync(Guid id, CancellationToken ct)
    {
        await ExistsAsync(id, ct);
        return await ChargeItemsAsync(db.LicenseCharges.AsNoTracking().Where(c => c.LicenseId == id), 5000, ct);
    }

    public async Task<IReadOnlyList<LicenseDocumentItem>> DocumentsAsync(Guid id, CancellationToken ct)
    {
        await ExistsAsync(id, ct);
        return await (from d in db.LicenseDocuments.AsNoTracking()
                      join f in db.Files.AsNoTracking() on d.FileId equals f.Id
                      where d.LicenseId == id
                      orderby d.AddedAt descending
                      select new LicenseDocumentItem(d.FileId, f.Name, d.Kind, d.Description, f.Size, d.ChargeId, d.RenewalId, d.AddedAt, d.AddedBy))
            .ToListAsync(ct);
    }

    /// <summary>The audit entries of the item and everything of it (§8.7), newest first.</summary>
    public async Task<Paged<AuditLogItem>> HistoryAsync(Guid id, PageQuery query, CancellationToken ct)
    {
        await ExistsAsync(id, ct);
        var page = await Services.Domain.Paging.PageAsync(db.AuditLog.AsNoTracking().Where(l => l.Subjects.Contains(id))
            .OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id), query, ct);
        return new Paged<AuditLogItem>(page.Items.Select(l => Api.V1.AuditLogController.ToItem(l) with { IpAddress = null, UserAgent = null }).ToList(),
            page.Page, page.PageSize, page.TotalCount);
    }

    // --- charges -----------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<LicenseChargeItem>> ChargesAsync(LicenseChargesQuery q, CancellationToken ct)
    {
        var charges = db.LicenseCharges.AsNoTracking();
        if (q.From is { } from)
        {
            charges = charges.Where(c => c.On >= from);
        }
        if (q.To is { } to)
        {
            charges = charges.Where(c => c.On <= to);
        }
        if (q.License is { } license)
        {
            charges = charges.Where(c => c.LicenseId == license);
        }
        if (q.Kind is { } kind)
        {
            charges = charges.Where(c => c.Kind == kind);
        }
        if (q.Source is { } source)
        {
            charges = charges.Where(c => c.Source == source);
        }
        if (q.Estimated is { } estimated)
        {
            charges = charges.Where(c => c.Estimated == estimated);
        }
        if (Text(q.Currency) is { } currency)
        {
            charges = charges.Where(c => c.Currency == currency);
        }
        if (q.Type is { } type)
        {
            charges = charges.Where(c => db.Licenses.Any(l => l.Id == c.LicenseId && l.Type == type));
        }
        if (q.Category is { } category)
        {
            charges = charges.Where(c => db.Licenses.Any(l => l.Id == c.LicenseId && l.CategoryId == category));
        }
        return await ChargeItemsAsync(charges, 10_000, ct);
    }

    private async Task<IReadOnlyList<LicenseChargeItem>> ChargeItemsAsync(IQueryable<LicenseChargeEntity> charges, int max, CancellationToken ct)
    {
        var rows = await (from c in charges
                          join l in db.Licenses.AsNoTracking() on c.LicenseId equals l.Id
                          orderby c.On descending, l.Number, c.CreatedAt descending
                          select new { c, l.Number, l.Type, l.Name }).Take(max).ToListAsync(ct);
        var ids = rows.Select(r => r.c.Id).ToList();
        var invoices = (await (from d in db.LicenseDocuments.AsNoTracking()
                               join f in db.Files.AsNoTracking() on d.FileId equals f.Id
                               where d.ChargeId != null && ids.Contains(d.ChargeId.Value)
                               select new { ChargeId = d.ChargeId!.Value, d.FileId, f.Name }).ToListAsync(ct))
            .GroupBy(x => x.ChargeId).ToDictionary(g => g.Key, g => g.First());
        return rows.Select(r => new LicenseChargeItem(r.c.Id, r.c.LicenseId, r.Number, LicenseWords.Number(r.Type, r.Number), r.Type, r.Name, r.c.Kind,
            r.c.Source, r.c.On, r.c.PeriodFrom, r.c.PeriodTo, r.c.Amount, r.c.Currency, r.c.Estimated, r.c.Reference, r.c.Note, Clock.AsUtc(r.c.CreatedAt),
            r.c.CreatedBy, r.c.UpdatedAt is { } u ? Clock.AsUtc(u) : null, r.c.UpdatedBy, invoices.TryGetValue(r.c.Id, out var inv) ? inv.FileId : null,
            invoices.TryGetValue(r.c.Id, out var inv2) ? inv2.Name : null)).ToList();
    }

    // --- the money views -------------------------------------------------------------------------------------------------------

    /// <summary>The items a money view covers (§8.2): not retired, and as the filters say.</summary>
    internal async Task<Loaded> MoneyItemsAsync(LicenseType? type, Guid? category, string? costCenter, Guid? location, Guid? owner, bool withRetired,
        CancellationToken ct)
    {
        var center = Text(costCenter);
        var loaded = await LoadAsync(q => q.Where(l => (withRetired || l.Status != LicenseStatus.Retired) && (type == null || l.Type == type)
            && (category == null || l.CategoryId == category) && (center == null || l.CostCenter == center) && (owner == null || l.OwnerId == owner)), ct);
        if (location is not { } loc)
        {
            return loaded;
        }
        var within = loaded.Tree.Descendants(loc).Append(loc).ToHashSet();
        return loaded with
        {
            Items = loaded.Items.Where(i => (i.L.LocationId is { } l && within.Contains(l)) || i.Hosts.Any(h => h.LocationId is { } hl && within.Contains(hl)))
                .ToList(),
        };
    }

    private async Task<List<LicenseChargeEntity>> ChargesOfAsync(IReadOnlyList<Item> items, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var ids = items.Select(i => i.L.Id).ToList();
        return await db.LicenseCharges.AsNoTracking().Where(c => ids.Contains(c.LicenseId) && c.On >= from && c.On <= to).ToListAsync(ct);
    }

    private string MainCurrency(string? asked, IEnumerable<string> present)
    {
        var list = present.Distinct().ToList();
        return Text(asked)?.ToUpperInvariant() is { } c ? c : list.Contains(options.CurrentValue.Currency) || list.Count == 0 ? options.CurrentValue.Currency : list[0];
    }

    private static IReadOnlyList<MoneyAmount> PerCurrency(IEnumerable<(string Currency, decimal Amount)> amounts, string main) =>
        amounts.GroupBy(a => a.Currency).Select(g => new MoneyAmount(g.Key, CostMath.Cents(g.Sum(a => a.Amount))))
            .OrderBy(m => m.Currency == main ? 0 : 1).ThenBy(m => m.Currency, StringComparer.Ordinal).ToList();

    private static string Month(DateOnly day) => day.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>The cost and licence overview (§8.2), in one call, for the filters.</summary>
    public async Task<LicenseOverview> OverviewAsync(LicenseMoneyFilter f, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var today = Clock.Today;
        var loaded = await MoneyItemsAsync(f.Type, f.Category, f.CostCenter, f.Location, f.Owner, false, ct);
        var items = loaded.Items;
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        var lastDay = new DateOnly(today.Year, today.Month, 1).AddMonths(13).AddDays(-1);
        var lastYearStart = new DateOnly(today.Year - 1, 1, 1);
        var charges = await ChargesOfAsync(items, firstMonth < lastYearStart ? firstMonth : lastYearStart, today.AddYears(1), ct);
        var forecast = CostMath.Forecast(items.Select(i => i.Cost), today.AddDays(1), lastDay, today);
        var main = MainCurrency(f.Currency, items.Select(i => i.L.Currency).Concat(charges.Select(c => c.Currency)));
        var currencies = items.Select(i => i.L.Currency).Concat(charges.Select(c => c.Currency)).Append(main).Distinct()
            .OrderBy(c => c == main ? 0 : 1).ThenBy(c => c, StringComparer.Ordinal).ToList();
        var byId = items.ToDictionary(i => i.L.Id);

        var perMonth = PerCurrency(items.Select(i => (i.L.Currency, CostMath.PerMonth(i.Cost, today))).Where(x => x.Item2 != 0), main);
        var perYear = PerCurrency(items.Select(i => (i.L.Currency, CostMath.PerYear(i.Cost, today))).Where(x => x.Item2 != 0), main);
        var next12End = today.AddMonths(12);
        var next12 = forecast.Where(e => e.Day <= next12End).ToList();
        var spentThis = PerCurrency(charges.Where(c => c.On.Year == today.Year && c.On <= today).Select(c => (c.Currency, c.Amount)), main);
        var spentLast = PerCurrency(charges.Where(c => c.On.Year == today.Year - 1).Select(c => (c.Currency, c.Amount)), main);

        var months = new List<LicenseMonth>();
        for (var m = firstMonth; m <= lastDay; m = m.AddMonths(1))
        {
            var key = Month(m);
            var spent = charges.Where(c => c.Currency == main && Month(c.On) == key && c.On <= today).Sum(c => c.Amount);
            var coming = forecast.Where(e => e.Currency == main && Month(e.Day) == key).ToList();
            var top = charges.Where(c => c.Currency == main && Month(c.On) == key && c.On <= today && byId.ContainsKey(c.LicenseId))
                .Select(c => (byId[c.LicenseId].CategoryName, c.Amount))
                .Concat(coming.Where(e => byId.ContainsKey(e.ItemId)).Select(e => (byId[e.ItemId].CategoryName, e.Amount)))
                .GroupBy(x => x.CategoryName).Select(g => new MoneyAmount(g.Key, CostMath.Cents(g.Sum(x => x.Amount))))
                .OrderByDescending(x => x.Amount).Take(3).ToList();
            months.Add(new LicenseMonth(key, CostMath.Cents(spent), CostMath.Cents(coming.Sum(e => e.Amount)),
                CostMath.Cents(coming.Where(e => e.IfRenewed).Sum(e => e.Amount)), top));
        }

        var running = items.Where(i => i.L.Currency == main).Select(i => (Item: i, PerYear: CostMath.PerYear(i.Cost, today))).Where(x => x.PerYear != 0).ToList();
        var byCategory = running.GroupBy(x => x.Item.CategoryName).Select(g => new LicenseBar(g.First().Item.L.CategoryId.ToString(), g.Key,
            CostMath.Cents(g.Sum(x => x.PerYear)), g.Count())).OrderByDescending(b => b.Amount).ToList();
        var providers = running.GroupBy(x => x.Item.L.Vendor ?? "").Select(g => new LicenseBar(g.Key, g.Key.Length == 0 ? "No provider" : g.Key,
            CostMath.Cents(g.Sum(x => x.PerYear)), g.Count())).OrderByDescending(b => b.Amount).ToList();
        var byProvider = providers.Count <= 11 ? providers : [.. providers.Take(10),
            new LicenseBar("", $"Other providers ({providers.Count - 10})", providers.Skip(10).Sum(p => p.Amount), providers.Skip(10).Sum(p => p.Items))];

        var attention = items.Where(i => i.L.VerdictCode >= 400)
            .Select(i => (Item: i, Finding: Findings(i, today).FirstOrDefault()))
            .OrderBy(x => Rank(x.Item.L.VerdictCode ?? 0)).ThenBy(x => x.Finding?.On ?? DateOnly.MaxValue).ToList();
        var failing = items.Count(i => i.L.VerdictCode is PNMTStatusCodes.LICENSE_EXPIRED or PNMTStatusCodes.LICENSE_EOL);
        var cancels = items.Select(i => CancelBy(i.L)).OfType<DateOnly>().Where(d => d >= today && d <= today.AddDays(90)).Order().ToList();

        var seats = items.Where(i => i.L.SeatsFromHosts && i.L.Seats is > 0)
            .Select(i => new LicenseSeatMeter(i.L.Id, i.L.Number, i.Display, i.L.Name, i.UsedSeats, i.L.Seats!.Value, i.L.SeatUnit))
            .OrderByDescending(s => (double)s.Used / s.Seats).ToList();

        var unused = items.Where(i => i.L.SeatsFromHosts && i.L.Seats is > 0 && i.UsedSeats < i.L.Seats).ToList();
        var estimateBefore = today.AddDays(-Math.Max(1, o.EstimateDays));
        var estimates = await db.LicenseCharges.AsNoTracking().CountAsync(c => c.Estimated && c.On < estimateBefore, ct);
        var worth = new LicenseWorthALook(
            unused.Sum(i => i.L.Seats!.Value - i.UsedSeats),
            CostMath.Cents(unused.Where(i => i.L.Currency == main).Sum(i => CostMath.PerYear(i.Cost, today) * (i.L.Seats!.Value - i.UsedSeats) / i.L.Seats!.Value)),
            unused.Count,
            items.Count(i => i.L.Status == LicenseStatus.Active && i.Cost.Periodic && i.Eol is { } eol && eol < today),
            items.Count(i => NobodyNotified(i, loaded.UnownedChoosers)),
            estimates,
            items.Count(i => i.L.Status == LicenseStatus.Active && i.L.PriceAmount is null && i.L.Kind is not (LicenseKind.Free or LicenseKind.Trial)),
            items.Count(i => ReviewDueOn(i.L) is { } due && due <= today));
        var ends = items.Where(i => i.Eol is { } eol && eol >= today && eol <= today.AddDays(365))
            .Select(i => new LicenseEndOfLife(i.L.Id, i.L.Number, i.Display, i.L.Name, i.Eol!.Value, i.Hosts.Count)).OrderBy(e => e.On).ToList();

        return new LicenseOverview(main, currencies, perMonth, perYear, PerCurrency(next12.Select(e => (e.Currency, e.Amount)), main),
            PerCurrency(next12.Where(e => e.IfRenewed).Select(e => (e.Currency, e.Amount)), main), spentThis, spentLast, today.Year,
            items.Count(i => i.L.VerdictCode >= 400), failing, cancels.Count, cancels.Count > 0 ? cancels[0] : null, months, byCategory, byProvider,
            attention.Take(10).Select(x => new LicenseAttention(x.Item.L.Id, x.Item.L.Number, x.Item.Display, x.Item.L.Type, x.Item.L.Name,
                x.Item.L.VerdictCode ?? 0, x.Finding?.Text ?? x.Item.L.VerdictMessage ?? "", x.Finding?.On)).ToList(), attention.Count, seats, worth, ends,
            items.Count);
    }

    private static int Rank(int code) => code switch
    {
        PNMTStatusCodes.LICENSE_EXPIRED => 0,
        PNMTStatusCodes.LICENSE_EOL => 1,
        PNMTStatusCodes.LICENSE_OVER_ASSIGNED => 2,
        PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE => 3,
        PNMTStatusCodes.LICENSE_EOL_SOON => 4,
        _ => 5,
    };

    public static readonly string[] Groups = ["category", "type", "provider", "manufacturer", "costCenter", "location", "host", "owner", "item"];

    /// <summary>The costs (§8.5): per month (or year) of the window, spent, forecast or both, grouped, in one currency.</summary>
    public async Task<LicenseCosts> CostsAsync(LicenseCostsQuery q, CancellationToken ct)
    {
        var today = Clock.Today;
        var group = Groups.FirstOrDefault(g => string.Equals(g, q.Group, StringComparison.OrdinalIgnoreCase)) ?? "category";
        var show = q.Show?.ToLowerInvariant() switch { "spent" => "spent", "forecast" => "forecast", _ => "both" };
        var columns = new List<(string Key, DateOnly From, DateOnly To)>();
        if (q.Years)
        {
            for (var y = today.Year - 4; y <= today.Year; y++)
            {
                columns.Add((y.ToString(CultureInfo.InvariantCulture), new DateOnly(y, 1, 1), new DateOnly(y, 12, 31)));
            }
        }
        else
        {
            var from = ParseMonth(q.From) ?? new DateOnly(today.Year, 1, 1);
            var to = ParseMonth(q.To) ?? new DateOnly(from.Year, 12, 1);
            new ApiValidator().Check(to >= from, "to", "The window ends after it starts.")
                .Check((to.Year - from.Year) * 12 + to.Month - from.Month < 60, "to", "At most 60 months.").ThrowIfInvalid();
            for (var m = from; m <= to; m = m.AddMonths(1))
            {
                columns.Add((Month(m), m, m.AddMonths(1).AddDays(-1)));
            }
        }
        var first = columns[0].From;
        var last = columns[^1].To;
        var loaded = await MoneyItemsAsync(q.Type, q.Category, q.CostCenter, q.Location, q.Owner, true, ct);
        var items = loaded.Items;
        var charges = show == "forecast" ? [] : await ChargesOfAsync(items, first, last < today ? last : today, ct);
        var forecast = show == "spent" ? [] : CostMath.Forecast(items.Select(i => i.Cost), first, last, today);
        var main = MainCurrency(q.Currency, items.Select(i => i.L.Currency).Concat(charges.Select(c => c.Currency)));
        var currencies = items.Select(i => i.L.Currency).Concat(charges.Select(c => c.Currency)).Append(main).Distinct()
            .OrderBy(c => c == main ? 0 : 1).ThenBy(c => c, StringComparer.Ordinal).ToList();

        int Column(DateOnly day) => columns.FindIndex(c => day >= c.From && day <= c.To);
        var values = new Dictionary<Guid, decimal[]>();
        decimal[] Of(Guid id) => values.TryGetValue(id, out var v) ? v : values[id] = new decimal[columns.Count];
        foreach (var c in charges.Where(c => c.Currency == main))
        {
            if (Column(c.On) is var ix and >= 0)
            {
                Of(c.LicenseId)[ix] += c.Amount;
            }
        }
        foreach (var e in forecast.Where(e => e.Currency == main))
        {
            if (Column(e.Day) is var ix and >= 0)
            {
                Of(e.ItemId)[ix] += e.Amount;
            }
        }
        var ifRenewed = forecast.Where(e => e.Currency == main && e.IfRenewed).Sum(e => e.Amount);
        var others = PerCurrency(charges.Where(c => c.Currency != main).Select(c => (c.Currency, c.Amount))
            .Concat(forecast.Where(e => e.Currency != main).Select(e => (e.Currency, e.Amount))), main);

        // Each item's share of each group (§6.5): one group, or shared over its hosts (by seats) or their top-level locations.
        var lines = new Dictionary<string, (string Label, List<(Item Item, decimal[] Values, decimal PerYear)> Items)>();
        void Add(string key, string label, Item item, decimal[] v, decimal perYear)
        {
            if (!lines.TryGetValue(key, out var line))
            {
                line = lines[key] = (label, []);
            }
            line.Items.Add((item, v, perYear));
        }
        var shared = false;
        foreach (var item in items.Where(i => i.L.Currency == main || values.ContainsKey(i.L.Id)))
        {
            var v = values.TryGetValue(item.L.Id, out var found) ? found : new decimal[columns.Count];
            var perYear = item.L.Currency == main ? CostMath.PerYear(item.Cost, today) : 0m;
            if (v.All(x => x == 0) && perYear == 0)
            {
                continue;
            }
            switch (group)
            {
                case "host" when item.Hosts.Count > 0:
                case "location" when item.L.LocationId is null && item.Hosts.Count > 0:
                {
                    shared |= item.Hosts.Count > 1;
                    var parts = group == "host"
                        ? item.Hosts.Select(h => (Key: h.Id.ToString(), Label: h.Name, h.Seats)).ToList()
                        : item.Hosts.Select(h => h.LocationId is { } hl && loaded.Tree.Lineage(hl) is { Count: > 0 } lin
                            ? (Key: lin[0].Id.ToString(), Label: lin[0].Name, h.Seats) : (Key: "", Label: "No location", h.Seats))
                            .GroupBy(p => p.Key).Select(g => (g.Key, g.First().Label, Seats: g.Sum(p => p.Seats))).ToList();
                    var weights = parts.Select(p => Math.Max(1, p.Seats)).ToList();
                    var columnShares = v.Select(x => CostMath.Share(x, weights)).ToList();
                    var yearShares = CostMath.Share(perYear, weights);
                    for (var p = 0; p < parts.Count; p++)
                    {
                        Add(parts[p].Key, parts[p].Label, item, columnShares.Select(s => s[p]).ToArray(), yearShares[p]);
                    }
                    break;
                }
                default:
                {
                    var (key, label) = group switch
                    {
                        "type" => (item.L.Type.ToString(), LicenseWords.TypeLabel(item.L.Type)),
                        "provider" => (item.L.Vendor ?? "", item.L.Vendor ?? "No provider"),
                        "manufacturer" => (item.L.Manufacturer ?? "", item.L.Manufacturer ?? "No manufacturer"),
                        "costCenter" => (item.L.CostCenter ?? "", item.L.CostCenter ?? "No cost centre"),
                        "owner" => (item.L.OwnerId?.ToString() ?? "", item.OwnerName ?? "No owner"),
                        "item" => (item.L.Id.ToString(), $"{item.Display} {item.L.Name}"),
                        "location" => item.L.LocationId is { } loc && loaded.Tree.Lineage(loc) is { Count: > 0 } lineage
                            ? (lineage[0].Id.ToString(), lineage[0].Name) : ("", "No location"),
                        "host" => ("", "No host"),
                        _ => (item.L.CategoryId.ToString(), item.CategoryName),
                    };
                    Add(key, label, item, v, perYear);
                    break;
                }
            }
        }
        var groups = lines.Select(kv =>
        {
            var children = kv.Value.Items.Select(x => new LicenseCostLine(x.Item.L.Id.ToString(), x.Item.L.Name, x.Item.L.Id, x.Item.Display,
                x.Values.Select(CostMath.Cents).ToList(), CostMath.Cents(x.Values.Sum()), CostMath.Cents(x.PerYear), []))
                .OrderByDescending(c => c.Total).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
            var sums = Enumerable.Range(0, columns.Count).Select(ix => CostMath.Cents(kv.Value.Items.Sum(x => x.Values[ix]))).ToList();
            return new LicenseCostLine(kv.Key, kv.Value.Label, null, null, sums, CostMath.Cents(sums.Sum()),
                CostMath.Cents(kv.Value.Items.Sum(x => x.PerYear)), group == "item" ? [] : children);
        }).OrderByDescending(g => g.Total).ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var totals = Enumerable.Range(0, columns.Count).Select(ix => groups.Sum(g => g.Values[ix])).ToList();
        return new LicenseCosts(main, currencies, group, show, columns.Select(c => c.Key).ToList(), columns.Select(c => c.To > today).ToList(), groups,
            totals, totals.Sum(), groups.Sum(g => g.PerYearNow), CostMath.Cents(ifRenewed), others, shared && group is "host" or "location");
    }

    internal static DateOnly? ParseMonth(string? text) =>
        DateOnly.TryParseExact(text?.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    /// <summary>What comes up in the next months (§8.4): the dates, the renewals within the window, what is overdue, and the cost per month.</summary>
    public async Task<LicenseUpcoming> UpcomingAsync(int months, LicenseType? type, Guid? owner, CancellationToken ct)
    {
        new ApiValidator().Check(months is >= 1 and <= 36, "months", "1 to 36 months.").ThrowIfInvalid();
        var today = Clock.Today;
        var end = today.AddMonths(months);
        var loaded = await MoneyItemsAsync(type, null, null, null, owner, false, ct);
        var items = loaded.Items;
        var entries = new List<LicenseUpcomingEntry>();
        var overdue = new List<LicenseUpcomingEntry>();
        foreach (var i in items)
        {
            var l = i.L;
            var nobody = NobodyNotified(i, loaded.UnownedChoosers);
            var termPrice = CostMath.UnitPrice(i.Cost) is { } unit && i.Cost.Periodic && l.TermMonths is { } t ? CostMath.Cents(unit / l.BillingMonths!.Value * t)
                : CostMath.UnitPrice(i.Cost) is { } once && !i.Cost.Periodic ? CostMath.Cents(once) : (decimal?)null;
            LicenseUpcomingEntry Entry(DateOnly day, string what, decimal? price = null) => new(l.Id, l.Number, i.Display, l.Type, l.Name, day, what,
                l.Renewal, price, price is null ? null : l.Currency, i.Hosts.Count, i.OwnerName, nobody, l.VerdictCode);
            if (l.ExpiresOn is { } expires)
            {
                var what = l.Kind switch { LicenseKind.Purchase => "warrantyEnds", LicenseKind.Lease => "leaseEnds", _ => "expires" };
                var automatic = l.Renewal == LicenseRenewal.Automatic && l.Status == LicenseStatus.Active;
                if (expires < today && !automatic)
                {
                    overdue.Add(Entry(expires, "expired"));
                }
                // Every renewal within the window (§8.4): a monthly subscription shows each month.
                var step = l.TermMonths is > 0 && l.Renewal != LicenseRenewal.None && l.Status == LicenseStatus.Active ? l.TermMonths.Value : 0;
                var day = expires;
                for (var k = 0; k < 400 && day <= end; k++)
                {
                    if (day >= today)
                    {
                        if (automatic)
                        {
                            entries.Add(Entry(day.AddDays(1), "renews", termPrice));
                            if (l.NoticeDays is { } notice && day.AddDays(-notice) is var cancelBy && cancelBy >= today && cancelBy <= end)
                            {
                                entries.Add(Entry(cancelBy, "cancelBy", termPrice));
                            }
                        }
                        else
                        {
                            entries.Add(Entry(day, l.Status == LicenseStatus.Cancelled ? "ends" : what, l.Renewal == LicenseRenewal.Manual ? termPrice : null));
                        }
                    }
                    if (step == 0)
                    {
                        break;
                    }
                    day = day.AddDays(1).AddMonths(step).AddDays(-1);
                }
            }
            if (i.Eol is { } eol)
            {
                if (eol < today)
                {
                    overdue.Add(Entry(eol, "pastEndOfLife"));
                }
                else if (eol <= end)
                {
                    entries.Add(Entry(eol, "endOfLife"));
                }
            }
            if (ReviewDueOn(l) is { } due)
            {
                if (due <= today)
                {
                    overdue.Add(Entry(due, "reviewDue"));
                }
                else if (due <= end)
                {
                    entries.Add(Entry(due, "reviewDue"));
                }
            }
        }
        var forecast = CostMath.Forecast(items.Select(i => i.Cost), today.AddDays(1), end, today);
        var main = options.CurrentValue.Currency;
        var byMonth = new List<LicenseUpcomingMonth>();
        for (var m = new DateOnly(today.Year, today.Month, 1); m <= end; m = m.AddMonths(1))
        {
            var key = Month(m);
            byMonth.Add(new LicenseUpcomingMonth(key, PerCurrency(forecast.Where(e => Month(e.Day) == key).Select(e => (e.Currency, e.Amount)), main),
                entries.Where(e => Month(e.Day) == key).OrderBy(e => e.Day).ThenBy(e => e.Number).ToList()));
        }
        return new LicenseUpcoming(months, PerCurrency(forecast.Select(e => (e.Currency, e.Amount)), main),
            PerCurrency(forecast.Where(e => e.IfRenewed).Select(e => (e.Currency, e.Amount)), main),
            overdue.OrderBy(e => e.Day).ToList(), byMonth.Where(b => b.Entries.Count > 0 || b.Cost.Count > 0).ToList());
    }

    // --- names, people, panels, targets, search ------------------------------------------------------------------------------

    public async Task<LicenseNames> NamesAsync(CancellationToken ct)
    {
        var items = await db.Licenses.AsNoTracking().Select(l => new { l.Manufacturer, l.Vendor, l.CostCenter, l.SeatUnit }).ToListAsync(ct);
        var templates = await db.LicenseTemplates.AsNoTracking().Select(t => new { t.Manufacturer, t.Vendor, t.SeatUnit }).ToListAsync(ct);
        static IReadOnlyList<LicenseNameUse> Uses(IEnumerable<string?> inItems, IEnumerable<string?> inTemplates)
        {
            var a = inItems.Where(n => !string.IsNullOrWhiteSpace(n)).GroupBy(n => n!).ToDictionary(g => g.Key, g => g.Count());
            var b = inTemplates.Where(n => !string.IsNullOrWhiteSpace(n)).GroupBy(n => n!).ToDictionary(g => g.Key, g => g.Count());
            return a.Keys.Union(b.Keys).Order(StringComparer.OrdinalIgnoreCase)
                .Select(n => new LicenseNameUse(n, a.GetValueOrDefault(n), b.GetValueOrDefault(n))).ToList();
        }
        // The usual units are offered even when nothing uses them yet (§3.3).
        string[] units = ["devices", "users", "cores", "instances", "sites", "lines", "mailboxes"];
        var seatUnits = Uses(items.Select(i => i.SeatUnit), templates.Select(t => t.SeatUnit)).ToList();
        seatUnits.AddRange(units.Where(u => seatUnits.All(x => x.Name != u)).Select(u => new LicenseNameUse(u, 0, 0)));
        return new LicenseNames(Uses(items.Select(i => i.Manufacturer), templates.Select(t => t.Manufacturer)),
            Uses(items.Select(i => i.Vendor), templates.Select(t => t.Vendor)), Uses(items.Select(i => i.CostCenter), []), seatUnits);
    }

    public async Task<IReadOnlyList<LicensePerson>> PeopleAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName).Select(u => new LicensePerson(u.Id, u.DisplayName, u.Username))
            .ToListAsync(ct);

    /// <summary>The items of a host, a location (its own and its hosts', below it too) or a sensor (linked), for the panels (§8.8).</summary>
    public async Task<IReadOnlyList<LicenseLinkedItem>> LinkedAsync(Guid? host, Guid? location, CancellationToken ct)
    {
        new ApiValidator().Check(host is not null ^ location is not null, "host", "A host or a location.").ThrowIfInvalid();
        var today = Clock.Today;
        Loaded loaded;
        if (host is { } h)
        {
            var ids = await db.LicenseAssignments.AsNoTracking().Where(a => a.HostId == h).Select(a => a.LicenseId).ToListAsync(ct);
            loaded = await LoadAsync(q => q.Where(l => ids.Contains(l.Id) && l.Status != LicenseStatus.Retired), ct);
        }
        else
        {
            loaded = await MoneyItemsAsync(null, null, null, location, null, false, ct);
        }
        return loaded.Items.Select(i => new LicenseLinkedItem(i.L.Id, i.L.Number, i.Display, i.L.Type, i.L.Name, i.L.VerdictCode, i.L.VerdictMessage,
                i.L.ExpiresOn, i.L.Status, PerCurrency([(i.L.Currency, CostMath.PerMonth(i.Cost, today))], i.L.Currency).Where(m => m.Amount != 0).ToList(),
                host is not null ? "host" : i.L.LocationId == location ? "location" : i.L.LocationId is not null && i.Hosts.Count == 0 ? "location" : "host"))
            .OrderBy(i => i.VerdictCode >= 400 ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>An item sensor's summary (§5.3); null for any other sensor or while the feature is off.</summary>
    public async Task<LicenseSensorSummary?> SensorSummaryAsync(Guid sensorId, CancellationToken ct)
    {
        var row = await (from a in db.LicenseAssignments.AsNoTracking()
                         join x in db.Licenses.AsNoTracking() on a.LicenseId equals x.Id
                         where a.SensorId == sensorId
                         select new { a.Seats, l = x }).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }
        var l = row.l;
        var used = await db.LicenseAssignments.AsNoTracking().Where(a => a.LicenseId == l.Id).SumAsync(a => a.Seats, ct);
        var eol = l.EndOfLifeFromTemplate && l.TemplateId is { } tid
            ? await db.LicenseTemplates.AsNoTracking().Where(t => t.Id == tid).Select(t => t.EndOfLifeOn).SingleOrDefaultAsync(ct) : l.EndOfLifeOn;
        return new LicenseSensorSummary(l.Id, l.Number, LicenseWords.Number(l.Type, l.Number), l.Type, l.Name, l.Kind, l.Status, l.Manufacturer, l.ProductName,
            l.ExpiresOn, eol, used, l.Seats, l.SeatUnit, row.Seats);
    }

    /// <summary>The items linked to a sensor (§5.4): their support details.</summary>
    public async Task<IReadOnlyList<LinkedItemSummary>> LinkedItemsAsync(Guid sensorId, CancellationToken ct)
    {
        var rows = await (from k in db.LicenseSensorLinks.AsNoTracking()
                          join l in db.Licenses.AsNoTracking() on k.LicenseId equals l.Id
                          where k.SensorId == sensorId && l.Status != LicenseStatus.Retired
                          orderby l.Number
                          select new { l, k.Note }).ToListAsync(ct);
        return rows.Select(r => new LinkedItemSummary(r.l.Id, r.l.Number, LicenseWords.Number(r.l.Type, r.l.Number), r.l.Type, r.l.Name, r.l.Vendor,
            r.l.SupportContact, r.l.SupportUrl, r.l.CustomerNumber, r.l.Sla, r.l.VerdictCode, r.Note)).ToList();
    }

    /// <summary>Hosts to assign or sensors to link (§10.1), without Monitoring: View: names only.</summary>
    public async Task<IReadOnlyList<LicenseTarget>> TargetsAsync(string? kind, string? q, Guid? license, CancellationToken ct)
    {
        var pattern = Services.Domain.Text.Pattern(q);
        var tree = await locationTree.GetAsync(ct);
        if (string.Equals(kind, "sensor", StringComparison.OrdinalIgnoreCase))
        {
            var linked = license is { } lid ? await db.LicenseSensorLinks.AsNoTracking().Where(k => k.LicenseId == lid).Select(k => k.SensorId).ToListAsync(ct) : [];
            var sensors = await db.Sensors.AsNoTracking().Where(s => s.Type != Lib.Models.Enum.SensorType.LICENSE
                    && (pattern == null || EF.Functions.ILike(s.Name, pattern, @"\") || EF.Functions.ILike(s.Parent.Name, pattern, @"\")))
                .OrderBy(s => s.Parent.Name).ThenBy(s => s.Name).Take(30).Select(s => new { s.Id, s.Name, Host = s.Parent.Name, s.Parent.LocationId }).ToListAsync(ct);
            return sensors.Select(s => new LicenseTarget(s.Id, s.Name, s.Host, s.LocationId is { } loc ? tree.Path(loc) : null, linked.Contains(s.Id))).ToList();
        }
        var assigned = license is { } id ? await db.LicenseAssignments.AsNoTracking().Where(a => a.LicenseId == id).Select(a => a.HostId).ToListAsync(ct) : [];
        var hosts = await db.Hosts.AsNoTracking().Where(h => pattern == null || EF.Functions.ILike(h.Name, pattern, @"\"))
            .OrderBy(h => h.Name).Take(30).Select(h => new { h.Id, h.Name, h.LocationId }).ToListAsync(ct);
        return hosts.Select(h => new LicenseTarget(h.Id, h.Name, null, h.LocationId is { } loc ? tree.Path(loc) : null, assigned.Contains(h.Id))).ToList();
    }

    /// <summary>
    /// The global search (§8.10): by number, name, product, manufacturer, vendor, reference and customer number; a query of exactly five
    /// letters and digits also by a key's last five characters. Never the note.
    /// </summary>
    public async Task<IReadOnlyList<SearchLicense>> SearchAsync(string term, string pattern, int limit, CancellationToken ct)
    {
        var number = LicenseWords.ParseNumber(term);
        var hint = term.Length == 5 && term.All(char.IsAsciiLetterOrDigit) ? term.ToUpperInvariant() : null;
        var rows = await db.Licenses.AsNoTracking()
            .Where(l => l.Number == number || EF.Functions.ILike(l.Name, pattern, @"\") || (l.ProductName != null && EF.Functions.ILike(l.ProductName, pattern, @"\"))
                || (l.Manufacturer != null && EF.Functions.ILike(l.Manufacturer, pattern, @"\")) || (l.Vendor != null && EF.Functions.ILike(l.Vendor, pattern, @"\"))
                || (l.Reference != null && EF.Functions.ILike(l.Reference, pattern, @"\"))
                || (l.CustomerNumber != null && EF.Functions.ILike(l.CustomerNumber, pattern, @"\"))
                || (hint != null && (l.LicenseKeyHint == hint || l.ProductKeyHint == hint)))
            .OrderBy(l => l.Number == number ? 0 : 1).ThenBy(l => l.Status == LicenseStatus.Retired ? 1 : 0).ThenBy(l => l.Name).Take(limit)
            .Select(l => new { l.Id, l.Number, l.Type, l.Name, l.ProductName, l.Edition, l.VerdictCode, l.Status }).ToListAsync(ct);
        return rows.Select(r => new SearchLicense(r.Id, r.Number, LicenseWords.Number(r.Type, r.Number), r.Type, r.Name,
            string.Join(" ", new[] { r.ProductName, r.Edition }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } p ? p : null,
            r.Status == LicenseStatus.Retired ? null : r.VerdictCode)).ToList();
    }

    /// <summary>
    /// Windows activation, matched (§8.11, phase L8): for an item, the Windows hosts whose installed key ends in one of its keys' last
    /// five characters, assigned or not; for a host, its activation and the items whose key it uses. Only the five characters are
    /// compared, as the agents report them. A volume key for KMS (GVLK) is the same everywhere and matches nothing.
    /// </summary>
    public async Task<IReadOnlyList<LicenseActivation>> ActivationsAsync(Guid? license, Guid? host, CancellationToken ct)
    {
        new ApiValidator().Check(license is not null ^ host is not null, "license", "An item or a host.").ThrowIfInvalid();
        var windows = await db.Sensors.AsNoTracking().Where(s => s.Type == Lib.Models.Enum.SensorType.WINDOWS && (host == null || s.ParentId == host))
            .Select(s => new { s.Id, s.ParentId, Host = s.Parent.Name }).ToListAsync(ct);
        var ids = windows.Select(s => s.Id).ToList();
        var sections = await db.AgentSections.AsNoTracking().Where(x => ids.Contains(x.SensorId) && x.Section == Lib.Agent.SectionNames.System)
            .Select(x => new { x.SensorId, x.Content }).ToListAsync(ct);
        var activations = sections
            .Select(x => (Sensor: windows.First(w => w.Id == x.SensorId), Activation: Agents.AgentSections.Parse<Lib.Agent.SystemSection>(x.Content)?.Os.Activation))
            .Where(x => x.Activation?.PartialKey is { Length: > 0 })
            .Select(x => (x.Sensor.ParentId, x.Sensor.Host, Key: x.Activation!.PartialKey!.Trim().ToUpperInvariant(), x.Activation.Channel,
                Kms: x.Activation.Channel?.Contains("GVLK", StringComparison.OrdinalIgnoreCase) == true))
            .ToList();
        var items = await db.Licenses.AsNoTracking().Where(l => l.Status != LicenseStatus.Retired && (l.LicenseKeyHint != null || l.ProductKeyHint != null)
                && (license == null || l.Id == license))
            .Select(l => new { l.Id, l.Number, l.Type, l.Name, l.LicenseKeyHint, l.ProductKeyHint }).ToListAsync(ct);
        var assigned = (await db.LicenseAssignments.AsNoTracking().Where(a => license == null || a.LicenseId == license)
            .Select(a => new { a.LicenseId, a.HostId }).ToListAsync(ct)).Select(a => (a.LicenseId, a.HostId)).ToHashSet();
        var result = new List<LicenseActivation>();
        foreach (var a in activations)
        {
            var matches = a.Kms ? [] : items.Where(i => i.LicenseKeyHint == a.Key || i.ProductKeyHint == a.Key)
                .Select(i => new LicenseActivationMatch(i.Id, i.Number, LicenseWords.Number(i.Type, i.Number), i.Name)).ToList();
            if (license is not null && matches.Count == 0)
            {
                continue;
            }
            result.Add(new LicenseActivation(a.ParentId, a.Host, a.Key, a.Channel, a.Kms,
                license is { } l && assigned.Contains((l, a.ParentId)), matches));
        }
        return result.OrderBy(r => r.Assigned).ThenBy(r => r.HostName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // --- exports (§8.3, §8.5) ----------------------------------------------------------------------------------------------

    /// <summary>The list as CSV: the visible columns and the useful rest; never the keys, the note or the documents.</summary>
    public static string RowsCsv(IReadOnlyList<LicenseRow> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Number,Type,Name,Kind,Status,Category,Manufacturer,Product,Edition,Vendor,Licensed to,Reference,Customer number,Cost centre,Location,Owner,"
            + "Hosts,Seats,Unit,Used seats,Price,Currency,Basis,Billing,Billing months,Per month,Per year,Starts,Expires,Term months,Renewal,Notice days,"
            + "Cancel by,End of life,Reviewed,Review due,State");
        foreach (var r in rows)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                r.Display, r.Type.ToString(), r.Name, r.Kind.ToString(), r.Status.ToString(), r.CategoryName, r.Manufacturer, r.ProductName, r.Edition, r.Vendor,
                r.LicensedTo, r.Reference, r.CustomerNumber, r.CostCenter, r.LocationName, r.OwnerName, string.Join("; ", r.HostNames), Num(r.Seats),
                r.SeatUnit, Num(r.UsedSeats), Money(r.PriceAmount), r.Currency, r.PriceBasis.ToString(), r.Billing.ToString(), Num(r.BillingMonths),
                Money(r.PerMonth), Money(r.PerYear), Day(r.StartsOn), Day(r.ExpiresOn), Num(r.TermMonths), r.Renewal.ToString(), Num(r.NoticeDays),
                Day(r.CancelBy), Day(r.EndOfLifeOn), Day(r.ReviewedOn), Day(r.ReviewDueOn), r.VerdictCode?.ToString(CultureInfo.InvariantCulture),
            }.Select(Api.V1.AuditLogController.Csv)));
        }
        return csv.ToString();
    }

    public static string CostsCsv(LicenseCosts costs)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", new[] { "Group", "Item", "Currency" }.Concat(costs.Columns).Append("Total").Append("Per year now")
            .Select(Api.V1.AuditLogController.Csv)));
        foreach (var g in costs.Groups)
        {
            var lines = g.Items.Count == 0 ? [g] : g.Items;
            foreach (var line in lines)
            {
                csv.AppendLine(string.Join(",", new[] { g.Label, line == g ? "" : $"{line.Display} {line.Label}", costs.Currency }
                    .Concat(line.Values.Select(v => Money(v))).Append(Money(line.Total)).Append(Money(line.PerYearNow)).Select(Api.V1.AuditLogController.Csv)));
            }
        }
        return csv.ToString();
    }

    public static string ChargesCsv(IReadOnlyList<LicenseChargeItem> charges)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Day,Number,Name,Kind,Source,Period from,Period to,Amount,Currency,Estimated,Reference,Note,Invoice");
        foreach (var c in charges)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                Day(c.On), c.Display, c.Name, c.Kind.ToString(), c.Source.ToString(), Day(c.PeriodFrom), Day(c.PeriodTo), Money(c.Amount), c.Currency,
                c.Estimated ? "yes" : "no", c.Reference, c.Note, c.InvoiceName,
            }.Select(Api.V1.AuditLogController.Csv)));
        }
        return csv.ToString();
    }

    private static string? Num(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Money(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture);

    private static string? Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // --- helpers -------------------------------------------------------------------------------------------------------------

    private async Task ExistsAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Licenses.AnyAsync(l => l.Id == id, ct))
        {
            throw ApiException.NotFound("Licence or service");
        }
    }

    private async Task<Dictionary<Guid, (int Code, string? Message)>> LastEventsAsync(IReadOnlyList<Guid> sensorIds, CancellationToken ct)
    {
        if (sensorIds.Count == 0)
        {
            return [];
        }
        var rows = await db.Events.AsNoTracking().Where(e => sensorIds.Contains(e.SensorId))
            .GroupBy(e => e.SensorId).Select(g => g.OrderByDescending(e => e.Created).Select(e => new { e.SensorId, e.Code, e.Message }).First())
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.SensorId, r => (r.Code, r.Message));
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
