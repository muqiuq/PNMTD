using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using PNMTD.Services.Domain;

namespace PNMTD.Responsibilities;

/// <summary>
/// Changing responsibilities (docs/responsibilities-plan.md §4, §5, §9): the fields and people, links, the global people, and the
/// pickers' lists. Each change writes a history row and is in the audit log. Information only: nothing is sent to anyone.
/// </summary>
public sealed class ResponsibilityService(PnmtdDbContext db, ResponsibilityQueries queries, SearchService search, LocationTree tree,
    IOptionsMonitor<KnowledgeBaseOptions> options, IServiceProvider services, IHttpContextAccessor accessor)
{
    /// <summary>A responsibility links at most this many pages, locations and hosts (§4.1).</summary>
    public const int MaxLinks = 200;

    public async Task<ResponsibilityDetail> CreateAsync(ResponsibilityRequest request, CancellationToken ct)
    {
        var (name, summary) = await ValidateAsync(request, null, null, null, ct);
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        var r = new ResponsibilityEntity
        {
            Id = Guid.NewGuid(), Name = name, NormalizedName = Normalize(name), Summary = summary, ResponsibleId = request.ResponsibleId,
            DeputyId = request.DeputyId, CreatedAt = now, CreatedBy = by.Id, CreatedByName = by.Name, UpdatedAt = now, UpdatedBy = by.Id,
            UpdatedByName = by.Name,
        };
        db.Responsibilities.Add(r);
        var people = await queries.PeopleAsync([r.ResponsibleId, r.DeputyId], ct);
        History(r.Id, now, by, ResponsibilityHistoryKind.Created, $"{name}: {PeopleText(people, r.ResponsibleId, r.DeputyId)}");
        AuditLog.Target(Target(r));
        AuditLog.Summary($"{name} created: {PeopleText(people, r.ResponsibleId, r.DeputyId)}");
        await db.SaveChangesAsync(ct);
        return await queries.GetAsync(r.Id, ct);
    }

    /// <summary>The whole form (§9.3): name, short description, responsible and deputy. A null person means nobody.</summary>
    public async Task<ResponsibilityDetail> UpdateAsync(Guid id, ResponsibilityRequest request, CancellationToken ct)
    {
        var r = await db.Responsibilities.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Responsibility");
        var (name, summary) = await ValidateAsync(request, id, r.ResponsibleId, r.DeputyId, ct);
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        var before = r.Name;
        var parts = new List<string>();
        if (r.Name != name)
        {
            History(id, now, by, ResponsibilityHistoryKind.Renamed, $"{r.Name} → {name}");
            parts.Add($"renamed {name}");
            r.Name = name;
            r.NormalizedName = Normalize(name);
        }
        if (r.Summary != summary)
        {
            History(id, now, by, ResponsibilityHistoryKind.SummaryChanged, summary ?? "(none)");
            parts.Add("short description changed");
            r.Summary = summary;
        }
        if (r.ResponsibleId != request.ResponsibleId || r.DeputyId != request.DeputyId)
        {
            var people = await queries.PeopleAsync([r.ResponsibleId, r.DeputyId, request.ResponsibleId, request.DeputyId], ct);
            var changes = new List<string>();
            if (r.ResponsibleId != request.ResponsibleId)
            {
                changes.Add($"responsible {NameOf(people, r.ResponsibleId)} → {NameOf(people, request.ResponsibleId)}");
            }
            if (r.DeputyId != request.DeputyId)
            {
                changes.Add($"deputy {NameOf(people, r.DeputyId)} → {NameOf(people, request.DeputyId)}");
            }
            var detail = string.Join(", ", changes);
            History(id, now, by, ResponsibilityHistoryKind.PeopleChanged, detail);
            parts.Add(detail);
            r.ResponsibleId = request.ResponsibleId;
            r.DeputyId = request.DeputyId;
        }
        if (parts.Count > 0)
        {
            Touch(r, now, by);
            AuditLog.Target(Target(r));
            AuditLog.Summary($"{before}: {string.Join("; ", parts)}");
            await db.SaveChangesAsync(ct);
        }
        return await queries.GetAsync(id, ct);
    }

    /// <summary>At once (decision 12): its links and history go with it; the audit log keeps what it was.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var r = await db.Responsibilities.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Responsibility");
        var links = await db.ResponsibilityLinks.CountAsync(l => l.ResponsibilityId == id, ct);
        var combinations = await db.ResponsibilityRedmine.CountAsync(c => c.ResponsibilityId == id, ct);
        var people = await queries.PeopleAsync([r.ResponsibleId, r.DeputyId], ct);
        AuditLog.Target(Target(r));
        AuditLog.Summary($"{r.Name} deleted with {Count(links, "link")} and {Count(combinations, "Redmine combination")}; it named "
            + PeopleText(people, r.ResponsibleId, r.DeputyId));
        db.Responsibilities.Remove(r);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The long description (§4.1), saved as a page's text is: at most <c>KnowledgeBase:MaxPageBytes</c>, from the revision it
    /// started from (409 <c>edit_conflict</c>), with the credentials check on new lines (422 <c>looks_like_credential</c>), a
    /// history row with both texts, and its files recorded as the responsibility's uses.
    /// </summary>
    public async Task<ResponsibilityDetail> SetDescriptionAsync(Guid id, ResponsibilityDescriptionRequest request, CancellationToken ct)
    {
        // Stored exactly as written; PostgreSQL's text can't hold U+0000.
        var text = (request.Content ?? "").Replace("\0", "");
        var max = options.CurrentValue.MaxPageBytes;
        new ApiValidator().Check(Encoding.UTF8.GetByteCount(text) <= max, "content", $"At most {max / 1024} KB.").ThrowIfInvalid();
        await SaveDescriptionAsync(id, text, request.Revision, request.AcknowledgeCredentials, null, ct);
        return await queries.GetAsync(id, ct);
    }

    /// <summary>
    /// The description from before a change in the history (§10.5), as a location's history restores a value: a new save of its
    /// own, on the current revision.
    /// </summary>
    public async Task<ResponsibilityDetail> RestoreAsync(Guid id, Guid rowId, CancellationToken ct)
    {
        var row = await db.ResponsibilityHistory.AsNoTracking().SingleOrDefaultAsync(h => h.Id == rowId && h.ResponsibilityId == id, ct)
            ?? throw ApiException.NotFound("History row");
        if (row.Kind != ResponsibilityHistoryKind.DescriptionChanged)
        {
            throw ApiException.Invalid("rowId", "Only a description can be restored.");
        }
        var revision = await db.Responsibilities.Where(r => r.Id == id).Select(r => r.Revision).SingleAsync(ct);
        var at = Clock.ToLocal(Clock.AsUtc(row.At));
        await SaveDescriptionAsync(id, row.OldValue ?? "", revision, true, $"restored from before {at:yyyy-MM-dd HH:mm}", ct);
        return await queries.GetAsync(id, ct);
    }

    private async Task SaveDescriptionAsync(Guid id, string text, int revision, bool acknowledgeCredentials, string? restored, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // One save of a description at a time, so two saves from the same revision can't both pass the check.
        var bytes = id.ToByteArray();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [BitConverter.ToInt64(bytes, 0) ^ BitConverter.ToInt64(bytes, 8)], ct);
        var r = await db.Responsibilities.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Responsibility");
        if (r.Description == text)
        {
            await transaction.CommitAsync(ct);
            return;
        }
        if (revision != r.Revision)
        {
            throw ApiException.EditConflict(r.Revision, r.UpdatedByName, r.UpdatedAt,
                $"{r.UpdatedByName ?? "Someone"} saved {r.Name}'s description in the meantime.");
        }
        if (!acknowledgeCredentials)
        {
            LocationValues.CheckCredentials(text, r.Description);
        }
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        var old = r.Description;
        var facts = MarkdownText.Read(text);
        var diff = LineDiff.Compare(old, text);
        var lines = $"+{diff.Added} −{diff.Removed} lines";
        r.Description = text;
        r.PlainText = facts.PlainText;
        r.Revision++;
        Touch(r, now, by);
        History(id, now, by, ResponsibilityHistoryKind.DescriptionChanged, restored is null ? lines : $"{lines}, {restored}", old, text);
        await FileUses.RecordAsync(db, facts.FileIds, FileOwner.Responsibility(id), ct);
        AuditLog.Target(Target(r));
        AuditLog.Summary($"{r.Name}: description {(restored ?? "changed")} ({lines})");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The global responsible and deputy (§5): both at once, a null meaning nobody.</summary>
    public async Task<ResponsibilityGlobal> SetGlobalAsync(ResponsibilityGlobalRequest request, CancellationToken ct)
    {
        var settings = await db.ResponsibilitySettings.SingleOrDefaultAsync(s => s.Id == ResponsibilitySettingsEntity.SingletonId, ct);
        if (settings is null)
        {
            settings = new ResponsibilitySettingsEntity { Id = ResponsibilitySettingsEntity.SingletonId };
            db.ResponsibilitySettings.Add(settings);
        }
        var v = new ApiValidator();
        await CheckPeopleAsync(v, request.ResponsibleId, request.DeputyId, settings.GlobalResponsibleId, settings.GlobalDeputyId, ct);
        v.ThrowIfInvalid();
        if (settings.GlobalResponsibleId != request.ResponsibleId || settings.GlobalDeputyId != request.DeputyId)
        {
            var people = await queries.PeopleAsync([settings.GlobalResponsibleId, settings.GlobalDeputyId, request.ResponsibleId, request.DeputyId], ct);
            var changes = new List<string>();
            if (settings.GlobalResponsibleId != request.ResponsibleId)
            {
                changes.Add($"global responsible {NameOf(people, settings.GlobalResponsibleId)} → {NameOf(people, request.ResponsibleId)}");
            }
            if (settings.GlobalDeputyId != request.DeputyId)
            {
                changes.Add($"global deputy {NameOf(people, settings.GlobalDeputyId)} → {NameOf(people, request.DeputyId)}");
            }
            var by = LocationActor.Of(accessor);
            settings.GlobalResponsibleId = request.ResponsibleId;
            settings.GlobalDeputyId = request.DeputyId;
            settings.UpdatedAt = Clock.UtcNow;
            settings.UpdatedBy = by.Id;
            settings.UpdatedByName = by.Name;
            AuditLog.Record(db, AuditEvents.GlobalResponsibleChanged, Capitalize(string.Join(", ", changes)), AuditWho.From(accessor));
            await db.SaveChangesAsync(ct);
        }
        return await queries.GlobalAsync(ct);
    }

    /// <summary>A link to a page, a location or a host (§4.1): once each, at most <see cref="MaxLinks"/>.</summary>
    public async Task<ResponsibilityDetail> LinkAsync(Guid id, ResponsibilityLinkRequest request, CancellationToken ct)
    {
        var r = await db.Responsibilities.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Responsibility");
        new ApiValidator()
            .Check(request.Kind is KbLinkKind.Page or KbLinkKind.Location or KbLinkKind.Host, "kind", "A page, a location or a host.")
            .Check(request.Id is not null, "id", "Which one.")
            .ThrowIfInvalid();
        var kind = request.Kind!.Value;
        var target = request.Id!.Value;
        var what = await DescribeAsync(kind, target, ct) ?? throw ApiException.NotFound(kind.ToString());
        var links = db.ResponsibilityLinks.Where(l => l.ResponsibilityId == id);
        var linked = kind switch
        {
            KbLinkKind.Page => await links.AnyAsync(l => l.PageId == target, ct),
            KbLinkKind.Location => await links.AnyAsync(l => l.LocationId == target, ct),
            _ => await links.AnyAsync(l => l.HostId == target, ct),
        };
        if (linked)
        {
            throw ApiException.Conflict(ResponsibilityErrors.AlreadyLinked, $"{r.Name} links {what} already.");
        }
        if (await links.CountAsync(ct) >= MaxLinks)
        {
            throw ApiException.Conflict(ResponsibilityErrors.TooManyLinks, $"A responsibility links at most {MaxLinks} pages, locations and hosts.");
        }
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        db.ResponsibilityLinks.Add(new ResponsibilityLinkEntity
        {
            Id = Guid.NewGuid(), ResponsibilityId = id, PageId = kind == KbLinkKind.Page ? target : null,
            LocationId = kind == KbLinkKind.Location ? target : null, HostId = kind == KbLinkKind.Host ? target : null,
            CreatedAt = now, CreatedBy = by.Id, CreatedByName = by.Name,
        });
        History(id, now, by, ResponsibilityHistoryKind.LinkAdded, what);
        Touch(r, now, by);
        // The object's id as a subject, so that its own History shows the link.
        AuditLog.Record(db, AuditEvents.ResponsibilityLinked, $"{r.Name}: linked {what}", AuditWho.From(accessor),
            AuditTarget.Of(AuditTargets.Responsibility, r.Id, r.Name, target));
        await db.SaveChangesAsync(ct);
        return await queries.GetAsync(id, ct);
    }

    public async Task<ResponsibilityDetail> UnlinkAsync(Guid id, Guid linkId, CancellationToken ct)
    {
        var r = await db.Responsibilities.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Responsibility");
        var link = await db.ResponsibilityLinks.SingleOrDefaultAsync(l => l.Id == linkId && l.ResponsibilityId == id, ct)
            ?? throw ApiException.NotFound("Link");
        var target = ResponsibilityQueries.RefOf(link);
        var what = await DescribeAsync(target.Kind, target.Id, ct) ?? $"a deleted {target.Kind.ToString().ToLowerInvariant()}";
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        db.ResponsibilityLinks.Remove(link);
        History(id, now, by, ResponsibilityHistoryKind.LinkRemoved, what);
        Touch(r, now, by);
        AuditLog.Record(db, AuditEvents.ResponsibilityUnlinked, $"{r.Name}: unlinked {what}", AuditWho.From(accessor),
            AuditTarget.Of(AuditTargets.Responsibility, r.Id, r.Name, target.Id));
        await db.SaveChangesAsync(ct);
        return await queries.GetAsync(id, ct);
    }

    // --- Redmine combinations (§6) ------------------------------------------------------------------------------------

    /// <summary>What the dialog picks from: the projects PNMT reads and Redmine's trackers, through the worker's lookups.</summary>
    public async Task<ResponsibilityRedmineLookups> RedmineLookupsAsync(CancellationToken ct)
    {
        if (!queries.RedmineConfigured)
        {
            return new ResponsibilityRedmineLookups(false, [], [], null);
        }
        try
        {
            var lookups = await services.GetRequiredService<RedmineService>().LookupsAsync(ct);
            return new ResponsibilityRedmineLookups(true, ResponsibilityQueries.ProjectsOf(lookups),
                lookups.Trackers.Select(t => new RedmineNamed(t.Id, t.Name ?? $"#{t.Id}")).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(), null);
        }
        catch (RedmineException e)
        {
            return new ResponsibilityRedmineLookups(true, [], [], e.Message);
        }
        catch (ApiException e) when (e.Status == StatusCodes.Status504GatewayTimeout)
        {
            return new ResponsibilityRedmineLookups(true, [], [], e.Message);
        }
    }

    public async Task<RedmineCombinationList> CombinationsAsync(int? project, Guid? responsibility, Guid? user, CancellationToken ct)
    {
        RequireRedmine();
        return new RedmineCombinationList(await queries.CombinationsAsync(responsibility, project, user, ct));
    }

    /// <summary>A Redmine project and tracker on this responsibility, with who handles it (§6.1): once across all of them.</summary>
    public async Task<ResponsibilityDetail> AddCombinationAsync(Guid id, RedmineCombinationRequest request, CancellationToken ct)
    {
        RequireRedmine();
        var r = await db.Responsibilities.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Responsibility");
        var (project, tracker, user, note) = await ValidateCombinationAsync(request, null, ct);
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        var c = new ResponsibilityRedmineEntity
        {
            Id = Guid.NewGuid(), ResponsibilityId = id, ProjectId = project.Id, ProjectName = project.Name, TrackerId = tracker?.Id,
            TrackerName = tracker?.Name, UserId = user.Id, Note = note, CreatedAt = now, CreatedBy = by.Id, CreatedByName = by.Name,
            UpdatedAt = now, UpdatedBy = by.Id, UpdatedByName = by.Name,
        };
        db.ResponsibilityRedmine.Add(c);
        var what = $"{Combination(project.Path, tracker?.Name)}: {user.Name}";
        History(id, now, by, ResponsibilityHistoryKind.RedmineAdded, what);
        Touch(r, now, by);
        AuditLog.Record(db, AuditEvents.ResponsibilityRedmineSaved, $"{r.Name}: added {what}", AuditWho.From(accessor), Target(r));
        await db.SaveChangesAsync(ct);
        return await queries.GetAsync(id, ct);
    }

    /// <summary>A change: its project, tracker, user or note, or the responsibility it is on (<c>responsibilityId</c>).</summary>
    public async Task<ResponsibilityDetail> UpdateCombinationAsync(Guid id, Guid combinationId, RedmineCombinationRequest request, CancellationToken ct)
    {
        RequireRedmine();
        var c = await db.ResponsibilityRedmine.SingleOrDefaultAsync(x => x.Id == combinationId && x.ResponsibilityId == id, ct)
            ?? throw ApiException.NotFound("Redmine combination");
        var from = await db.Responsibilities.SingleAsync(x => x.Id == id, ct);
        var to = request.ResponsibilityId is { } other && other != id
            ? await db.Responsibilities.SingleOrDefaultAsync(x => x.Id == other, ct) ?? throw ApiException.Invalid("responsibilityId", "No such responsibility.")
            : from;
        var (project, tracker, user, note) = await ValidateCombinationAsync(request, c, ct);
        var people = await queries.PeopleAsync([c.UserId], ct);
        var before = $"{Combination(c.ProjectName, c.TrackerName)}: {NameOf(people, c.UserId)}{(c.Note is null ? "" : $" ({c.Note})")}";
        var after = $"{Combination(project.Path, tracker?.Name)}: {user.Name}{(note is null ? "" : $" ({note})")}";
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        c.ProjectId = project.Id;
        c.ProjectName = project.Name;
        c.TrackerId = tracker?.Id;
        c.TrackerName = tracker?.Name;
        c.UserId = user.Id;
        c.Note = note;
        c.ResponsibilityId = to.Id;
        c.UpdatedAt = now;
        c.UpdatedBy = by.Id;
        c.UpdatedByName = by.Name;
        if (to.Id != from.Id)
        {
            History(from.Id, now, by, ResponsibilityHistoryKind.RedmineRemoved, $"{before}, moved to {to.Name}");
            History(to.Id, now, by, ResponsibilityHistoryKind.RedmineAdded, $"{after}, moved from {from.Name}");
            Touch(from, now, by);
        }
        else
        {
            History(to.Id, now, by, ResponsibilityHistoryKind.RedmineChanged, $"{before} → {after}");
        }
        Touch(to, now, by);
        AuditLog.Record(db, AuditEvents.ResponsibilityRedmineSaved,
            to.Id == from.Id ? $"{to.Name}: {before} → {after}" : $"{from.Name}: {before}, moved to {to.Name}: {after}", AuditWho.From(accessor),
            AuditTarget.Of(AuditTargets.Responsibility, to.Id, to.Name, from.Id));
        await db.SaveChangesAsync(ct);
        return await queries.GetAsync(to.Id, ct);
    }

    public async Task<ResponsibilityDetail> DeleteCombinationAsync(Guid id, Guid combinationId, CancellationToken ct)
    {
        var c = await db.ResponsibilityRedmine.SingleOrDefaultAsync(x => x.Id == combinationId && x.ResponsibilityId == id, ct)
            ?? throw ApiException.NotFound("Redmine combination");
        var r = await db.Responsibilities.SingleAsync(x => x.Id == id, ct);
        var people = await queries.PeopleAsync([c.UserId], ct);
        var what = $"{Combination(c.ProjectName, c.TrackerName)}: {NameOf(people, c.UserId)}";
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        db.ResponsibilityRedmine.Remove(c);
        History(id, now, by, ResponsibilityHistoryKind.RedmineRemoved, what);
        Touch(r, now, by);
        AuditLog.Record(db, AuditEvents.ResponsibilityRedmineDeleted, $"{r.Name}: removed {what}", AuditWho.From(accessor), Target(r));
        await db.SaveChangesAsync(ct);
        return await queries.GetAsync(id, ct);
    }

    private void RequireRedmine()
    {
        if (!queries.RedmineConfigured)
        {
            throw new ApiException(StatusCodes.Status404NotFound, ResponsibilityErrors.RedmineNotConfigured, "Redmine isn't configured.");
        }
    }

    /// <summary>
    /// A project PNMT reads, a tracker of Redmine's (or none), an active user, a short note; once per project and tracker. An
    /// unchanged project, tracker or user is kept as it is, even when Redmine no longer knows it or the user was deactivated.
    /// </summary>
    private async Task<(RedmineCombinationProject Project, RedmineNamed? Tracker, ResponsibilityPerson User, string? Note)> ValidateCombinationAsync(
        RedmineCombinationRequest request, ResponsibilityRedmineEntity? current, CancellationToken ct)
    {
        var note = Text.Trimmed(request.Note);
        var v = new ApiValidator()
            .Check(request.ProjectId is not null, "projectId", "A project.")
            .Check(request.UserId is not null, "userId", "Who handles it.")
            .Check(note is null || note.Length <= 200, "note", "At most 200 characters.")
            .Check(note is null || (!note.Contains('\n') && !note.Contains('\r')), "note", "One line.");
        v.ThrowIfInvalid();
        var keepProject = current is not null && current.ProjectId == request.ProjectId;
        var keepTracker = current is not null && current.TrackerId == request.TrackerId;
        RedmineLookups? lookups = null;
        if (!keepProject || !keepTracker)
        {
            try
            {
                lookups = await services.GetRequiredService<RedmineService>().LookupsAsync(ct);
            }
            catch (RedmineException e)
            {
                throw new ApiException(StatusCodes.Status503ServiceUnavailable, ResponsibilityErrors.RedmineUnreachable,
                    $"Redmine can't be reached: projects and trackers can't be listed right now. {e.Message}");
            }
        }
        var projects = lookups is null ? [] : ResponsibilityQueries.ProjectsOf(lookups);
        var project = keepProject
            ? projects.FirstOrDefault(p => p.Id == current!.ProjectId) ?? new RedmineCombinationProject(current!.ProjectId, current.ProjectName, null, current.ProjectName, 0)
            : projects.FirstOrDefault(p => p.Id == request.ProjectId);
        RedmineNamed? tracker = null;
        if (request.TrackerId is { } trackerId)
        {
            tracker = keepTracker
                ? lookups?.Trackers.Where(t => t.Id == trackerId).Select(t => new RedmineNamed(t.Id, t.Name ?? $"#{t.Id}")).FirstOrDefault()
                    ?? new RedmineNamed(trackerId, current!.TrackerName ?? $"#{trackerId}")
                : lookups!.Trackers.Where(t => t.Id == trackerId).Select(t => new RedmineNamed(t.Id, t.Name ?? $"#{t.Id}")).FirstOrDefault();
            v.Check(tracker is not null, "trackerId", "Not one of Redmine's trackers.");
        }
        v.Check(project is not null, "projectId", "Not one of PNMT's Redmine projects.");
        var userId = request.UserId!.Value;
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new ResponsibilityPerson(u.Id, u.DisplayName, u.IsActive))
            .SingleOrDefaultAsync(ct);
        v.Check(user is not null && (user.Active || current?.UserId == userId), "userId", "An active user.");
        v.ThrowIfInvalid();
        var other = await (from c in db.ResponsibilityRedmine
                           where c.ProjectId == project!.Id && c.TrackerId == request.TrackerId && c.Id != (current == null ? Guid.Empty : current.Id)
                           join r in db.Responsibilities on c.ResponsibilityId equals r.Id
                           join u in db.Users on c.UserId equals u.Id into users
                           from u in users.DefaultIfEmpty()
                           select new { r.Name, User = u == null ? null : u.DisplayName }).FirstOrDefaultAsync(ct);
        if (other is not null)
        {
            throw ApiException.Conflict(ResponsibilityErrors.DuplicateCombination,
                $"{Combination(project!.Path, tracker?.Name)} is already listed on {other.Name}, with {other.User ?? "nobody"}.");
        }
        return (project!, tracker, user!, note);
    }

    private static string Combination(string project, string? tracker) => $"{project} · {tracker ?? "every tracker"}";

    public async Task<Paged<ResponsibilityHistoryItem>> HistoryAsync(Guid id, PageQuery query, CancellationToken ct)
    {
        if (!await db.Responsibilities.AnyAsync(r => r.Id == id, ct))
        {
            throw ApiException.NotFound("Responsibility");
        }
        return await Paging.PageAsync(db.ResponsibilityHistory.AsNoTracking().Where(h => h.ResponsibilityId == id)
            .OrderByDescending(h => h.At).ThenByDescending(h => h.Kind)
            .Select(h => new ResponsibilityHistoryItem(h.Id, h.At, h.ByName, h.Kind, h.Detail)), query, ct);
    }

    public async Task<ResponsibilityHistoryDetail> HistoryRowAsync(Guid id, Guid rowId, CancellationToken ct) =>
        await db.ResponsibilityHistory.AsNoTracking().Where(h => h.Id == rowId && h.ResponsibilityId == id)
            .Select(h => new ResponsibilityHistoryDetail(h.Id, h.At, h.ByName, h.Kind, h.Detail, h.OldValue, h.NewValue))
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("History row");

    /// <summary>Whom a responsibility can name: the active users (§11), never their contact details.</summary>
    public async Task<ResponsibilityPeople> PeopleAsync(CancellationToken ct) => new(
        await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => new ResponsibilityPersonOption(u.Id, u.DisplayName, u.Username, u.RedmineUserId != null))
            .ToListAsync(ct));

    /// <summary>Pages, locations and hosts by name, for the link picker (§9.3), without Monitoring: View.</summary>
    public Task<SearchResult> TargetsAsync(string? q, CancellationToken ct) => search.ObjectsAsync(q, 8, ct);

    // --- Rules -------------------------------------------------------------------------------------------------------------

    private async Task<(string Name, string? Summary)> ValidateAsync(ResponsibilityRequest request, Guid? id, Guid? responsible, Guid? deputy,
        CancellationToken ct)
    {
        var name = Text.Trimmed(request.Name) ?? "";
        var summary = Text.Trimmed(request.Summary);
        var v = new ApiValidator()
            .Check(name.Length is >= 1 and <= 100, "name", "1 to 100 characters.")
            .Check(!name.Contains('\n') && !name.Contains('\r'), "name", "One line.")
            .Check(summary is null || summary.Length <= 300, "summary", "At most 300 characters.")
            .Check(summary is null || (!summary.Contains('\n') && !summary.Contains('\r')), "summary", "One line.");
        await CheckPeopleAsync(v, request.ResponsibleId, request.DeputyId, responsible, deputy, ct);
        v.ThrowIfInvalid();
        var normalized = Normalize(name);
        if (await db.Responsibilities.AnyAsync(r => r.NormalizedName == normalized && r.Id != id, ct))
        {
            throw ApiException.Conflict(ResponsibilityErrors.DuplicateName, $"A responsibility named {name} exists already.");
        }
        return (name, summary);
    }

    /// <summary>
    /// A newly chosen person is an active user; one who stays chosen may have been deactivated since (§4.2). The deputy is
    /// never the responsible.
    /// </summary>
    private async Task CheckPeopleAsync(ApiValidator v, Guid? responsible, Guid? deputy, Guid? currentResponsible, Guid? currentDeputy,
        CancellationToken ct)
    {
        (string Field, Guid? Id, Guid? Current)[] chosen = [("responsibleId", responsible, currentResponsible), ("deputyId", deputy, currentDeputy)];
        var ids = chosen.Where(c => c.Id is not null && c.Id != c.Current).Select(c => c.Id!.Value).Distinct().ToList();
        var active = ids.Count == 0 ? [] : await db.Users.Where(u => ids.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        foreach (var (field, id, current) in chosen)
        {
            v.Check(id is null || id == current || active.Contains(id.Value), field, "An active user.");
        }
        v.Check(responsible is null || deputy is null || responsible != deputy, "deputyId", "Someone other than the responsible.");
    }

    /// <summary>"page KB-42 Replacing a UPS battery", "location Zurich › Building A", "host sw-core-01"; null when it doesn't exist.</summary>
    private async Task<string?> DescribeAsync(KbLinkKind kind, Guid target, CancellationToken ct)
    {
        switch (kind)
        {
            case KbLinkKind.Page:
                var page = await db.KbPages.AsNoTracking().Where(p => p.Id == target).Select(p => new { p.Number, p.Title }).SingleOrDefaultAsync(ct);
                return page is null ? null : $"page KB-{page.Number} {page.Title}";
            case KbLinkKind.Location:
                var snapshot = await tree.GetAsync(ct);
                return snapshot.Contains(target) ? $"location {snapshot.Path(target)}" : null;
            default:
                var host = await db.Hosts.AsNoTracking().Where(h => h.Id == target).Select(h => h.Name).SingleOrDefaultAsync(ct);
                return host is null ? null : $"host {host}";
        }
    }

    private void History(Guid id, DateTime at, LocationActor by, ResponsibilityHistoryKind kind, string? detail, string? oldValue = null,
        string? newValue = null) =>
        db.ResponsibilityHistory.Add(new ResponsibilityHistoryEntity
        {
            Id = Guid.NewGuid(), ResponsibilityId = id, At = at, ByUserId = by.Id, ByName = by.Name, Kind = kind,
            Detail = detail is null ? null : Agents.Cuts.Cut(detail, 2000), OldValue = oldValue, NewValue = newValue,
        });

    private static void Touch(ResponsibilityEntity r, DateTime now, LocationActor by)
    {
        r.UpdatedAt = now;
        r.UpdatedBy = by.Id;
        r.UpdatedByName = by.Name;
    }

    private static string PeopleText(IReadOnlyDictionary<Guid, ResponsibilityPerson> people, Guid? responsible, Guid? deputy) =>
        $"responsible {NameOf(people, responsible)}, deputy {NameOf(people, deputy)}";

    private static string NameOf(IReadOnlyDictionary<Guid, ResponsibilityPerson> people, Guid? id) =>
        id is null ? "nobody" : ResponsibilityQueries.Person(people, id)?.Name ?? "a removed user";

    private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static AuditTarget Target(ResponsibilityEntity r) => AuditTarget.Of(AuditTargets.Responsibility, r.Id, r.Name);

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
}
