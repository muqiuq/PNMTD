using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Write;

/// <summary>
/// Locations, the knowledge base, Redmine links and Cookie's cases (docs/mcp-plan.md §7.3). A page or a field marked
/// sensitive doesn't exist for MCP, for writing either; marking is the person's, in PNMT, with the two statements.
/// </summary>
public static class KnowledgeWriteTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("location_create", "Create a location", "A new location (a site, a building, a room), at the top or inside another.",
            Schema(Text("name", "The location's name", true), Text("description", "A short description"), Id("parentId", "The location it is part of", required: false)),
            Area.Locations, Level.Edit, ToolKind.Write, LocationCreateAsync) { AuditEvents = [AuditEvents.LocationCreated] },
        new("location_update", "Change a location", "Renames a location, changes its description, or moves it into another (or to the top).",
            Schema(Id("id", "The location's id"), Text("name", "A new name"), Text("description", "A new description; an empty text clears it"),
                Id("parentId", "Move it into this location", required: false), Bool("toTop", "true to move it to the top")),
            Area.Locations, Level.Edit, ToolKind.Write, LocationUpdateAsync) { AuditEvents = [AuditEvents.LocationUpdated, AuditEvents.LocationMoved], Idempotent = true },
        new("location_set_value", "Fill in a location's field",
            "Sets one information field of a location (see location_details for the fields and their keys); null clears it, back to the inherited value. " +
            "Fields marked sensitive aren't available.",
            Schema(Id("locationId", "The location"), Text("field", "The field's key, as on-site-contact", true),
                new Field("value", "string", "The value: text or Markdown; a number, true or false, or a date (yyyy-MM-dd) as text for those fields"),
                Bool("clear", "true to clear the field instead")),
            Area.Locations, Level.Edit, ToolKind.Write, LocationValueAsync) { AuditEvents = [AuditEvents.LocationValueChanged], Idempotent = true },
        new("kb_page_create", "Create a knowledge base page",
            "A new page: title, Markdown, summary, tags. Credentials are refused (looks_like_credential); a page that needs to be kept from machines is " +
            "marked sensitive by a person in PNMT.",
            Schema(Text("title", "3 to 160 characters", true), Text("content", "Markdown", true), Text("summary", "At most 300 characters"),
                new Field("tags", "array", "Tags") { Extra = new System.Text.Json.Nodes.JsonObject { ["items"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" } } },
                Choice("status", "Published when left out", Enum.GetNames<KbPageStatus>())),
            Area.KnowledgeBase, Level.Edit, ToolKind.Write, KbCreateAsync) { AuditEvents = [AuditEvents.KbPageCreated] },
        new("kb_page_update", "Change a knowledge base page",
            "Changes the given fields of a page; the others, its links and AI tags stay. revision is the page's revision you read (kb_page): if someone " +
            "saved in between, the answer is edit_conflict with the current one.",
            Schema(Int("number", "The page's number", true), Int("revision", "The revision you read", true), Text("title", "3 to 160 characters"),
                Text("content", "Markdown, the whole page"), Text("summary", "At most 300 characters"),
                new Field("tags", "array", "The tags, all of them") { Extra = new System.Text.Json.Nodes.JsonObject { ["items"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" } } },
                Choice("status", "Its status", Enum.GetNames<KbPageStatus>())),
            Area.KnowledgeBase, Level.Edit, ToolKind.Write, KbUpdateAsync) { AuditEvents = [AuditEvents.KbPageSaved] },
        new("kb_page_archive", "Archive or restore a page", "Archives a page (it stays, out of the lists) or restores it.",
            Schema(Int("number", "The page's number", true), Bool("archived", "true to archive, false to restore", true)),
            Area.KnowledgeBase, Level.Edit, ToolKind.Write, KbArchiveAsync) { AuditEvents = [AuditEvents.KbPageArchived, AuditEvents.KbPageRestored], Idempotent = true },
        new("ticket_link", "Link a Redmine ticket",
            "Links a Redmine ticket to a page, a location, a host or a sensor, with a note; or changes a link's note, or removes the link (linkId).",
            Schema(Int("ticket", "The ticket's number"), Int("pageNumber", "A knowledge base page"), Id("locationId", "A location", required: false),
                Id("hostId", "A host", required: false), Id("sensorId", "A sensor", required: false), Text("note", "Why it is linked"),
                Id("linkId", "An existing link, to change its note or remove it", required: false), Bool("remove", "true to remove the link linkId")),
            Area.Redmine, Level.Link, ToolKind.Write, TicketLinkAsync)
        { AuditEvents = [AuditEvents.RedmineLinkAdded, AuditEvents.RedmineLinkChanged, AuditEvents.RedmineLinkRemoved], OpenWorld = true },
        new("case_comment", "Comment on a case", "Adds a comment to one of Cookie's cases, as anyone may in Cookie's window.",
            Schema(Id("caseId", "The case"), Text("text", "The comment", true)),
            Area.Cookie, Level.Use, ToolKind.Write, CaseCommentAsync) { AuditEvents = [AuditEvents.CookieCaseCommented] },
    ];

    private static object LocationResult(ToolContext ctx, LocationDetail l)
    {
        ctx.Target("Location", l.Id, l.Name);
        return new { l.Id, l.Name, l.Description, l.ParentId, l.Path, Url = ctx.Link($"locations/{l.Id}") };
    }

    private static async Task<object> LocationCreateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var location = await ctx.Services.GetRequiredService<LocationService>().CreateAsync(new LocationRequest
        {
            Name = TextOf(args, "name", 1, 200),
            Description = OptionalText(args, "description", 1000),
            ParentId = OptionalId(args, "parentId"),
        }, ct);
        ctx.Summary = $"created location {location.Path}";
        return LocationResult(ctx, location);
    }

    /// <summary>A rename (or a new description) or a move, one per call, so each is one audit entry with its own event.</summary>
    private static async Task<object> LocationUpdateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var locations = ctx.Services.GetRequiredService<LocationService>();
        var current = await locations.GetAsync(id, own: true, ct);
        var name = OptionalText(args, "name", 200);
        var description = OptionalText(args, "description", 1000);
        var move = OptionalBool(args, "toTop") == true || Has(args, "parentId");
        if (move && (name is not null || description is not null))
        {
            throw ToolException.Invalid("One change at a time: rename (or describe) it, then move it in a second call.");
        }
        LocationDetail result;
        if (move)
        {
            AuditScope.Current?.UseEvent(AuditEvents.LocationMoved);
            result = await locations.MoveAsync(id, new LocationMoveRequest { ParentId = OptionalBool(args, "toTop") == true ? null : OptionalId(args, "parentId") }, ct);
        }
        else if (name is not null || description is not null)
        {
            result = await locations.UpdateAsync(id, new LocationRequest
            {
                Name = name ?? current.Name,
                Description = description is null ? current.Description : description.Length == 0 ? null : description,
                ParentId = current.ParentId,
            }, ct);
        }
        else
        {
            throw ToolException.Invalid("Nothing to change: give a name, a description, parentId or toTop.");
        }
        ctx.Summary = $"{(move ? "moved" : "changed")} location {result.Path}";
        return LocationResult(ctx, result);
    }

    private static async Task<object> LocationValueAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var locationId = IdOf(args, "locationId");
        var key = TextOf(args, "field", 1, 100);
        // MachineAccess's rule for writing too: a sensitive or archived field isn't there for MCP.
        var field = await db.LocationFields.AsNoTracking().SingleOrDefaultAsync(f => f.Key == key && !f.Sensitive && f.ArchivedAt == null, ct)
            ?? throw ToolException.NotFound("field");
        var values = ctx.Services.GetRequiredService<LocationValues>();
        var revision = await db.LocationValues.AsNoTracking().Where(v => v.LocationId == locationId && v.FieldId == field.Id).Select(v => (int?)v.Revision)
            .SingleOrDefaultAsync(ct) ?? 0;
        JsonElement? value = OptionalBool(args, "clear") == true || !Has(args, "value") ? null : args.GetProperty("value").Clone();
        var saved = await values.SetAsync(locationId, field.Id, new LocationValueRequest { Value = value, Revision = revision }, ct);
        ctx.Target("Location", locationId, null);
        ctx.Summary = $"{field.Label} {(value is null ? "cleared" : "set")}";
        return new { Field = field.Key, field.Label, saved.Value, saved.Revision, InheritedFrom = saved.InheritedFrom, Url = ctx.Link($"locations/{locationId}/information") };
    }

    /// <summary>A page as MCP may see it: not sensitive. Anything else is "no such page".</summary>
    private static async Task<KbPageDetail> PageAsync(ToolContext ctx, PnmtdDbContext db, int number, CancellationToken ct)
    {
        if (!await MachineAccess.Pages(db).AnyAsync(p => p.Number == number, ct))
        {
            throw ToolException.NotFound("page");
        }
        return await ctx.Services.GetRequiredService<KbPageService>().GetAsync(number, ct);
    }

    private static object PageResult(ToolContext ctx, KbPageDetail p)
    {
        ctx.Target("KbPage", p.Id, $"KB-{p.Number}");
        return new { p.Id, p.Number, p.Title, p.Summary, p.Status, Tags = p.Tags.Select(t => t.Name), p.Revision, p.UpdatedAt, p.ArchivedAt, Url = ctx.Link($"kb/{p.Number}") };
    }

    private static List<string>? TagsOf(JsonElement args)
    {
        if (!Has(args, "tags"))
        {
            return null;
        }
        var tags = args.GetProperty("tags");
        return tags.ValueKind == JsonValueKind.Array && tags.EnumerateArray().All(t => t.ValueKind == JsonValueKind.String)
            ? [.. tags.EnumerateArray().Select(t => t.GetString()!.Trim()).Where(t => t.Length > 0)]
            : throw ToolException.Invalid("tags must be a list of texts.");
    }

    private static async Task<object> KbCreateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var saved = await ctx.Services.GetRequiredService<KbPageService>().CreateAsync(new KbPageRequest
        {
            Title = TextOf(args, "title", 3, 160),
            Content = TextOf(args, "content", 1, 1_000_000),
            Summary = OptionalText(args, "summary", 300),
            Tags = TagsOf(args) ?? [],
            Status = OptionalEnum<KbPageStatus>(args, "status") ?? KbPageStatus.Published,
        }, ct);
        ctx.Summary = $"created page KB-{saved.Page.Number}";
        return PageResult(ctx, saved.Page);
    }

    private static async Task<object> KbUpdateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var current = await PageAsync(ctx, db, IntOf(args, "number"), ct);
        var saved = await ctx.Services.GetRequiredService<KbPageService>().UpdateAsync(current.Id, new KbPageRequest
        {
            Title = OptionalText(args, "title", 160) ?? current.Title,
            Content = OptionalText(args, "content", 1_000_000) ?? current.Content,
            Summary = OptionalText(args, "summary", 300) ?? current.Summary,
            Tags = TagsOf(args) ?? [.. current.Tags.Select(t => t.Name)],
            AiTags = null,
            Status = OptionalEnum<KbPageStatus>(args, "status") ?? current.Status,
            Links = [.. current.Links.Select(l => new KbLinkRequest { Kind = l.Kind, Id = l.Id })],
            Revision = IntOf(args, "revision"),
        }, ct);
        ctx.Summary = $"saved page KB-{saved.Page.Number} (revision {saved.Page.Revision})";
        return PageResult(ctx, saved.Page);
    }

    private static async Task<object> KbArchiveAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var current = await PageAsync(ctx, db, IntOf(args, "number"), ct);
        var archive = OptionalBool(args, "archived") ?? throw ToolException.Invalid("archived is needed.");
        if (!archive)
        {
            AuditScope.Current?.UseEvent(AuditEvents.KbPageRestored);
        }
        var page = await ctx.Services.GetRequiredService<KbPageService>().ArchiveAsync(current.Id, archive, ct);
        ctx.Summary = $"{(archive ? "archived" : "restored")} page KB-{page.Number}";
        return PageResult(ctx, page);
    }

    private static async Task<object> TicketLinkAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var redmine = ctx.Services.GetRequiredService<RedmineService>();
        if (OptionalId(args, "linkId") is { } linkId)
        {
            if (OptionalBool(args, "remove") == true)
            {
                AuditScope.Current?.UseEvent(AuditEvents.RedmineLinkRemoved);
                await redmine.RemoveLinkAsync(linkId, ct);
                ctx.Summary = "removed a ticket's link";
                return new { Removed = linkId };
            }
            AuditScope.Current?.UseEvent(AuditEvents.RedmineLinkChanged);
            var changed = await redmine.UpdateLinkAsync(linkId, new RedmineLinkNoteRequest { Note = OptionalText(args, "note", 500) }, ct);
            ctx.Summary = "changed a ticket link's note";
            return changed;
        }
        Guid? pageId = null;
        if (Has(args, "pageNumber"))
        {
            var number = IntOf(args, "pageNumber");
            pageId = await MachineAccess.Pages(db).Where(p => p.Number == number).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct)
                ?? throw ToolException.NotFound("page");
        }
        var added = await redmine.AddLinkAsync(new RedmineLinkRequest
        {
            IssueId = IntOf(args, "ticket"),
            PageId = pageId,
            LocationId = OptionalId(args, "locationId"),
            HostId = OptionalId(args, "hostId"),
            SensorId = OptionalId(args, "sensorId"),
            Note = OptionalText(args, "note", 500),
        }, ct);
        ctx.Target("RedmineTicket", IntOf(args, "ticket"), null);
        ctx.Summary = $"linked ticket #{IntOf(args, "ticket")}";
        return added;
    }

    private static async Task<object> CaseCommentAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "caseId");
        var user = ctx.Services.GetRequiredService<IHttpContextAccessor>().HttpContext!.User;
        await ctx.Services.GetRequiredService<CookieService>().CommentAsync(id, new CookieCaseCommentRequest(TextOf(args, "text", 1, 2000)), user, ct);
        ctx.Target("CookieCase", id, null);
        ctx.Summary = "commented on a case";
        return new { CaseId = id, Commented = true, Url = ctx.Link($"cookie/cases?case={id}") };
    }
}
