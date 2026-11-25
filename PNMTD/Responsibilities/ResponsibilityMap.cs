using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;

namespace PNMTD.Responsibilities;

/// <summary>
/// The overview (docs/responsibilities-extensions-plan.md §6, §7): the responsibilities on a map, with their people, the objects they link
/// and the notes, and their saved places: one layout for everyone, saved by hand as on the network map. A responsibility without a
/// saved place is placed automatically; people and objects always are.
/// </summary>
public sealed class ResponsibilityMapService(PnmtdDbContext db, ResponsibilityQueries queries, LocationTree tree, IHttpContextAccessor accessor)
{
    /// <summary>The most linked objects drawn (§6.1); the rest are counted.</summary>
    public const int MaxObjects = 600;

    public const int MaxPositions = 2000;

    /// <summary>At most this many notes on the overview (§7.2), each at most <see cref="MaxNoteLength"/> characters.</summary>
    public const int MaxNotes = 500;

    public const int MaxNoteLength = 2000;

    /// <summary>Far beyond any map; keeps a bad client from writing places the layout can't handle.</summary>
    public const double MaxCoordinate = 100_000;

    public async Task<ResponsibilityMap> MapAsync(ResponsibilityMapQuery query, CancellationToken ct)
    {
        var list = await queries.ListAsync(null, false, ct);
        var positions = await db.ResponsibilityMapPositions.AsNoTracking().ToDictionaryAsync(p => p.ResponsibilityId, ct);
        var nodes = list.Items.Select(i =>
            {
                var p = positions.GetValueOrDefault(i.Id);
                return new ResponsibilityMapNode(i.Id, i.Name, i.Summary, i.Color, p?.X, p?.Y,
                    p is null ? null : new ResponsibilityMapPosition(p.UpdatedAt, p.UpdatedByName), i.Responsible?.Id, i.Deputy?.Id, i.GlobalStandsIn,
                    i.Attention, i.Links, i.Combinations);
            })
            .ToList();

        // The people they name and the global people, with the availability plan's short code and colour: no contact details.
        var ids = list.Items.SelectMany(i => new[] { i.Responsible?.Id, i.Deputy?.Id }).Append(list.Global.Responsible?.Id).Append(list.Global.Deputy?.Id)
            .OfType<Guid>().Distinct().ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).OrderBy(u => u.DisplayName)
            .Select(u => new ResponsibilityMapPerson(u.Id, u.DisplayName, u.ShortCode, u.Color, u.IsActive)).ToListAsync(ct);

