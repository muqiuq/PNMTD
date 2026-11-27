using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Notifications;

namespace PNMTD.Licenses;

/// <summary>
/// The owner's notices (docs/licences-plan.md §5.5), written when sent from the items as they are then: an item renewed, reviewed or
/// retired in the meantime is left out, and an e-mail with nothing left isn't sent. Nothing while the feature is off. The price is in
/// them, since the recipient holds <i>View</i>; never the keys, the note, the support contact or the documents.
/// </summary>
public abstract class LicenseNotice(IConfiguration configuration) : IUserNotificationRenderer
{
    public abstract string Kind { get; }

    protected abstract bool Review { get; }

    public async Task<RenderedNotification?> RenderAsync(PnmtdDbContext db, UserNotificationBatch batch, string? baseUrl, CancellationToken ct)
    {
        if (!LicenseSetup.Enabled(configuration))
        {
            return null;
        }
        var ids = batch.Rows.Select(r => r.SubjectId).OfType<Guid>().Distinct().ToList();
        var items = await db.Licenses.AsNoTracking().Where(l => ids.Contains(l.Id) && l.Status != LicenseStatus.Retired).ToListAsync(ct);
        var today = Clock.Today;
        var lines = new List<(int Order, string Display, string Line)>();
        foreach (var item in items.OrderBy(i => i.Number))
        {
            // An item that changed hands in the meantime is no longer this person's (or no longer nobody's).
            if (Kind == UserNotificationKinds.LicenseUnowned ? item.OwnerId is not null : item.OwnerId != batch.UserId)
            {
                continue;
            }
            var display = LicenseWords.Number(item.Type, item.Number);
            var link = baseUrl is null ? "" : $"\n  {baseUrl}/pnmt-admin/licences/{item.Number}";
            if (Review)
            {
                if (LicenseEvaluator.ReviewDue(item, today) is not { } due)
                {
                    continue;
                }
                var last = item.ReviewedOn is { } on ? $"last reviewed on {Clock.FormatDay(on)}" : "never reviewed";
                lines.Add((0, display, $"- {display} {item.Name}: review due since {Clock.FormatDay(due)} ({last}){link}"));
                continue;
            }
            var renewed = item.VerdictCode == PNMTStatusCodes.LICENSE_RENEWED && batch.Rows.Any(r => r.SubjectId == item.Id
                && r.Detail?.StartsWith("Renewed automatically", StringComparison.Ordinal) == true);
            if (item.VerdictCode is not >= 400 && !renewed)
            {
                continue;
            }
            var text = Finding(item.VerdictMessage);
            lines.Add((renewed ? 1 : 0, display, $"- {display} {item.Name}: {text}{Price(item, today, renewed)}{link}"));
        }
        if (lines.Count == 0)
        {
            return null;
        }
        var ordered = lines.OrderBy(l => l.Order).ToList();
        var displays = ordered.Select(l => l.Display).ToList();
        var subject = ordered.Count == 1
            ? $"PNMT: {ordered[0].Line[2..].Split('\n')[0]}"
            : Review ? $"PNMT: {ordered.Count} reviews of licences and services are due" : $"PNMT: {ordered.Count} licences and services need attention";
        var intro = Review ? "These are due for a review:" : Kind == UserNotificationKinds.LicenseUnowned
            ? "These licences and services have no owner, and need attention:" : "These need your attention:";
        var because = Kind == UserNotificationKinds.LicenseUnowned ? "you chose licences and services nobody owns"
            : $"you are the owner of {Names(displays)}";
        return new RenderedNotification(Cut(subject, 200), $"{intro}\n\n{string.Join("\n", ordered.Select(l => l.Line))}",
            $"Licences & services: {(Review ? "reviews" : "warnings")} for {Names(displays)}", because);
    }

    /// <summary>The verdict's message without its <i>· name (LIC-12)</i>, which the line names already.</summary>
    internal static string Finding(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "needs attention";
        }
        var cut = message.LastIndexOf(" · ", StringComparison.Ordinal);
        var text = cut > 0 ? message[..cut] : message;
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    /// <summary>
    /// What it costs, for the decision at hand: a year of a recurring price, the next term after an automatic renewal or before a
    /// cancellation deadline, a one-time price.
    /// </summary>
    internal static string Price(LicenseEntity item, DateOnly today, bool renewed)
    {
        var cost = CostItem.From(item);
        if (CostMath.UnitPrice(cost) is not { } unit)
        {
            return "";
        }
        if (!cost.Periodic)
        {
            return $" · {Money(item.Currency, unit)} one-time";
        }
        var perMonth = unit / item.BillingMonths!.Value;
        if (item.Renewal == LicenseRenewal.Automatic && item.TermMonths is { } term)
        {
            return $" · {Money(item.Currency, perMonth * term)} for {(renewed ? "the new term" : "the next term")} ({term} month{(term == 1 ? "" : "s")})";
        }
        return $" · {Money(item.Currency, perMonth * 12)} a year";
    }

    internal static string Money(string currency, decimal amount) => $"{currency} {CostMath.Cents(amount).ToString("#,0.00", CultureInfo.InvariantCulture)}";

    private static string Names(IReadOnlyList<string> displays) => displays.Count switch
    {
        0 => "these",
        1 => displays[0],
        2 => $"{displays[0]} and {displays[1]}",
        _ => $"{string.Join(", ", displays.Take(displays.Count - 1))} and {displays[^1]}",
    };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary><c>license.warning</c>: an owner's items that went bad, a reminder, an automatic renewal.</summary>
public sealed class LicenseWarningNotice(IConfiguration configuration) : LicenseNotice(configuration)
{
    public override string Kind => UserNotificationKinds.LicenseWarning;

    protected override bool Review => false;
}

/// <summary><c>license.review</c>: an owner's items whose review fell due.</summary>
public sealed class LicenseReviewNotice(IConfiguration configuration) : LicenseNotice(configuration)
{
    public override string Kind => UserNotificationKinds.LicenseReview;

    protected override bool Review => true;
}

/// <summary><c>license.unowned</c>: the warnings about items nobody owns, to whoever chose them.</summary>
public sealed class LicenseUnownedNotice(IConfiguration configuration) : LicenseNotice(configuration)
{
    public override string Kind => UserNotificationKinds.LicenseUnowned;

    protected override bool Review => false;
}
