using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Locations;
using PNMTD.Models.Db;

namespace PNMTD.Knowledge;

/// <summary>A location's value as a machine may read it: its own, or inherited from <see cref="InheritedFrom"/>.</summary>
public sealed record MachineLocationValue(Guid FieldId, string Key, string Label, LocationFieldType Type, string? Value, Guid? InheritedFrom);

/// <summary>
/// What any machine reader may see (docs/locations-knowledge-plan.md §7.4): today the AI tasks, with package eight MCP
/// and with package nine Cookie, which must use it too. Query filters, not a check at the end, so a feature can't forget
/// it by building its own query:
/// <list type="bullet">
/// <item><b>Pages</b> that aren't sensitive, and only as they are now: nothing here leads to kb_revisions, so a redacted
///   or edited-out text doesn't come back through the history. Archived pages are included and say so
///   (<c>ArchivedAt</c>). Their tags, links and summary are those of readable pages.</item>
/// <item><b>Files</b> used by the current content of a readable page, or by a readable field's value.</item>
/// <item><b>Location values</b> of fields not marked sensitive (nor archived), inherited ones included.</item>
/// <item><b>Page links</b> whose two pages are both readable (docs/knowledge-base-extensions-plan.md §7.5).</item>
/// </list>
/// The tag task refuses a sensitive page twice: in the endpoint and here (§7.3).
/// </summary>
public static class MachineAccess
{
    /// <summary>The pages a machine may read, untracked.</summary>
    public static IQueryable<KbPageEntity> Pages(PnmtdDbContext db) => db.KbPages.AsNoTracking().Where(p => !p.Sensitive);

    /// <summary>
    /// The page links a machine may read, untracked: both pages readable, so a sensitive page never appears through a link from a
    /// readable one. Each link is two rows, one per page.
    /// </summary>
    public static IQueryable<KbLinkEntity> PageLinks(PnmtdDbContext db) => db.KbLinks.AsNoTracking()
        .Where(l => l.TargetPageId != null
            && db.KbPages.Any(p => p.Id == l.PageId && !p.Sensitive)
            && db.KbPages.Any(p => p.Id == l.TargetPageId && !p.Sensitive));

    /// <summary>
    /// The files a machine may read, untracked: not deleted, and referenced by the current content of a page that isn't
    /// sensitive, or by the value of a field that isn't sensitive. A use (file_uses) narrows the search; the reference in
    /// the text decides, since a use outlives the revision that made it.
    /// </summary>
    public static IQueryable<FileEntity> Files(PnmtdDbContext db) => db.Files.FromSql(
        $"""
        SELECT f.* FROM files f
        WHERE f."DeletedAt" IS NULL AND (
            EXISTS (SELECT 1 FROM file_uses u JOIN kb_pages p ON p."Id" = u."PageId"
                    WHERE u."FileId" = f."Id" AND NOT p."Sensitive" AND strpos(p."Content", f."Id"::text) > 0)
            OR EXISTS (SELECT 1 FROM file_uses u
                       JOIN location_values v ON v."LocationId" = u."LocationId"
                       JOIN location_fields lf ON lf."Id" = v."FieldId"
                       WHERE u."FileId" = f."Id" AND NOT lf."Sensitive" AND lf."ArchivedAt" IS NULL
                         AND strpos(v."Value"::text, f."Id"::text) > 0))
        """).AsNoTracking();

    /// <summary>The stored values a machine may read, untracked: of fields that are neither sensitive nor archived.</summary>
    public static IQueryable<LocationValueEntity> LocationValues(PnmtdDbContext db) =>
        db.LocationValues.AsNoTracking().Where(v => db.LocationFields.Any(f => f.Id == v.FieldId && !f.Sensitive && f.ArchivedAt == null));

    /// <summary>
    /// A location's values as a machine may read them: its own, and for inherited fields without one, the nearest
    /// ancestor's (§3.3). Only what <see cref="LocationValues(PnmtdDbContext)"/> lets through, so a sensitive field's value
    /// isn't inherited either.
    /// </summary>
    public static async Task<IReadOnlyList<MachineLocationValue>> LocationValuesAsync(PnmtdDbContext db, LocationTreeSnapshot tree, Guid locationId,
        CancellationToken ct)
    {
        var lineage = tree.Lineage(locationId).Select(n => n.Id).ToList();
        if (lineage.Count == 0)
        {
            return [];
        }
        var fields = await db.LocationFields.AsNoTracking().Where(f => !f.Sensitive && f.ArchivedAt == null).OrderBy(f => f.Position).ToListAsync(ct);
        var values = await LocationValues(db).Where(v => lineage.Contains(v.LocationId) && v.Value != null).ToListAsync(ct);
        var result = new List<MachineLocationValue>();
        foreach (var field in fields)
        {
            if (values.FirstOrDefault(v => v.FieldId == field.Id && v.LocationId == locationId) is { } own)
            {
                result.Add(new MachineLocationValue(field.Id, field.Key, field.Label, field.Type, own.Value, null));
                continue;
            }
            if (!field.Inherited)
            {
                continue;
            }
            // Nearest first: the lineage runs from the top down to the location itself.
            foreach (var ancestor in lineage.Take(lineage.Count - 1).Reverse())
            {
                if (values.FirstOrDefault(v => v.FieldId == field.Id && v.LocationId == ancestor) is { } inherited)
                {
                    result.Add(new MachineLocationValue(field.Id, field.Key, field.Label, field.Type, inherited.Value, ancestor));
                    break;
                }
            }
        }
        return result;
    }
}
