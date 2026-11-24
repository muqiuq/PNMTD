using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PNMTD.Auth;
using PNMTD.Cookie;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Availability;

/// <summary>Who stands in at a moment (docs/cookie-plan.md §8.2): the people, and as what.</summary>
public sealed record Responsibility(IReadOnlyList<Guid> UserIds, string As)
{
    public static readonly Responsibility Nobody = new([], "nobody");
}

/// <summary>
/// The plan for a range of days, read once (docs/cookie-plan.md §8.2): the hours, the types, the people in the plan, their
/// cells and the days' rows. It answers who works, who is responsible, and the counts.
/// </summary>
public sealed class PlanSnapshot
{
    private readonly Dictionary<(Guid, DateOnly), AvailabilityEntryEntity> cells;
    private readonly Dictionary<Guid, AvailabilityTypeEntity> typesById;
    private readonly Dictionary<Guid, IReadOnlyList<DefaultDay>> weeks;
    private readonly Dictionary<DateOnly, AvailabilityDayEntity> days;
    private readonly IReadOnlyDictionary<Guid, List<Guid>> members;

    internal PlanSnapshot(AvailabilitySettings settings, IReadOnlyList<AvailabilityTypeEntity> types, IReadOnlyList<UserEntity> people,
        IEnumerable<AvailabilityEntryEntity> entries, IEnumerable<AvailabilityDayEntity> dayRows, IReadOnlyDictionary<Guid, IReadOnlyList<DefaultDay>> otherWeeks,
        IReadOnlyDictionary<Guid, List<Guid>> groupMembers)
    {
        Settings = settings;
        Calendar = settings.Calendar();
        Types = types;
        People = people;
        typesById = types.ToDictionary(t => t.Id);
        cells = entries.ToDictionary(e => (e.UserId, e.Date));
        days = dayRows.ToDictionary(d => d.Date);
        weeks = people.ToDictionary(p => p.Id, p => global::PNMTD.Auth.People.WeekOf(p.DefaultWeek));
        foreach (var (id, week) in otherWeeks)
        {
            weeks.TryAdd(id, week);
        }
        members = groupMembers;
    }

    public AvailabilitySettings Settings { get; }

    public WorkingCalendar Calendar { get; }

    public IReadOnlyList<AvailabilityTypeEntity> Types { get; }

    /// <summary>The people in the plan, in the plan's order.</summary>
    public IReadOnlyList<UserEntity> People { get; }

    public AvailabilityTypeEntity? DefaultType => Types.FirstOrDefault(t => t.IsDefault && !t.Retired);

    public AvailabilityTypeEntity? DayOffType => Types.FirstOrDefault(t => t.IsDayOff && !t.Retired);

    public Holiday? HolidayOn(DateOnly day) => Settings.Holidays.FirstOrDefault(h => h.Date == day);

    /// <summary>A weekend or a closed holiday: greyed, nobody needed (§8.2).</summary>
    public bool Closed(DateOnly day) => !Calendar.AlwaysOpen && Calendar.PeriodsOn(day).Count == 0;

    public string Hours(DateOnly day) => Closed(day) ? "closed"
        : Calendar.AlwaysOpen ? "always"
        : string.Join(", ", Calendar.PeriodsOn(day).Select(p => $"{Clock.ToLocal(p.From):HH:mm}–{(Clock.ToLocal(p.To) is var to && to.TimeOfDay == TimeSpan.Zero ? "24:00" : to.ToString("HH:mm", CultureInfo.InvariantCulture))}"));

    public AvailabilityEntryEntity? Cell(Guid user, DateOnly day) => cells.GetValueOrDefault((user, day));

    public AvailabilityDayEntity? Row(DateOnly day) => days.GetValueOrDefault(day);

    public AvailabilityTypeEntity? Type(Guid id) => typesById.GetValueOrDefault(id);

    /// <summary>The person's default day: whether they usually work that weekday.</summary>
    public DefaultDay DefaultDay(Guid user, DateOnly day) =>
        (weeks.TryGetValue(user, out var week) ? week : global::PNMTD.Auth.People.StandardWeek).First(d => d.Day == day.DayOfWeek);

