using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Responsibilities;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Write;

/// <summary>
/// Creating and changing responsibilities (docs/mcp-extensions-plan.md §8) with <i>Responsibilities: Edit</i>, through the same service
/// as the page: their fields and people, the description, links and Redmine combinations. Deleting them and the global people are
/// <i>Manage</i>, which no token has. A change says <i>via MCP</i> in the responsibility's history.
/// </summary>
public static class ResponsibilityWriteTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("responsibility_options", "Whom a responsibility can name",
            "For responsibility_create, _update and _redmine: the active users a responsibility can name as responsible, deputy or handler (name, " +
            "username, whether a Redmine user is linked; never contact details), and the Redmine projects and trackers PNMT reads.",
            Schema(), Area.Responsibilities, Level.Edit, ToolKind.Read, OptionsAsync),
        new("responsibility_create", "Create a responsibility",
            "A new responsibility: who owns an area of work, such as Network or Backup, with a deputy. Information only: nobody is told. People are " +
            "users' ids from responsibility_options. The description is written with responsibility_update.",
            Schema(Text("name", "1 to 100 characters, unique", true), Text("summary", "One line, at most 300 characters"),
                Id("responsibleId", "The responsible", required: false), Id("deputyId", "The deputy", required: false)),
            Area.Responsibilities, Level.Edit, ToolKind.Write, CreateAsync) { AuditEvents = [AuditEvents.ResponsibilityCreated] },
        new("responsibility_update", "Change a responsibility",
            "Changes the given fields of a responsibility; the others stay. With description, its long description in Markdown: baseRevision is " +
            "the revision responsibility_get gave; if someone saved in between, the answer is edit_conflict. Credentials are refused " +
            "(looks_like_credential).",
            Schema(Id("id", "The responsibility"), Text("name", "1 to 100 characters"), Text("summary", "One line; an empty text clears it"),
                Id("responsibleId", "The responsible", required: false), Bool("clearResponsible", "true for nobody"),
                Id("deputyId", "The deputy", required: false), Bool("clearDeputy", "true for nobody"),
                Text("description", "The long description, Markdown, the whole text"), Int("baseRevision", "With description: the revision you read")),
            Area.Responsibilities, Level.Edit, ToolKind.Write, UpdateAsync) { AuditEvents = [AuditEvents.ResponsibilityChanged] },
        new("responsibility_link", "Link a responsibility",
            "Links a knowledge base page (pageNumber), a location (locationId) or a host (hostId) to a responsibility, or unlinks it (action unlink, " +
            "the same target or linkId). The page, location or host then shows the responsibility.",
            Schema(Id("id", "The responsibility"), Choice("action", "link or unlink", ["link", "unlink"], true),
                Int("pageNumber", "A knowledge base page"), Id("locationId", "A location", required: false), Id("hostId", "A host", required: false),
                Id("linkId", "unlink: the link, as responsibility_get names it", required: false)),
            Area.Responsibilities, Level.Edit, ToolKind.Write, LinkAsync)
        {
            AuditEvents = [AuditEvents.ResponsibilityLinked, AuditEvents.ResponsibilityUnlinked], Idempotent = true,
        },
        new("responsibility_redmine", "A responsibility's Redmine combination",
            "Adds a Redmine project and tracker to a responsibility with the user who handles it (add), changes one (update, by combinationId; what " +
            "is left out stays), or removes one (remove). The projects and trackers are responsibility_options'. Needs Redmine configured in PNMT.",
            Schema(Id("id", "The responsibility"), Choice("action", "add, update or remove", ["add", "update", "remove"], true),
                Id("combinationId", "update, remove: the combination", required: false), Int("projectId", "The Redmine project"),
                Int("trackerId", "The tracker; left out on add: every tracker"), Bool("everyTracker", "update: every tracker instead of one"),
                Id("userId", "The user who handles it", required: false), Text("note", "A note; an empty text clears it")),
            Area.Responsibilities, Level.Edit, ToolKind.Write, RedmineAsync)
        {
            AuditEvents = [AuditEvents.ResponsibilityRedmineSaved, AuditEvents.ResponsibilityRedmineDeleted],
        },
    ];

    private static async Task<object> OptionsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var service = ctx.Services.GetRequiredService<ResponsibilityService>();
        var people = await service.PeopleAsync(ct);
        var queries = ctx.Services.GetRequiredService<ResponsibilityQueries>();
        var redmine = queries.RedmineConfigured ? await service.RedmineLookupsAsync(ct) : null;
        ctx.Summary = $"{people.Users.Count} people a responsibility can name";
        return new
        {
            People = people.Users.Select(u => new { u.Id, u.Name, u.Username, u.RedmineLinked }),
            Redmine = redmine is null ? null : new { redmine.Configured, Projects = ToolSafety.Scanned(redmine.Projects), Trackers = redmine.Trackers, redmine.Error },
        };
    }

    /// <summary>The responsibility after a change, as responsibility_get says it, with the ids to change it further.</summary>
    private static object Result(ToolContext ctx, ResponsibilityDetail r)
    {
        ctx.Target("Responsibility", r.Id, r.Name);
        return new
        {
            r.Id, r.Name, r.Summary, r.Revision, Responsible = r.Responsible is { } p ? new { p.Id, p.Name, p.Active } : null,
            Deputy = r.Deputy is { } d ? new { d.Id, d.Name, d.Active } : null,
            Links = r.Links.Select(l => new { l.LinkId, Kind = l.Kind.ToString(), l.Id, l.Name, l.Number }),
            Redmine = r.Redmine.Select(c => new { c.Id, c.ProjectId, c.ProjectPath, c.TrackerId, c.TrackerName, HandledBy = c.User?.Name, c.Note, c.Warning }),
            Url = ctx.Link($"admin/responsibilities/{r.Id}"),
        };
    }

    private static async Task<object> CreateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var r = await ctx.Services.GetRequiredService<ResponsibilityService>().CreateAsync(new ResponsibilityRequest
        {
            Name = TextOf(args, "name", 1, 100),
            Summary = OptionalText(args, "summary", 300),
            ResponsibleId = OptionalId(args, "responsibleId"),
            DeputyId = OptionalId(args, "deputyId"),
        }, ct);
        ctx.Summary = $"created responsibility {r.Name}";
        return Result(ctx, r);
    }

    private static async Task<object> UpdateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var service = ctx.Services.GetRequiredService<ResponsibilityService>();
        var queries = ctx.Services.GetRequiredService<ResponsibilityQueries>();
        var current = await queries.GetAsync(id, ct);
        var form = new[] { "name", "summary", "responsibleId", "clearResponsible", "deputyId", "clearDeputy" }.Any(f => Has(args, f));
        var description = OptionalText(args, "description", 200_000);
        if (!form && description is null)
        {
            throw ToolException.Invalid("Nothing to change: give a field, or description with baseRevision.");
        }
        var r = current;
        if (form)
        {
            var summary = OptionalText(args, "summary", 300);
            r = await service.UpdateAsync(id, new ResponsibilityRequest
            {
                Name = OptionalText(args, "name", 100) ?? current.Name,
                Summary = summary is null ? current.Summary : summary.Length == 0 ? null : summary,
                // The whole form: the colour stays as the page set it (docs/responsibilities-extensions-plan.md §8.4).
                Color = current.Color,
                ResponsibleId = OptionalBool(args, "clearResponsible") == true ? null : OptionalId(args, "responsibleId") ?? current.Responsible?.Id,
                DeputyId = OptionalBool(args, "clearDeputy") == true ? null : OptionalId(args, "deputyId") ?? current.Deputy?.Id,
            }, ct);
        }
        if (description is not null)
        {
            var revision = Has(args, "baseRevision") ? IntOf(args, "baseRevision") : throw ToolException.Invalid("description needs baseRevision, from responsibility_get.");
            // Never acknowledged over MCP: a line that looks like a credential is refused, as on a page.
            r = await service.SetDescriptionAsync(id, new ResponsibilityDescriptionRequest { Content = description, Revision = revision }, ct);
        }
        ctx.Summary = $"changed responsibility {r.Name}";
        return Result(ctx, r);
    }

    private static async Task<object> LinkAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var service = ctx.Services.GetRequiredService<ResponsibilityService>();
        (KbLinkKind Kind, Guid Id)? target = null;
        if (Has(args, "pageNumber"))
        {
            var number = IntOf(args, "pageNumber");
            // A page machines may not read is no page here (docs/locations-knowledge-plan.md §7.4).
            target = (KbLinkKind.Page, await MachineAccess.Pages(db).Where(p => p.Number == number).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct)
                ?? throw ToolException.NotFound("page"));
        }
        else if (OptionalId(args, "locationId") is { } location)
        {
            target = (KbLinkKind.Location, location);
        }
        else if (OptionalId(args, "hostId") is { } host)
        {
            target = (KbLinkKind.Host, host);
        }
        ResponsibilityDetail r;
        switch (OptionalText(args, "action", 8))
        {
            case "link":
                var (kind, targetId) = target ?? throw ToolException.Invalid("Give pageNumber, locationId or hostId.");
                r = await service.LinkAsync(id, new ResponsibilityLinkRequest { Kind = kind, Id = targetId }, ct);
                ctx.Summary = $"linked a {kind.ToString().ToLowerInvariant()} to {r.Name}";
                break;
            case "unlink":
                var current = await ctx.Services.GetRequiredService<ResponsibilityQueries>().GetAsync(id, ct);
                var link = OptionalId(args, "linkId") is { } linkId ? current.Links.FirstOrDefault(l => l.LinkId == linkId)
                    : target is var (k, t) ? current.Links.FirstOrDefault(l => l.Kind == k && l.Id == t)
                    : throw ToolException.Invalid("Give linkId, or the pageNumber, locationId or hostId to unlink.");
                if (link is null || (link.Kind == KbLinkKind.Page && !await MachineAccess.Pages(db).AnyAsync(p => p.Id == link.Id, ct)))
                {
                    throw ToolException.NotFound("link");
                }
                AuditScope.Current?.UseEvent(AuditEvents.ResponsibilityUnlinked);
                r = await service.UnlinkAsync(id, link.LinkId, ct);
                ctx.Summary = $"unlinked {link.Name} from {r.Name}";
                break;
            default:
                throw ToolException.Invalid("action is link or unlink.");
        }
        return Result(ctx, r);
    }

    private static async Task<object> RedmineAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var service = ctx.Services.GetRequiredService<ResponsibilityService>();
        ResponsibilityDetail r;
        string? note() => OptionalText(args, "note", 200);
        switch (OptionalText(args, "action", 8))
        {
            case "add":
                r = await service.AddCombinationAsync(id, new RedmineCombinationRequest
                {
                    ProjectId = Has(args, "projectId") ? IntOf(args, "projectId") : throw ToolException.Invalid("projectId is needed."),
                    TrackerId = Has(args, "trackerId") ? IntOf(args, "trackerId") : null,
                    UserId = OptionalId(args, "userId") ?? throw ToolException.Invalid("userId is needed: who handles it."),
                    Note = note() is { Length: > 0 } n ? n : null,
                }, ct);
                ctx.Summary = $"added a Redmine combination to {r.Name}";
                break;
            case "update":
                var combinationId = IdOf(args, "combinationId");
                var current = (await ctx.Services.GetRequiredService<ResponsibilityQueries>().GetAsync(id, ct)).Redmine.FirstOrDefault(c => c.Id == combinationId)
                    ?? throw ToolException.NotFound("Redmine combination");
                var given = note();
                r = await service.UpdateCombinationAsync(id, combinationId, new RedmineCombinationRequest
                {
                    ProjectId = Has(args, "projectId") ? IntOf(args, "projectId") : current.ProjectId,
                    TrackerId = OptionalBool(args, "everyTracker") == true ? null : Has(args, "trackerId") ? IntOf(args, "trackerId") : current.TrackerId,
                    UserId = OptionalId(args, "userId") ?? current.User?.Id,
                    Note = given is null ? current.Note : given.Length == 0 ? null : given,
                }, ct);
                ctx.Summary = $"changed a Redmine combination of {r.Name}";
                break;
            case "remove":
                AuditScope.Current?.UseEvent(AuditEvents.ResponsibilityRedmineDeleted);
                r = await service.DeleteCombinationAsync(id, IdOf(args, "combinationId"), ct);
                ctx.Summary = $"removed a Redmine combination from {r.Name}";
                break;
            default:
                throw ToolException.Invalid("action is add, update or remove.");
        }
        return Result(ctx, r);
    }
}
