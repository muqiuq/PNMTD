using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Knowledge;

/// <summary>The problem codes of the knowledge base's API (docs/locations-knowledge-plan.md §9).</summary>
public static class KbErrors
{
    public const string Archived = "archived";
    public const string NotArchived = "not_archived";
    public const string Sensitive = "sensitive";
    public const string LooksLikeCredential = "looks_like_credential";
    public const string AiUnavailable = "ai_unavailable";
    public const string AiBudgetSpent = "ai_budget_spent";
}

/// <summary>
/// The draw behind the AI question after a saved edit (§7.2): a seeded <see cref="Random"/> in the tests, so "about one
/// in ten" can be checked.
/// </summary>
public sealed class KbAiDraw(Random random)
{
    /// <summary>0 to 99.</summary>
    public int Percent()
    {
        lock (random)
        {
            return random.Next(100);
        }
    }
}

/// <summary>
/// The knowledge base's pages (docs/locations-knowledge-plan.md §6): read by number, changed by id. Every change is a
/// revision (<see cref="KbRevisions"/>); a save carries the revision it started from and is refused with 409
/// <c>edit_conflict</c> when someone saved in between (§5.7). An archived page is read-only until restored; only an Admin
/// deletes one, and only once archived (§6.2). The save's answer says whether the AI question follows (§7.2).
/// </summary>
public sealed class KbPageService(PnmtdDbContext db, KbRevisions revisions, IHttpContextAccessor accessor, IOptionsMonitor<KnowledgeBaseOptions> kb,
    IOptionsMonitor<AiOptions> ai, LocationTree tree, KbPresence presence, KbViews views, KbAiDraw draw, KbAiScans scans)
{
    public const int MaxLinks = 50;

    public static readonly TimeSpan AiQuestionPause = TimeSpan.FromHours(24);

    /// <summary>AI tags are on: AI usable, and <c>KnowledgeBase:AiTags</c> (§7.2).</summary>
    public bool AiTagsOn => ai.CurrentValue.Usable && kb.CurrentValue.AiTags;

    // --- reading -------------------------------------------------------------------------------------------------------

    public async Task<KbPageDetail> GetAsync(int number, CancellationToken ct)
    {
        var id = await db.KbPages.AsNoTracking().Where(p => p.Number == number).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Page");
        return await DetailAsync(id, ct);
    }

    public async Task<KbPageDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var page = await db.KbPages.AsNoTracking().Include(p => p.Tags).ThenInclude(t => t.Tag).Include(p => p.Links).AsSplitQuery()
            .SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw ApiException.NotFound("Page");
        var facts = MarkdownText.Read(page.Content);
        var links = await KbLinkNames.ResolveAsync(db, tree, KbRevisions.LinksOf(page), ct);
        var files = await FilesAsync(facts.FileIds, ct);
        var linkedFrom = await db.KbPageRefs.AsNoTracking().Where(r => r.ToNumber == page.Number)
            .Join(db.KbPages, r => r.FromPageId, p => p.Id, (r, p) => p)
            .Where(p => p.ArchivedAt == null)
            .OrderBy(p => p.Number)
            .Select(p => new KbPageRef(p.Number, p.Title, false))
            .ToListAsync(ct);
        var numbers = facts.PageNumbers.Where(n => n != page.Number).ToList();
        var targets = await db.KbPages.AsNoTracking().Where(p => numbers.Contains(p.Number))
            .Select(p => new { p.Number, p.Title, Archived = p.ArchivedAt != null }).ToListAsync(ct);
        var linksTo = numbers.Select(n => targets.FirstOrDefault(t => t.Number == n) is { } t
            ? new KbPageRef(n, t.Title, t.Archived) : new KbPageRef(n, null, false)).ToList();
        var me = revisions.Actor.Id;
        return new KbPageDetail(page.Id, page.Number, page.Title, page.Summary, page.Content, page.Status,
            page.Tags.Select(t => new KbPageTag(t.Tag!.Name, t.Source)).OrderBy(t => t.Name, StringComparer.Ordinal).ToList(),
            links, page.Sensitive, page.Revision, page.CreatedAt, page.CreatedByName, page.UpdatedAt, page.UpdatedByName, page.ArchivedAt,
            page.ArchivedByName, page.Views, files, linkedFrom, linksTo, presence.Others(page.Id, me), AiTagsOn);
    }

    /// <summary>The files a content uses, in its order, without those deleted (the page shows <i>File removed</i>).</summary>
    public async Task<IReadOnlyList<FileItem>> FilesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var files = await db.Files.AsNoTracking().Where(f => ids.Contains(f.Id) && f.DeletedAt == null).ToListAsync(ct);
        return ids.Select(id => files.FirstOrDefault(f => f.Id == id)).OfType<FileEntity>().Select(FileNames.Item).ToList();
    }

    // --- saving --------------------------------------------------------------------------------------------------------

    private sealed record Input(string Title, string? Summary, string Content, List<string> Tags, HashSet<string>? AiTags, KbPageStatus Status,
        List<KbLinkRef> Links, string? Note);

    public async Task<KbSaveResult> CreateAsync(KbPageRequest request, CancellationToken ct)
    {
        var input = await ValidateAsync(request, ct);
        CheckCredentials(request, "", input.Content);
        var actor = revisions.Actor;
        var now = Clock.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var page = new KbPageEntity
        {
            Id = Guid.NewGuid(), CreatedAt = now, CreatedBy = actor.Id, CreatedByName = actor.Name, UpdatedAt = now, UpdatedBy = actor.Id,
            UpdatedByName = actor.Name,
        };
        db.KbPages.Add(page);
        await ApplyAsync(page, input, ct);
        var marked = request.MarkSensitive;
        if (marked)
        {
            page.Sensitive = true;
        }
        await revisions.CommitAsync(page, null, input.Note, null, ct);
        await db.SaveChangesAsync(ct);
        if (marked)
        {
            AuditSensitivity(page, null);
            await db.SaveChangesAsync(ct);
        }
        var ask = await AskAsync(page, isNew: true, isEdit: true, ct);
        await tx.CommitAsync(ct);
        var sameTitle = await db.KbPages.AsNoTracking()
            .Where(p => p.Id != page.Id && p.ArchivedAt == null && p.Title.ToLower() == page.Title.ToLower())
            .OrderBy(p => p.Number).Select(p => new KbPageRef(p.Number, p.Title, false)).Take(5).ToListAsync(ct);
        return new KbSaveResult(await DetailAsync(page.Id, ct), ask, sameTitle);
    }

    public async Task<KbSaveResult> UpdateAsync(Guid id, KbPageRequest request, CancellationToken ct)
    {
        var input = await ValidateAsync(request, ct);
        if (request.Revision is null)
        {
            throw ApiException.Invalid("revision", "The revision the edit started from.");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var page = await revisions.LockAsync(id, ct);
        ReadOnly(page);
        if (request.Revision != page.Revision)
        {
            throw Conflict(page);
        }
        var startedFrom = page.Content;
        CheckCredentials(request, startedFrom, input.Content);
        var before = KbSnapshot.Of(page);
        await ApplyAsync(page, input, ct);
        var marked = request.MarkSensitive && !page.Sensitive;
        if (marked)
        {
            page.Sensitive = true;
        }
        if (!Differs(before, KbSnapshot.Of(page)))
        {
            // Nothing changed: no revision, no question.
            return new KbSaveResult(await DetailAsync(id, ct), false, []);
        }
        var revision = await revisions.CommitAsync(page, before, input.Note, null, ct);
        if (marked)
        {
            AuditSensitivity(page, null);
            scans.Cancel(page.Id);
        }
        await db.SaveChangesAsync(ct);
        var ask = await AskAsync(page, isNew: false, isEdit: revision.IsEdit, ct);
        await tx.CommitAsync(ct);
        return new KbSaveResult(await DetailAsync(id, ct), ask, []);
    }

    private async Task<Input> ValidateAsync(KbPageRequest r, CancellationToken ct)
    {
        var v = new ApiValidator();
        var title = (r.Title ?? "").Trim();
        v.Check(title.Length is >= 3 and <= 160, "title", "3–160 characters.");
        var summary = Text.Trimmed(r.Summary);
        v.Check(summary is null || summary.Length <= 300, "summary", "At most 300 characters.");
        var content = r.Content ?? "";
        var max = kb.CurrentValue.MaxPageBytes;
        v.Check(Encoding.UTF8.GetByteCount(content) <= max, "content", $"At most {max / 1024:N0} KB of Markdown; files don't count.");
        var tags = new List<string>();
        foreach (var raw in r.Tags ?? [])
        {
            if (KbTags.Normalize(raw) is { } tag)
            {
                if (!tags.Contains(tag))
                {
                    tags.Add(tag);
                }
            }
            else
            {
                v.Add("tags", $"“{Cut(raw ?? "", 40)}” is not a tag: 2–40 letters, digits and dashes.");
            }
        }
        v.Check(tags.Count <= KbTags.MaxPerPage, "tags", $"At most {KbTags.MaxPerPage} tags.");
        var note = Text.Trimmed(r.Note);
        v.Check(note is null || note.Length <= 200, "note", "At most 200 characters.");
        var links = (r.Links ?? []).Select(l => new KbLinkRef(l.Kind, l.Id)).Distinct().ToList();
        v.Check(links.Count <= MaxLinks, "links", $"At most {MaxLinks} links.");
        v.Check(Enum.IsDefined(r.Status), "status", "Draft, Published or NeedsReview.");
        v.ThrowIfInvalid();
        var missing = await MissingTargetsAsync(links, ct);
        if (missing > 0)
        {
            throw ApiException.Invalid("links", missing == 1 ? "A linked location, host or sensor doesn't exist." : $"{missing} linked objects don't exist.");
        }
        var aiTags = r.AiTags?.Select(KbTags.Normalize).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return new Input(title, summary, content, tags, aiTags, r.Status, links, note);
    }

    private async Task<int> MissingTargetsAsync(IReadOnlyList<KbLinkRef> links, CancellationToken ct)
    {
        var locations = links.Where(l => l.Kind == KbLinkKind.Location).Select(l => l.Id).ToList();
        var hosts = links.Where(l => l.Kind == KbLinkKind.Host).Select(l => l.Id).ToList();
        var sensors = links.Where(l => l.Kind == KbLinkKind.Sensor).Select(l => l.Id).ToList();
        var found = (locations.Count == 0 ? 0 : await db.Locations.CountAsync(l => locations.Contains(l.Id), ct))
            + (hosts.Count == 0 ? 0 : await db.Hosts.CountAsync(h => hosts.Contains(h.Id), ct))
            + (sensors.Count == 0 ? 0 : await db.Sensors.CountAsync(s => sensors.Contains(s.Id), ct));
        return links.Count - found;
    }

    /// <summary>The request's fields on the page; tags and links as sets, kept rows keeping their source and time.</summary>
    private async Task ApplyAsync(KbPageEntity page, Input input, CancellationToken ct)
    {
        var actor = revisions.Actor;
        var now = Clock.UtcNow;
        page.Title = input.Title;
        page.Summary = input.Summary;
        page.Content = input.Content;
        page.Status = input.Status;
        await SetTagsAsync(page, input.Tags, input.AiTags, actor, ct);
        page.Links.RemoveAll(l => !input.Links.Contains(Ref(l)));
        foreach (var link in input.Links.Where(l => page.Links.All(existing => Ref(existing) != l)).ToList())
        {
            var entity = new KbLinkEntity
            {
                Id = Guid.NewGuid(), PageId = page.Id, CreatedAt = now, CreatedBy = actor.Id,
                LocationId = link.Kind == KbLinkKind.Location ? link.Id : null,
                HostId = link.Kind == KbLinkKind.Host ? link.Id : null,
                SensorId = link.Kind == KbLinkKind.Sensor ? link.Id : null,
            };
            // Added explicitly: found through the collection, a row with its key set would be taken for an existing one.
            db.KbLinks.Add(entity);
            page.Links.Add(entity);
        }
    }

    /// <summary>
    /// The page's tags as <paramref name="names"/>: new ones manual; an AI tag stays one while
    /// <paramref name="stillAi"/> names it (null: every AI tag stays), and is confirmed otherwise (§6.4).
    /// </summary>
    internal async Task SetTagsAsync(KbPageEntity page, IReadOnlyList<string> names, IReadOnlySet<string>? stillAi, KbActor actor, CancellationToken ct,
        KbTagSource source = KbTagSource.Manual)
    {
        var tags = await KbTags.ResolveAsync(db, names, ct);
        page.Tags.RemoveAll(t => !names.Contains(t.Tag!.NormalizedName));
        foreach (var tag in tags)
        {
            if (page.Tags.FirstOrDefault(t => t.TagId == tag.Id) is { } existing)
            {
                if (existing.Source == KbTagSource.Ai && stillAi is not null && !stillAi.Contains(tag.NormalizedName))
                {
                    existing.Source = KbTagSource.Manual;
                }
                continue;
            }
            page.Tags.Add(new KbPageTagEntity { PageId = page.Id, TagId = tag.Id, Tag = tag, Source = source, AddedAt = Clock.UtcNow, AddedBy = actor.Id });
        }
    }

    private static KbLinkRef Ref(KbLinkEntity l) => KbRevisions.RefOf(l);

    private static bool Differs(KbSnapshot before, KbSnapshot after) =>
        KbRevisions.Compare(before, after, before.Content == after.Content ? null : new LineDiffResult(0, 0, [])).Any();

    /// <summary>
    /// The credentials warning (§5.7): lines of the new content that look like credentials and weren't such lines in the
    /// content the edit started from. It doesn't block: answered, the save goes through.
    /// </summary>
    private static void CheckCredentials(KbPageRequest request, string startedFrom, string content)
    {
        if (request.AcknowledgeCredentials || request.MarkSensitive)
        {
            return;
        }
        var findings = SecretScanner.Scan(content);
        if (findings.Count == 0)
        {
            return;
        }
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var old = SecretScanner.Scan(startedFrom);
        var oldLines = startedFrom.Replace("\r\n", "\n").Split('\n');
        var known = old.Select(f => oldLines[f.Line - 1].Trim()).ToHashSet(StringComparer.Ordinal);
        var fresh = findings.Where(f => !known.Contains(lines[f.Line - 1].Trim())).ToList();
        if (fresh.Count > 0)
        {
            throw LooksLikeCredential(fresh, "Some lines look like credentials. PNMT is not a password manager.");
        }
    }

    /// <summary>422 <c>looks_like_credential</c> with the line numbers and what they look like, never the text (§7.3).</summary>
    public static ApiException LooksLikeCredential(IReadOnlyList<SecretFinding> findings, string detail) =>
        new(StatusCodes.Status422UnprocessableEntity, KbErrors.LooksLikeCredential, detail, extensions: new Dictionary<string, object?>
        {
            ["lines"] = findings.Select(f => f.Line).ToList(),
            ["kinds"] = findings.Select(f => f.Kind).ToList(),
        });

    public static ApiException Conflict(KbPageEntity page) =>
        ApiException.EditConflict(page.Revision, page.UpdatedByName, page.UpdatedAt,
            $"{page.UpdatedByName ?? "Someone"} saved KB-{page.Number} in the meantime (revision {page.Revision}).");

    private static void ReadOnly(KbPageEntity page)
    {
        if (page.ArchivedAt is not null)
        {
            throw ApiException.Conflict(KbErrors.Archived, $"KB-{page.Number} is archived and read-only; restore it first.");
        }
    }

    /// <summary>
    /// Whether the AI question follows the save (§7.2), and if so it is noted, so the same person isn't asked about the
    /// same page again within 24 hours: always after a new page's first save; after an edit of the title, summary or
    /// content, with <c>AiQuestionOnEditPercent</c>. Never for a sensitive or archived page, nor while AI tags are off.
    /// </summary>
    private async Task<bool> AskAsync(KbPageEntity page, bool isNew, bool isEdit, CancellationToken ct)
    {
        if (!AiTagsOn || page.Sensitive || page.ArchivedAt is not null || revisions.Actor.Id is not { } user)
        {
            return false;
        }
        var now = Clock.UtcNow;
        var asked = await db.KbAiQuestions.SingleOrDefaultAsync(q => q.PageId == page.Id && q.UserId == user, ct);
        if (!isNew)
        {
            if (!isEdit || (asked is not null && now - asked.AskedAt < AiQuestionPause) || draw.Percent() >= kb.CurrentValue.AiQuestionOnEditPercent)
            {
                return false;
            }
        }
        await NoteQuestionAsync(db, page.Id, user, asked, ct);
        return true;
    }

    /// <summary>The person was asked about the page now (§7.2): by the save's question, or by starting a scan.</summary>
    internal static async Task NoteQuestionAsync(PnmtdDbContext db, Guid page, Guid user, KbAiQuestionEntity? asked, CancellationToken ct)
    {
        asked ??= await db.KbAiQuestions.SingleOrDefaultAsync(q => q.PageId == page && q.UserId == user, ct);
        if (asked is null)
        {
            db.KbAiQuestions.Add(new KbAiQuestionEntity { PageId = page, UserId = user, AskedAt = Clock.UtcNow });
        }
        else
        {
            asked.AskedAt = Clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    // --- archive, delete -----------------------------------------------------------------------------------------------

    public async Task<KbPageDetail> ArchiveAsync(Guid id, bool archive, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var page = await revisions.LockAsync(id, ct);
        if ((page.ArchivedAt is not null) != archive)
        {
            var before = KbSnapshot.Of(page);
            var actor = revisions.Actor;
            page.ArchivedAt = archive ? Clock.UtcNow : null;
            page.ArchivedBy = archive ? actor.Id : null;
            page.ArchivedByName = archive ? actor.Name : null;
            await revisions.CommitAsync(page, before, null, null, ct);
            await db.SaveChangesAsync(ct);
            if (archive)
            {
                scans.Cancel(page.Id);
            }
        }
        await tx.CommitAsync(ct);
        return await DetailAsync(id, ct);
    }

    /// <summary>
    /// An archived page, with everything of it (§6.2); files no other page or location uses are marked deleted and leave
    /// the disk 7 days later. The auth log keeps its number and title (<c>KbPageDeleted</c>).
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var page = await revisions.LockAsync(id, ct);
        if (page.ArchivedAt is null)
        {
            throw ApiException.Conflict(KbErrors.NotArchived, $"Archive KB-{page.Number} before deleting it.");
        }
        var files = await db.FileUses.Where(u => u.PageId == id).Select(u => u.FileId).ToListAsync(ct);
        var count = await db.KbRevisions.CountAsync(r => r.PageId == id, ct);
        db.KbPages.Remove(page);
        UdpViews.Audit(db, accessor, AuthEvents.KbPageDeleted, Cut($"KB-{page.Number} “{page.Title}”, {count} revision{(count == 1 ? "" : "s")}", 1000));
        await db.SaveChangesAsync(ct);
        var now = Clock.UtcNow;
        await db.Files.Where(f => files.Contains(f.Id) && f.DeletedAt == null && !db.FileUses.Any(u => u.FileId == f.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, now), ct);
        await tx.CommitAsync(ct);
        scans.Cancel(id);
    }

    // --- history -------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<KbRevisionItem>> RevisionsAsync(Guid id, CancellationToken ct)
    {
        await ExistsAsync(id, ct);
        var rows = await db.KbRevisions.AsNoTracking().Where(r => r.PageId == id).OrderByDescending(r => r.Number)
            .Select(r => new { r.Number, r.At, r.ByName, r.Note, r.Added, r.Removed, r.Changes, r.IsEdit, r.Declaration, r.Title, r.Status })
            .ToListAsync(ct);
        return rows.Select(r => new KbRevisionItem(r.Number, r.At, r.ByName, r.Note, r.Added, r.Removed, KbRevisions.Changes(r.Changes), r.IsEdit,
            Declaration(r.Declaration), r.Title, r.Status)).ToList();
    }

    public async Task<KbRevisionDetail> RevisionAsync(Guid id, int number, CancellationToken ct)
    {
        var r = await RevisionEntityAsync(id, number, ct);
        var links = await KbLinkNames.ResolveAsync(db, tree, KbRevisions.Links(r.Links), ct);
        var files = await FilesAsync(MarkdownText.Read(r.Content).FileIds, ct);
        return new KbRevisionDetail(r.Number, r.At, r.ByName, r.Note, KbRevisions.Changes(r.Changes), Declaration(r.Declaration), r.Title, r.Summary,
            r.Content, r.Tags, r.Status, r.Sensitive, r.Archived, links, files);
    }

    /// <summary>Two revisions compared (§6.3): by default the current one against the one before it.</summary>
    public async Task<KbDiff> DiffAsync(Guid id, int? from, int? to, CancellationToken ct)
    {
        var current = await db.KbPages.AsNoTracking().Where(p => p.Id == id).Select(p => (int?)p.Revision).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Page");
        var toNumber = to ?? current;
        var fromNumber = from ?? Math.Max(0, toNumber - 1);
        var b = await RevisionEntityAsync(id, toNumber, ct);
        var a = fromNumber == 0 ? null : await RevisionEntityAsync(id, fromNumber, ct);
        var diff = LineDiff.Compare(a?.Content ?? "", b.Content);
        var changes = KbRevisions.Compare(a is null ? null : Snapshot(a), Snapshot(b), diff.Identical ? null : diff).ToList();
        return new KbDiff(fromNumber, toNumber, changes, diff.Added, diff.Removed, a?.Content ?? "", b.Content,
            diff.Hunks.Select(h => new KbDiffHunk(h.OldStart, h.OldCount, h.NewStart, h.NewCount,
                h.Lines.Select(l => new KbDiffLine(l.Kind, l.Text, l.OldNumber, l.NewNumber)).ToList())).ToList());
    }

    /// <summary>
    /// A new revision with an old one's title, summary, content and tags (§6.3); the status and links stay as they are.
    /// </summary>
    public async Task<KbPageDetail> RestoreRevisionAsync(Guid id, int number, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var page = await revisions.LockAsync(id, ct);
        ReadOnly(page);
        var old = await RevisionEntityAsync(id, number, ct);
        var before = KbSnapshot.Of(page);
        page.Title = old.Title;
        page.Summary = old.Summary;
        page.Content = old.Content;
        await SetTagsAsync(page, old.Tags.Select(KbTags.Normalize).OfType<string>().Distinct().ToList(), null, revisions.Actor, ct);
        await revisions.CommitAsync(page, before, null, [new KbChange(KbChangeKinds.Restored, $"from {number}")], ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DetailAsync(id, ct);
    }

    /// <summary>
    /// Replaces an exact text with <c>[redacted]</c> wherever the page's text was copied (§6.3): every revision's title,
    /// summary, content, note, tags and list of what changed (a title's change names the old title); the page itself,
    /// whose search text follows; and the AI request log of the page's tag scans, requests and answers, with their work
    /// items. Scans of the page are cancelled first, so none writes the text to the log after it was cleaned. The history
    /// and the auth log say how many revisions and AI requests held it, never the text. Links, declarations and page
    /// links hold no text of the page.
    /// </summary>
    public async Task<KbRedactResult> RedactAsync(Guid id, KbRedactRequest request, CancellationToken ct)
    {
        var text = request.Text ?? "";
        if (text.Trim().Length < 4)
        {
            throw ApiException.Invalid("text", "The exact text, at least 4 characters.");
        }
        var redaction = new KbRedaction(text);
        await scans.CancelAsync(id, TimeSpan.FromSeconds(10));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var page = await revisions.LockAsync(id, ct);
        var rows = await db.KbRevisions.Where(r => r.PageId == id).ToListAsync(ct);
        var occurrences = 0;
        var changed = 0;
        foreach (var r in rows)
        {
            var changes = KbRevisions.Changes(r.Changes);
            var count = redaction.Count(r.Title) + redaction.Count(r.Summary) + redaction.Count(r.Content) + redaction.Count(r.Note)
                + r.Tags.Sum(redaction.Count) + changes.Sum(c => redaction.Count(c.Text));
            if (count == 0)
            {
                continue;
            }
            occurrences += count;
            changed++;
            r.Title = redaction.Replace(r.Title, 160)!;
            r.Summary = redaction.Replace(r.Summary, 300);
            r.Content = redaction.Replace(r.Content)!;
            r.Note = redaction.Replace(r.Note, 200);
            r.Tags = r.Tags.Select(t => redaction.Replace(t)!).ToList();
            r.Changes = KbRevisions.Serialize(changes.Select(c => c with { Text = redaction.Replace(c.Text, 300)! }).ToList());
        }
        var inPage = redaction.Count(page.Title) + redaction.Count(page.Summary) + redaction.Count(page.Content);
        var aiRequests = await RedactAiLogAsync(id, redaction, ct);
        if (changed == 0 && inPage == 0 && aiRequests == 0)
        {
            throw ApiException.Invalid("text", "The text doesn't occur in this page, its history or its AI requests.");
        }
        occurrences += inPage;
        var before = KbSnapshot.Of(page);
        page.Title = redaction.Replace(page.Title, 160)!;
        page.Summary = redaction.Replace(page.Summary, 300);
        page.Content = redaction.Replace(page.Content)!;
        var summary = $"1 text in {changed} revision{(changed == 1 ? "" : "s")}" +
            (aiRequests > 0 ? $" and {aiRequests} AI request{(aiRequests == 1 ? "" : "s")}" : "");
        // The redaction's own revision compares with the redacted history: only what it says.
        await revisions.CommitAsync(page, before with { Title = page.Title, Summary = page.Summary, Content = page.Content }, null,
            [new KbChange(KbChangeKinds.Redacted, summary)], ct);
        UdpViews.Audit(db, accessor, AuthEvents.KbRevisionRedacted, $"KB-{page.Number}: {summary}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // A scan started meanwhile waited for the row lock and found the old revision: it goes stale, and stops here.
        scans.Cancel(id);
        return new KbRedactResult(changed, occurrences, aiRequests, await DetailAsync(id, ct));
    }

    /// <summary>
    /// The text out of the AI request log of the page's tag scans (their requests and answers: scrubbed, but not of it)
    /// and out of their work items' results and errors; how many requests held it. Saved with the caller's transaction.
    /// </summary>
    private async Task<int> RedactAiLogAsync(Guid page, KbRedaction redaction, CancellationToken ct)
    {
        var work = await db.AiWork.Where(w => w.Purpose == KbTagsHandler.Name && w.SubjectId == page).ToListAsync(ct);
        foreach (var w in work)
        {
            w.Result = redaction.ReplaceInJson(w.Result).Value;
            w.LastError = redaction.Replace(w.LastError, 1000);
        }
        var ids = work.Select(w => (Guid?)w.Id).ToList();
        if (ids.Count == 0)
        {
            return 0;
        }
        var requests = await db.AiRequests.Where(r => ids.Contains(r.WorkId)).ToListAsync(ct);
        var count = 0;
        foreach (var r in requests)
        {
            var (request, inRequest) = redaction.ReplaceInJson(AiRequestLog.Gunzip(r.Request));
            var (response, inResponse) = redaction.ReplaceInJson(AiRequestLog.Gunzip(r.Response));
            var (error, inError) = redaction.ReplaceInJson(r.Error);
            if (inRequest + inResponse + inError == 0)
            {
                continue;
            }
            count++;
            var (requestBytes, cutRequest) = AiRequestLog.Gzip(request ?? "");
            r.Request = requestBytes!;
            r.Truncated |= cutRequest;
            if (response is not null)
            {
                var (responseBytes, cutResponse) = AiRequestLog.Gzip(response);
                r.Response = responseBytes;
                r.Truncated |= cutResponse;
            }
            r.Error = error is { Length: > 1000 } e ? e[..1000] : error;
        }
        return count;
    }

    // --- sensitive (§7.1) ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Marks the page sensitive at once, cancelling a scan that runs for it; unmarking takes both statements for the
    /// current revision, recorded as the revision's declaration. Both are revisions and go to the auth log.
    /// </summary>
    public async Task<KbPageDetail> SetSensitiveAsync(Guid id, KbSensitiveRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var page = await revisions.LockAsync(id, ct);
        if (page.Sensitive == request.Sensitive)
        {
            return await DetailAsync(id, ct);
        }
        KbStoredDeclaration? declaration = null;
        if (!request.Sensitive)
        {
            if (!KbStatementNames.Complete(request.Statements))
            {
                throw ApiException.Invalid("statements", "Confirm both statements to unmark the page.");
            }
            if (request.Revision is null)
            {
                throw ApiException.Invalid("revision", "The revision the statements are for.");
            }
            if (request.Revision != page.Revision)
            {
                throw Conflict(page);
            }
            var actor = revisions.Actor;
            declaration = new KbStoredDeclaration(actor.Id, actor.Name, Clock.UtcNow, KbStatementNames.Both, page.Revision);
        }
        var before = KbSnapshot.Of(page);
        page.Sensitive = request.Sensitive;
        await revisions.CommitAsync(page, before, null, null, ct, declaration);
        AuditSensitivity(page, declaration);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (page.Sensitive)
        {
            scans.Cancel(page.Id);
        }
        return await DetailAsync(id, ct);
    }

    private void AuditSensitivity(KbPageEntity page, KbStoredDeclaration? declaration) =>
        UdpViews.Audit(db, accessor, AuthEvents.KbPageSensitivityChanged, page.Sensitive
            ? $"KB-{page.Number} marked sensitive"
            : $"KB-{page.Number} unmarked, with both statements for revision {declaration?.Revision}");

    // --- presence and views --------------------------------------------------------------------------------------------

    /// <summary>An open editor's report (§5.7): who else is editing.</summary>
    public async Task<IReadOnlyList<KbEditor>> EditingAsync(Guid id, KbEditingRequest? request, CancellationToken ct)
    {
        await ExistsAsync(id, ct);
        var actor = revisions.Actor;
        if (actor.Id is { } user)
        {
            presence.Report(id, user, actor.Name, request?.Stopped == true);
        }
        return presence.Others(id, actor.Id);
    }

    public async Task ViewAsync(Guid id, CancellationToken ct)
    {
        await ExistsAsync(id, ct);
        if (revisions.Actor.Id is { } user)
        {
            await views.RecordAsync(db, id, user, ct);
        }
    }

    // --- helpers -------------------------------------------------------------------------------------------------------

    private async Task ExistsAsync(Guid id, CancellationToken ct)
    {
        if (!await db.KbPages.AnyAsync(p => p.Id == id, ct))
        {
            throw ApiException.NotFound("Page");
        }
    }

    private async Task<KbRevisionEntity> RevisionEntityAsync(Guid id, int number, CancellationToken ct) =>
        await db.KbRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.PageId == id && r.Number == number, ct)
        ?? throw ApiException.NotFound("Revision");

    private static KbSnapshot Snapshot(KbRevisionEntity r) => new(r.Title, r.Summary, r.Content,
        r.Tags.Select(t => (t, KbTagSource.Manual)).ToList(), r.Status, r.Sensitive, r.Archived, KbRevisions.Links(r.Links));

    public static KbDeclaration? Declaration(string? json) =>
        KbRevisions.Declaration(json) is { } d ? new KbDeclaration(d.ByName, d.At, d.Statements, d.Revision) : null;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
