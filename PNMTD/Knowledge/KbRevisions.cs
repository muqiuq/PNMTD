using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Knowledge;

/// <summary>The kinds of a revision's list of what changed (§6.3); the UI names them.</summary>
public static class KbChangeKinds
{
    public const string Created = "created";
    public const string Content = "content";
    public const string Title = "title";
    public const string Summary = "summary";
    public const string Tags = "tags";
    public const string Status = "status";
    public const string Links = "links";
    public const string Files = "files";
    public const string Sensitive = "sensitive";
    public const string Archived = "archived";
    public const string Restored = "restored";
    public const string AiTags = "aiTags";
    public const string Redacted = "redacted";
}

/// <summary>The statements of §7.2, as a declaration names them.</summary>
public static class KbStatementNames
{
    public const string NoCredentials = "noCredentials";
    public const string NoPersonalData = "noPersonalData";

    public static readonly IReadOnlyList<string> Both = [NoCredentials, NoPersonalData];

    public static bool Complete(KbStatements? statements) => statements is { NoCredentials: true, NoPersonalData: true };
}

/// <summary>A page's link as a revision stores it (<c>kb_revisions.Links</c>).</summary>
public sealed record KbLinkRef(KbLinkKind Kind, Guid Id);

/// <summary>The stored declaration (<c>kb_revisions.Declaration</c>): §7.2's who, when, which statements, which revision.</summary>
public sealed record KbStoredDeclaration(Guid? ByUserId, string ByName, DateTime At, IReadOnlyList<string> Statements, int Revision);

/// <summary>What a revision snapshots (§6.3), and what two of them are compared by.</summary>
public sealed record KbSnapshot(
    string Title,
    string? Summary,
    string Content,
    IReadOnlyList<(string Name, KbTagSource Source)> Tags,
    KbPageStatus Status,
    bool Sensitive,
    bool Archived,
    IReadOnlyList<KbLinkRef> Links)
{
    /// <summary>The page as it is: its tags with <c>Tag</c> loaded, and its links.</summary>
    public static KbSnapshot Of(KbPageEntity page) => new(page.Title, page.Summary, page.Content,
        page.Tags.Select(t => (t.Tag!.Name, t.Source)).OrderBy(t => t.Name, StringComparer.Ordinal).ToList(),
        page.Status, page.Sensitive, page.ArchivedAt is not null, KbRevisions.LinksOf(page));
}

/// <summary>Who does something: the user's id and their display name at the time (§8.2); the CLI without a user.</summary>
public sealed record KbActor(Guid? Id, string Name)
{
    public static KbActor Of(IHttpContextAccessor accessor) => Of(accessor.HttpContext?.User);

    public static KbActor Of(ClaimsPrincipal? user)
    {
        var name = user?.FindFirstValue(SessionClaims.DisplayName) is { Length: > 0 } display ? display : user?.Identity?.Name;
        return new KbActor(user?.UserId(), string.IsNullOrWhiteSpace(name) ? "cli" : name.Length > 200 ? name[..200] : name);
    }
}

