using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Availability;

/// <summary>
/// The notes on the plan's days (docs/availability-extensions-plan.md §6): everyone in the plan and <i>Availability: Edit</i> write
/// them; one edits one's own; one's own and, with <i>Edit</i>, anyone's are deleted. Whoever sees the plan reads them.
/// </summary>
public sealed partial class AvailabilityService
{
    /// <summary>The notes from <paramref name="from"/> to <paramref name="to"/>, oldest first, each with its author's current name.</summary>
    private static async Task<List<AvailabilityNoteItem>> NotesAsync(PnmtdDbContext db, DateOnly from, DateOnly to, Guid? me, CancellationToken ct)
    {
        var notes = await db.AvailabilityNotes.AsNoTracking().Where(n => n.Date >= from && n.Date <= to)
            .OrderBy(n => n.Date).ThenBy(n => n.CreatedAt).ThenBy(n => n.Id).ToListAsync(ct);
        if (notes.Count == 0)
        {
            return [];
        }
        var authors = notes.Select(n => n.UserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => authors.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return [.. notes.Select(n => ToItem(n, names.GetValueOrDefault(n.UserId), me))];
    }

    private static AvailabilityNoteItem ToItem(AvailabilityNoteEntity n, string? currentName, Guid? me) =>
        new(n.Id, n.Date, n.UserId, string.IsNullOrWhiteSpace(currentName) ? n.AuthorName : currentName, n.Text, n.CreatedAt, n.ChangedAt, n.UserId == me);

    /// <summary>One line of 1 to 300 characters: line breaks become spaces.</summary>
    private static string NoteText(string? text)
    {
        var line = string.Join(' ', (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
        new ApiValidator().Check(line.Length is > 0 and <= AvailabilityNoteEntity.MaxText, "text", $"A note of 1 to {AvailabilityNoteEntity.MaxText} characters.")
            .ThrowIfInvalid();
        return line;
    }

    private static string Day(DateOnly day) => day.ToString("d.M.yyyy", CultureInfo.InvariantCulture);

    public async Task<AvailabilityNoteItem> AddNoteAsync(AvailabilityNoteRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var me = user.UserId() ?? throw ApiException.Forbidden("Notes are written by a signed-in person.");
        if (!user.Has(Area.Availability, Level.Edit) && !await InPlanAsync(user, ct))
        {
            throw ApiException.Forbidden("Notes are written by everyone in the plan and by Availability: Edit.");
        }
        var text = NoteText(request.Text);
        await using var db = await dbs.CreateDbContextAsync(ct);
        if (await db.AvailabilityNotes.CountAsync(n => n.Date == request.Date, ct) >= AvailabilityNoteEntity.MaxPerDay)
        {
            throw ApiException.BadRequest($"At most {AvailabilityNoteEntity.MaxPerDay} notes a day.");
        }
        var name = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        var now = Clock.UtcNow;
        var note = new AvailabilityNoteEntity
        {
            Id = Guid.NewGuid(), Date = request.Date, UserId = me, AuthorName = CookieName(name ?? user.Identity?.Name), Text = text, CreatedAt = now, ChangedAt = now,
        };
        db.AvailabilityNotes.Add(note);
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"Availability: a note on {Day(note.Date)} added");
        return ToItem(note, name, me);
    }

    public async Task<AvailabilityNoteItem> ChangeNoteAsync(Guid id, AvailabilityNoteChange request, ClaimsPrincipal user, CancellationToken ct)
    {
        var me = user.UserId();
        var text = NoteText(request.Text);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var note = await db.AvailabilityNotes.SingleOrDefaultAsync(n => n.Id == id, ct) ?? throw ApiException.NotFound("Note");
        if (note.UserId != me)
        {
            throw ApiException.Forbidden("Only its author changes a note.");
        }
        note.Text = text;
        note.ChangedAt = Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        var name = await db.Users.AsNoTracking().Where(u => u.Id == note.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        AuditLog.Summary($"Availability: a note on {Day(note.Date)} changed");
        return ToItem(note, name, me);
    }

    public async Task DeleteNoteAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var me = user.UserId();
        await using var db = await dbs.CreateDbContextAsync(ct);
        var note = await db.AvailabilityNotes.SingleOrDefaultAsync(n => n.Id == id, ct) ?? throw ApiException.NotFound("Note");
        if (note.UserId != me && !user.Has(Area.Availability, Level.Edit))
        {
            throw ApiException.Forbidden("One's own notes; anyone's needs Availability: Edit.");
        }
        db.AvailabilityNotes.Remove(note);
        await db.SaveChangesAsync(ct);
        AuditLog.Summary(note.UserId == me
            ? $"Availability: a note on {Day(note.Date)} removed"
            : $"Availability: a note on {Day(note.Date)} by {note.AuthorName} removed");
    }
}