        var objects = await ObjectsAsync(query, ct);
        var notes = await db.ResponsibilityMapNotes.AsNoTracking().OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToListAsync(ct);
        return new ResponsibilityMap(nodes, people, [.. objects.Take(MaxObjects)], [.. notes.Select(Note)], list.Global, objects.Count,
            objects.Count > MaxObjects, positions.Count);
    }

    /// <summary>The linked objects of the layers asked for, each with the responsibilities that link it; the most shared first.</summary>
    private async Task<List<ResponsibilityMapObject>> ObjectsAsync(ResponsibilityMapQuery query, CancellationToken ct)
    {
        if (!query.Hosts && !query.Locations && !query.Pages)
        {
            return [];
        }
        var links = await db.ResponsibilityLinks.AsNoTracking()
            .Where(l => (query.Hosts && l.HostId != null) || (query.Locations && l.LocationId != null) || (query.Pages && l.PageId != null))
            .ToListAsync(ct);
        var byTarget = links.GroupBy(ResponsibilityQueries.RefOf)
            .ToDictionary(g => g.Key, g => g.Select(l => new ResponsibilityMapObjectLink(l.ResponsibilityId, l.Id)).ToList());
        var names = await KbLinkNames.ResolveAsync(db, tree, [.. byTarget.Keys], ct);
        return names.Where(n => n.Exists)
            .Select(n => new ResponsibilityMapObject(n.Kind, n.Id, n.Name, n.Number, byTarget[new KbLinkRef(n.Kind, n.Id)]))
            .OrderByDescending(o => o.Links.Count).ThenBy(o => o.Kind).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Id)
            .ToList();
    }

    /// <summary>
    /// Saves responsibilities' and notes' places, adding or updating; those deleted between the drag and the save are skipped. A note's
    /// move doesn't change when its text was last changed.
    /// </summary>
    public async Task<ResponsibilityMapSaveResult> SaveAsync(ResponsibilityMapPositionsRequest request, CancellationToken ct)
    {
        var list = request.Responsibilities ?? [];
        var moves = request.Notes ?? [];
        new ApiValidator()
            .Check(list.Count + moves.Count is >= 1 and <= MaxPositions, "responsibilities", $"Between 1 and {MaxPositions:N0} places.")
            .Check(list.Concat(moves).All(p => Valid(p.X) && Valid(p.Y)), "responsibilities",
                $"X and Y are numbers between -{MaxCoordinate:N0} and {MaxCoordinate:N0}.")
            .Check(list.Select(p => p.Id).Distinct().Count() == list.Count, "responsibilities", "Each responsibility once.")
            .Check(moves.Select(p => p.Id).Distinct().Count() == moves.Count, "notes", "Each note once.")
            .ThrowIfInvalid();
        var noteIds = moves.Select(p => p.Id).ToList();
        var notes = await db.ResponsibilityMapNotes.Where(n => noteIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        foreach (var p in moves.Where(p => notes.ContainsKey(p.Id)))
        {
            notes[p.Id].X = p.X;
            notes[p.Id].Y = p.Y;
        }
        var ids = list.Select(p => p.Id).ToList();
        var known = await db.Responsibilities.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var rows = await db.ResponsibilityMapPositions.Where(p => ids.Contains(p.ResponsibilityId)).ToDictionaryAsync(p => p.ResponsibilityId, ct);
        var who = AuditWho.From(accessor);
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        var saved = list.Where(p => known.ContainsKey(p.Id)).ToList();
        foreach (var p in saved)
        {
            if (!rows.TryGetValue(p.Id, out var row))
            {
                row = new ResponsibilityMapPositionEntity { ResponsibilityId = p.Id };
                db.ResponsibilityMapPositions.Add(row);
            }
            row.X = p.X;
            row.Y = p.Y;
            row.UpdatedAt = now;
            row.UpdatedBy = by.Id;
            row.UpdatedByName = by.Name;
        }
        await db.SaveChangesAsync(ct);
        var names = saved.Select(p => (p.Id, known[p.Id])).OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase).ToList();
        var movedNotes = moves.Count(p => notes.ContainsKey(p.Id));
        AuditLog.Target(AuditTarget.Many(AuditTargets.Responsibility, names));
        var what = new List<string>();
        if (names.Count > 0)
        {
            what.Add(names.Count == 1 ? names[0].Item2 : $"{names.Count} responsibilities");
        }
        if (movedNotes > 0)
        {
            what.Add(movedNotes == 1 ? "1 note" : $"{movedNotes} notes");
        }
        AuditLog.Summary(what.Count == 0 ? "Overview: nothing saved, everything was gone"
            : $"Overview: saved the place{(names.Count + movedNotes == 1 ? "" : "s")} of {string.Join(" and ", what)}");
        return new ResponsibilityMapSaveResult(saved.Count + movedNotes, list.Count + moves.Count - saved.Count - movedNotes);
    }

    /// <summary>One responsibility placed automatically again. 404 when it has no saved place.</summary>
    public async Task ClearAsync(Guid id, CancellationToken ct)
    {
        var row = await db.ResponsibilityMapPositions.SingleOrDefaultAsync(p => p.ResponsibilityId == id, ct) ?? throw ApiException.NotFound("Saved place");
        var name = await db.Responsibilities.Where(r => r.Id == id).Select(r => r.Name).SingleAsync(ct);
        db.ResponsibilityMapPositions.Remove(row);
        await db.SaveChangesAsync(ct);
        AuditLog.Target(AuditTarget.Of(AuditTargets.Responsibility, id, name));
        AuditLog.Summary($"Overview: {name} back to automatic");
    }

    /// <summary>Every responsibility placed automatically again; the notes keep their places. Answers how many had one.</summary>
    public async Task<int> ResetAsync(CancellationToken ct)
    {
        var rows = await db.ResponsibilityMapPositions.ToListAsync(ct);
        var ids = rows.Select(r => r.ResponsibilityId).ToList();
        var names = await db.Responsibilities.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.Name }).ToListAsync(ct);
        db.ResponsibilityMapPositions.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        AuditLog.Target(AuditTarget.Many(AuditTargets.Responsibility, [.. names.Select(n => (n.Id, n.Name)).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)]));
        AuditLog.Summary($"Overview: all responsibilities back to automatic ({rows.Count} saved place{(rows.Count == 1 ? "" : "s")} cleared)");
        return rows.Count;
    }

    // --- notes (§7) ----------------------------------------------------------------------------------------------------------

    /// <summary>A note where the person is looking; 409 <c>too_many_notes</c> beyond <see cref="MaxNotes"/>.</summary>
    public async Task<ResponsibilityMapNote> CreateNoteAsync(ResponsibilityNoteRequest request, CancellationToken ct)
    {
        var (text, colour, width) = ValidNote(request);
        new ApiValidator()
            .Check(request.X is { } x && Valid(x) && request.Y is { } y && Valid(y), "x", $"X and Y are numbers between -{MaxCoordinate:N0} and {MaxCoordinate:N0}.")
            .ThrowIfInvalid();
        if (await db.ResponsibilityMapNotes.CountAsync(ct) >= MaxNotes)
        {
            throw ApiException.Conflict(ResponsibilityErrors.TooManyNotes, $"The overview holds at most {MaxNotes} notes.");
        }
        if (!request.AcknowledgeCredentials)
        {
            LocationValues.CheckCredentials(text, null);
        }
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        var note = new ResponsibilityMapNoteEntity
        {
            Id = Guid.NewGuid(), Text = text, Colour = colour.ToString(), Width = width.ToString(), X = request.X!.Value, Y = request.Y!.Value,
            CreatedAt = now, CreatedBy = by.Id, CreatedByName = by.Name, UpdatedAt = now, UpdatedBy = by.Id, UpdatedByName = by.Name,
        };
        db.ResponsibilityMapNotes.Add(note);
        AuditLog.Target(AuditTarget.Of(AuditTargets.ResponsibilityNote, note.Id, Short(text)));
        AuditLog.Summary($"Overview note: {Short(text)}");
        await db.SaveChangesAsync(ct);
        return Note(note);
    }

    /// <summary>Its text, colour and width; its place stays (it is saved with the layout).</summary>
    public async Task<ResponsibilityMapNote> UpdateNoteAsync(Guid id, ResponsibilityNoteRequest request, CancellationToken ct)
    {
        var note = await db.ResponsibilityMapNotes.SingleOrDefaultAsync(n => n.Id == id, ct) ?? throw ApiException.NotFound("Note");
        var (text, colour, width) = ValidNote(request);
        if (!request.AcknowledgeCredentials)
        {
            LocationValues.CheckCredentials(text, note.Text);
        }
        if (note.Text != text || note.Colour != colour.ToString() || note.Width != width.ToString())
        {
            var by = LocationActor.Of(accessor);
            note.Text = text;
            note.Colour = colour.ToString();
            note.Width = width.ToString();
            note.UpdatedAt = Clock.UtcNow;
            note.UpdatedBy = by.Id;
            note.UpdatedByName = by.Name;
            AuditLog.Target(AuditTarget.Of(AuditTargets.ResponsibilityNote, note.Id, Short(text)));
            AuditLog.Summary($"Overview note changed: {Short(text)}");
            await db.SaveChangesAsync(ct);
        }
        return Note(note);
    }

    public async Task DeleteNoteAsync(Guid id, CancellationToken ct)
    {
        var note = await db.ResponsibilityMapNotes.SingleOrDefaultAsync(n => n.Id == id, ct) ?? throw ApiException.NotFound("Note");
        db.ResponsibilityMapNotes.Remove(note);
        AuditLog.Target(AuditTarget.Of(AuditTargets.ResponsibilityNote, note.Id, Short(note.Text)));
        AuditLog.Summary($"Overview note deleted: {Short(note.Text)}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>1 to <see cref="MaxNoteLength"/> characters, the ends trimmed; a colour and a width (yellow and medium unless given).</summary>
    private static (string Text, ResponsibilityNoteColour Colour, ResponsibilityNoteWidth Width) ValidNote(ResponsibilityNoteRequest request)
    {
        // PostgreSQL's text can't hold U+0000.
        var text = (request.Text ?? "").Replace("\0", "").Trim();
        new ApiValidator()
            .Check(text.Length is >= 1 and <= MaxNoteLength, "text", $"1 to {MaxNoteLength:N0} characters.")
            .ThrowIfInvalid();
        return (text, request.Colour ?? ResponsibilityNoteColour.Yellow, request.Width ?? ResponsibilityNoteWidth.Medium);
    }

    private static ResponsibilityMapNote Note(ResponsibilityMapNoteEntity n) => new(n.Id, n.Text,
        Enum.TryParse<ResponsibilityNoteColour>(n.Colour, out var colour) ? colour : ResponsibilityNoteColour.Yellow,
        Enum.TryParse<ResponsibilityNoteWidth>(n.Width, out var width) ? width : ResponsibilityNoteWidth.Medium,
        n.X, n.Y, n.CreatedAt, n.CreatedByName, n.UpdatedAt, n.UpdatedByName);

    /// <summary>The note's first line, at most 60 characters, for the audit log.</summary>
    private static string Short(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= 60 ? line : $"{line[..59]}…";
    }

    private static bool Valid(double v) => double.IsFinite(v) && Math.Abs(v) <= MaxCoordinate;
}
