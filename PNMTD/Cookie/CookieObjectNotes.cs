using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Tools;

namespace PNMTD.Cookie;

/// <summary>
/// Cookie's notes on hosts and sensors (docs/cookie-object-notes-plan.md §3): the rows the executor writes, what Cookie reads
/// back as its own words, and what a tool shows within the caller's rights and the note's areas (cookie-plan §9.2).
/// </summary>
public static class CookieObjectNotes
{
    /// <summary>A host's or a sensor's notes, newest first.</summary>
    public static IQueryable<CookieObjectNoteEntity> Of(PnmtdDbContext db, Guid objectId) =>
        db.CookieObjectNotes.AsNoTracking().Where(n => n.HostId == objectId || n.SensorId == objectId).OrderByDescending(n => n.At);

    public static Task<int> CountAsync(PnmtdDbContext db, Guid objectId, CancellationToken ct) => Of(db, objectId).CountAsync(ct);

    /// <summary>The object's name, and whether it is a host; null for neither a host nor a sensor.</summary>
    public static async Task<(string Name, bool IsHost)?> ObjectAsync(PnmtdDbContext db, Guid id, CancellationToken ct)
    {
        if (await db.Hosts.AsNoTracking().Where(h => h.Id == id).Select(h => h.Name).FirstOrDefaultAsync(ct) is { } host)
        {
            return (host, true);
        }
        if (await db.Sensors.AsNoTracking().Where(s => s.Id == id).Select(s => s.Name).FirstOrDefaultAsync(ct) is { } sensor)
        {
            return (sensor, false);
        }
        return null;
    }

    /// <summary>A note of Cookie's (§3.2), added to <paramref name="db"/> with its audit entry; the caller saves.</summary>
    public static async Task<CookieObjectNoteEntity> AddAsync(PnmtdDbContext db, Guid objectId, string text, Guid? caseId, long decisionId,
        IReadOnlyCollection<string> areas, CancellationToken ct)
    {
        var note = new CookieObjectNoteEntity
        {
            At = Clock.UtcNow, Text = CaseBook.Cut(text, 1500)!, CaseId = caseId, DecisionId = decisionId,
            Areas = areas.Count == 0 ? ["Monitoring"] : [.. areas.Distinct().Order()],
        };
        if (await db.Hosts.AsNoTracking().Where(h => h.Id == objectId).Select(h => new { h.Id, h.Name }).FirstOrDefaultAsync(ct) is { } host)
        {
            note.HostId = host.Id;
            AuditLog.Record(db, AuditEvents.CookieNoteAdded, AuditLog.Cut($"A note on {host.Name}: {text}", AuditLog.MaxDetail), AuditWho.Cookie("note"),
                AuditTarget.Of(AuditTargets.Host, host.Id, host.Name));
        }
        else
        {
            var sensor = await db.Sensors.AsNoTracking().Where(s => s.Id == objectId).Select(s => new { s.Id, s.Name, s.ParentId }).SingleAsync(ct);
            note.SensorId = sensor.Id;
            AuditLog.Record(db, AuditEvents.CookieNoteAdded, AuditLog.Cut($"A note on {sensor.Name}: {text}", AuditLog.MaxDetail), AuditWho.Cookie("note"),
                AuditTarget.Of(AuditTargets.Sensor, sensor.Id, sensor.Name, sensor.ParentId));
        }
        db.CookieObjectNotes.Add(note);
        return note;
    }

    /// <summary>
    /// What Cookie reads back in a case's brief (§3.3): its latest notes on the case's host and sensors, masked, as its own
    /// words and not wrapped as outside text. Null when there are none.
    /// </summary>
    public static async Task<IReadOnlyList<object>?> ForCaseAsync(PnmtdDbContext db, CookieCaseEntity c, IMasker privacy, CancellationToken ct) =>
        (await ForCasesAsync(db, [c], privacy, ct)).GetValueOrDefault(c.Id);

