using PNMTD.Auth;
using PNMTD.Availability;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

/// <summary>An availability type (docs/cookie-plan.md §8.2): its code, colour and checkboxes; <see cref="Works"/> as code reads them.</summary>
public sealed record AvailabilityTypeItem(
    Guid Id,
    string Name,
    string Code,
    string Color,
    IReadOnlyList<AvailabilityFlag> Flags,
    bool IsDefault,
    bool IsDayOff,
    int Order,
    bool Retired,
    bool Works);

/// <summary>A person's column: their short code and colour.</summary>
public sealed record AvailabilityPersonItem(Guid Id, string Name, string Code, string? Color, bool IsMe);

/// <summary>A count column as shown.</summary>
public sealed record AvailabilityColumnItem(Guid Id, string Name, int? Minimum);

/// <summary>A person's cell on a day; <see cref="Filled"/> when the fill wrote it.</summary>
public sealed record AvailabilityCell(Guid UserId, Guid TypeId, bool Filled);

/// <summary>
/// A day of the plan (§8.2): its hours or closed, the responsible and the fallback (and whether they work that day), the
/// comment, everyone's cells, the counts in the order of the columns, and how many cells are empty on a working day.
/// </summary>
public sealed record AvailabilityDayItem(
    DateOnly Date,
    string Weekday,
    bool Closed,
    string? Holiday,
    string Hours,
    Guid? ResponsibleId,
    Guid? FallbackId,
    string? Comment,
    bool? ResponsibleWorks,
    bool? FallbackWorks,
    IReadOnlyList<AvailabilityCell> Cells,
    IReadOnlyList<int> Counts,
    int Missing,
    IReadOnlyList<AvailabilityNoteItem> Notes);

/// <summary>
/// A note on a day (docs/availability-extensions-plan.md §6): its author's current name (the name when written once the user is
/// gone), and whether it is the reader's own.
/// </summary>
public sealed record AvailabilityNoteItem(Guid Id, DateOnly Date, Guid UserId, string Author, string Text, DateTime CreatedAt, DateTime ChangedAt, bool Mine);

/// <summary><c>POST /availability/notes</c>: a note on a day, one line of 1 to 300 characters.</summary>
public sealed record AvailabilityNoteRequest(DateOnly Date, string Text);

/// <summary><c>PUT /availability/notes/{id}</c>: one's own note's new text.</summary>
public sealed record AvailabilityNoteChange(string Text);

/// <summary>
/// <c>GET /availability/plan</c>: the days from <see cref="From"/> to <see cref="To"/>, with the types, the people and the
/// count columns; whether the reader may edit everything, and whether they are in the plan (their own column). The dates are
/// in <see cref="TimeZone"/>, PNMT's (docs/availability-extensions-plan.md §3.3); <see cref="ResponsibleLabel"/> is what the
/// pages call the responsible (§7).
/// </summary>
public sealed record AvailabilityPlanItem(
    DateOnly From,
    DateOnly To,
    DateOnly Today,
    IReadOnlyList<AvailabilityTypeItem> Types,
    IReadOnlyList<AvailabilityPersonItem> People,
    IReadOnlyList<AvailabilityColumnItem> Columns,
    IReadOnlyList<AvailabilityDayItem> Days,
    bool CanEditAll,
    bool InPlan,
    string? GlobalFallback,
    string TimeZone,
    string ResponsibleLabel);

public sealed class AvailabilityPlanQuery
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }
}

/// <summary>A cell to set; no type empties it.</summary>
public sealed record AvailabilityCellChange(Guid UserId, DateOnly Date, Guid? TypeId);

/// <summary><c>PUT /availability/entries</c>: cells of several people and days at once.</summary>
public sealed record AvailabilityCellsRequest(IReadOnlyList<AvailabilityCellChange> Cells);

/// <summary>A day's row: the responsible, the fallback and the comment, as a whole.</summary>
public sealed record AvailabilityDayChange(DateOnly Date, Guid? ResponsibleId, Guid? FallbackId, string? Comment);

