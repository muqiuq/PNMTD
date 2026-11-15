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
    int Missing);

/// <summary>
/// <c>GET /availability/plan</c>: the days from <see cref="From"/> to <see cref="To"/>, with the types, the people and the
/// count columns; whether the reader may edit everything, and whether they are in the plan (their own column).
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
    string? GlobalFallback);

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
public sealed record AvailabilitySettingsItem(IReadOnlyList<AvailabilityTypeItem> Types, AvailabilitySettings Settings, IReadOnlyList<Ref>? Groups = null);

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
    int PlanOrder);

/// <summary><c>PUT /availability/people/{id}</c>: a person's place in the plan.</summary>
public sealed record AvailabilityPersonRequest(bool InPlan, string? ShortCode, string? Color, IReadOnlyList<DefaultDay>? DefaultWeek, int PlanOrder);

/// <summary><c>GET /account/contact</c>: one's own contact details, and how the plan shows one (set by others).</summary>
public sealed record AccountContactItem(string? Email, string? PushoverKey, string? RedmineName, bool InPlan, string? ShortCode, string? Color);

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
    int PlanOrder);
