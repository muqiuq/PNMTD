using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Licenses;

/// <summary>The templates (§3.2) and the categories (§3.1): Manage changes them, View reads them.</summary>
public sealed partial class LicenseService
{
    // --- templates ---------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<LicenseTemplateItem>> TemplatesAsync(CancellationToken ct)
    {
        var templates = await db.LicenseTemplates.AsNoTracking().OrderBy(t => t.NormalizedName).ToListAsync(ct);
        var categories = await db.LicenseCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var uses = (await db.Licenses.AsNoTracking().Where(l => l.TemplateId != null).Select(l => new { l.TemplateId, l.EndOfLifeFromTemplate }).ToListAsync(ct))
            .GroupBy(l => l.TemplateId!.Value).ToDictionary(g => g.Key, g => (Items: g.Count(), Follow: g.Count(x => x.EndOfLifeFromTemplate)));
        return templates.Select(t => Item(t, categories.GetValueOrDefault(t.CategoryId, "?"), uses.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<LicenseTemplateItem> TemplateAsync(Guid id, CancellationToken ct) =>
        (await TemplatesAsync(ct)).SingleOrDefault(t => t.Id == id) ?? throw ApiException.NotFound("Template");

    public async Task<LicenseTemplateItem> CreateTemplateAsync(LicenseTemplateRequest request, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var t = new LicenseTemplateEntity { Id = Guid.NewGuid(), CreatedAt = now, CreatedBy = By.Name, UpdatedAt = now };
        await ApplyTemplateAsync(t, request, ct);
        db.LicenseTemplates.Add(t);
        AuditLog.Target(Audit.AuditTarget.Of(Audit.AuditTargets.LicenseTemplate, t.Id, t.Name));
        AuditLog.Summary($"Template {t.Name} created");
        await db.SaveChangesAsync(ct);
        return await TemplateAsync(t.Id, ct);
    }

    /// <summary>A template's changes reach no item's own fields, but its end of life is followed (§8.6): those items are evaluated again.</summary>
    public async Task<LicenseTemplateItem> UpdateTemplateAsync(Guid id, LicenseTemplateRequest request, CancellationToken ct)
    {
        var t = await db.LicenseTemplates.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Template");
        var (eol, note) = (t.EndOfLifeOn, t.EndOfLifeNote);
        await ApplyTemplateAsync(t, request, ct);
        t.UpdatedAt = Clock.UtcNow;
        List<Guid> followers = [];
        if (t.EndOfLifeOn != eol || t.EndOfLifeNote != note)
        {
            followers = await db.Licenses.Where(l => l.TemplateId == id && l.EndOfLifeFromTemplate).Select(l => l.Id).ToListAsync(ct);
            await db.Licenses.Where(l => followers.Contains(l.Id)).ExecuteUpdateAsync(u => u.SetProperty(l => l.VerdictKey, (string?)null), ct);
        }
        AuditLog.Target(Audit.AuditTarget.Of(Audit.AuditTargets.LicenseTemplate, t.Id, t.Name));
        await db.SaveChangesAsync(ct);
        await evaluator.EvaluateManyAsync(followers, Actor, ct);
        return await TemplateAsync(id, ct);
    }

    /// <summary>Its items stay as they are; those that followed its end of life keep it as their own (§8.6).</summary>
    public async Task DeleteTemplateAsync(Guid id, CancellationToken ct)
    {
        var t = await db.LicenseTemplates.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Template");
        var (eol, note) = (t.EndOfLifeOn, t.EndOfLifeNote);
        var followed = await db.Licenses.Where(l => l.TemplateId == id && l.EndOfLifeFromTemplate)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.EndOfLifeFromTemplate, false).SetProperty(l => l.EndOfLifeOn, eol).SetProperty(l => l.EndOfLifeNote, note), ct);
        var items = await db.Licenses.CountAsync(l => l.TemplateId == id, ct);
        db.LicenseTemplates.Remove(t);
        AuditLog.Target(Audit.AuditTarget.Of(Audit.AuditTargets.LicenseTemplate, t.Id, t.Name));
        AuditLog.Summary($"Template {t.Name} deleted; its {items} item(s) stay, {followed} keep its end of life as their own");
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyTemplateAsync(LicenseTemplateEntity t, LicenseTemplateRequest r, CancellationToken ct)
    {
        var name = Trim(r.Name) ?? Trim($"{r.ProductName} {r.Edition}");
        var product = Trim(r.ProductName);
        var currency = NormalizeCurrency(r.Currency);
        var categoryId = r.CategoryId ?? await db.LicenseCategories.Where(c => c.DefaultFor == r.Type).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
            ?? await db.LicenseCategories.OrderBy(c => c.SortOrder).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        var v = new ApiValidator()
            .Check(name is { Length: >= 1 and <= 200 }, "name", "1 to 200 characters.")
            .Check(Enum.IsDefined(r.Type), "type", "License, Service or Hardware.")
            .Check(product is { Length: >= 1 and <= 200 }, "productName", "The product's name, 1 to 200 characters.")
            .Check(r.Type == LicenseType.Service || Trim(r.Manufacturer) is not null, "manufacturer", "The manufacturer.")
            .Check(Fits(r.Manufacturer, 100), "manufacturer", "At most 100 characters.")
            .Check(Fits(r.Edition, 100), "edition", "At most 100 characters.")
            .Check(Fits(r.Vendor, 100), "vendor", "At most 100 characters.")
            .Check(IsUrl(r.ProductUrl), "productUrl", "An http:// or https:// address, at most 500 characters.")
            .Check(r.Kind is null || (Enum.IsDefined(r.Type) && LicenseWords.Kinds[r.Type].Contains(r.Kind.Value)), "kind", "One of the type's kinds.")
            .Check(r.Seats is null or (>= 1 and <= 1_000_000), "seats", "1 to 1,000,000.")
            .Check(Fits(r.SeatUnit, 30), "seatUnit", "At most 30 characters.")
            .Check(r.PriceAmount is null or (>= 0 and <= 9_999_999_999m), "priceAmount", "0 or more.")
            .Check(currency is null || (currency.Length == 3 && currency.All(char.IsAsciiLetterUpper)), "currency", "Three letters, as EUR.")
            .Check(r.BillingMonths is null or (>= 1 and <= 120), "billingMonths", "1 to 120 months.")
            .Check(r.TermMonths is null or (>= 1 and <= 120), "termMonths", "1 to 120 months.")
            .Check(r.WarnDays is null or (>= 0 and <= 730), "warnDays", "0 to 730 days.")
            .Check(r.NoticeDays is null or (>= 0 and <= 730), "noticeDays", "0 to 730 days.")
            .Check(r.ReviewMonths is null or (>= 1 and <= 60), "reviewMonths", "1 to 60 months.")
            .Check(Fits(r.SupportContact, 500), "supportContact", "At most 500 characters.")
            .Check(IsUrl(r.SupportUrl), "supportUrl", "An http:// or https:// address, at most 500 characters.")
            .Check(Fits(r.Sla, 200), "sla", "At most 200 characters.")
            .Check(Fits(r.EndOfLifeNote, 200), "endOfLifeNote", "At most 200 characters.")
            .Check(Fits(r.Note, 2000), "note", "At most 2,000 characters.");
        v.Check(categoryId is { } cid && await db.LicenseCategories.AnyAsync(c => c.Id == cid, ct), "categoryId", "No such category.");
        v.ThrowIfInvalid();
        var normalized = name!.ToUpperInvariant();
        if (await db.LicenseTemplates.AnyAsync(x => x.NormalizedName == normalized && x.Id != t.Id, ct))
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, $"A template named {name} exists already.");
        }
        t.Name = name;
        t.NormalizedName = normalized;
        t.Type = r.Type;
        t.CategoryId = categoryId!.Value;
        t.Manufacturer = Trim(r.Manufacturer);
        t.ProductName = product!;
        t.Edition = Trim(r.Edition);
        t.Vendor = Trim(r.Vendor);
        t.ProductUrl = Trim(r.ProductUrl);
        t.Kind = r.Kind;
        t.Seats = r.Seats;
        t.SeatUnit = Trim(r.SeatUnit);
        t.SeatsFromHosts = r.SeatsFromHosts;
        t.PriceAmount = r.PriceAmount;
        t.Currency = currency;
        t.PriceBasis = r.PriceBasis;
        t.Billing = r.Billing;
        t.BillingMonths = r.BillingMonths;
        t.TermMonths = r.TermMonths;
        t.Renewal = r.Renewal;
        t.WarnDays = r.WarnDays;
        t.NoticeDays = r.NoticeDays;
        t.WarnBeforeRenewal = r.WarnBeforeRenewal;
        t.ReviewMonths = r.ReviewMonths;
        t.SupportContact = Trim(r.SupportContact);
        t.SupportUrl = Trim(r.SupportUrl);
        t.Sla = Trim(r.Sla);
        t.EndOfLifeOn = r.EndOfLifeOn;
        t.EndOfLifeNote = Trim(r.EndOfLifeNote);
        t.Note = Trim(r.Note);
    }

