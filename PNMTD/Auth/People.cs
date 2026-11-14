using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Notifications;

namespace PNMTD.Auth;

/// <summary>One weekday of a person's default week (docs/cookie-plan.md §8.1): whether they usually work, and with which type.</summary>
public sealed record DefaultDay(DayOfWeek Day, bool Works, Guid? TypeId = null);

/// <summary>A person's contact details and their place in the availability plan (docs/cookie-plan.md §8.1).</summary>
public sealed record PersonProfile(
    string? Email,
    string? PushoverKey,
    string? RedmineName,
    bool InPlan,
    string? ShortCode,
    string? Color,
    IReadOnlyList<DefaultDay> DefaultWeek,
    int PlanOrder);

/// <summary>
/// What belongs to a person (docs/cookie-plan.md §8.1, revision 7): the e-mail address, the Pushover key and the Redmine user,
/// and for the availability plan a short code, a colour and a default week. Each user sets their own address and key;
/// admins set everything; <i>Availability: Manage</i> sets the plan's fields.
/// </summary>
public static partial class People
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Every day, with the default type: the working hours decide which days are closed (a weekend, a holiday), so someone
    /// without a week of their own works whenever PNMT's hours are open.
    /// </summary>
    public static IReadOnlyList<DefaultDay> StandardWeek { get; } =
    [
        .. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new DefaultDay(d, true)),
    ];

    /// <summary>The stored default week, each weekday once, Monday first; the standard week where nothing is stored.</summary>
    public static IReadOnlyList<DefaultDay> WeekOf(string? json)
    {
        List<DefaultDay>? stored = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                stored = JsonSerializer.Deserialize<List<DefaultDay>>(json, Json);
            }
            catch (JsonException)
            {
                stored = null;
            }
        }
        return [.. StandardWeek.Select(d => stored?.FirstOrDefault(s => s.Day == d.Day) ?? d)];
    }

    public static PersonProfile Of(UserEntity u) =>
        new(u.Email, u.PushoverKey, u.RedmineName, u.InPlan, u.ShortCode, u.Color, WeekOf(u.DefaultWeek), u.PlanOrder);

    public static void ValidateContact(ApiValidator v, string? email, string? pushoverKey)
    {
        v.Check(string.IsNullOrWhiteSpace(email) || email.Trim().Length <= 254 && System.Net.Mail.MailAddress.TryCreate(email.Trim(), out _),
            "email", "Not an e-mail address.");
        v.Check(string.IsNullOrWhiteSpace(pushoverKey) || PushoverKeyPattern().IsMatch(pushoverKey.Trim()), "pushoverKey",
            "A Pushover user key has 30 letters and digits.");
    }

    /// <summary>Checks and applies the plan's fields: a unique code of 3 letters, a colour, a known type per working weekday.</summary>
    public static async Task ApplyPlanAsync(PnmtdDbContext db, UserEntity user, bool inPlan, string? shortCode, string? color,
        IReadOnlyList<DefaultDay>? week, int planOrder, ApiValidator v, CancellationToken ct)
    {
        var code = string.IsNullOrWhiteSpace(shortCode) ? null : shortCode.Trim().ToUpperInvariant();
        v.Check(code is null || CodePattern().IsMatch(code), "shortCode", "3 letters.");
        v.Check(!inPlan || code is not null, "shortCode", "Someone in the plan needs a short code.");
        if (code is not null && await db.Users.AnyAsync(u => u.Id != user.Id && u.ShortCode == code, ct))
        {
            v.Add("shortCode", $"{code} is taken.");
        }
        var colour = string.IsNullOrWhiteSpace(color) ? null : color.Trim().ToLowerInvariant();
        v.Check(colour is null || ColorPattern().IsMatch(colour), "color", "A colour as #rrggbb.");
        var days = week ?? WeekOf(user.DefaultWeek);
        v.Check(days.Select(d => d.Day).Distinct().Count() == days.Count, "defaultWeek", "Each weekday once.");
        var typeIds = days.Where(d => d.TypeId is not null).Select(d => d.TypeId!.Value).Distinct().ToList();
        if (typeIds.Count > 0)
        {
            var known = await db.AvailabilityTypes.AsNoTracking().Where(t => typeIds.Contains(t.Id) && !t.Retired).CountAsync(ct);
            v.Check(known == typeIds.Count, "defaultWeek", "An availability type that doesn't exist.");
        }
        v.Check(planOrder is >= 0 and <= 10_000, "planOrder", "0 to 10,000.");
        v.ThrowIfInvalid();
        user.InPlan = inPlan;
        user.ShortCode = code;
        user.Color = colour;
        user.DefaultWeek = JsonSerializer.Serialize(StandardWeek.Select(d => days.FirstOrDefault(x => x.Day == d.Day) ?? d), Json);
        user.PlanOrder = planOrder;
    }

    public static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();

    /// <summary>A test message to the person's own address or key (<i>Send a test</i>).</summary>
    public static async Task<DeliveryResult> TestAsync(NotificationService notifications, UserEntity user, string? channel, bool simulate, CancellationToken ct)
    {
        var pushover = string.Equals(channel, "pushover", StringComparison.OrdinalIgnoreCase);
        var address = pushover ? user.PushoverKey : user.Email;
        if (string.IsNullOrWhiteSpace(address))
        {
            throw ApiException.Conflict("contact_missing", pushover ? "Save a Pushover key first." : "Save an e-mail address first.");
        }
        var recipient = pushover ? $"pushover://{address.Trim()}" : address.Trim();
        var text = string.Create(CultureInfo.InvariantCulture, $"A test from PNMT for {user.DisplayName}: this is where PNMT's assistant reaches you.");
        var result = await notifications.SendAsync(new NotificationMessage(recipient, "PNMT: a test", text, text, Importance.Normal), "Contact test", simulate, ct);
        return result ?? throw ApiException.Conflict("contact_no_provider", pushover ? "Pushover isn't set up on this server." : "E-mail isn't set up on this server.");
    }

    [GeneratedRegex("^[A-Za-z0-9]{30}$")]
    private static partial Regex PushoverKeyPattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex ColorPattern();
}