    /// <summary>
    /// Whether a person works that day (§8.2): their cell's type decides by its checkboxes; without a cell, their default
    /// week. Nobody works on a closed day.
    /// </summary>
    public bool Works(Guid user, DateOnly day)
    {
        if (Closed(day))
        {
            return false;
        }
        if (Cell(user, day) is { } cell)
        {
            return Type(cell.TypeId)?.Works ?? false;
        }
        return DefaultDay(user, day).Works;
    }

    /// <summary>Whether the person's cell that day has the checkbox (the counts and Cookie's staffing read cells only).</summary>
    public bool Has(Guid user, DateOnly day, AvailabilityFlag flag) => Cell(user, day) is { } cell && Type(cell.TypeId)?.Has(flag) == true;

    public int Count(CountColumn column, DateOnly day)
    {
        if (Closed(day))
        {
            return 0;
        }
        var who = column.GroupId is { } g ? People.Where(p => members.GetValueOrDefault(g)?.Contains(p.Id) == true) : People;
        return who.Count(p => column.Flags.Any(f => Has(p.Id, day, f)));
    }

    /// <summary>
    /// Who is responsible on a day (§8.2), whatever the hour: the responsible if they work, else the fallback if they work,
    /// else the global fallback (a user who works, or a group's members who work), else nobody.
    /// </summary>
    public Responsibility ResponsibleOn(DateOnly day)
    {
        if (Closed(day))
        {
            return Responsibility.Nobody;
        }
        var row = Row(day);
        if (row?.ResponsibleId is { } r && Works(r, day))
        {
            return new Responsibility([r], "responsible");
        }
        if (row?.FallbackId is { } f && Works(f, day))
        {
            return new Responsibility([f], "fallback");
        }
        if (Settings.GlobalFallbackUserId is { } u && Works(u, day))
        {
            return new Responsibility([u], "global fallback");
        }
        if (Settings.GlobalFallbackGroupId is { } g)
        {
            var working = (members.GetValueOrDefault(g) ?? []).Where(m => Works(m, day)).ToList();
            if (working.Count > 0)
            {
                return new Responsibility(working, "global fallback");
            }
        }
        return Responsibility.Nobody;
    }

    /// <summary>
    /// The chain on a day (docs/board-messages-plan.md §9.5): the responsible, the fallback, the global fallback user, then the global
    /// fallback group's members, each step with those who work that day, and each person once. Its first step is
    /// <see cref="ResponsibleOn"/>'s; the next ones are the deputies. Empty on a closed day.
    /// </summary>
    public IReadOnlyList<Responsibility> ChainOn(DateOnly day)
    {
        if (Closed(day))
        {
            return [];
        }
        var seen = new HashSet<Guid>();
        var steps = new List<Responsibility>();
        void Add(IEnumerable<Guid> people, string As)
        {
            var working = people.Where(p => Works(p, day) && seen.Add(p)).ToList();
            if (working.Count > 0)
            {
                steps.Add(new Responsibility(working, As));
            }
        }
        var row = Row(day);
        if (row?.ResponsibleId is { } r)
        {
            Add([r], "responsible");
        }
        if (row?.FallbackId is { } f)
        {
            Add([f], "fallback");
        }
        if (Settings.GlobalFallbackUserId is { } u)
        {
            Add([u], "global fallback");
        }
        if (Settings.GlobalFallbackGroupId is { } g)
        {
            Add(members.GetValueOrDefault(g) ?? [], "global fallback");
        }
        return steps;
    }

    /// <summary>Who is responsible at <paramref name="utc"/>: nobody outside working hours (§8.3).</summary>
    public Responsibility ResponsibleAt(DateTime utc) =>
        Calendar.IsWorking(utc) ? ResponsibleOn(DateOnly.FromDateTime(Clock.ToLocal(utc))) : Responsibility.Nobody;
}

