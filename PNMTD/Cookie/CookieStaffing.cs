using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Ai;
using PNMTD.Availability;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>
/// What Cookie reads from the availability plan (docs/cookie-plan.md §8.4): for each day, who is responsible and as what,
/// who works and where, who is away and why (the checkboxes, never a type's name), the counts below their minimum, and the
/// day's comment as untrusted text. People are placeholders, never their codes: the codes in the comment are replaced too.
/// </summary>
public static class CookieStaffing
{
    public const int MaxDays = 14;

    public static async Task<IReadOnlyList<object>> DescribeAsync(AvailabilityPlan plan, PnmtdDbContext db, DateOnly from, DateOnly to, Pseudonyms p,
        CancellationToken ct)
    {
        var snapshot = await plan.ReadAsync(from, to, ct);
        var everyone = snapshot.People.ToDictionary(u => u.Id);
        var others = snapshot.Settings.GlobalFallbackUserId is { } g && !everyone.ContainsKey(g)
            ? await db.Users.AsNoTracking().Where(u => u.Id == g).ToListAsync(ct) : [];
        foreach (var u in others)
        {
            everyone[u.Id] = u;
        }
        string Person(Guid id) => everyone.TryGetValue(id, out var u) ? p.Mask(u.DisplayName) : "someone outside the plan";
        var days = new List<object>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var row = snapshot.Row(day);
            var closed = snapshot.Closed(day);
            var responsible = snapshot.ResponsibleOn(day);
            var works = new List<object>();
            var away = new List<object>();
            foreach (var person in snapshot.People)
            {
                var type = snapshot.Cell(person.Id, day) is { } cell ? snapshot.Type(cell.TypeId) : null;
                if (snapshot.Works(person.Id, day))
                {
                    var where = type is null ? "working (as usual)"
                        : string.Join(", ", new[] { (type.OnSite, "on site"), (type.WorkTravel, "work travel"), (type.HomeOffice, "home office") }
                            .Where(x => x.Item1).Select(x => x.Item2));
                    works.Add(new { Person = Person(person.Id), Where = where });
                }
                else if (!closed)
                {
                    var why = type is null ? "not working (as usual)"
                        : string.Join(", ", Enum.GetValues<AvailabilityFlag>().Where(type.Has).Where(f => f is AvailabilityFlag.Vacation or AvailabilityFlag.Holiday
                            or AvailabilityFlag.Compensation).Select(Label)) is { Length: > 0 } reasons ? reasons : "not working";
                    away.Add(new { Person = Person(person.Id), Why = why });
                }
            }
            var shortOf = snapshot.Settings.CountColumns.Where(c => c.Shown && c.Minimum is > 0 && !closed)
                .Select(c => (c, n: snapshot.Count(c, day))).Where(x => x.n < x.c.Minimum).Select(x => $"{x.c.Name}: {x.n}, the minimum is {x.c.Minimum}").ToList();
            days.Add(new
            {
                Date = day.ToString("yyyy-MM-dd ddd", CultureInfo.InvariantCulture),
                Closed = closed,
                Hours = snapshot.Hours(day),
                Holiday = snapshot.HolidayOn(day)?.Name,
                Responsible = responsible.UserIds.Count == 0 ? (closed ? null : "nobody who works")
                    : $"{string.Join(", ", responsible.UserIds.Select(Person))} (as {responsible.As})",
                PlannedResponsible = row?.ResponsibleId is { } r ? Person(r) : null,
                PlannedFallback = row?.FallbackId is { } f ? Person(f) : null,
                Works = works,
                Away = away,
                ShortOf = shortOf,
                Comment = string.IsNullOrWhiteSpace(row?.Comment) ? null
                    : $"<untrusted source=\"plan comment\">{p.Mask(Codes(InputSanitizer.Clean(row.Comment, 500).Text, snapshot.People, p))}</untrusted>",
            });
        }
        return days;
    }

    /// <summary>The people's short codes in a text, as their placeholders.</summary>
    private static string Codes(string text, IReadOnlyList<UserEntity> people, Pseudonyms p)
    {
        foreach (var person in people.Where(u => u.ShortCode is { Length: 3 }))
        {
            text = Regex.Replace(text, $@"\b{Regex.Escape(person.ShortCode!)}\b", p.Mask(person.DisplayName), RegexOptions.None, TimeSpan.FromMilliseconds(200));
        }
        return text;
    }

    private static string Label(AvailabilityFlag flag) => flag switch
    {
        AvailabilityFlag.Vacation => "vacation",
        AvailabilityFlag.Holiday => "holiday",
        AvailabilityFlag.Compensation => "compensation",
        AvailabilityFlag.OnSite => "on site",
        AvailabilityFlag.WorkTravel => "work travel",
        _ => "home office",
    };

    /// <summary>Today's staffing for the brief.</summary>
    public static async Task<object?> TodayAsync(AvailabilityPlan plan, PnmtdDbContext db, Pseudonyms p, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var days = await DescribeAsync(plan, db, today, today, p, ct);
        return days.Count == 1 ? days[0] : null;
    }
}
