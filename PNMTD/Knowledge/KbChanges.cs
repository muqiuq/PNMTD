using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Knowledge;

/// <summary>
/// The recent changes on top of the knowledge base (docs/knowledge-base-extensions-plan.md §6): the pages changed in the last
/// <c>RecentChangesDays</c>, each once with its newest change, newest first, at most <c>RecentChangesMax</c>. A change is a
/// revision that created the page, changed its title, summary or content (<c>IsEdit</c>), changed its status, or restored an old
/// revision. Tags, links, the sensitive mark, <i>Written with AI</i>, archiving, AI tags and redacting are housekeeping: the
/// history has them. Archived pages are left out; sensitive ones are in, since people read them.
/// </summary>
public sealed class KbChanges(PnmtdDbContext db, IOptionsMonitor<KnowledgeBaseOptions> kb)
{
    public const string Created = "created";
    public const string Edited = "edited";
    public const string Restored = "restored";
    public const string Status = "status";

    // jsonb containment on kb_revisions.Changes, as KbRevisions writes it (camelCase).
    private const string StatusJson = $$"""[{"kind":"{{KbChangeKinds.Status}}"}]""";
    private const string RestoredJson = $$"""[{"kind":"{{KbChangeKinds.Restored}}"}]""";

    public async Task<KbRecentChanges> RecentAsync(CancellationToken ct)
    {
        var options = kb.CurrentValue;
        var days = Math.Max(1, options.RecentChangesDays);
        var since = Clock.UtcNow.AddDays(-days);
        var max = Math.Clamp(options.RecentChangesMax, 1, 50);
        var changes = db.KbRevisions.AsNoTracking()
            .Where(r => r.At >= since
                && (r.IsEdit || EF.Functions.JsonContains(r.Changes, StatusJson) || EF.Functions.JsonContains(r.Changes, RestoredJson)))
            .Where(r => db.KbPages.Any(p => p.Id == r.PageId && p.ArchivedAt == null));
        var newest = await changes.GroupBy(r => r.PageId)
            .Select(g => new { PageId = g.Key, Number = g.Max(r => r.Number), At = g.Max(r => r.At), Count = g.Count(), People = g.Select(r => r.ByName).Distinct().Count() })
            .OrderByDescending(x => x.At).ThenByDescending(x => x.Number).Take(max).ToListAsync(ct);
        if (newest.Count == 0)
        {
            return new KbRecentChanges(days, []);
        }
        var ids = newest.Select(n => n.PageId).ToList();
        var pages = await db.KbPages.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Number, p.Title, p.Status, p.Sensitive, p.WrittenWithAi }).ToDictionaryAsync(p => p.Id, ct);
        var numbers = newest.Select(n => n.Number).Distinct().ToList();
        var rows = await db.KbRevisions.AsNoTracking().Where(r => ids.Contains(r.PageId) && numbers.Contains(r.Number))
            .Select(r => new { r.PageId, r.Number, r.At, r.ByName, r.Added, r.Removed, r.Note, r.Changes, r.IsEdit })
            .ToListAsync(ct);
        var revisions = rows.Where(r => newest.Any(n => n.PageId == r.PageId && n.Number == r.Number)).ToDictionary(r => r.PageId);
        var result = new List<KbRecentChange>();
        foreach (var n in newest)
        {
            if (!pages.TryGetValue(n.PageId, out var page) || !revisions.TryGetValue(n.PageId, out var r))
            {
                continue;
            }
            var (kind, detail) = What(KbRevisions.Changes(r.Changes), r.IsEdit);
            result.Add(new KbRecentChange(page.Id, page.Number, page.Title, page.Status, page.Sensitive, page.WrittenWithAi, r.Number, kind, detail,
                Clock.AsUtc(r.At), r.ByName, r.Added, r.Removed, r.Note, n.Count, n.People));
        }
        return new KbRecentChanges(days, result);
    }

    /// <summary>What a change is called: created before restored before edited before a status alone.</summary>
    internal static (string Kind, string? Detail) What(IReadOnlyList<KbChange> changes, bool isEdit)
    {
        if (changes.Any(c => c.Kind == KbChangeKinds.Created))
        {
            return (Created, null);
        }
        if (changes.FirstOrDefault(c => c.Kind == KbChangeKinds.Restored) is { } restored)
        {
            return (Restored, restored.Text);
        }
        var status = changes.FirstOrDefault(c => c.Kind == KbChangeKinds.Status);
        return isEdit ? (Edited, status?.Text) : (Status, status?.Text);
    }
}