    private static LicenseTemplateItem Item(LicenseTemplateEntity t, string category, (int Items, int Follow) uses) =>
        new(t.Id, t.Name, t.Type, t.CategoryId, category, t.Manufacturer, t.ProductName, t.Edition, t.Vendor, t.ProductUrl, t.Kind, t.Seats, t.SeatUnit,
            t.SeatsFromHosts, t.PriceAmount, t.Currency, t.PriceBasis, t.Billing, t.BillingMonths, t.TermMonths, t.Renewal, t.WarnDays, t.NoticeDays,
            t.WarnBeforeRenewal, t.ReviewMonths, t.SupportContact, t.SupportUrl, t.Sla, t.EndOfLifeOn, t.EndOfLifeNote, t.Note, uses.Items, uses.Follow,
            Clock.AsUtc(t.CreatedAt), t.CreatedBy, Clock.AsUtc(t.UpdatedAt));

    // --- categories --------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<LicenseCategoryItem>> CategoriesAsync(CancellationToken ct)
    {
        var categories = await db.LicenseCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var items = await db.Licenses.AsNoTracking().Where(l => l.Status != LicenseStatus.Retired).ToListAsync(ct);
        var templates = (await db.LicenseTemplates.AsNoTracking().Select(t => t.CategoryId).ToListAsync(ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var today = Clock.Today;
        return categories.Select(c =>
        {
            var own = items.Where(i => i.CategoryId == c.Id).ToList();
            var perYear = own.Select(i => (i.Currency, Amount: CostMath.PerYear(CostItem.From(i), today))).Where(x => x.Amount != 0)
                .GroupBy(x => x.Currency).Select(g => new MoneyAmount(g.Key, CostMath.Cents(g.Sum(x => x.Amount)))).OrderBy(m => m.Currency).ToList();
            return new LicenseCategoryItem(c.Id, c.Name, c.Description, c.SortOrder, c.DefaultFor, own.Count, templates.GetValueOrDefault(c.Id), perYear);
        }).ToList();
    }

    public async Task<IReadOnlyList<LicenseCategoryItem>> CreateCategoryAsync(LicenseCategoryRequest request, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var order = await db.LicenseCategories.MaxAsync(c => (int?)c.SortOrder, ct) ?? 0;
        var c = new LicenseCategoryEntity { Id = Guid.NewGuid(), SortOrder = order + 1, CreatedAt = now, UpdatedAt = now };
        await ApplyCategoryAsync(c, request, ct);
        db.LicenseCategories.Add(c);
        AuditLog.Target(Audit.AuditTarget.Of(Audit.AuditTargets.LicenseCategory, c.Id, c.Name));
        AuditLog.Summary($"Category {c.Name} created");
        await db.SaveChangesAsync(ct);
        return await CategoriesAsync(ct);
    }

    public async Task<IReadOnlyList<LicenseCategoryItem>> UpdateCategoryAsync(Guid id, LicenseCategoryRequest request, CancellationToken ct)
    {
        var c = await db.LicenseCategories.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Category");
        await ApplyCategoryAsync(c, request, ct);
        c.UpdatedAt = Clock.UtcNow;
        AuditLog.Target(Audit.AuditTarget.Of(Audit.AuditTargets.LicenseCategory, c.Id, c.Name));
        await db.SaveChangesAsync(ct);
        return await CategoriesAsync(ct);
    }

    public async Task<IReadOnlyList<LicenseCategoryItem>> OrderCategoriesAsync(LicenseCategoryOrderRequest request, CancellationToken ct)
    {
        var categories = await db.LicenseCategories.ToListAsync(ct);
        var ids = request.Ids ?? [];
        new ApiValidator().Check(ids.Count == categories.Count && ids.Distinct().Count() == ids.Count && categories.All(c => ids.Contains(c.Id)), "ids",
            "Every category, once.").ThrowIfInvalid();
        foreach (var c in categories)
        {
            c.SortOrder = ids.ToList().IndexOf(c.Id) + 1;
        }
        AuditLog.Target(Audit.AuditTarget.Of(Audit.AuditTargets.LicenseCategory, null, "categories"));
        AuditLog.Summary("Categories reordered: " + string.Join(", ", categories.OrderBy(c => c.SortOrder).Select(c => c.Name)));
        await db.SaveChangesAsync(ct);
        return await CategoriesAsync(ct);
    }

    /// <summary>Deleted with its items and templates moved to another (§3.1), in one transaction; the last one stays.</summary>
    public async Task<IReadOnlyList<LicenseCategoryItem>> DeleteCategoryAsync(Guid id, Guid? moveTo, CancellationToken ct)
    {
        var c = await db.LicenseCategories.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Category");
        if (await db.LicenseCategories.CountAsync(ct) <= 1)
        {
            throw ApiException.Conflict("last_category", "The last category stays: every item is in one.");
        }
        var used = await db.Licenses.CountAsync(l => l.CategoryId == id, ct) + await db.LicenseTemplates.CountAsync(t => t.CategoryId == id, ct);
        LicenseCategoryEntity? target = null;
        if (used > 0)
        {
            target = moveTo is { } to && to != id ? await db.LicenseCategories.SingleOrDefaultAsync(x => x.Id == to, ct) : null;
            if (target is null)
            {
                throw ApiException.Invalid("moveTo", $"{used} item(s) and template(s) are in it: the category they move to.");
            }
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (target is not null)
        {
            await db.Licenses.Where(l => l.CategoryId == id).ExecuteUpdateAsync(u => u.SetProperty(l => l.CategoryId, target.Id), ct);
            await db.LicenseTemplates.Where(t => t.CategoryId == id).ExecuteUpdateAsync(u => u.SetProperty(t => t.CategoryId, target.Id), ct);
        }
        db.LicenseCategories.Remove(c);
        AuditLog.Target(Audit.AuditTarget.Of(Audit.AuditTargets.LicenseCategory, c.Id, c.Name));
        AuditLog.Summary($"Category {c.Name} deleted" + (target is null ? "" : $"; {used} item(s) and template(s) moved to {target.Name}"));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await CategoriesAsync(ct);
    }

    private async Task ApplyCategoryAsync(LicenseCategoryEntity c, LicenseCategoryRequest r, CancellationToken ct)
    {
        var name = Trim(r.Name);
        new ApiValidator()
            .Check(name is { Length: >= 1 and <= 60 }, "name", "1 to 60 characters.")
            .Check(Fits(r.Description, 200), "description", "At most 200 characters.")
            .Check(r.DefaultFor is null || Enum.IsDefined(r.DefaultFor.Value), "defaultFor", "License, Service or Hardware.")
            .ThrowIfInvalid();
        var normalized = name!.ToUpperInvariant();
        if (await db.LicenseCategories.AnyAsync(x => x.NormalizedName == normalized && x.Id != c.Id, ct))
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, $"A category named {name} exists already.");
        }
        if (r.DefaultFor is { } type && c.DefaultFor != type)
        {
            // One default per type: the one that had it gives it up.
            await db.LicenseCategories.Where(x => x.DefaultFor == type && x.Id != c.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.DefaultFor, (LicenseType?)null), ct);
        }
        c.Name = name;
        c.NormalizedName = normalized;
        c.Description = Trim(r.Description);
        c.DefaultFor = r.DefaultFor;
    }
}
