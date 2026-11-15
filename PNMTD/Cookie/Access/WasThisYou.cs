using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Access;

/// <summary>What the <i>Was this you?</i> page shows: the one login, and whether it can still be answered.</summary>
public sealed record WasThisYouView(bool Open, string? Login, string? Target, string? Way, string? Source, string? Device, DateTime? At, string? Answer);

/// <summary>
/// <i>Was this you?</i> (docs/cookie-access-plan.md §6.6): the account's own person is asked by e-mail, once per case, with a link
/// to a page that needs no sign-in and whose buttons, not the link, answer. The e-mail is code's text from the login only, so it
/// needs no Seraph. It goes to the person's address as it was before the login: an address changed in the last 7 days gets it too.
/// <i>No</i> makes the case alarming at once; <i>Yes</i> closes an unusual one, and is only noted on a suspicious or alarming one.
/// </summary>
public sealed class WasThisYou(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, Outbox outbox, AccessNotices notices, IConfiguration configuration)
{
    public static readonly TimeSpan AddressChangedWithin = TimeSpan.FromDays(7);

    /// <summary>Asks the person of the case's newest linked login, if the settings, the verdict's row and the limits allow it.</summary>
    public async Task<string?> AskAsync(Guid caseId, AccessVerdict verdict, CancellationToken ct)
    {
        var s = await settings.GetAsync<AccessSettings>(ct);
        if (s.Notices.Of(verdict) is not { Ask: true })
        {
            return null;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var reviews = await db.AccessReviews.Where(r => r.CaseId == caseId).OrderByDescending(r => r.At).ToListAsync(ct);
        if (reviews.Count == 0 || reviews.Any(r => r.AskedAt is not null))
        {
            return null;
        }
        var review = reviews.FirstOrDefault(r => r.PersonId is not null && !r.Late && s.WasThisYou.For(r.Target));
        if (review is null)
        {
            return null;
        }
        var person = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == review.PersonId && u.IsActive, ct);
        var now = Clock.UtcNow;
        string Note(string text)
        {
            CaseBook.Entry(db, caseId, now, "was-this-you", text);
            return text;
        }
        if (person is null || string.IsNullOrWhiteSpace(person.Email))
        {
            var why = Note("Was this you? isn't asked: the person has no e-mail address, or isn't active.");
            await db.SaveChangesAsync(ct);
            return why;
        }
        if (configuration["PublicBaseUrl"] is not { Length: > 0 } baseUrl)
        {
            var why = Note("Was this you? isn't asked: PublicBaseUrl isn't set, so the e-mail can't link to PNMT.");
            await db.SaveChangesAsync(ct);
            return why;
        }
        var dayAgo = now.AddDays(-1);
        if (await db.CookieOutbox.CountAsync(o => o.Channel == Models.Db.CookieChannel.WasThisYou && o.UserId == person.Id && o.CreatedAt >= dayAgo
                && (o.State == Models.Db.CookieOutboxState.Sent || o.State == Models.Db.CookieOutboxState.Simulated), ct) >= s.WasThisYou.PerPersonDay)
        {
            var why = Note($"Was this you? isn't asked: {s.WasThisYou.PerPersonDay} a day per person.");
            await db.SaveChangesAsync(ct);
            return why;
        }
        var addresses = new List<string> { person.Email.Trim() };
        addresses.AddRange((await OldAddressesAsync(db, person.Id, now, ct)).Where(a => !addresses.Contains(a, StringComparer.OrdinalIgnoreCase)));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        review.TokenHash = Hash(token);
        review.TokenExpiresAt = now.AddHours(s.WasThisYou.ValidHours);
        review.AskedAt = now;
        review.AskedAddresses = addresses;
        var link = $"{baseUrl.TrimEnd('/')}/pnmt-admin/was-this-you/{token}";
        var subject = review.Target == AccessTargetKind.Pnmt ? "Was this you? A sign-in to PNMT" : $"Was this you? A login to {review.TargetName}";
        var body = $"""
            Hello {person.DisplayName},

            PNMT saw this, and it is new for your account:

            {AccessWatch.Describe(review)}

            Was this you? Answer here: {link}

            The link works once, for {s.WasThisYou.ValidHours} hours. If it wasn't you, say so: the people who look after access are told at once.
            """;
        await db.SaveChangesAsync(ct);
        foreach (var address in addresses)
        {
            await outbox.SendAsync(new OutboxRequest(Models.Db.CookieChannel.WasThisYou, subject, body, caseId, UserId: person.Id, Recipient: person.DisplayName,
                Address: address, Source: "code", Unlimited: true), ct);
        }
        var changed = addresses.Count > 1 ? $"; the address changed within {AddressChangedWithin.TotalDays:0} days, so the old one got it too" : "";
        var asked = Note($"Was this you? asked {person.DisplayName} by e-mail{changed}.");
        await db.SaveChangesAsync(ct);
        return asked;
    }

