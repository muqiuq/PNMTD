using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Notifications;

namespace PNMTD.Knowledge;

/// <summary>
/// The e-mails about likes and uses of a page (docs/knowledge-base-extensions-plan.md §9.4), written from a batch and the page as it
/// is now. A sensitive page is named by its number only, without its title or the notes: an e-mail leaves PNMT. A page that is gone
/// leaves nothing to say.
/// </summary>
public abstract class KbReactionNotice : IUserNotificationRenderer
{
    public abstract string Kind { get; }

    public async Task<RenderedNotification?> RenderAsync(PnmtdDbContext db, UserNotificationBatch batch, string? baseUrl, CancellationToken ct)
    {
        if (batch.SubjectId is not { } id)
        {
            return null;
        }
        var page = await db.KbPages.AsNoTracking().Where(p => p.Id == id).Select(p => new { p.Number, p.Title, p.Sensitive }).SingleOrDefaultAsync(ct);
        if (page is null)
        {
            return null;
        }
        var name = page.Sensitive ? $"KB-{page.Number}" : $"KB-{page.Number} “{page.Title}”";
        var link = baseUrl is null ? "" : $"\n\n{baseUrl}/pnmt-admin/kb/{page.Number}";
        var (subject, body) = Write(batch, name, page.Sensitive);
        return new RenderedNotification($"PNMT: {subject}", body + link, $"Knowledge base: {Kind} on KB-{page.Number}",
            $"you wrote or edited KB-{page.Number}");
    }

    protected abstract (string Subject, string Body) Write(UserNotificationBatch batch, string page, bool sensitive);

    /// <summary>"Anna", "Anna and Ben", "Anna, Ben and 3 others": each person once, in the order they came.</summary>
    internal static string Names(IEnumerable<string> names)
    {
        var people = names.Distinct().ToList();
        return people.Count switch
        {
            0 => "Someone",
            1 => people[0],
            2 => $"{people[0]} and {people[1]}",
            3 => $"{people[0]}, {people[1]} and {people[2]}",
            _ => $"{people[0]}, {people[1]} and {people.Count - 2} others",
        };
    }
}

/// <summary><c>kb.liked</c>: who liked the page.</summary>
public sealed class KbLikedNotice : KbReactionNotice
{
    public override string Kind => UserNotificationKinds.KbLiked;

    protected override (string Subject, string Body) Write(UserNotificationBatch batch, string page, bool sensitive)
    {
        var names = Names(batch.Rows.Select(r => r.ActorName));
        return ($"{names} liked {page}", $"{names} liked {page}.");
    }
}

/// <summary><c>kb.used</c>: who used the page to solve a problem, when, and what it solved.</summary>
public sealed class KbUsedNotice : KbReactionNotice
{
    public override string Kind => UserNotificationKinds.KbUsed;

    protected override (string Subject, string Body) Write(UserNotificationBatch batch, string page, bool sensitive)
    {
        var names = Names(batch.Rows.Select(r => r.ActorName));
        var times = batch.Rows.Count == 1 ? "" : $" ({batch.Rows.Count} times)";
        var lines = batch.Rows.Select(r =>
            $"- {r.ActorName}, {Clock.ToLocal(r.CreatedAt):yyyy-MM-dd HH:mm}{(sensitive || string.IsNullOrWhiteSpace(r.Detail) ? "" : $": {r.Detail}")}");
        return ($"{names} used {page} to solve a problem{times}", $"{names} used {page} to solve a problem{times}.\n\n{string.Join("\n", lines)}");
    }
}
