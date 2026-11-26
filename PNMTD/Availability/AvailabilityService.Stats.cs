using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Availability;

/// <summary>
/// The Status tab's numbers (docs/availability-extensions-plan.md §9, §11), from one read of the plan. Only open days count; the
/// people are those in the plan now; a day as responsible is the day as planned, whether the person worked or not (decision 5).
/// Done is up to and including today, in PNMT's zone.
/// </summary>
public sealed partial class AvailabilityService
{
    /// <summary>The days ahead the tiles look at (§9.5).</summary>
    public const int AheadDays = 30;

    /// <summary>The days ahead the list of who is away covers (§9.5).</summary>
    public const int AwayDays = 14;

    private sealed class Tally
    {
        public int Done;
        public int Planned;

        public void Add(bool done)
        {
            if (done)
            {
                Done++;
            }
            else
            {
                Planned++;
            }
        }

        public AvailabilityTally Item() => new(Done, Planned);
    }

    private sealed class MonthTally
    {
        public readonly Dictionary<Guid, Tally> Types = [];
        public readonly Tally Empty = new();
        public readonly Tally Responsible = new();
        public readonly Tally Fallback = new();
    }

    public async Task<AvailabilityStatsItem> StatsAsync(AvailabilityStatsQuery query, ClaimsPrincipal user, CancellationToken ct)
    {
        var today = AvailabilityPlan.Today;
        var year = query.Year ?? today.Year;
        if (year < 2000 || year > today.Year + 1)
        {
            throw ApiException.BadRequest($"A year from 2000 to {today.Year + 1}.");
        }
        if (!await MaySeeAsync(user, ct))
        {
            return new AvailabilityStatsItem(year, year, today, Clock.TimeZone.Id, AvailabilitySettings.DefaultResponsibleLabel, [], [], [], [], [], [], null);
        }
        var first = new DateOnly(year, 1, 1);
        var last = new DateOnly(year, 12, 31);
        var aheadEnd = today.AddDays(AheadDays - 1);
        var from = first < today.AddDays(-364) ? first : today.AddDays(-364);
        var to = last > aheadEnd ? last : aheadEnd;
        var snapshot = await plan.ReadAsync(from, to, ct);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var earliest = await db.AvailabilityEntries.AsNoTracking().MinAsync(e => (DateOnly?)e.Date, ct);
        var people = snapshot.People;
        var ids = people.Select(p => p.Id).ToHashSet();
        var weights = people.Sum(p => Math.Max(0, p.ResponsibleShare));
        var targets = people.ToDictionary(p => p.Id, p => weights > 0 ? Math.Max(0, p.ResponsibleShare) / (double)weights : 0);
        var columns = snapshot.Settings.CountColumns.Where(c => c.Shown).ToList();

        // The year, month by month, and its open days.
        var months = people.ToDictionary(p => p.Id, _ => Enumerable.Range(0, 12).Select(_ => new MonthTally()).ToArray());
        var days = new List<AvailabilityStatsDay>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (snapshot.Closed(day))
            {
                continue;
            }
            var done = day <= today;
            var row = snapshot.Row(day);
            foreach (var p in people)
            {
                var month = months[p.Id][day.Month - 1];
                if (snapshot.Cell(p.Id, day) is { } cell)
                {
                    if (!month.Types.TryGetValue(cell.TypeId, out var tally))
                    {
                        month.Types[cell.TypeId] = tally = new Tally();
                    }
                    tally.Add(done);
                }
                else
                {
                    month.Empty.Add(done);
                }
                if (row?.ResponsibleId == p.Id)
                {
                    month.Responsible.Add(done);
                }
                if (row?.FallbackId == p.Id)
                {
                    month.Fallback.Add(done);
                }
            }
            days.Add(new AvailabilityStatsDay(day, [.. columns.Select(c => snapshot.Count(c, day))], row?.ResponsibleId));
        }
        var monthItems = people.SelectMany(p => months[p.Id].Select((m, i) => new AvailabilityStatsMonth(p.Id, i + 1,
            [.. m.Types.Select(t => new AvailabilityTypeTally(t.Key, t.Value.Done, t.Value.Planned))], m.Empty.Item(), m.Responsible.Item(), m.Fallback.Item())))
            .ToList();