/// <summary><c>PUT /availability/days</c>: rows of several days at once.</summary>
public sealed record AvailabilityDaysRequest(IReadOnlyList<AvailabilityDayChange> Days);

/// <summary>
/// <c>POST /availability/fill</c> (everyone) or <c>…/fill/mine</c>: <i>Fill</i>, or <i>Fill again</i> (also replaces what the
/// fill wrote). The future only (docs/adjustments1.md): <see cref="From"/> and <see cref="To"/> within today and 364 days on,
/// the whole year when left out. <see cref="People"/>: only these columns. For <i>Fill again</i>: <see cref="OnlyTypes"/>
/// replaces only cells of these types, <see cref="OverwriteOwn"/> what people set themselves too. Empty cells are always filled.
/// </summary>
public sealed record AvailabilityFillRequest(bool Again, DateOnly? From = null, DateOnly? To = null, IReadOnlyList<Guid>? People = null,
    IReadOnlyList<Guid>? OnlyTypes = null, bool OverwriteOwn = false);

public sealed record AvailabilityFillResult(int Written, int Replaced);

/// <summary>A type as saved: without an id, a new one.</summary>
public sealed record AvailabilityTypeInput(Guid? Id, string Name, string Code, string Color, IReadOnlyList<AvailabilityFlag> Flags, bool IsDefault, bool IsDayOff);

/// <summary>The plan's settings (§9.4): the types (retired ones too), the rest, and the groups to pick from (count columns, the global fallback).</summary>
public sealed record AvailabilitySettingsItem(IReadOnlyList<AvailabilityTypeItem> Types, AvailabilitySettings Settings, IReadOnlyList<Ref>? Groups = null,
    string? TimeZone = null);

/// <summary>
/// <c>PUT /availability/settings</c>: the types in their order (a type left out is retired when it is in use, deleted
/// otherwise), and the rest.
/// </summary>
public sealed record AvailabilitySettingsRequest(IReadOnlyList<AvailabilityTypeInput> Types, AvailabilitySettings Settings);

/// <summary>A user with their place in the plan (§8.1), for the plan's <i>People</i> tab.</summary>
public sealed record AvailabilityPeopleItem(
    Guid Id,
    string Username,
    string DisplayName,
    bool InPlan,
    string? ShortCode,
    string? Color,
    IReadOnlyList<DefaultDay> DefaultWeek,
    int PlanOrder,
    int ResponsibleShare);

/// <summary>
/// <c>PUT /availability/people/{id}</c>: a person's place in the plan. <see cref="ResponsibleShare"/> (0 to 500,
/// docs/availability-extensions-plan.md §8) is left as it is when null.
/// </summary>
public sealed record AvailabilityPersonRequest(bool InPlan, string? ShortCode, string? Color, IReadOnlyList<DefaultDay>? DefaultWeek, int PlanOrder,
    int? ResponsibleShare = null);

/// <summary>
/// <c>GET /account/contact</c>: one's own contact details, and how the plan shows one (set by others). <see cref="RedmineUserId"/>:
/// linked to that Redmine user (docs/cookie-redmine-tab-plan.md §4.4); null, found by the name, if at all.
/// </summary>
public sealed record AccountContactItem(string? Email, string? PushoverKey, string? RedmineName, bool InPlan, string? ShortCode, string? Color,
    int? RedmineUserId = null);

/// <summary><c>PUT /account/contact</c>: one's own e-mail address and Pushover key.</summary>
public sealed record AccountContactRequest(string? Email, string? PushoverKey);

/// <summary><c>POST /account/contact/test</c>: <c>email</c> or <c>pushover</c>.</summary>
public sealed record ContactTestRequest(string Channel);

public sealed record ContactTestResult(bool Ok, string Detail);

/// <summary><c>PUT /users/{id}/profile</c> (admins): the person's contact details and their place in the plan (§8.1).</summary>
public sealed record UserProfileRequest(
    string? Email,
    string? PushoverKey,
    string? RedmineName,
    bool InPlan,
    string? ShortCode,
    string? Color,
    IReadOnlyList<DefaultDay>? DefaultWeek,
    int PlanOrder,
    IReadOnlyList<string>? ServerAccounts = null,
    int? ResponsibleShare = null);

