using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Knowledge;

/// <summary>
/// Tags (docs/locations-knowledge-plan.md §6.4): 2–40 characters, letters (umlauts included), digits and <c>-</c>, stored
/// in lower case and unique, so <i>Backup</i> is <i>backup</i>. Admins rename (an existing name merges the two) and
/// delete them; a delete is a revision of every page that had the tag.
/// </summary>
public sealed partial class KbTags(PnmtdDbContext db, IHttpContextAccessor accessor, KbRevisions revisions)
{
    public const int MinLength = 2;

    public const int MaxLength = 40;

    /// <summary>A page has at most this many (§6.1).</summary>
    public const int MaxPerPage = 30;

    /// <summary>
    /// The tag a person or the AI meant, or null when there is none: trimmed, without a leading <c>#</c>, in lower case,
    /// with spaces and underscores as <c>-</c>; then only letters, digits and single dashes, 2–40 characters.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var tag = Separators().Replace(raw.Normalize(System.Text.NormalizationForm.FormC).Trim().TrimStart('#').ToLowerInvariant(), "-");
        tag = Dashes().Replace(tag, "-").Trim('-');
        return tag.Length is >= MinLength and <= MaxLength && Valid().IsMatch(tag) ? tag : null;
    }

    /// <summary>The tags of the names given, created where missing (not saved), in the order given, each once.</summary>
    public static async Task<List<KbTagEntity>> ResolveAsync(PnmtdDbContext db, IEnumerable<string> names, CancellationToken ct)
    {
        var wanted = names.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }
        var known = await db.KbTags.Where(t => wanted.Contains(t.NormalizedName)).ToListAsync(ct);
        known.AddRange(db.KbTags.Local.Where(t => wanted.Contains(t.NormalizedName) && !known.Contains(t)));
        var result = new List<KbTagEntity>();
        foreach (var name in wanted)
        {
            var tag = known.FirstOrDefault(t => t.NormalizedName == name);
            if (tag is null)
            {
                tag = new KbTagEntity { Id = Guid.NewGuid(), Name = name, NormalizedName = name, CreatedAt = Clock.UtcNow };
                db.KbTags.Add(tag);
                known.Add(tag);
            }
            result.Add(tag);
        }
        return result;
    }

    /// <summary>Every tag with its pages (not archived), the most used first; <paramref name="search"/> a substring.</summary>
    public async Task<IReadOnlyList<KbTagItem>> ListAsync(string? search, CancellationToken ct)
    {
        var pattern = Text.Pattern(search?.TrimStart('#'));
        var tags = db.KbTags.AsNoTracking();
        if (pattern is not null)
        {
            tags = tags.Where(t => EF.Functions.ILike(t.Name, pattern, @"\"));
        }
        var rows = await tags
            .Select(t => new { t.Id, t.Name, Pages = db.KbPageTags.Count(pt => pt.TagId == t.Id && db.KbPages.Any(p => p.Id == pt.PageId && p.ArchivedAt == null)) })
            .OrderByDescending(t => t.Pages).ThenBy(t => t.Name)
            .ToListAsync(ct);
        return rows.Select(t => new KbTagItem(t.Id, t.Name, t.Pages)).ToList();
    }

    /// <summary>
    /// The <paramref name="limit"/> most used tags among <paramref name="pages"/>, for the AI's vocabulary (§7.3): there,
    /// only the pages a machine may read, so a tag of sensitive pages alone doesn't leave PNMT.
    /// </summary>
    public static async Task<IReadOnlyList<string>> VocabularyAsync(IQueryable<KbPageEntity> pages, PnmtdDbContext db, int limit, CancellationToken ct) =>
        await db.KbTags.AsNoTracking()
            .Select(t => new { t.Name, Pages = db.KbPageTags.Count(pt => pt.TagId == t.Id && pages.Any(p => p.Id == pt.PageId)) })
            .Where(t => t.Pages > 0)
            .OrderByDescending(t => t.Pages).ThenBy(t => t.Name)
            .Take(limit)
            .Select(t => t.Name)
            .ToListAsync(ct);

    /// <summary>
    /// Renames a tag; to an existing tag's name, merges it into that one: its pages get the other tag (manual if either
    /// was), and it is deleted. The pages' search text follows. A vocabulary change, not a page's: no revisions.
    /// </summary>
    public async Task<KbTagChange> RenameAsync(Guid id, KbTagRequest request, CancellationToken ct)
    {
        var name = Normalize(request.Name);
        if (name is null)
        {
            throw ApiException.Invalid("name", "2–40 letters, digits and dashes.");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tag = await db.KbTags.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw ApiException.NotFound("Tag");
        var target = await db.KbTags.SingleOrDefaultAsync(t => t.NormalizedName == name && t.Id != id, ct);
        var pageIds = await db.KbPageTags.Where(pt => pt.TagId == id).Select(pt => pt.PageId).ToListAsync(ct);
        if (target is null)
        {
            tag.Name = name;
            tag.NormalizedName = name;
        }
        else
        {
            var moving = await db.KbPageTags.Where(pt => pt.TagId == id).ToListAsync(ct);
            var existing = await db.KbPageTags.Where(pt => pt.TagId == target.Id && pageIds.Contains(pt.PageId)).ToListAsync(ct);
            foreach (var old in moving)
            {
                db.KbPageTags.Remove(old);
                if (existing.FirstOrDefault(e => e.PageId == old.PageId) is { } both)
                {
                    if (old.Source == KbTagSource.Manual)
                    {
                        both.Source = KbTagSource.Manual;
                    }
                }
                else
                {
                    db.KbPageTags.Add(new KbPageTagEntity
                    {
                        PageId = old.PageId, TagId = target.Id, Source = old.Source, AddedAt = old.AddedAt, AddedBy = old.AddedBy,
                    });
                }
            }
            db.KbTags.Remove(tag);
        }
        await db.SaveChangesAsync(ct);
        await RefreshTagsTextAsync(db, pageIds, ct);
        await tx.CommitAsync(ct);
        var result = target ?? tag;
        var pages = await db.KbPageTags.CountAsync(pt => pt.TagId == result.Id && db.KbPages.Any(p => p.Id == pt.PageId && p.ArchivedAt == null), ct);
        return new KbTagChange(new KbTagItem(result.Id, result.Name, pages), target is not null, pageIds.Count);
    }

    /// <summary>Removes a tag from every page, as a revision of each (§6.4), and writes <c>KbTagDeleted</c>.</summary>
    public async Task<int> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tag = await db.KbTags.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw ApiException.NotFound("Tag");
        var pageIds = await db.KbPageTags.Where(pt => pt.TagId == id).Select(pt => pt.PageId).ToListAsync(ct);
        foreach (var pageId in pageIds)
        {
            var page = await revisions.LockAsync(pageId, ct);
            var before = KbSnapshot.Of(page);
            page.Tags.RemoveAll(t => t.TagId == id);
            await revisions.CommitAsync(page, before, "The tag was deleted.", null, ct);
        }
        db.KbTags.Remove(tag);
        UdpViews.Audit(db, accessor, AuditEvents.KbTagDeleted, $"{tag.Name}: removed from {pageIds.Count} page{(pageIds.Count == 1 ? "" : "s")}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return pageIds.Count;
    }

    /// <summary>The pages' TagsText (the search's weight A) from their tags as they are now.</summary>
    internal static async Task RefreshTagsTextAsync(PnmtdDbContext db, IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        if (pageIds.Count == 0)
        {
            return;
        }
        var pages = await db.KbPages.Include(p => p.Tags).ThenInclude(t => t.Tag).Where(p => pageIds.Contains(p.Id)).ToListAsync(ct);
        foreach (var page in pages)
        {
            page.TagsText = KbRevisions.TagsText(page);
        }
        await db.SaveChangesAsync(ct);
    }

    [GeneratedRegex(@"[\s_]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex Dashes();

    [GeneratedRegex(@"^[\p{L}\p{N}]+(?:-[\p{L}\p{N}]+)*$")]
    private static partial Regex Valid();
}