        AvailabilityShareWindow Window(string name, DateOnly start, DateOnly end)
        {
            var counted = new Dictionary<Guid, int>();
            int open = 0, others = 0, without = 0;
            for (var day = start; day <= end; day = day.AddDays(1))
            {
                if (snapshot.Closed(day))
                {
                    continue;
                }
                open++;
                if (snapshot.Row(day)?.ResponsibleId is not { } r)
                {
                    without++;
                }
                else if (ids.Contains(r))
                {
                    counted[r] = counted.GetValueOrDefault(r) + 1;
                }
                else
                {
                    others++;
                }
            }
            var total = counted.Values.Sum();
            var rows = people.Select(p =>
            {
                var d = counted.GetValueOrDefault(p.Id);
                var target = targets[p.Id];
                var expected = target * total;
                return new AvailabilityShareRow(p.Id, d, total > 0 ? Math.Round(d / (double)total, 4) : 0, Math.Round(target, 4), Math.Round(expected, 2),
                    Math.Round(d - expected, 2));
            }).ToList();
            return new AvailabilityShareWindow(name, start, end, open, total, others, without, rows);
        }
        var windows = new List<AvailabilityShareWindow>
        {
            Window("yearToDate", first, last < today ? last : today),
            Window("year", first, last),
            Window("last365", today.AddDays(-364), today),
            Window("last30", today.AddDays(-29), today),
        };

        // The next 30 days (§9.5), as the plan's warnings have them.
        int withoutResponsible = 0, below = 0, empty = 0;
        DateOnly? firstWithout = null, firstBelow = null, firstEmpty = null;
        var away = new List<AvailabilityAwayDay>();
        for (var day = today; day <= aheadEnd; day = day.AddDays(1))
        {
            if (snapshot.Closed(day))
            {
                continue;
            }
            if (snapshot.Row(day)?.ResponsibleId is not { } r || !snapshot.Works(r, day))
            {
                withoutResponsible++;
                firstWithout ??= day;
            }
            if (columns.Any(c => c.Minimum is { } m && snapshot.Count(c, day) < m))
            {
                below++;
                firstBelow ??= day;
            }
            var missing = people.Count(p => snapshot.Cell(p.Id, day) is null);
            if (missing > 0)
            {
                empty += missing;
                firstEmpty ??= day;
            }
            if (day.DayNumber - today.DayNumber < AwayDays)
            {
                away.AddRange(people.Select(p => (p, cell: snapshot.Cell(p.Id, day)))
                    .Where(x => x.cell is not null && snapshot.Type(x.cell.TypeId) is { } t && Away(t))
                    .Select(x => new AvailabilityAwayDay(x.p.Id, day, x.cell!.TypeId)));
            }
        }

        return new AvailabilityStatsItem(year, Math.Min(earliest?.Year ?? today.Year, today.Year), today, Clock.TimeZone.Id, snapshot.Settings.LabelOrDefault(),
            [.. snapshot.Types.Select(ToItem)], [.. columns.Select(c => new AvailabilityColumnItem(c.Id, c.Name, c.Minimum))],
            [.. people.Select(p => new AvailabilityStatsPerson(p.Id, p.DisplayName, p.ShortCode!, p.Color, p.ResponsibleShare, Math.Round(targets[p.Id], 4)))],
            monthItems, windows, days,
            new AvailabilityStatsAhead(today, aheadEnd, withoutResponsible, firstWithout, below, firstBelow, empty, firstEmpty, away));
    }

    /// <summary>Away for the list of the next days: vacation, a holiday or compensation, and not working.</summary>
    private static bool Away(AvailabilityTypeEntity t) => !t.Works && (t.Vacation || t.Holiday || t.Compensation);
}
