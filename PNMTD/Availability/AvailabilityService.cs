using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Availability;

/// <summary>
/// The availability plan's API (docs/cookie-plan.md §8.2, §9.4): the table, the cells, the days' rows, the fill, the people
/// and the settings. Everyone in the plan edits their own column from today on; <i>Edit</i> edits everything;
/// <i>Manage</i> sets the people and the settings.
/// </summary>
public sealed partial class AvailabilityService(IDbContextFactory<PnmtdDbContext> dbs, AvailabilityPlan plan)
{
    private static readonly string[] Weekdays = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"];

    public static AvailabilityTypeItem ToItem(AvailabilityTypeEntity t) => new(t.Id, t.Name, t.Code, t.Color,
        [.. Enum.GetValues<AvailabilityFlag>().Where(t.Has)], t.IsDefault, t.IsDayOff, t.Order, t.Retired, t.Works);

    private static string Name(ClaimsPrincipal user) => CookieName(user.Identity?.Name);

    private static string CookieName(string? name) => string.IsNullOrWhiteSpace(name) ? "someone" : name.Length > 200 ? name[..200] : name;

    /// <summary>Whether the reader is in the plan (and so sees it without a right).</summary>
    public async Task<bool> InPlanAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var id = user.UserId();
        await using var db = await dbs.CreateDbContextAsync(ct);
        return id is { } u && await db.Users.AsNoTracking().AnyAsync(x => x.Id == u && x.InPlan && x.IsActive && x.ShortCode != null, ct);
    }

    private async Task<bool> MaySeeAsync(ClaimsPrincipal user, CancellationToken ct) =>
        user.Has(Area.Availability, Level.View) || await InPlanAsync(user, ct);

    public async Task<AvailabilityPlanItem> PlanAsync(AvailabilityPlanQuery query, ClaimsPrincipal user, CancellationToken ct)
    {
        var today = AvailabilityPlan.Today;
        if (!await MaySeeAsync(user, ct))
        {
            // Not in the plan and no right to see it: an empty plan, so the page can say so.
            return new AvailabilityPlanItem(today, today, today, [], [], [], [], false, false, null);
        }
        var from = query.From ?? today.AddDays(-7);
        var to = query.To ?? from.AddDays(60);
        if (to < from || to.DayNumber - from.DayNumber >= AvailabilityPlan.MaxRange)
        {
            throw ApiException.BadRequest($"From before to, at most {AvailabilityPlan.MaxRange} days.");
        }
        var snapshot = await plan.ReadAsync(from, to, ct);
        var me = user.UserId();
        var columns = snapshot.Settings.CountColumns.Where(c => c.Shown).ToList();
        var fillEnd = today.AddDays(AvailabilityPlan.FillDays - 1);
        var days = new List<AvailabilityDayItem>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var row = snapshot.Row(day);
            var closed = snapshot.Closed(day);
            var cells = snapshot.People.Select(p => snapshot.Cell(p.Id, day)).Where(c => c is not null)
                .Select(c => new AvailabilityCell(c!.UserId, c.TypeId, c.Filled)).ToList();
            var missing = closed || day < today || day > fillEnd ? 0 : snapshot.People.Count(p => snapshot.Cell(p.Id, day) is null);
            days.Add(new AvailabilityDayItem(day, Weekdays[(int)day.DayOfWeek], closed, snapshot.HolidayOn(day)?.Name, snapshot.Hours(day),
                row?.ResponsibleId, row?.FallbackId, row?.Comment,
                row?.ResponsibleId is { } r ? snapshot.Works(r, day) : null, row?.FallbackId is { } f ? snapshot.Works(f, day) : null,
                cells, [.. columns.Select(c => snapshot.Count(c, day))], missing));
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        string? fallback = snapshot.Settings.GlobalFallbackUserId is { } fu
            ? await db.Users.AsNoTracking().Where(u => u.Id == fu).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : snapshot.Settings.GlobalFallbackGroupId is { } fg
                ? await db.Groups.AsNoTracking().Where(g => g.Id == fg).Select(g => g.Name).FirstOrDefaultAsync(ct) : null;
        return new AvailabilityPlanItem(from, to, today, [.. snapshot.Types.Select(ToItem)],
            [.. snapshot.People.Select(p => new AvailabilityPersonItem(p.Id, p.DisplayName, p.ShortCode!, p.Color, p.Id == me))],
            [.. columns.Select(c => new AvailabilityColumnItem(c.Id, c.Name, c.Minimum))], days,
            user.Has(Area.Availability, Level.Edit), snapshot.People.Any(p => p.Id == me), fallback);
    }

    /// <summary>Sets cells: one's own column from today on, anything with <i>Edit</i> (§8.2).</summary>
    public async Task<int> SaveCellsAsync(AvailabilityCellsRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var editAll = user.Has(Area.Availability, Level.Edit);
        var me = user.UserId();
        var today = AvailabilityPlan.Today;
        if (request.Cells is not { Count: > 0 and <= 5000 })
        {
            throw ApiException.BadRequest("1 to 5,000 cells at once.");
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var people = (await AvailabilityPlan.PeopleAsync(db, ct)).ToDictionary(p => p.Id);
        var types = (await plan.TypesAsync(ct)).Where(t => !t.Retired).ToDictionary(t => t.Id);
        foreach (var c in request.Cells)
        {
            if (!people.ContainsKey(c.UserId))
            {
                throw ApiException.BadRequest("Someone who isn't in the plan.");
            }
            if (!editAll && (c.UserId != me || c.Date < today))
            {
                throw ApiException.Forbidden("Your own column, from today on. Everything else needs Availability: Edit.");
            }
            if (c.TypeId is { } t && !types.ContainsKey(t))
            {
                throw ApiException.BadRequest("An availability type that doesn't exist.");
            }
        }
        var dates = request.Cells.Select(c => c.Date).Distinct().ToList();
        var users = request.Cells.Select(c => c.UserId).Distinct().ToList();
        var existing = (await db.AvailabilityEntries.Where(e => users.Contains(e.UserId) && dates.Contains(e.Date)).ToListAsync(ct))
            .ToDictionary(e => (e.UserId, e.Date));
        var now = Clock.UtcNow;
        var by = Name(user);
        foreach (var c in request.Cells.GroupBy(c => (c.UserId, c.Date)).Select(g => g.Last()))
        {
            existing.TryGetValue((c.UserId, c.Date), out var entry);
            if (c.TypeId is not { } typeId)
            {
                if (entry is not null)
                {
                    db.AvailabilityEntries.Remove(entry);
                }
                continue;
            }
            if (entry is null)
            {
                db.AvailabilityEntries.Add(new AvailabilityEntryEntity { UserId = c.UserId, Date = c.Date, TypeId = typeId, SetAt = now, SetBy = by });
            }
            else
            {
                entry.TypeId = typeId;
                entry.Filled = false;
                entry.SetAt = now;
                entry.SetBy = by;
            }
        }
        await db.SaveChangesAsync(ct);
        var codes = request.Cells.Select(c => people[c.UserId].ShortCode).Distinct().Order(StringComparer.Ordinal);
        AuditLog.Summary($"Availability: {request.Cells.Count} cell(s) of {string.Join(", ", codes)}, {Range(dates)}");
        return request.Cells.Count;
    }

    /// <summary>Sets the days' rows: the responsible, the fallback, the comment (<i>Edit</i>).</summary>
    public async Task<int> SaveDaysAsync(AvailabilityDaysRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request.Days is not { Count: > 0 and <= 1000 })
        {
            throw ApiException.BadRequest("1 to 1,000 days at once.");
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var people = (await AvailabilityPlan.PeopleAsync(db, ct)).Select(p => p.Id).ToHashSet();
        var v = new ApiValidator();
        foreach (var d in request.Days)
        {
            var field = $"days.{d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
            v.Check(d.ResponsibleId is null || people.Contains(d.ResponsibleId.Value), field, "The responsible must be in the plan.");
            v.Check(d.FallbackId is null || people.Contains(d.FallbackId.Value), field, "The fallback must be in the plan.");
            v.Check((d.Comment ?? "").Length <= 500, field, "A comment of at most 500 characters.");
        }
        v.ThrowIfInvalid();
        var dates = request.Days.Select(d => d.Date).Distinct().ToList();
        var rows = await db.AvailabilityDays.Where(d => dates.Contains(d.Date)).ToDictionaryAsync(d => d.Date, ct);
        var now = Clock.UtcNow;
        foreach (var d in request.Days.GroupBy(d => d.Date).Select(g => g.Last()))
        {
            var comment = People.Clean(d.Comment, 500);
            if (!rows.TryGetValue(d.Date, out var row))
            {
                if (d.ResponsibleId is null && d.FallbackId is null && comment is null)
                {
                    continue;
                }
                db.AvailabilityDays.Add(row = new AvailabilityDayEntity { Date = d.Date });
            }
            row.ResponsibleId = d.ResponsibleId;
            row.FallbackId = d.FallbackId;
            row.Comment = comment;
            row.ChangedAt = now;
            row.ChangedBy = Name(user);
        }
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"Availability: the responsible, fallback or comment of {Range(dates)}");
        return dates.Count;
    }

    private static string Range(IReadOnlyCollection<DateOnly> dates) => dates.Count == 1
        ? dates.First().ToString("d.M.yyyy", CultureInfo.InvariantCulture)
        : $"{dates.Min().ToString("d.M.yyyy", CultureInfo.InvariantCulture)} to {dates.Max().ToString("d.M.yyyy", CultureInfo.InvariantCulture)}";

    /// <summary><i>Fill</i> or <i>Fill again</i> for the next 365 days (§8.2): never what a person set.</summary>
    public async Task<AvailabilityFillResult> FillAsync(AvailabilityFillRequest request, bool onlyMine, ClaimsPrincipal user, CancellationToken ct)
    {
        if (onlyMine && !await InPlanAsync(user, ct))
        {
            throw ApiException.Conflict("availability_not_in_plan", "You aren't in the plan.");
        }
        var today = AvailabilityPlan.Today;
        var last = today.AddDays(AvailabilityPlan.FillDays - 1);
        var from = request.From ?? today;
        var to = request.To ?? last;
        new ApiValidator()
            .Check(from >= today, "from", "The fill writes the future only: from today on.")
            .Check(to <= last, "to", $"At most {AvailabilityPlan.FillDays} days ahead.")
            .Check(from <= to, "to", "Not before from.")
            .Check(request.OnlyTypes is null || request.OnlyTypes.Count > 0, "onlyTypes", "At least one type, or none for every type.")
            .Check(request.People is null || request.People.Count > 0, "people", "At least one person, or none for everyone.")
            .ThrowIfInvalid();
        var options = new FillOptions(from, to, onlyMine ? null : request.People?.ToHashSet(), request.Again ? request.OnlyTypes?.ToHashSet() : null,
            request.Again && request.OverwriteOwn);
        var result = await FillAsync(onlyMine ? user.UserId() : null, request.Again, Name(user), ct, options);
        AuditLog.Summary($"Availability: {(request.Again ? "Fill again" : "Fill")}{(onlyMine ? " of their own column" : "")}, " +
            $"{Range([from, to])}{(options.People is { } p ? $", {p.Count} people" : "")}{(options.OnlyTypes is { } t ? $", {t.Count} types" : "")}" +
            $"{(options.OverwriteOwn ? ", people's own entries too" : "")}: {result.Written} cell(s) written, {result.Replaced} replaced");
        return result;
    }

    /// <summary>What a fill covers (docs/adjustments1.md): its days, its people, and for <i>Fill again</i> what it may replace.</summary>
    public sealed record FillOptions(DateOnly From, DateOnly To, IReadOnlySet<Guid>? People, IReadOnlySet<Guid>? OnlyTypes, bool OverwriteOwn);

    /// <summary>
    /// Writes every empty working day of the next 365 from the person's default week: the weekday's type (or the default)
    /// on the days they work, the type for days off on the others. <paramref name="again"/> also replaces what the fill
    /// wrote. Closed days stay empty.
    /// </summary>
    public async Task<AvailabilityFillResult> FillAsync(Guid? onlyUser, bool again, string by, CancellationToken ct, FillOptions? options = null)
    {
        var today = options?.From ?? AvailabilityPlan.Today;
        var end = options?.To ?? AvailabilityPlan.Today.AddDays(AvailabilityPlan.FillDays - 1);
        var snapshot = await plan.ReadAsync(today, end, ct);
        if (snapshot.DefaultType is not { } standard)
        {
            return new AvailabilityFillResult(0, 0);
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var people = snapshot.People.Where(p => (onlyUser is null || p.Id == onlyUser) && (options?.People is not { } only || only.Contains(p.Id))).ToList();
        var ids = people.Select(p => p.Id).ToList();
        var own = options?.OverwriteOwn == true;
        var filled = again
            ? await db.AvailabilityEntries.Where(e => ids.Contains(e.UserId) && e.Date >= today && e.Date <= end && (e.Filled || own))
                .ToDictionaryAsync(e => (e.UserId, e.Date), ct)
            : [];
        var types = snapshot.Types.Where(t => !t.Retired).ToDictionary(t => t.Id);
        var now = Clock.UtcNow;
        int written = 0, replaced = 0;
        foreach (var person in people)
        {
            for (var day = today; day <= end; day = day.AddDays(1))
            {
                if (snapshot.Closed(day))
                {
                    continue;
                }
                var cell = snapshot.Cell(person.Id, day);
                if (cell is not null && !(again && (cell.Filled || own) && (options?.OnlyTypes is not { } types1 || (cell.TypeId is { } current && types1.Contains(current)))))
                {
                    continue;
                }
                var usual = snapshot.DefaultDay(person.Id, day);
                var type = usual.Works ? (usual.TypeId is { } t && types.TryGetValue(t, out var chosen) ? chosen : standard) : snapshot.DayOffType;
                if (type is null)
                {
                    continue;
                }
                if (cell is null)
                {
                    db.AvailabilityEntries.Add(new AvailabilityEntryEntity
                    {
                        UserId = person.Id, Date = day, TypeId = type.Id, Filled = true, SetAt = now, SetBy = by,
                    });
                    written++;
                }
                else if (cell.TypeId != type.Id && filled.TryGetValue((person.Id, day), out var row))
                {
                    // A person's own entry that is replaced is the fill's from now on.
                    row.Filled = true;
                    row.TypeId = type.Id;
                    row.SetAt = now;
                    row.SetBy = by;
                    replaced++;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return new AvailabilityFillResult(written, replaced);
    }

    // --- The people (§8.1) and the settings (§9.4) ---------------------------------------------------------------------

    public async Task<IReadOnlyList<AvailabilityPeopleItem>> PeopleAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        return [.. (await db.Users.AsNoTracking().Where(u => u.IsActive).OrderByDescending(u => u.InPlan).ThenBy(u => u.PlanOrder).ThenBy(u => u.DisplayName)
            .ToListAsync(ct)).Select(u => new AvailabilityPeopleItem(u.Id, u.Username, u.DisplayName, u.InPlan, u.ShortCode, u.Color, People.WeekOf(u.DefaultWeek), u.PlanOrder))];
    }

    public async Task<AvailabilityPeopleItem> SavePersonAsync(Guid id, AvailabilityPersonRequest request, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var u = await db.Users.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("User");
        await People.ApplyPlanAsync(db, u, request.InPlan, request.ShortCode, request.Color, request.DefaultWeek, request.PlanOrder, new ApiValidator(), ct);
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"Availability: {u.DisplayName} {(u.InPlan ? $"in the plan as {u.ShortCode}" : "not in the plan")}");
        return new AvailabilityPeopleItem(u.Id, u.Username, u.DisplayName, u.InPlan, u.ShortCode, u.Color, People.WeekOf(u.DefaultWeek), u.PlanOrder);
    }

    public async Task<AvailabilitySettingsItem> SettingsAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).Select(g => new Ref(g.Id, g.Name)).ToListAsync(ct);
        return new([.. (await plan.TypesAsync(ct)).Select(ToItem)], await plan.SettingsAsync(ct), groups);
    }

    public async Task<AvailabilitySettingsItem> SaveSettingsAsync(AvailabilitySettingsRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request.Types is null || request.Settings is null)
        {
            throw ApiException.BadRequest("The types and the settings.");
        }
        var v = new ApiValidator();
        request.Settings.Validate(v);
        v.Check(request.Types.Count is > 0 and <= 40, "types", "1 to 40 types.");
        v.Check(request.Types.Count(t => t.IsDefault) == 1, "types", "Exactly one type is the default.");
        v.Check(request.Types.Count(t => t.IsDayOff) <= 1, "types", "At most one type is for days off.");
        v.Check(request.Types.Select(t => t.Code.Trim().ToUpperInvariant()).Distinct().Count() == request.Types.Count, "types", "Each code once.");
        foreach (var (t, i) in request.Types.Select((t, i) => (t, i)))
        {
            var field = $"types.{i}";
            v.Check(t.Name.Trim().Length is > 0 and <= 60, field, "A name of 1 to 60 characters.");
            v.Check(TypeCode().IsMatch(t.Code.Trim().ToUpperInvariant()), field, "A code of 2 to 4 letters or digits.");
            v.Check(Colours.IsHex(Colours.Normalize(t.Color)), field, Colours.Hint);
        }
        v.ThrowIfInvalid();
        await using var db = await dbs.CreateDbContextAsync(ct);
        var existing = await db.AvailabilityTypes.ToDictionaryAsync(t => t.Id, ct);
        var kept = new HashSet<Guid>();
        foreach (var (input, order) in request.Types.Select((t, i) => (t, i)))
        {
            AvailabilityTypeEntity type;
            if (input.Id is { } id && existing.TryGetValue(id, out var found))
            {
                type = found;
            }
            else
            {
                type = new AvailabilityTypeEntity { Id = Guid.NewGuid() };
                db.AvailabilityTypes.Add(type);
            }
            kept.Add(type.Id);
            type.Name = input.Name.Trim();
            type.Code = input.Code.Trim().ToUpperInvariant();
            type.Color = input.Color.Trim().ToLowerInvariant();
            type.Vacation = input.Flags.Contains(AvailabilityFlag.Vacation);
            type.Holiday = input.Flags.Contains(AvailabilityFlag.Holiday);
            type.OnSite = input.Flags.Contains(AvailabilityFlag.OnSite);
            type.WorkTravel = input.Flags.Contains(AvailabilityFlag.WorkTravel);
            type.HomeOffice = input.Flags.Contains(AvailabilityFlag.HomeOffice);
            type.Compensation = input.Flags.Contains(AvailabilityFlag.Compensation);
            type.IsDefault = input.IsDefault;
            type.IsDayOff = input.IsDayOff;
            type.Order = order;
            type.Retired = false;
        }
        foreach (var gone in existing.Values.Where(t => !kept.Contains(t.Id)))
        {
            gone.IsDefault = false;
            gone.IsDayOff = false;
            // A code taken by a new type frees itself: a retired type keeps its cells, under a code no one types.
            if (await db.AvailabilityEntries.AnyAsync(e => e.TypeId == gone.Id, ct))
            {
                gone.Retired = true;
            }
            else
            {
                db.AvailabilityTypes.Remove(gone);
            }
        }
        var row = await db.AvailabilitySettings.SingleOrDefaultAsync(ct);
        if (row is null)
        {
            db.AvailabilitySettings.Add(row = new AvailabilitySettingsEntity());
        }
        row.Json = JsonSerializer.Serialize(request.Settings, AvailabilitySettings.Json);
        row.ChangedAt = Clock.UtcNow;
        row.ChangedBy = Name(user);
        await db.SaveChangesAsync(ct);
        plan.Changed();
        AuditLog.Summary($"Availability: the settings, {request.Types.Count} type(s), {request.Settings.CountColumns.Count} count column(s)");
        return await SettingsAsync(ct);
    }

    [GeneratedRegex("^[A-Z0-9]{2,4}$")]
    private static partial Regex TypeCode();
}