// --- The Status tab (docs/availability-extensions-plan.md §9 to §11) --------------------------------------------------------

/// <summary>Days up to and including today, and days after it.</summary>
public sealed record AvailabilityTally(int Done, int Planned);

/// <summary>A type's days in a month.</summary>
public sealed record AvailabilityTypeTally(Guid TypeId, int Done, int Planned);

/// <summary>A person in the stats: their share as responsible (a weight) and the target it gives (0 to 1 of everyone's days).</summary>
public sealed record AvailabilityStatsPerson(Guid Id, string Name, string Code, string? Color, int Share, double Target);

/// <summary>
/// A person's month (§9.2): the open days per type of their cell, the open days without a cell, and the days the plan names them
/// as the responsible and as the fallback.
/// </summary>
public sealed record AvailabilityStatsMonth(Guid UserId, int Month, IReadOnlyList<AvailabilityTypeTally> Types, AvailabilityTally Empty,
    AvailabilityTally Responsible, AvailabilityTally Fallback);

/// <summary>A person's days as responsible in a window (§9.3), against their share.</summary>
public sealed record AvailabilityShareRow(Guid UserId, int Days, double Actual, double Target, double Expected, double Deviation);

/// <summary>
/// A window of days as responsible (§9.3): <c>yearToDate</c>, <c>year</c>, <c>last365</c> or <c>last30</c>. <see cref="Total"/> is the
/// days of the people in the plan; <see cref="Others"/> those of people no longer in it; <see cref="WithoutResponsible"/> the open
/// days that name nobody. Empty (from after to) for a year still to come.
/// </summary>
public sealed record AvailabilityShareWindow(string Window, DateOnly From, DateOnly To, int OpenDays, int Total, int Others, int WithoutResponsible,
    IReadOnlyList<AvailabilityShareRow> People);

/// <summary>An open day of the year (§9.4): the counts in the order of the columns, and the planned responsible.</summary>
public sealed record AvailabilityStatsDay(DateOnly Date, IReadOnlyList<int> Counts, Guid? ResponsibleId);

/// <summary>A day someone is away in the next 14 days (§9.5): vacation, holiday or compensation.</summary>
public sealed record AvailabilityAwayDay(Guid UserId, DateOnly Date, Guid TypeId);

/// <summary>
/// The next 30 days from today (§9.5): open days with the responsible's warning (nobody, or someone who doesn't work), days below a
/// count column's minimum, empty cells that should be set, each with its first day; and who is away in the next 14 days.
/// </summary>
public sealed record AvailabilityStatsAhead(DateOnly From, DateOnly To, int WithoutResponsible, DateOnly? FirstWithoutResponsible, int BelowMinimum,
    DateOnly? FirstBelowMinimum, int EmptyCells, DateOnly? FirstEmpty, IReadOnlyList<AvailabilityAwayDay> Away);

/// <summary>
/// <c>GET /availability/stats?year=</c> (§11): what the browser can't work out itself. It sums months into the year and types into
/// their meanings. <see cref="FirstYear"/>: the first year with cells, for the year to choose.
/// </summary>
public sealed record AvailabilityStatsItem(
    int Year,
    int FirstYear,
    DateOnly Today,
    string TimeZone,
    string ResponsibleLabel,
    IReadOnlyList<AvailabilityTypeItem> Types,
    IReadOnlyList<AvailabilityColumnItem> Columns,
    IReadOnlyList<AvailabilityStatsPerson> People,
    IReadOnlyList<AvailabilityStatsMonth> Months,
    IReadOnlyList<AvailabilityShareWindow> Windows,
    IReadOnlyList<AvailabilityStatsDay> Days,
    AvailabilityStatsAhead? Ahead);

public sealed class AvailabilityStatsQuery
{
    public int? Year { get; init; }
}