    /// <summary>The person's addresses before a change in the last 7 days, from the audit log's changes.</summary>
    private static async Task<List<string>> OldAddressesAsync(PnmtdDbContext db, Guid userId, DateTime now, CancellationToken ct)
    {
        var since = now - AddressChangedWithin;
        var id = userId.ToString();
        var changes = await db.AuditLog.AsNoTracking()
            .Where(a => a.CreatedAt >= since && a.Changes != null && (a.TargetId == id || a.Subjects.Contains(userId))
                && (a.EventType == AuditEvents.ContactChanged || a.EventType == AuditEvents.UserUpdated))
            .Select(a => a.Changes!).ToListAsync(ct);
        var old = new List<string>();
        foreach (var json in changes)
        {
            try
            {
                foreach (var change in JsonSerializer.Deserialize<List<AuditChange>>(json, CookieSettings.Json) ?? [])
                {
                    if (string.Equals(change.Field, "Email", StringComparison.OrdinalIgnoreCase) && change.Old is { Length: > 3 } was && was.Contains('@'))
                    {
                        old.Add(was.Trim());
                    }
                }
            }
            catch (JsonException)
            {
                // A change written in another shape names no address.
            }
        }
        return [.. old.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>The page's login, or a closed view: a token used, expired or unknown shows the same.</summary>
    public async Task<WasThisYouView> ViewAsync(string token, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var review = await FindAsync(db, token, ct);
        if (review is null)
        {
            return new WasThisYouView(false, null, null, null, null, null, null, null);
        }
        return new WasThisYouView(true, AccessWatch.Describe(review), review.Target == AccessTargetKind.Pnmt ? AccessWatch.PnmtName : review.TargetName,
            AccessWatch.Way(review.Way), review.Source, review.Device, review.At, null);
    }

    private static async Task<AccessReviewEntity?> FindAsync(PnmtdDbContext db, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }
        var hash = Hash(token.Trim());
        var now = Clock.UtcNow;
        return await db.AccessReviews.SingleOrDefaultAsync(r => r.TokenHash == hash && r.TokenExpiresAt > now, ct);
    }

    /// <summary>
    /// The person's answer (§6.6), once: <i>No</i> makes the case alarming and not expected and tells the contacts as alarming;
    /// <i>Yes</i> closes an unusual case as expected, and is only noted on a suspicious or alarming one. Null for a link that no
    /// longer works.
    /// </summary>
    public async Task<string?> AnswerAsync(string token, bool itWasMe, string? from, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var review = await FindAsync(db, token, ct);
        if (review is null)
        {
            return null;
        }
        var now = Clock.UtcNow;
        review.TokenHash = null;
        review.PersonAnswer = itWasMe ? "yes" : "no";
        review.PersonAnsweredAt = now;
        review.PersonAnsweredFrom = CaseBook.Cut(from, 100);
        var person = await db.Users.AsNoTracking().Where(u => u.Id == review.PersonId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "The person";
        var c = review.CaseId is { } caseId ? await db.CookieCases.SingleOrDefaultAsync(x => x.Id == caseId, ct) : null;
        AuditLog.Summary($"Was this you? {person}: {(itWasMe ? "yes" : "no")}, about {AccessWatch.Describe(review)}");
        if (c is null)
        {
            await db.SaveChangesAsync(ct);
            return itWasMe ? "Thank you." : "Thank you. The people who look after access are told.";
        }
        var verdict = AccessSignals.VerdictOf(c.AccessVerdict) ?? AccessVerdict.Unusual;
        if (!itWasMe)
        {
            c.AccessVerdict = AccessSignals.Name(AccessVerdict.Alarming);
            c.AccessAnswer = AccessService.NotExpected;
            c.AccessAnsweredAt = now;
            c.AccessAnsweredBy = CaseBook.Cut($"{person} (Was this you?)", 200);
            c.Importance = Lib.Models.Enum.Importance.High;
            c.Open = true;
            c.ChangedAt = now;
            c.StoppedAt = null;
            CaseBook.Entry(db, c.Id, now, "was-this-you", $"{person} says it wasn't them.", "person", person);
            await db.AccessReviews.Where(r => r.CaseId == c.Id && r.State != AccessReviewState.NotJudged)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, AccessReviewState.Alarming), ct);
            await RateAsync(db, c.Id, expected: false, person, "Was this you? No.", ct);
            await db.SaveChangesAsync(ct);
            await notices.NotifyAsync(c.Id, AccessVerdict.Alarming, $"{person} says it wasn't them.", "code", ct, force: true);
            return "Thank you. The people who look after access are told now.";
        }
        if (verdict <= AccessVerdict.Unusual && c.AccessAnswer is null)
        {
            c.AccessAnswer = AccessService.Expected;
            c.AccessAnsweredAt = now;
            c.AccessAnsweredBy = CaseBook.Cut($"{person} (Was this you?)", 200);
            c.Open = false;
            c.ClosedAt = now;
            c.ChangedAt = now;
            CaseBook.Entry(db, c.Id, now, "was-this-you", $"{person} says it was them: the case closes as expected.", "person", person);
            await RateAsync(db, c.Id, expected: true, person, "Was this you? Yes.", ct);
        }
        else
        {
            c.ChangedAt = now;
            CaseBook.Entry(db, c.Id, now, "was-this-you", $"{person} says it was them. A contact still answers, because whoever has the password may have the mailbox too.",
                "person", person);
        }
        await db.SaveChangesAsync(ct);
        return "Thank you.";
    }

