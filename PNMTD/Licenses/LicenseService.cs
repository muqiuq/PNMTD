using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Locations;
using PNMTD.Models.Db;

namespace PNMTD.Licenses;

/// <summary>
/// Changing licences &amp; services (docs/licences-plan.md §3, §5, §6, §7): the items, their life, hosts and links, renewals, reviews,
/// charges, documents, keys and names; the templates and the categories. Each change is in the audit log through the request's entry,
/// and is evaluated at once (§5.5), with the person as the notices' actor.
/// </summary>
public sealed partial class LicenseService(PnmtdDbContext db, LicenseQueries queries, LicenseEvaluator evaluator, LicenseKeyProtector keys,
    IOptionsMonitor<LicenseOptions> options, IHttpContextAccessor accessor)
{
    public const int MaxPerCall = 500;

    private LocationActor By => LocationActor.Of(accessor);

    private LicenseActor Actor => By is var by ? new LicenseActor(by.Id, by.Name) : LicenseActor.Pnmt;

    // --- items ---------------------------------------------------------------------------------------------------------------

    public async Task<LicenseDetail> CreateAsync(LicenseRequest request, CancellationToken ct)
    {
        var item = new LicenseEntity { Id = Guid.NewGuid(), CreatedAt = Clock.UtcNow, CreatedBy = By.Name, Status = LicenseStatus.Active };
        await ApplyAsync(item, request, null, ct);
        var today = Clock.Today;
        var start = item.StartsOn ?? today;
        item.StartsOn ??= today;
        item.ChargesFrom = item.RecordCharges ? request.RecordPastCharges ? start : start > today ? start : today : null;
        item.UpdatedAt = item.CreatedAt;
        db.Licenses.Add(item);
        await db.SaveChangesAsync(ct);
        AuditLog.Target(LicenseEvaluator.Target(item));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)} {item.Name} created ({LicenseWords.TypeLabel(item.Type).ToLowerInvariant()}, {item.Kind})");
        await evaluator.EvaluateAsync(item.Id, Actor, ct);
        return await queries.GetAsync(item.Id, ct);
    }

    public async Task<LicenseDetail> UpdateAsync(Guid id, LicenseRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var (name, type, recorded) = (item.Name, item.Type, item.RecordCharges);
        await ApplyAsync(item, request, item, ct);
        if (item.RecordCharges && !recorded)
        {
            item.ChargesFrom = Clock.Today;
        }
        item.VerdictKey = null;
        item.UpdatedAt = Clock.UtcNow;
        if (item.Name != name || item.Type != type)
        {
            await RenameSensorsAsync(item, ct);
        }
        AuditLog.Target(LicenseEvaluator.Target(item));
        await db.SaveChangesAsync(ct);
        await evaluator.EvaluateAsync(id, Actor, ct);
        return await queries.GetAsync(id, ct);
    }

    /// <summary>The next item of the same kind (§8.7): everything but the keys, documents, charges, renewals and the review.</summary>
    public async Task<LicenseDetail> DuplicateAsync(Guid id, CancellationToken ct)
    {
        var source = await db.Licenses.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw ApiException.NotFound("Licence or service");
        var now = Clock.UtcNow;
        var copy = new LicenseEntity
        {
            Id = Guid.NewGuid(), Type = source.Type, Name = Cut($"{source.Name} (copy)", 200), TemplateId = source.TemplateId, CategoryId = source.CategoryId,
            Manufacturer = source.Manufacturer, ProductName = source.ProductName, Edition = source.Edition, Vendor = source.Vendor, Kind = source.Kind,
            LicensedTo = source.LicensedTo, CustomerNumber = source.CustomerNumber, PortalUrl = source.PortalUrl, SupportContact = source.SupportContact,
            SupportUrl = source.SupportUrl, Sla = source.Sla, CostCenter = source.CostCenter, LocationId = source.LocationId, OwnerId = source.OwnerId,
            Seats = source.Seats, SeatUnit = source.SeatUnit, SeatsFromHosts = source.SeatsFromHosts, PriceAmount = source.PriceAmount,
            Currency = source.Currency, PriceBasis = source.PriceBasis, Billing = source.Billing, BillingMonths = source.BillingMonths,
            RecordCharges = source.RecordCharges, ChargesFrom = source.RecordCharges ? Clock.Today : null, StartsOn = Clock.Today,
            ExpiresOn = source.ExpiresOn, TermMonths = source.TermMonths, Renewal = source.Renewal, WarnDays = source.WarnDays,
            NoticeDays = source.NoticeDays, WarnBeforeRenewal = source.WarnBeforeRenewal, EndOfLifeFromTemplate = source.EndOfLifeFromTemplate,
            EndOfLifeOn = source.EndOfLifeOn, EndOfLifeNote = source.EndOfLifeNote, EolWarnDays = source.EolWarnDays, ReviewMonths = source.ReviewMonths,
            Status = LicenseStatus.Active, Note = source.Note, CreatedAt = now, CreatedBy = By.Name, UpdatedAt = now,
        };
        db.Licenses.Add(copy);
        await db.SaveChangesAsync(ct);
        AuditLog.Target(LicenseEvaluator.Target(copy));
        AuditLog.Summary($"{LicenseWords.Number(copy.Type, copy.Number)} {copy.Name} created as a copy of {LicenseWords.Number(source.Type, source.Number)}");
        await evaluator.EvaluateAsync(copy.Id, Actor, ct);
        return await queries.GetAsync(copy.Id, ct);
    }

    /// <summary>Renew… (§5.6): a renewal row, the new expiry, the price from now on, a charge when PNMT records none, and the paper.</summary>
    public async Task<LicenseRenewalItem> RenewAsync(Guid id, LicenseRenewRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var today = Clock.Today;
        var term = item.TermMonths ?? 12;
        var preset = item.ExpiresOn is { } e && e.AddDays(1).AddMonths(term) > today ? e.AddDays(1).AddMonths(term).AddDays(-1) : today.AddMonths(term).AddDays(-1);
        var newExpiry = request.NewExpiresOn ?? preset;
        var currency = NormalizeCurrency(request.Currency) ?? item.Currency;
        var v = new ApiValidator()
            .Check(item.ExpiresOn is null || newExpiry > item.ExpiresOn, "newExpiresOn", "The new expiry is after the old one.")
            .Check(request.PriceAmount is null or (>= 0 and <= 9_999_999_999m), "priceAmount", "0 or more.")
            .Check(currency.Length == 3, "currency", "Three letters, as EUR.")
            .Check(Fits(request.Reference, 200), "reference", "At most 200 characters.")
            .Check(Fits(request.Note, 500), "note", "At most 500 characters.")
            .Check(request.ChargeAmount is null or (>= 0 and <= 9_999_999_999m), "chargeAmount", "0 or more.");
        v.ThrowIfInvalid();
        var priceChanged = request.PriceAmount is { } price && (price != item.PriceAmount || currency != item.Currency);
        var renewal = new LicenseRenewalEntity
        {
            Id = Guid.NewGuid(), LicenseId = id, Kind = LicenseRenewalKind.Renewal, On = today, PreviousExpiresOn = item.ExpiresOn, NewExpiresOn = newExpiry,
            PriceAmount = priceChanged ? request.PriceAmount : null, Currency = priceChanged ? currency : null, Reference = Trim(request.Reference),
            Note = Trim(request.Note), CreatedAt = Clock.UtcNow, CreatedBy = By.Name,
        };
        db.LicenseRenewals.Add(renewal);
        var was = item.ExpiresOn;
        item.ExpiresOn = newExpiry;
        if (priceChanged)
        {
            item.PriceAmount = request.PriceAmount;
            item.Currency = currency;
        }
        if (request.Reactivate && item.Status != LicenseStatus.Active)
        {
            SetStatus(item, LicenseStatus.Active);
        }
        if (!item.RecordCharges && request.ChargeAmount is { } charge)
        {
            db.LicenseCharges.Add(new LicenseChargeEntity
            {
                Id = Guid.NewGuid(), LicenseId = id, Kind = LicenseChargeKind.Period, Source = LicenseChargeSource.Person, On = request.ChargeOn ?? today,
                PeriodFrom = was?.AddDays(1), PeriodTo = newExpiry, Amount = charge, Currency = item.Currency, Reference = Trim(request.Reference),
                CreatedAt = Clock.UtcNow, CreatedBy = By.Name,
            });
        }
        item.VerdictKey = null;
        item.UpdatedAt = Clock.UtcNow;
        AuditLog.Target(LicenseEvaluator.Target(item));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)} {item.Name} renewed until {Clock.FormatDay(newExpiry)}"
            + (was is { } w ? $" (was {Clock.FormatDay(w)})" : "")
            + (priceChanged ? $", price from now on {LicenseNotice.Money(item.Currency, item.PriceAmount!.Value)}" : "")
            + (renewal.Reference is { } r ? $", {r}" : ""));
        await db.SaveChangesAsync(ct);
        await evaluator.EvaluateAsync(id, Actor, ct);
        return (await queries.RenewalsAsync(id, ct)).First(x => x.Id == renewal.Id);
    }

    /// <summary>Mark reviewed… (§5.7), for one item or many.</summary>
    public async Task<LicenseBulkResult> ReviewAsync(IReadOnlyList<Guid> ids, LicenseReviewRequest request, CancellationToken ct)
    {
        var on = request.On ?? Clock.Today;
        new ApiValidator()
            .Check(ids.Count is >= 1 and <= MaxPerCall, "ids", $"1 to {MaxPerCall} items.")
            .Check(on <= Clock.Today, "on", "Not in the future.")
            .Check(Fits(request.Note, 500), "note", "At most 500 characters.")
            .ThrowIfInvalid();
        var items = await db.Licenses.Where(l => ids.Contains(l.Id)).ToListAsync(ct);
        if (items.Count != ids.Distinct().Count())
        {
            throw ApiException.NotFound("Licence or service");
        }
        foreach (var item in items)
        {
            item.ReviewedOn = on;
            item.ReviewedBy = Cut(By.Name, 200);
            item.ReviewNote = Trim(request.Note);
            item.UpdatedAt = Clock.UtcNow;
        }
        AuditLog.Target(Audit.AuditTarget.Many(Audit.AuditTargets.License, items.Select(i => (i.Id, $"{LicenseWords.Number(i.Type, i.Number)} {i.Name}")).ToList()));
        AuditLog.Summary($"{string.Join(", ", items.Take(20).Select(i => LicenseWords.Number(i.Type, i.Number)))}{(items.Count > 20 ? $" and {items.Count - 20} more" : "")} reviewed on {Clock.FormatDay(on)}"
            + (Trim(request.Note) is { } note ? $": {note}" : ""));
        await db.SaveChangesAsync(ct);
        await evaluator.EvaluateManyAsync(items.Select(i => i.Id).ToList(), Actor, ct);
        return new LicenseBulkResult(items.Count);
    }

    /// <summary>Cancel… (§5.6): it runs to its expiry and isn't renewed; one without an expiry is given its last day.</summary>
    public async Task<LicenseDetail> CancelAsync(Guid id, LicenseCancelRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        new ApiValidator()
            .Check(item.Status == LicenseStatus.Active, "status", "Only an active item can be cancelled.")
            .Check(item.ExpiresOn is not null || request.EndsOn is not null, "endsOn", "Its last day: it has no expiry.")
            .ThrowIfInvalid();
        if (request.EndsOn is { } ends)
        {
            item.ExpiresOn = ends;
        }
        SetStatus(item, LicenseStatus.Cancelled);
        item.VerdictKey = null;
        AuditLog.Target(LicenseEvaluator.Target(item));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)} {item.Name} cancelled: it ends on {Clock.FormatDay(item.ExpiresOn!.Value)}");
        await db.SaveChangesAsync(ct);
        await evaluator.EvaluateAsync(id, Actor, ct);
        return await queries.GetAsync(id, ct);
    }

    public async Task<LicenseDetail> ReactivateAsync(Guid id, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        if (item.Status == LicenseStatus.Active)
        {
            throw ApiException.Conflict("already_active", "It is active.");
        }
        SetStatus(item, LicenseStatus.Active);
        item.VerdictKey = null;
        if (item.RecordCharges && (item.ChargesFrom is null || item.ChargesFrom < Clock.Today))
        {
            // Nothing is recorded for the time it was retired or cancelled (§6.2).
            item.ChargesFrom = Clock.Today;
        }
        AuditLog.Target(LicenseEvaluator.Target(item));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)} {item.Name} reactivated");
        await db.SaveChangesAsync(ct);
        await evaluator.EvaluateAsync(id, Actor, ct);
        return await queries.GetAsync(id, ct);
    }

    /// <summary>Retire… (§5.8): its hosts and sensors go, its charges and notices stop; the item and its records stay.</summary>
    public async Task<LicenseDetail> RetireAsync(Guid id, LicenseRetireRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        if (item.Status == LicenseStatus.Retired)
        {
            throw ApiException.Conflict("already_retired", "It is retired.");
        }
        var assignments = await db.LicenseAssignments.Where(a => a.LicenseId == id).ToListAsync(ct);
        if (assignments.Count > 0 && !request.Confirmed)
        {
            throw ApiException.Conflict("confirm_needed", $"It is on {assignments.Count} host{(assignments.Count == 1 ? "" : "s")}: their sensors go too. Confirm.");
        }
        await RemoveAssignmentsAsync(assignments, ct);
        SetStatus(item, LicenseStatus.Retired);
        item.VerdictCode = null;
        item.VerdictKey = null;
        item.VerdictMessage = null;
        AuditLog.Target(LicenseEvaluator.Target(item));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)} {item.Name} retired" + (assignments.Count > 0 ? $"; removed from {assignments.Count} host(s)" : ""));
        await db.SaveChangesAsync(ct);
        return await queries.GetAsync(id, ct);
    }

    /// <summary>Delete… (§5.8): the number must match; its sensors, hosts, links, renewals, charges and documents go. Its files go to the cleanup.</summary>
    public async Task DeleteAsync(Guid id, string? number, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        if (LicenseWords.ParseNumber(number) != item.Number)
        {
            throw ApiException.Invalid("number", $"Type {LicenseWords.Number(item.Type, item.Number)} to delete it.");
        }
        var assignments = await db.LicenseAssignments.Where(a => a.LicenseId == id).ToListAsync(ct);
        var charges = await db.LicenseCharges.AsNoTracking().Where(c => c.LicenseId == id).GroupBy(c => c.Currency)
            .Select(g => new { g.Key, Sum = g.Sum(c => c.Amount) }).ToListAsync(ct);
        var documents = await db.LicenseDocuments.CountAsync(d => d.LicenseId == id, ct);
        await RemoveAssignmentsAsync(assignments, ct);
        AuditLog.Target(LicenseEvaluator.Target(item));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)} {item.Name} deleted, with {assignments.Count} host(s), {documents} document(s) and charges of "
            + (charges.Count == 0 ? "nothing" : string.Join(", ", charges.Select(c => LicenseNotice.Money(c.Key, c.Sum)))));
        db.Licenses.Remove(item);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The keys, shown (§3.4): audited by the endpoint. 409 <c>key_lost</c> when the key ring that protected one is gone.</summary>
    public async Task<LicenseKeys> KeysAsync(Guid id, CancellationToken ct)
    {
        var item = await db.Licenses.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw ApiException.NotFound("Licence or service");
        var license = keys.Unprotect(item.LicenseKeyProtected);
        var product = keys.Unprotect(item.ProductKeyProtected);
        if ((item.LicenseKeyProtected is not null && license is null) || (item.ProductKeyProtected is not null && product is null))
        {
            throw ApiException.Conflict("key_lost", "This key can't be read: PNMT's key ring was replaced. Enter the key again.");
        }
        AuditLog.Target(LicenseEvaluator.Target(item));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)}: the keys were shown");
        return new LicenseKeys(license, product);
    }

    // --- hosts and linked sensors (§5.1, §5.4) -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<LicenseAssignmentItem>> AssignAsync(Guid id, LicenseAssignRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        if (item.Status == LicenseStatus.Retired)
        {
            throw ApiException.Conflict("license_retired", "A retired item can't be assigned: reactivate it first.");
        }
        var hostIds = (request.HostIds ?? []).Distinct().ToList();
        new ApiValidator()
            .Check(hostIds.Count is >= 1 and <= MaxPerCall, "hostIds", $"1 to {MaxPerCall} hosts.")
            .Check(request.Seats is null or (>= 1 and <= 1_000_000), "seats", "1 to 1,000,000.")
            .Check(Fits(request.Note, 500), "note", "At most 500 characters.")
            .ThrowIfInvalid();
        var hosts = await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id)).Select(h => new { h.Id, h.Name, h.LocationId }).ToListAsync(ct);
        if (hosts.Count != hostIds.Count)
        {
            throw ApiException.Invalid("hostIds", "No such host.");
        }
        var existing = await db.LicenseAssignments.Where(a => a.LicenseId == id).Select(a => a.HostId).ToListAsync(ct);
        var added = hosts.Where(h => !existing.Contains(h.Id)).ToList();
        if (existing.Count + added.Count > options.CurrentValue.MaxAssignments)
        {
            throw ApiException.Conflict("too_many_assignments", $"At most {options.CurrentValue.MaxAssignments} hosts per item (Licenses__MaxAssignments).");
        }
        var now = Clock.UtcNow;
        foreach (var host in added)
        {
            var sensor = LicenseSensors.New(host.Id, item.Type, item.Name, item.Number, now);
            db.Sensors.Add(sensor);
            db.LicenseAssignments.Add(new LicenseAssignmentEntity
            {
                Id = Guid.NewGuid(), LicenseId = id, HostId = host.Id, SensorId = sensor.Id, Seats = request.Seats ?? 1, Note = Trim(request.Note),
                AssignedAt = now, AssignedBy = By.Name,
            });
        }
        item.VerdictKey = null;
        AuditLog.Target(new Audit.AuditTarget(Audit.AuditTargets.License, id.ToString(), $"{LicenseWords.Number(item.Type, item.Number)} {item.Name}",
            [id, .. added.Select(h => h.Id).Take(999)]));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)} {item.Name} assigned to "
            + (added.Count == 0 ? "no new host" : string.Join(", ", added.Take(20).Select(h => h.Name)) + (added.Count > 20 ? $" and {added.Count - 20} more" : "")));
        await db.SaveChangesAsync(ct);
        await evaluator.EvaluateAsync(id, Actor, ct);
        return await queries.AssignmentsAsync(id, ct);
    }

    public async Task<IReadOnlyList<LicenseAssignmentItem>> UpdateAssignmentAsync(Guid id, Guid hostId, LicenseAssignmentRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var assignment = await db.LicenseAssignments.SingleOrDefaultAsync(a => a.LicenseId == id && a.HostId == hostId, ct)
            ?? throw ApiException.NotFound("Assignment");
        new ApiValidator()
            .Check(request.Seats is null or (>= 1 and <= 1_000_000), "seats", "1 to 1,000,000.")
            .Check(Fits(request.Note, 500), "note", "At most 500 characters.")
            .ThrowIfInvalid();
        assignment.Seats = request.Seats ?? assignment.Seats;
        assignment.Note = Trim(request.Note);
        item.VerdictKey = null;
        AuditLog.Target(new Audit.AuditTarget(Audit.AuditTargets.License, id.ToString(), $"{LicenseWords.Number(item.Type, item.Number)} {item.Name}", [id, hostId]));
        await db.SaveChangesAsync(ct);
        await evaluator.EvaluateAsync(id, Actor, ct);
        return await queries.AssignmentsAsync(id, ct);
    }

    public async Task<IReadOnlyList<LicenseAssignmentItem>> UnassignAsync(Guid id, LicenseUnassignRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var hostIds = (request.HostIds ?? []).Distinct().ToList();
        new ApiValidator().Check(hostIds.Count is >= 1 and <= MaxPerCall, "hostIds", $"1 to {MaxPerCall} hosts.").ThrowIfInvalid();
        var assignments = await db.LicenseAssignments.Where(a => a.LicenseId == id && hostIds.Contains(a.HostId)).ToListAsync(ct);
        var names = await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id)).Select(h => h.Name).ToListAsync(ct);
        await RemoveAssignmentsAsync(assignments, ct);
        item.VerdictKey = null;
        AuditLog.Target(new Audit.AuditTarget(Audit.AuditTargets.License, id.ToString(), $"{LicenseWords.Number(item.Type, item.Number)} {item.Name}",
            [id, .. hostIds.Take(999)]));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)} {item.Name} unassigned from {string.Join(", ", names.Take(20))}");
        await db.SaveChangesAsync(ct);
        await evaluator.EvaluateAsync(id, Actor, ct);
        return await queries.AssignmentsAsync(id, ct);
    }

    public async Task<IReadOnlyList<LicenseSensorLinkItem>> LinkAsync(Guid id, LicenseLinkRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var sensorIds = (request.SensorIds ?? []).Distinct().ToList();
        new ApiValidator()
            .Check(sensorIds.Count is >= 1 and <= MaxPerCall, "sensorIds", $"1 to {MaxPerCall} sensors.")
            .Check(Fits(request.Note, 200), "note", "At most 200 characters.")
            .ThrowIfInvalid();
        var sensors = await db.Sensors.AsNoTracking().Where(s => sensorIds.Contains(s.Id)).Select(s => new { s.Id, s.Name, s.Type, s.ParentId }).ToListAsync(ct);
        new ApiValidator()
            .Check(sensors.Count == sensorIds.Count, "sensorIds", "No such sensor.")
            .Check(sensors.All(s => s.Type != SensorType.LICENSE), "sensorIds", "An item sensor is the item already.")
            .ThrowIfInvalid();
        var existing = await db.LicenseSensorLinks.Where(k => k.LicenseId == id).Select(k => k.SensorId).ToListAsync(ct);
        var added = sensors.Where(s => !existing.Contains(s.Id)).ToList();
        foreach (var sensor in added)
        {
            db.LicenseSensorLinks.Add(new LicenseSensorLinkEntity
            {
                Id = Guid.NewGuid(), LicenseId = id, SensorId = sensor.Id, Note = Trim(request.Note), CreatedAt = Clock.UtcNow, CreatedBy = By.Name,
            });
        }
        AuditLog.Target(new Audit.AuditTarget(Audit.AuditTargets.License, id.ToString(), $"{LicenseWords.Number(item.Type, item.Number)} {item.Name}",
            [id, .. added.Select(s => s.Id).Take(999)]));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)} {item.Name} linked to {(added.Count == 0 ? "no new sensor" : string.Join(", ", added.Take(20).Select(s => s.Name)))}");
        await db.SaveChangesAsync(ct);
        return await queries.SensorLinksAsync(id, ct);
    }

    public async Task<IReadOnlyList<LicenseSensorLinkItem>> UnlinkAsync(Guid id, Guid sensorId, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var link = await db.LicenseSensorLinks.SingleOrDefaultAsync(k => k.LicenseId == id && k.SensorId == sensorId, ct) ?? throw ApiException.NotFound("Link");
        db.LicenseSensorLinks.Remove(link);
        AuditLog.Target(new Audit.AuditTarget(Audit.AuditTargets.License, id.ToString(), $"{LicenseWords.Number(item.Type, item.Number)} {item.Name}", [id, sensorId]));
        await db.SaveChangesAsync(ct);
        return await queries.SensorLinksAsync(id, ct);
    }

    // --- renewals and charges (§5.6, §6.2, §6.3) ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<LicenseRenewalItem>> UpdateRenewalAsync(Guid id, Guid renewalId, LicenseRenewalChangeRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var renewal = await db.LicenseRenewals.SingleOrDefaultAsync(r => r.Id == renewalId && r.LicenseId == id, ct) ?? throw ApiException.NotFound("Renewal");
        new ApiValidator().Check(Fits(request.Reference, 200), "reference", "At most 200 characters.").Check(Fits(request.Note, 500), "note", "At most 500 characters.")
            .ThrowIfInvalid();
        renewal.Reference = Trim(request.Reference);
        renewal.Note = Trim(request.Note);
        AuditLog.Target(LicenseEvaluator.Target(item));
        await db.SaveChangesAsync(ct);
        return await queries.RenewalsAsync(id, ct);
    }

    /// <summary>A renewal deleted (Manage): the expiry stays, it is the item's own field (§5.6).</summary>
    public async Task<IReadOnlyList<LicenseRenewalItem>> DeleteRenewalAsync(Guid id, Guid renewalId, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var renewal = await db.LicenseRenewals.SingleOrDefaultAsync(r => r.Id == renewalId && r.LicenseId == id, ct) ?? throw ApiException.NotFound("Renewal");
        db.LicenseRenewals.Remove(renewal);
        AuditLog.Target(LicenseEvaluator.Target(item));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)}: the renewal of {Clock.FormatDay(renewal.On)} (until {Clock.FormatDay(renewal.NewExpiresOn)}) deleted");
        await db.SaveChangesAsync(ct);
        return await queries.RenewalsAsync(id, ct);
    }

    /// <summary>Record a cost… (§6.3): by a person; a credit is given positive and stored negative.</summary>
    public async Task<LicenseChargeItem> RecordChargeAsync(Guid id, LicenseChargeRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var (amount, currency) = ValidCharge(request, item.Currency, true);
        var charge = new LicenseChargeEntity
        {
            Id = Guid.NewGuid(), LicenseId = id, Kind = request.Kind, Source = LicenseChargeSource.Person, On = request.On ?? Clock.Today,
            PeriodFrom = request.PeriodFrom, PeriodTo = request.PeriodTo, Amount = request.Kind == LicenseChargeKind.Credit ? -amount : amount,
            Currency = currency, Estimated = request.Estimated ?? false, Reference = Trim(request.Reference), Note = Trim(request.Note),
            CreatedAt = Clock.UtcNow, CreatedBy = By.Name,
        };
        db.LicenseCharges.Add(charge);
        AuditLog.Target(LicenseEvaluator.Target(item));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)} {item.Name}: {LicenseNotice.Money(currency, charge.Amount)} ({request.Kind}) on {Clock.FormatDay(charge.On)}"
            + (charge.Reference is { } r ? $", {r}" : ""));
        await db.SaveChangesAsync(ct);
        return (await queries.ItemChargesAsync(id, ct)).First(c => c.Id == charge.Id);
    }

    /// <summary>A correction (§6.2, §6.3): PNMT's own stay automatic; entering the invoice's amount clears <i>estimated</i>.</summary>
    public async Task<LicenseChargeItem> UpdateChargeAsync(Guid id, Guid chargeId, LicenseChargeRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var charge = await db.LicenseCharges.SingleOrDefaultAsync(c => c.Id == chargeId && c.LicenseId == id, ct) ?? throw ApiException.NotFound("Charge");
        var (amount, currency) = ValidCharge(request, charge.Currency, false);
        var kind = charge.Source == LicenseChargeSource.Automatic ? charge.Kind : request.Kind;
        var signed = kind == LicenseChargeKind.Credit ? -Math.Abs(amount) : request.Amount is null ? charge.Amount : amount;
        if (request.Amount is not null && signed != charge.Amount)
        {
            charge.Estimated = false;
        }
        charge.Kind = kind;
        charge.Amount = signed;
        charge.Currency = currency;
        charge.On = request.On ?? charge.On;
        charge.PeriodFrom = charge.Source == LicenseChargeSource.Automatic ? charge.PeriodFrom : request.PeriodFrom ?? charge.PeriodFrom;
        charge.PeriodTo = request.PeriodTo ?? charge.PeriodTo;
        charge.Reference = Trim(request.Reference);
        charge.Note = Trim(request.Note);
        if (request.Estimated is { } estimated)
        {
            charge.Estimated = estimated;
        }
        charge.UpdatedAt = Clock.UtcNow;
        charge.UpdatedBy = By.Name;
        AuditLog.Target(LicenseEvaluator.Target(item));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)}: the charge of {Clock.FormatDay(charge.On)} is now {LicenseNotice.Money(charge.Currency, charge.Amount)}"
            + (charge.Reference is { } r ? $", {r}" : ""));
        await db.SaveChangesAsync(ct);
        return (await queries.ItemChargesAsync(id, ct)).First(c => c.Id == charge.Id);
    }

    public async Task DeleteChargeAsync(Guid id, Guid chargeId, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var charge = await db.LicenseCharges.SingleOrDefaultAsync(c => c.Id == chargeId && c.LicenseId == id, ct) ?? throw ApiException.NotFound("Charge");
        if (charge.Source == LicenseChargeSource.Automatic)
        {
            throw ApiException.Conflict("automatic_charge", "It would be recorded again: set it to 0 with a note instead.");
        }
        db.LicenseCharges.Remove(charge);
        AuditLog.Target(LicenseEvaluator.Target(item));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)}: the charge of {LicenseNotice.Money(charge.Currency, charge.Amount)} on {Clock.FormatDay(charge.On)} deleted");
        await db.SaveChangesAsync(ct);
    }

    private (decimal Amount, string Currency) ValidCharge(LicenseChargeRequest r, string itemCurrency, bool create)
    {
        var currency = NormalizeCurrency(r.Currency) ?? itemCurrency;
        new ApiValidator()
            .Check(Enum.IsDefined(r.Kind), "kind", "Purchase, Period, Extra or Credit.")
            .Check(!create || r.Amount is not null, "amount", "The amount.")
            .Check(r.Amount is null or (>= -9_999_999_999m and <= 9_999_999_999m), "amount", "Too large.")
            .Check(!create || r.Amount is null or >= 0, "amount", "Positive; a credit is stored negative.")
            .Check(currency.Length == 3 && currency.All(char.IsAsciiLetterUpper), "currency", "Three letters, as EUR.")
            .Check(r.PeriodFrom is null || r.PeriodTo is null || r.PeriodTo >= r.PeriodFrom, "periodTo", "After the period's start.")
            .Check(Fits(r.Reference, 200), "reference", "At most 200 characters.")
            .Check(Fits(r.Note, 500), "note", "At most 500 characters.")
            .ThrowIfInvalid();
        return (Math.Abs(r.Amount ?? 0m), currency);
    }

    // --- documents (§7) ----------------------------------------------------------------------------------------------------------

    public async Task<LicenseDocumentItem> AddDocumentAsync(Guid id, Stream body, string? name, string? kind, Guid? chargeId, Guid? renewalId, FileActor actor,
        FileStore store, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        LicenseDocumentKind? parsed = Enum.TryParse<LicenseDocumentKind>(kind, true, out var k) ? k : null;
        new ApiValidator().Check(kind is null || parsed is not null, "kind", "Invoice, Contract, Quote, LicenseFile, KeyFile, Warranty or Other.").ThrowIfInvalid();
        await CheckOwnAsync(id, chargeId, renewalId, ct);
        var file = await store.StoreAsync(body, FileKind.Attachment, name, null, actor, ct);
        await FileUses.RecordAsync(db, [file.Id], FileOwner.License(id), ct);
        if (!await db.LicenseDocuments.AnyAsync(d => d.LicenseId == id && d.FileId == file.Id, ct))
        {
            db.LicenseDocuments.Add(new LicenseDocumentEntity
            {
                Id = Guid.NewGuid(), LicenseId = id, FileId = file.Id, Kind = parsed ?? SuggestKind(file.Name), ChargeId = chargeId, RenewalId = renewalId,
                AddedAt = Clock.UtcNow, AddedBy = By.Name,
            });
        }
        AuditLog.Target(new Audit.AuditTarget(Audit.AuditTargets.License, id.ToString(), $"{LicenseWords.Number(item.Type, item.Number)} {item.Name}", [id, file.Id]));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)}: {file.Name} added ({file.Size} bytes)");
        await db.SaveChangesAsync(ct);
        return (await queries.DocumentsAsync(id, ct)).First(d => d.FileId == file.Id);
    }

    public async Task<IReadOnlyList<LicenseDocumentItem>> UpdateDocumentAsync(Guid id, Guid fileId, LicenseDocumentChangeRequest request, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var document = await db.LicenseDocuments.SingleOrDefaultAsync(d => d.LicenseId == id && d.FileId == fileId, ct) ?? throw ApiException.NotFound("Document");
        new ApiValidator().Check(Fits(request.Description, 200), "description", "At most 200 characters.").ThrowIfInvalid();
        await CheckOwnAsync(id, request.ChargeId, request.RenewalId, ct);
        document.Kind = request.Kind ?? document.Kind;
        document.Description = Trim(request.Description);
        document.ChargeId = request.ChargeId;
        document.RenewalId = request.RenewalId;
        AuditLog.Target(new Audit.AuditTarget(Audit.AuditTargets.License, id.ToString(), $"{LicenseWords.Number(item.Type, item.Number)} {item.Name}", [id, fileId]));
        await db.SaveChangesAsync(ct);
        return await queries.DocumentsAsync(id, ct);
    }

    /// <summary>Removed (§7.3): its two rows go; the file, unused then, goes the cleanup's way.</summary>
    public async Task RemoveDocumentAsync(Guid id, Guid fileId, CancellationToken ct)
    {
        var item = await TrackedAsync(id, ct);
        var document = await db.LicenseDocuments.SingleOrDefaultAsync(d => d.LicenseId == id && d.FileId == fileId, ct) ?? throw ApiException.NotFound("Document");
        var name = await db.Files.AsNoTracking().Where(f => f.Id == fileId).Select(f => f.Name).SingleOrDefaultAsync(ct);
        db.LicenseDocuments.Remove(document);
        await db.FileUses.Where(u => u.LicenseId == id && u.FileId == fileId).ExecuteDeleteAsync(ct);
        AuditLog.Target(new Audit.AuditTarget(Audit.AuditTargets.License, id.ToString(), $"{LicenseWords.Number(item.Type, item.Number)} {item.Name}", [id, fileId]));
        AuditLog.Summary($"{LicenseWords.Number(item.Type, item.Number)}: {name} removed");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A document, only through its item (§7.2); audited by the endpoint.</summary>
    public async Task<FileEntity> DocumentAsync(Guid id, Guid fileId, CancellationToken ct)
    {
        var row = await (from d in db.LicenseDocuments.AsNoTracking()
                         join f in db.Files.AsNoTracking() on d.FileId equals f.Id
                         join l in db.Licenses.AsNoTracking() on d.LicenseId equals l.Id
                         where d.LicenseId == id && d.FileId == fileId
                         select new { f, l.Type, l.Number, l.Name, d.Kind }).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Document");
        AuditLog.Target(new Audit.AuditTarget(Audit.AuditTargets.License, id.ToString(), $"{LicenseWords.Number(row.Type, row.Number)} {row.Name}", [id, fileId]));
        AuditLog.Summary($"{LicenseWords.Number(row.Type, row.Number)}: {row.f.Name} ({row.Kind}) downloaded");
        return row.f;
    }

    internal static LicenseDocumentKind SuggestKind(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith(".lic", StringComparison.Ordinal) ? LicenseDocumentKind.LicenseFile
            : n.EndsWith(".key", StringComparison.Ordinal) ? LicenseDocumentKind.KeyFile
            : n.Contains("invoice") || n.Contains("rechnung") || n.Contains("facture") ? LicenseDocumentKind.Invoice
            : n.Contains("warranty") || n.Contains("garantie") ? LicenseDocumentKind.Warranty
            : n.Contains("contract") || n.Contains("vertrag") ? LicenseDocumentKind.Contract
            : n.Contains("quote") || n.Contains("offer") || n.Contains("angebot") || n.Contains("offerte") ? LicenseDocumentKind.Quote
            : LicenseDocumentKind.Other;
    }

    private async Task CheckOwnAsync(Guid id, Guid? chargeId, Guid? renewalId, CancellationToken ct)
    {
        var v = new ApiValidator();
        if (chargeId is { } c)
        {
            v.Check(await db.LicenseCharges.AnyAsync(x => x.Id == c && x.LicenseId == id, ct), "chargeId", "Not one of its charges.");
        }
        if (renewalId is { } r)
        {
            v.Check(await db.LicenseRenewals.AnyAsync(x => x.Id == r && x.LicenseId == id, ct), "renewalId", "Not one of its renewals.");
        }
        v.ThrowIfInvalid();
    }

    // --- names (§8.6) --------------------------------------------------------------------------------------------------------

    /// <summary>Rename… a manufacturer, vendor, cost centre or unit everywhere; to a name in use, the two merge.</summary>
    public async Task<LicenseRenameResult> RenameAsync(LicenseRenameRequest request, CancellationToken ct)
    {
        var field = request.Field?.Trim() ?? "";
        var from = request.From ?? "";
        var to = request.To?.Trim() ?? "";
        var max = field == "seatUnit" ? 30 : 100;
        new ApiValidator()
            .Check(field is "manufacturer" or "vendor" or "costCenter" or "seatUnit", "field", "manufacturer, vendor, costCenter or seatUnit.")
            .Check(from.Length > 0, "from", "The name to rename.")
            .Check(to.Length is >= 1 && to.Length <= max, "to", $"1 to {max} characters.")
            .ThrowIfInvalid();
        var (items, templates) = field switch
        {
            "manufacturer" => (await db.Licenses.Where(l => l.Manufacturer == from).ExecuteUpdateAsync(u => u.SetProperty(l => l.Manufacturer, to), ct),
                await db.LicenseTemplates.Where(t => t.Manufacturer == from).ExecuteUpdateAsync(u => u.SetProperty(t => t.Manufacturer, to), ct)),
            "vendor" => (await db.Licenses.Where(l => l.Vendor == from).ExecuteUpdateAsync(u => u.SetProperty(l => l.Vendor, to), ct),
                await db.LicenseTemplates.Where(t => t.Vendor == from).ExecuteUpdateAsync(u => u.SetProperty(t => t.Vendor, to), ct)),
            "costCenter" => (await db.Licenses.Where(l => l.CostCenter == from).ExecuteUpdateAsync(u => u.SetProperty(l => l.CostCenter, to), ct), 0),
            _ => (await db.Licenses.Where(l => l.SeatUnit == from).ExecuteUpdateAsync(u => u.SetProperty(l => l.SeatUnit, to), ct),
                await db.LicenseTemplates.Where(t => t.SeatUnit == from).ExecuteUpdateAsync(u => u.SetProperty(t => t.SeatUnit, to), ct)),
        };
        AuditLog.Summary($"{field} {from} → {to}: {items} item(s), {templates} template(s)");
        return new LicenseRenameResult(items, templates);
    }

    // --- the item's fields -----------------------------------------------------------------------------------------------------

    /// <summary>The whole form, validated (§3.3), onto the item; the keys protected; the credentials checked on the note and the support contact.</summary>
    private async Task ApplyAsync(LicenseEntity item, LicenseRequest r, LicenseEntity? existing, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var v = new ApiValidator();
        var template = r.TemplateId is { } tid ? await db.LicenseTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tid, ct) : null;
        v.Check(Enum.IsDefined(r.Type), "type", "License, Service or Hardware.");
        v.Check(r.TemplateId is null || template is not null, "templateId", "No such template.");
        var name = Trim(r.Name) ?? (template is not null ? Trim($"{template.ProductName} {template.Edition}") : null);
        v.Check(name is { Length: >= 1 and <= 200 }, "name", "1 to 200 characters.");
        v.Check(Enum.IsDefined(r.Type) && LicenseWords.Kinds[r.Type].Contains(r.Kind), "kind",
            Enum.IsDefined(r.Type) ? $"For a {LicenseWords.TypeLabel(r.Type).ToLowerInvariant()}: {string.Join(", ", LicenseWords.Kinds[r.Type])}." : "A kind.");
        var categoryId = r.CategoryId ?? template?.CategoryId
            ?? await db.LicenseCategories.Where(c => c.DefaultFor == r.Type).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
            ?? await db.LicenseCategories.OrderBy(c => c.SortOrder).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        v.Check(categoryId is { } cid && await db.LicenseCategories.AnyAsync(c => c.Id == cid, ct), "categoryId", "No such category.");
        v.Check(Fits(r.Manufacturer, 100), "manufacturer", "At most 100 characters.");
        v.Check(Fits(r.ProductName, 200), "productName", "At most 200 characters.");
        v.Check(Fits(r.Edition, 100), "edition", "At most 100 characters.");
        v.Check(Fits(r.Vendor, 100), "vendor", "At most 100 characters.");
        v.Check(Fits(r.LicensedTo, 200), "licensedTo", "At most 200 characters.");
        v.Check(Fits(r.Reference, 200), "reference", "At most 200 characters.");
        v.Check(Fits(r.CustomerNumber, 100), "customerNumber", "At most 100 characters.");
        v.Check(IsUrl(r.PortalUrl), "portalUrl", "An http:// or https:// address, at most 500 characters.");
        v.Check(IsUrl(r.SupportUrl), "supportUrl", "An http:// or https:// address, at most 500 characters.");
        v.Check(Fits(r.SupportContact, 500), "supportContact", "At most 500 characters.");
        v.Check(Fits(r.Sla, 200), "sla", "At most 200 characters.");
        v.Check(Fits(r.CostCenter, 100), "costCenter", "At most 100 characters.");
        if (r.LocationId is { } loc)
        {
            v.Check(await db.Locations.AnyAsync(l => l.Id == loc, ct), "locationId", "No such location.");
        }
        if (r.OwnerId is { } owner)
        {
            v.Check(await db.Users.AnyAsync(u => u.Id == owner, ct), "ownerId", "No such user.");
        }
        v.Check(r.LicenseKey is null || r.LicenseKey.Length <= 4000, "licenseKey", "At most 4,000 characters.");
        v.Check(r.ProductKey is null || r.ProductKey.Length <= 4000, "productKey", "At most 4,000 characters.");
        v.Check(r.Seats is null or (>= 1 and <= 1_000_000), "seats", "1 to 1,000,000.");
        var unit = Trim(r.SeatUnit) ?? existing?.SeatUnit ?? "devices";
        v.Check(unit.Length <= 30, "seatUnit", "At most 30 characters.");
        v.Check(r.PriceAmount is null or (>= 0 and <= 9_999_999_999m), "priceAmount", "0 or more.");
        var currency = NormalizeCurrency(r.Currency) ?? existing?.Currency ?? o.Currency;
        v.Check(currency.Length == 3 && currency.All(char.IsAsciiLetterUpper), "currency", "Three letters, as EUR.");
        v.Check(Enum.IsDefined(r.PriceBasis), "priceBasis", "Total or PerSeat.");
        v.Check(Enum.IsDefined(r.Billing), "billing", "OneTime, Recurring or Variable.");
        v.Check(Enum.IsDefined(r.Renewal), "renewal", "None, Manual or Automatic.");
        var billingMonths = r.Billing == LicenseBilling.OneTime ? null : r.BillingMonths ?? r.TermMonths;
        v.Check(r.Billing == LicenseBilling.OneTime || billingMonths is >= 1 and <= 120, "billingMonths", "How often it is paid for: 1 to 120 months.");
        v.Check(r.TermMonths is null or (>= 1 and <= 120), "termMonths", "1 to 120 months.");
        v.Check(r.PriceBasis != LicensePriceBasis.PerSeat || r.Seats is not null, "seats", "A price per seat needs the seats.");
        v.Check(!r.SeatsFromHosts || r.Seats is not null, "seats", "Counting seats on hosts needs the seats.");
        v.Check(r.Renewal != LicenseRenewal.Automatic || (r.TermMonths is not null && r.ExpiresOn is not null), "termMonths",
            "An automatic renewal needs the term and the expiry.");
        v.Check(r.Renewal != LicenseRenewal.Manual || r.ExpiresOn is not null, "expiresOn", "A renewal by hand needs the expiry.");
        v.Check(r.Kind != LicenseKind.Trial || r.ExpiresOn is not null, "expiresOn", "A trial needs its end.");
        v.Check(r.WarnDays is null or (>= 0 and <= 730), "warnDays", "0 to 730 days.");
        v.Check(r.NoticeDays is null or (>= 0 and <= 730), "noticeDays", "0 to 730 days.");
        v.Check(r.Renewal != LicenseRenewal.Automatic || r.NoticeDays is null || r.TermMonths is null || r.NoticeDays < r.TermMonths * 28, "noticeDays",
            "Shorter than the term.");
        v.Check(r.EolWarnDays is null or (>= 0 and <= 1095), "eolWarnDays", "0 to 1,095 days.");
        v.Check(r.ReviewMonths is null or (>= 1 and <= 60), "reviewMonths", "1 to 60 months.");
        v.Check(!r.EndOfLifeFromTemplate || template is not null, "endOfLifeFromTemplate", "Following a template's end of life needs a template.");
        v.Check(Fits(r.EndOfLifeNote, 200), "endOfLifeNote", "At most 200 characters.");
        v.Check(r.ReviewedOn is null || r.ReviewedOn <= Clock.Today, "reviewedOn", "Not in the future.");
        v.Check(r.Note is null || r.Note.Length <= 10_000, "note", "At most 10,000 characters.");
        v.Check(r.StartsOn is null || r.ExpiresOn is null || r.ExpiresOn >= r.StartsOn, "expiresOn", "After the start.");
        v.ThrowIfInvalid();

        if (!r.AcknowledgeCredentials)
        {
            var findings = SecretScanner.Scan(r.Note).Concat(SecretScanner.Scan(r.SupportContact)).ToList();
            var known = existing is null ? [] : SecretScanner.Scan(existing.Note).Concat(SecretScanner.Scan(existing.SupportContact)).Select(f => f.Kind).ToList();
            if (findings.Count > 0 && (existing is null || r.Note != existing.Note || r.SupportContact != existing.SupportContact)
                && findings.Count > known.Count)
            {
                throw KbPageService.LooksLikeCredential(findings,
                    "This looks like a key. Keys belong in the licence key and product key fields, which are encrypted and shown only on request.");
            }
        }

        item.Type = r.Type;
        item.Name = name!;
        item.TemplateId = r.TemplateId;
        item.CategoryId = categoryId!.Value;
        item.Manufacturer = Trim(r.Manufacturer);
        item.ProductName = Trim(r.ProductName);
        item.Edition = Trim(r.Edition);
        item.Vendor = Trim(r.Vendor);
        item.Kind = r.Kind;
        item.LicensedTo = Trim(r.LicensedTo);
        item.Reference = Trim(r.Reference);
        item.CustomerNumber = Trim(r.CustomerNumber);
        item.PortalUrl = Trim(r.PortalUrl);
        item.SupportContact = Trim(r.SupportContact);
        item.SupportUrl = Trim(r.SupportUrl);
        item.Sla = Trim(r.Sla);
        item.CostCenter = Trim(r.CostCenter);
        item.LocationId = r.LocationId;
        item.OwnerId = r.OwnerId;
        if (r.LicenseKey is not null)
        {
            item.LicenseKeyProtected = r.LicenseKey.Trim().Length == 0 ? null : keys.Protect(r.LicenseKey.Trim());
            item.LicenseKeyHint = LicenseKeyProtector.Hint(r.LicenseKey);
        }
        if (r.ProductKey is not null)
        {
            item.ProductKeyProtected = r.ProductKey.Trim().Length == 0 ? null : keys.Protect(r.ProductKey.Trim());
            item.ProductKeyHint = LicenseKeyProtector.Hint(r.ProductKey);
        }
        item.Seats = r.Seats;
        item.SeatUnit = unit;
        item.SeatsFromHosts = r.SeatsFromHosts;
        item.PriceAmount = r.PriceAmount;
        item.Currency = currency;
        item.PriceBasis = r.PriceBasis;
        item.Billing = r.Billing;
        item.BillingMonths = billingMonths;
        item.RecordCharges = r.RecordCharges ?? existing?.RecordCharges ?? o.RecordCharges;
        item.StartsOn = r.StartsOn ?? existing?.StartsOn;
        item.ExpiresOn = r.ExpiresOn;
        item.TermMonths = r.TermMonths;
        item.Renewal = r.Renewal;
        item.WarnDays = r.WarnDays ?? existing?.WarnDays ?? o.WarnDays;
        item.NoticeDays = r.NoticeDays;
        item.WarnBeforeRenewal = r.WarnBeforeRenewal;
        item.EndOfLifeFromTemplate = r.EndOfLifeFromTemplate;
        item.EndOfLifeOn = r.EndOfLifeOn;
        item.EndOfLifeNote = Trim(r.EndOfLifeNote);
        item.EolWarnDays = r.EolWarnDays ?? existing?.EolWarnDays ?? o.EolWarnDays;
        if (existing is null || r.ReviewedOn != existing.ReviewedOn)
        {
            item.ReviewedOn = r.ReviewedOn;
            item.ReviewedBy = r.ReviewedOn is null ? null : Cut(By.Name, 200);
        }
        item.ReviewMonths = r.ReviewMonths;
        item.Note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note;
    }

    private async Task RenameSensorsAsync(LicenseEntity item, CancellationToken ct)
    {
        var sensorIds = await db.LicenseAssignments.Where(a => a.LicenseId == item.Id && a.SensorId != null).Select(a => a.SensorId!.Value).ToListAsync(ct);
        var name = LicenseWords.SensorName(item.Type, item.Name, item.Number);
        foreach (var sensor in await db.Sensors.Where(s => sensorIds.Contains(s.Id)).ToListAsync(ct))
        {
            sensor.Name = name;
        }
    }

    /// <summary>Assignments removed with their sensors and the sensors' events (§5.3).</summary>
    private async Task RemoveAssignmentsAsync(IReadOnlyList<LicenseAssignmentEntity> assignments, CancellationToken ct)
    {
        var sensorIds = assignments.Where(a => a.SensorId != null).Select(a => a.SensorId!.Value).ToList();
        db.LicenseAssignments.RemoveRange(assignments);
        await db.SaveChangesAsync(ct);
        if (sensorIds.Count > 0)
        {
            await db.Sensors.Where(s => sensorIds.Contains(s.Id)).ExecuteDeleteAsync(ct);
        }
    }

    private void SetStatus(LicenseEntity item, LicenseStatus status)
    {
        item.Status = status;
        item.StatusChangedAt = Clock.UtcNow;
        item.StatusChangedBy = Cut(By.Name, 200);
        item.UpdatedAt = Clock.UtcNow;
    }

    private async Task<LicenseEntity> TrackedAsync(Guid id, CancellationToken ct) =>
        await db.Licenses.SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw ApiException.NotFound("Licence or service");

    internal static string? NormalizeCurrency(string? currency) => string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();

    internal static bool Fits(string? text, int max) => text is null || text.Trim().Length <= max;

    internal static bool IsUrl(string? text) =>
        string.IsNullOrWhiteSpace(text) || (text.Trim().Length <= 500 && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    internal static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    internal static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