/// <summary>
/// The availability plan (docs/cookie-plan.md §8.2, §9.4): reads it for a range of days, and keeps its settings cached for
/// the case book, the duties and Cookie.
/// </summary>
public sealed class AvailabilityPlan(IDbContextFactory<PnmtdDbContext> dbs, IMemoryCache cache)
{
    /// <summary>The plan is kept filled this far ahead (§8.2).</summary>
    public const int FillDays = 365;

    /// <summary>The most days one request reads.</summary>
    public const int MaxRange = 400;

    private const string SettingsKey = "availability-settings";
    private const string TypesKey = "availability-types";

    public static DateOnly Today => DateOnly.FromDateTime(Clock.LocalNow);

    public async Task<AvailabilitySettings> SettingsAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync(SettingsKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            await using var db = await dbs.CreateDbContextAsync(ct);
            var json = await db.AvailabilitySettings.AsNoTracking().Select(s => s.Json).FirstOrDefaultAsync(ct);
            return AvailabilitySettings.Parse(json);
        }))!;

    /// <summary>The working hours and holidays (§7.3): the ticket clocks count them.</summary>
    public async Task<WorkingCalendar> CalendarAsync(CancellationToken ct) => (await SettingsAsync(ct)).Calendar();

    public async Task<IReadOnlyList<AvailabilityTypeEntity>> TypesAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync(TypesKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            await using var db = await dbs.CreateDbContextAsync(ct);
            return (IReadOnlyList<AvailabilityTypeEntity>)await db.AvailabilityTypes.AsNoTracking().OrderBy(t => t.Order).ThenBy(t => t.Code).ToListAsync(ct);
        }))!;

    public void Changed()
    {
        cache.Remove(SettingsKey);
        cache.Remove(TypesKey);
    }

    /// <summary>The plan from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    public async Task<PlanSnapshot> ReadAsync(DateOnly from, DateOnly to, CancellationToken ct, IEnumerable<Guid>? alsoUsers = null)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var settings = await SettingsAsync(ct);
        var types = await TypesAsync(ct);
        var people = await PeopleAsync(db, ct);
        var ids = people.Select(p => p.Id).ToList();
        var entries = await db.AvailabilityEntries.AsNoTracking().Where(e => e.Date >= from && e.Date <= to).ToListAsync(ct);
        var rows = await db.AvailabilityDays.AsNoTracking().Where(d => d.Date >= from && d.Date <= to).ToListAsync(ct);
        var groups = settings.CountColumns.Where(c => c.GroupId is not null).Select(c => c.GroupId!.Value)
            .Concat(settings.GlobalFallbackGroupId is { } g ? [g] : []).Distinct().ToList();
        var members = (await db.UserGroups.AsNoTracking().Where(m => groups.Contains(m.GroupId)).Select(m => new { m.GroupId, m.UserId }).ToListAsync(ct))
            .GroupBy(m => m.GroupId).ToDictionary(x => x.Key, x => x.Select(m => m.UserId).ToList());
        var others = (alsoUsers ?? []).Concat(members.Values.SelectMany(m => m)).Concat(settings.GlobalFallbackUserId is { } u ? [u] : [])
            .Where(id => !ids.Contains(id)).Distinct().ToList();
        var weeks = others.Count == 0 ? new Dictionary<Guid, IReadOnlyList<DefaultDay>>()
            : (await db.Users.AsNoTracking().Where(x => others.Contains(x.Id)).Select(x => new { x.Id, x.DefaultWeek }).ToListAsync(ct))
                .ToDictionary(x => x.Id, x => People.WeekOf(x.DefaultWeek));
        return new PlanSnapshot(settings, types, people, entries, rows, weeks, members);
    }

    /// <summary>One day's plan.</summary>
    public Task<PlanSnapshot> DayAsync(DateOnly day, CancellationToken ct, IEnumerable<Guid>? alsoUsers = null) => ReadAsync(day, day, ct, alsoUsers);

    /// <summary>The active people in the plan, in its order.</summary>
    public static async Task<List<UserEntity>> PeopleAsync(PnmtdDbContext db, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.InPlan && u.IsActive && u.ShortCode != null).OrderBy(u => u.PlanOrder).ThenBy(u => u.ShortCode)
            .ToListAsync(ct);
}