    /// <summary>
    /// The verdicts' decisions of the case, rated by the answer (§7), if nobody rated them yet: <i>Expected</i> rates <i>unusual</i>
    /// (and <i>normal</i>) right and <i>suspicious</i> and <i>alarming</i> wrong; <i>Not expected</i> rates <i>normal</i> wrong and the rest right.
    /// A verdict decision's outcome is its verdict.
    /// </summary>
    internal static async Task RateAsync(PnmtdDbContext db, Guid caseId, bool expected, string by, string comment, CancellationToken ct)
    {
        var ids = await db.AccessReviews.AsNoTracking().Where(r => r.CaseId == caseId && r.DecisionId != null).Select(r => r.DecisionId!.Value).Distinct().ToListAsync(ct);
        var decisions = await db.CookieDecisions.Where(d => ids.Contains(d.Id) && d.Kind == AccessJudge.DecisionKind && d.Rating == null).ToListAsync(ct);
        foreach (var d in decisions)
        {
            var v = AccessSignals.VerdictOf(d.Outcome) ?? AccessVerdict.Unusual;
            d.Rating = expected ? (v <= AccessVerdict.Unusual ? "right" : "wrong") : (v == AccessVerdict.Normal ? "wrong" : "right");
            d.RatingComment = CaseBook.Cut(comment, 1000);
            d.RatedByName = CaseBook.Cut(by, 200);
            d.RatedAt = Clock.UtcNow;
        }
    }
}