    /// <summary>The same for several cases at once, by case id; a case without notes has no entry.</summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<object>>> ForCasesAsync(PnmtdDbContext db, IReadOnlyCollection<CookieCaseEntity> cases, IMasker privacy,
        CancellationToken ct, int perCase = 5)
    {
        var objects = cases.ToDictionary(c => c.Id, c =>
        {
            var ids = new List<Guid>();
            if (c.HostId is { } host)
            {
                ids.Add(host);
            }
            foreach (var m in JsonSerializer.Deserialize<List<CaseMember>>(c.Members, CookieSettings.Json) ?? [])
            {
                if (m.Type == CaseMember.Sensor && Guid.TryParse(m.Id, out var sensor))
                {
                    ids.Add(sensor);
                }
            }
            return ids;
        });
        var all = objects.Values.SelectMany(x => x).Distinct().ToList();
        var result = new Dictionary<Guid, IReadOnlyList<object>>();
        if (all.Count == 0)
        {
            return result;
        }
        var notes = await db.CookieObjectNotes.AsNoTracking()
            .Where(n => n.HostId != null && all.Contains(n.HostId.Value) || n.SensorId != null && all.Contains(n.SensorId.Value))
            .OrderByDescending(n => n.At).Take(perCase * cases.Count * 2).ToListAsync(ct);
        if (notes.Count == 0)
        {
            return result;
        }
        var names = await NamesAsync(db, all, ct);
        foreach (var (caseId, ids) in objects)
        {
            var mine = notes.Where(n => ids.Contains(n.HostId ?? n.SensorId!.Value)).Take(perCase)
                .Select(n => (object)new { At = Local(n.At), On = names.GetValueOrDefault(n.HostId ?? n.SensorId!.Value), Text = privacy.Mask(n.Text) }).ToList();
            if (mine.Count > 0)
            {
                result[caseId] = mine;
            }
        }
        return result;
    }

    /// <summary>A tool's view (§3.3): the latest notes the caller may see, masked for Cookie, as they are for MCP.</summary>
    public static async Task<IReadOnlyList<object>?> ForToolAsync(PnmtdDbContext db, ToolContext ctx, Guid objectId, CancellationToken ct, int take = 10)
    {
        var notes = (await Of(db, objectId).Take(take * 4).ToListAsync(ct)).Where(n => CookieAccess.CanSee(ctx.Rights, n.Areas)).Take(take)
            .Select(n => (object)new { At = Local(n.At), Text = ctx.Privacy.Mask(n.Text), n.CaseId }).ToList();
        return notes.Count == 0 ? null : notes;
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(PnmtdDbContext db, List<Guid> ids, CancellationToken ct)
    {
        var hosts = await db.Hosts.AsNoTracking().Where(h => ids.Contains(h.Id)).Select(h => new { h.Id, h.Name }).ToListAsync(ct);
        var sensors = await db.Sensors.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, Name = s.Parent.Name + " / " + s.Name }).ToListAsync(ct);
        return hosts.Concat(sensors).ToDictionary(x => x.Id, x => x.Name);
    }

    private static string Local(DateTime at) => Clock.ToLocal(at).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// The pages' side of Cookie's notes (docs/cookie-object-notes-plan.md §5, §6): the notes a reader may see on a host or a
/// sensor, with the case's title for <i>Cookie: Use</i>, and a person with <i>Monitoring: Edit</i> removing one, audited.
/// </summary>
public sealed class CookieObjectNoteService(PnmtdDbContext db)
{
    public async Task<IReadOnlyList<CookieObjectNoteItem>> ListAsync(Guid objectId, ClaimsPrincipal user, CancellationToken ct)
    {
        var rights = user.Rights();
        var notes = (await CookieObjectNotes.Of(db, objectId).Take(200).ToListAsync(ct)).Where(n => CookieAccess.CanSee(rights, n.Areas)).ToList();
        var caseIds = notes.Select(n => n.CaseId).OfType<Guid>().Distinct().ToList();
        var titles = rights.Has(Area.Cookie, Level.Use) && caseIds.Count > 0
            ? await db.CookieCases.AsNoTracking().Where(c => caseIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Title, ct)
            : [];
        return [.. notes.Select(n => new CookieObjectNoteItem(n.Id, Clock.AsUtc(n.At), n.Text, n.CaseId, n.CaseId is { } id ? titles.GetValueOrDefault(id) : null, n.Areas))];
    }

    /// <summary>404 for a note the reader may not see, as for one that isn't there.</summary>
    public async Task RemoveAsync(long id, ClaimsPrincipal user, CancellationToken ct)
    {
        var note = await db.CookieObjectNotes.SingleOrDefaultAsync(n => n.Id == id, ct);
        if (note is null || !CookieAccess.CanSee(user.Rights(), note.Areas))
        {
            throw ApiException.NotFound("Note");
        }
        if (note.HostId is { } hostId && await db.Hosts.AsNoTracking().Where(h => h.Id == hostId).Select(h => new { h.Id, h.Name }).FirstOrDefaultAsync(ct) is { } host)
        {
            AuditLog.Target(AuditTarget.Of(AuditTargets.Host, host.Id, host.Name));
        }
        else if (note.SensorId is { } sensorId
            && await db.Sensors.AsNoTracking().Where(s => s.Id == sensorId).Select(s => new { s.Id, s.Name, s.ParentId }).FirstOrDefaultAsync(ct) is { } sensor)
        {
            AuditLog.Target(AuditTarget.Of(AuditTargets.Sensor, sensor.Id, sensor.Name, sensor.ParentId));
        }
        AuditLog.Summary($"Cookie's note of {Clock.ToLocal(note.At):yyyy-MM-dd HH:mm} removed: {CaseBook.Cut(note.Text, 300)}");
        db.CookieObjectNotes.Remove(note);
        await db.SaveChangesAsync(ct);
    }
}
