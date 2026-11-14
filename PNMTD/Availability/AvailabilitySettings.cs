using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PNMTD.Api;
using PNMTD.Cookie;
using PNMTD.Models.Db;

namespace PNMTD.Availability;

/// <summary>
/// A count column of the plan (docs/cookie-plan.md §8.2): the people of a day whose type has any of <see cref="Flags"/>,
/// everyone or a group's members, red below <see cref="Minimum"/>.
/// </summary>
public sealed record CountColumn(Guid Id, string Name, List<AvailabilityFlag> Flags, Guid? GroupId = null, int? Minimum = null, bool Shown = true);

/// <summary>
/// The plan's settings besides the types (docs/cookie-plan.md §8.2, §9.4): the working hours for each weekday and the
/// holidays (§7.3, the ticket clocks count them), the count columns, and the global fallback.
/// </summary>
public sealed record AvailabilitySettings
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public List<DayHours> Week { get; init; } = [.. WorkingCalendar.DefaultWeek];

    public List<Holiday> Holidays { get; init; } = [];

    public List<CountColumn> CountColumns { get; init; } =
    [
        new(new Guid("6f1c5a40-0000-4000-8000-000000000001"), "On site", [AvailabilityFlag.OnSite], Minimum: 1),
        new(new Guid("6f1c5a40-0000-4000-8000-000000000002"), "Remote", [AvailabilityFlag.HomeOffice, AvailabilityFlag.WorkTravel]),
    ];

    /// <summary>Who stands in when the day's responsible and fallback don't work: a user …</summary>
    public Guid? GlobalFallbackUserId { get; init; }

    /// <summary>… or a group, whose members who work that day are reached.</summary>
    public Guid? GlobalFallbackGroupId { get; init; }

    public WorkingCalendar Calendar() => new(Week, Holidays);

    public static AvailabilitySettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AvailabilitySettings();
        }
        try
        {
            return JsonSerializer.Deserialize<AvailabilitySettings>(json, Json) ?? new AvailabilitySettings();
        }
        catch (JsonException)
        {
            return new AvailabilitySettings();
        }
    }

    public void Validate(ApiValidator v)
    {
        v.Check(Week.Select(d => d.Day).Distinct().Count() == Week.Count, "week", "Each day once.");
        foreach (var day in Week)
        {
            Periods(v, $"week.{day.Day}", day.Periods);
        }
        v.Check(Holidays.Select(h => h.Date).Distinct().Count() == Holidays.Count, "holidays", "Each date once.");
        v.Check(Holidays.Count <= 500, "holidays", "At most 500.");
        foreach (var holiday in Holidays)
        {
            var field = $"holidays.{holiday.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
            v.Check(holiday.Name.Length <= 100, field, "At most 100 characters.");
            Periods(v, field, holiday.Periods);
        }
        v.Check(CountColumns.Count <= 12, "countColumns", "At most 12.");
        v.Check(CountColumns.Select(c => c.Id).Distinct().Count() == CountColumns.Count, "countColumns", "Each column once.");
        foreach (var c in CountColumns)
        {
            var field = $"countColumns.{c.Id}";
            v.Check(c.Name.Trim().Length is > 0 and <= 40, field, "A name of 1 to 40 characters.");
            v.Check(c.Flags.Count > 0, field, "Choose at least one checkbox to count.");
            v.Check(c.Minimum is null or >= 0 and <= 1000, field, "A minimum of 0 to 1,000.");
        }
        v.Check(GlobalFallbackUserId is null || GlobalFallbackGroupId is null, "globalFallback", "A user or a group, not both.");
    }

    private static void Periods(ApiValidator v, string field, IReadOnlyList<HoursPeriod> periods)
    {
        v.Check(periods.Count <= 6, field, "At most 6 periods a day.");
        var parsed = new List<(TimeOnly From, TimeOnly To)>();
        foreach (var p in periods)
        {
            if (!p.TryParse(out var from, out var to))
            {
                v.Add(field, $"{p.From}–{p.To}: from and to as HH:mm, from before to.");
                continue;
            }
            parsed.Add((from, to));
        }
        var ordered = parsed.OrderBy(p => p.From).ToList();
        v.Check(ordered.Zip(ordered.Skip(1)).All(pair => pair.First.To <= pair.Second.From), field, "Periods may not overlap.");
    }
}