/// <summary>
/// Every change to a page is a revision (docs/locations-knowledge-plan.md §6.3): a full snapshot, with who and when,
/// the note, the content's lines added and removed (LineDiff), the list of what changed, and for AI scans and unmarking
/// sensitive the declaration. Each commit also derives what the server reads from the Markdown (§6.7, §6.5, §5.5): the
/// plain text and the tags' text for the search, the page links behind <i>Linked from</i>, and the files used.
/// Changes run in a transaction with the page's row locked (<see cref="LockAsync"/>), so two saves from the same revision
/// can't both pass the conflict check.
/// </summary>
public sealed class KbRevisions(PnmtdDbContext db, IHttpContextAccessor accessor)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>The request's user, or <see cref="As"/>: the AI tag scan writes in the name of who made the statements.</summary>
    public KbActor Actor => As ?? KbActor.Of(accessor);

    public KbActor? As { get; init; }

    /// <summary>The page, tracked, with its tags and links, its row locked until the caller's transaction ends.</summary>
    public async Task<KbPageEntity> LockAsync(Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM kb_pages WHERE "Id" = {id} FOR UPDATE""", ct);
        return await db.KbPages.Include(p => p.Tags).ThenInclude(t => t.Tag).Include(p => p.Links).AsSplitQuery()
            .SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw ApiException.NotFound("Page");
    }

    /// <summary>
    /// The revision after a change (not saved): <paramref name="before"/> null for a new page. The page's revision and
    /// its updated fields move on; <paramref name="extra"/> goes after what the comparison finds.
    /// </summary>
    public async Task<KbRevisionEntity> CommitAsync(KbPageEntity page, KbSnapshot? before, string? note, IEnumerable<KbChange>? extra,
        CancellationToken ct, KbStoredDeclaration? declaration = null)
    {
        var after = KbSnapshot.Of(page);
        var facts = MarkdownText.Read(page.Content);
        page.PlainText = facts.PlainText;
        page.TagsText = TagsText(page);
        await RefsAsync(page, facts.PageNumbers, ct);
        var filesAdded = await FileUses.RecordAsync(db, facts.FileIds, page.Id, null, ct);
        var diff = before is null || before.Content != after.Content ? LineDiff.Compare(before?.Content ?? "", after.Content) : null;
        var changes = Compare(before, after, diff).ToList();
        if (filesAdded.Count > 0)
        {
            changes.Add(new KbChange(KbChangeKinds.Files, $"+{filesAdded.Count}"));
        }
        changes.AddRange(extra ?? []);
        var actor = Actor;
        var now = Clock.UtcNow;
        page.Revision++;
        page.UpdatedAt = now;
        page.UpdatedBy = actor.Id;
        page.UpdatedByName = actor.Name;
        var revision = new KbRevisionEntity
        {
            Id = Guid.NewGuid(),
            PageId = page.Id,
            Number = page.Revision,
            At = now,
            ByUserId = actor.Id,
            ByName = actor.Name,
            Title = page.Title,
            Summary = page.Summary,
            Content = page.Content,
            Tags = after.Tags.Select(t => t.Name).ToList(),
            Status = page.Status,
            Sensitive = page.Sensitive,
            Archived = page.ArchivedAt is not null,
            Links = JsonSerializer.Serialize(after.Links, Json),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Added = diff?.Added ?? 0,
            Removed = diff?.Removed ?? 0,
            Changes = JsonSerializer.Serialize(changes, Json),
            Declaration = declaration is null ? null : JsonSerializer.Serialize(declaration, Json),
            IsEdit = before is null || before.Title != after.Title || before.Summary != after.Summary || diff is not null,
        };
        db.KbRevisions.Add(revision);
        return revision;
    }

    /// <summary>What changed between two snapshots, in the order of §6.3.</summary>
    public static IEnumerable<KbChange> Compare(KbSnapshot? before, KbSnapshot after, LineDiffResult? diff)
    {
        if (before is null)
        {
            yield return new KbChange(KbChangeKinds.Created, "");
            if (after.Tags.Count > 0)
            {
                yield return new KbChange(KbChangeKinds.Tags, Cut(string.Join(" ", after.Tags.Select(t => "+" + t.Name))));
            }
            if (after.Links.Count > 0)
            {
                yield return new KbChange(KbChangeKinds.Links, $"+{after.Links.Count}");
            }
            if (after.Sensitive)
            {
                yield return new KbChange(KbChangeKinds.Sensitive, "marked");
            }
            yield break;
        }
        if (diff is not null)
        {
            yield return new KbChange(KbChangeKinds.Content, $"+{diff.Added} −{diff.Removed}");
        }
        if (before.Title != after.Title)
        {
            yield return new KbChange(KbChangeKinds.Title, Cut($"{before.Title} → {after.Title}"));
        }
        if (before.Summary != after.Summary)
        {
            yield return new KbChange(KbChangeKinds.Summary, "");
        }
        var oldTags = before.Tags.ToDictionary(t => t.Name, t => t.Source, StringComparer.Ordinal);
        var newTags = after.Tags.ToDictionary(t => t.Name, t => t.Source, StringComparer.Ordinal);
        var tagParts = newTags.Keys.Where(t => !oldTags.ContainsKey(t)).Select(t => "+" + t)
            .Concat(oldTags.Keys.Where(t => !newTags.ContainsKey(t)).Select(t => "−" + t))
            .Concat(newTags.Where(t => oldTags.TryGetValue(t.Key, out var was) && was == KbTagSource.Ai && t.Value == KbTagSource.Manual)
                .Select(t => "✓" + t.Key))
            .ToList();
        if (tagParts.Count > 0)
        {
            yield return new KbChange(KbChangeKinds.Tags, Cut(string.Join(" ", tagParts)));
        }
        if (before.Status != after.Status)
        {
            yield return new KbChange(KbChangeKinds.Status, $"{StatusName(before.Status)} → {StatusName(after.Status)}");
        }
        var linksAdded = after.Links.Count(l => !before.Links.Contains(l));
        var linksRemoved = before.Links.Count(l => !after.Links.Contains(l));
        if (linksAdded + linksRemoved > 0)
        {
            yield return new KbChange(KbChangeKinds.Links, $"+{linksAdded} −{linksRemoved}");
        }
        if (before.Sensitive != after.Sensitive)
        {
            yield return new KbChange(KbChangeKinds.Sensitive, after.Sensitive ? "marked" : "unmarked");
        }
        if (before.Archived != after.Archived)
        {
            yield return new KbChange(KbChangeKinds.Archived, after.Archived ? "archived" : "restored");
        }
    }

    public static string StatusName(KbPageStatus status) => status switch
    {
        KbPageStatus.Draft => "Draft",
        KbPageStatus.NeedsReview => "Needs review",
        _ => "Published",
    };

    /// <summary>The page's tag names, separated by spaces: the search's weight A with the title (§6.7).</summary>
    public static string TagsText(KbPageEntity page) =>
        string.Join(" ", page.Tags.Select(t => t.Tag?.Name).OfType<string>().Order(StringComparer.Ordinal));

    public static IReadOnlyList<KbLinkRef> LinksOf(KbPageEntity page) => page.Links.Select(RefOf).OrderBy(l => l.Kind).ThenBy(l => l.Id).ToList();

    public static KbLinkRef RefOf(KbLinkEntity l) => l.LocationId is { } location ? new KbLinkRef(KbLinkKind.Location, location)
        : l.HostId is { } host ? new KbLinkRef(KbLinkKind.Host, host) : new KbLinkRef(KbLinkKind.Sensor, l.SensorId!.Value);

    public static IReadOnlyList<KbChange> Changes(string json) => Read<List<KbChange>>(json) ?? [];

    public static IReadOnlyList<KbLinkRef> Links(string? json) => Read<List<KbLinkRef>>(json) ?? [];

    public static KbStoredDeclaration? Declaration(string? json) => Read<KbStoredDeclaration>(json);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The page links of its Markdown, by number, replacing the last save's; a link to itself doesn't count.</summary>
    private async Task RefsAsync(KbPageEntity page, IReadOnlyList<int> numbers, CancellationToken ct)
    {
        var wanted = numbers.Where(n => n != page.Number).ToHashSet();
        var existing = db.Entry(page).State == EntityState.Added ? [] : await db.KbPageRefs.Where(r => r.FromPageId == page.Id).ToListAsync(ct);
        foreach (var gone in existing.Where(r => !wanted.Contains(r.ToNumber)))
        {
            db.KbPageRefs.Remove(gone);
        }
        foreach (var number in wanted.Where(n => existing.All(r => r.ToNumber != n)))
        {
            db.KbPageRefs.Add(new KbPageRefEntity { FromPageId = page.Id, ToNumber = number });
        }
    }

    private static string Cut(string text) => text.Length <= 300 ? text : text[..299] + "…";
}
