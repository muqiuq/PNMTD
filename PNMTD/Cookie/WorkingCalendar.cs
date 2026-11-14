using System.Globalization;
using PNMTD.Lib.Helper;

namespace PNMTD.Cookie;

/// <summary>A from–to period of one day, in PNMT's time zone: "08:00" to "17:00".</summary>
public sealed record HoursPeriod(string From, string To)
{
    public bool TryParse(out TimeOnly from, out TimeOnly to)
    {
        var ok = TimeOnly.TryParseExact(From, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out from);
        ok &= To is "24:00"
            ? (to = TimeOnly.MaxValue) == TimeOnly.MaxValue
            : TimeOnly.TryParseExact(To, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out to);
        return ok && from < to;
    }
}

/// <summary>A weekday's hours: none is closed.</summary>
public sealed record DayHours(DayOfWeek Day, IReadOnlyList<HoursPeriod> Periods);

/// <summary>A holiday: its hours replace the weekday's; none is closed (docs/cookie-plan.md §7.3).</summary>
public sealed record Holiday(DateOnly Date, string Name, IReadOnlyList<HoursPeriod> Periods);

/// <summary>
/// Working hours and holidays (docs/cookie-plan.md §7.3), in PNMT's time zone (<see cref="Clock.TimeZone"/>). Every
/// instant in and out is UTC. With no working hours at all, every hour counts, so that no deadline is lost.
/// </summary>
public sealed class WorkingCalendar
{
    /// <summary>How far ahead a deadline is looked for: a year of closed days ends the search.</summary>
    private const int MaxDays = 400;

    private readonly Dictionary<DayOfWeek, List<(TimeOnly From, TimeOnly To)>> week = [];
    private readonly Dictionary<DateOnly, List<(TimeOnly From, TimeOnly To)>> holidays = [];

    public WorkingCalendar(IEnumerable<DayHours> days, IEnumerable<Holiday> holidayList)
    {
        foreach (var day in days)
        {
            week[day.Day] = Parse(day.Periods);
        }
        foreach (var holiday in holidayList)
        {
            holidays[holiday.Date] = Parse(holiday.Periods);
        }
        AlwaysOpen = week.Values.All(p => p.Count == 0) && holidays.Values.All(p => p.Count == 0);
    }

    /// <summary>No hours set anywhere: every hour is a working hour.</summary>
    public bool AlwaysOpen { get; }

    /// <summary>Monday to Friday, 08:00 to 17:00.</summary>
    public static IReadOnlyList<DayHours> DefaultWeek { get; } =
    [
        .. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            .Select(d => new DayHours(d, [new HoursPeriod("08:00", "17:00")])),
        new DayHours(DayOfWeek.Saturday, []),
        new DayHours(DayOfWeek.Sunday, []),
    ];

    /// <summary>A day's periods as local wall-clock times.</summary>
    public IReadOnlyList<(DateTime From, DateTime To)> PeriodsOn(DateOnly day)
    {
        if (AlwaysOpen)
        {
            return [(day.ToDateTime(TimeOnly.MinValue), day.AddDays(1).ToDateTime(TimeOnly.MinValue))];
        }
        var periods = holidays.TryGetValue(day, out var special) ? special : week.GetValueOrDefault(day.DayOfWeek) ?? [];
        return periods.Select(p => (day.ToDateTime(p.From), p.To == TimeOnly.MaxValue ? day.AddDays(1).ToDateTime(TimeOnly.MinValue) : day.ToDateTime(p.To)))
            .ToList();
    }

    public bool IsWorking(DateTime utc)
    {
        var local = Clock.ToLocal(utc);
        return PeriodsOn(DateOnly.FromDateTime(local)).Any(p => local >= p.From && local < p.To);
    }

    /// <summary><paramref name="utc"/> itself when it is a working hour, else the start of the next working period.</summary>
    public DateTime NextWorkingStart(DateTime utc)
    {
        var local = Clock.ToLocal(utc);
        var day = DateOnly.FromDateTime(local);
        for (var i = 0; i < MaxDays; i++, day = day.AddDays(1))
        {
            foreach (var (from, to) in PeriodsOn(day))
            {
                if (local < to)
                {
                    return local >= from ? Clock.AsUtc(utc) : Clock.ToUtc(from);
                }
            }
        }
        return Clock.AsUtc(utc);
    }

    /// <summary>
    /// The instant <paramref name="duration"/> of working time after <paramref name="startUtc"/>: Friday 16:00 plus 4
    /// working hours, with hours of 08:00 to 17:00, is Monday 11:00.
    /// </summary>
    public DateTime AddWorkingTime(DateTime startUtc, TimeSpan duration)
    {
        var left = duration;
        var local = Clock.ToLocal(startUtc);
        var day = DateOnly.FromDateTime(local);
        for (var i = 0; i < MaxDays; i++, day = day.AddDays(1))
        {
            foreach (var (from, to) in PeriodsOn(day))
            {
                if (to <= local)
                {
                    continue;
                }
                var begin = local > from ? local : from;
                var available = to - begin;
                if (available >= left)
                {
                    return Clock.ToUtc(begin + left);
                }
                left -= available;
            }
        }
        return Clock.AsUtc(startUtc) + duration;
    }

    /// <summary>The working time between two instants; zero when <paramref name="toUtc"/> is not after <paramref name="fromUtc"/>.</summary>
    public TimeSpan WorkingTimeBetween(DateTime fromUtc, DateTime toUtc)
    {
        var from = Clock.ToLocal(fromUtc);
        var to = Clock.ToLocal(toUtc);
        if (to <= from)
        {
            return TimeSpan.Zero;
        }
        var total = TimeSpan.Zero;
        for (var day = DateOnly.FromDateTime(from); day <= DateOnly.FromDateTime(to); day = day.AddDays(1))
        {
            foreach (var (start, end) in PeriodsOn(day))
            {
                var a = start > from ? start : from;
                var b = end < to ? end : to;
                if (b > a)
                {
                    total += b - a;
                }
            }
        }
        return total;
    }

    private static List<(TimeOnly From, TimeOnly To)> Parse(IEnumerable<HoursPeriod> periods) =>
        periods.Select(p => p.TryParse(out var from, out var to) ? (from, to) : ((TimeOnly, TimeOnly)?)null)
            .Where(p => p is not null).Select(p => p!.Value).OrderBy(p => p.Item1).ToList();
}
