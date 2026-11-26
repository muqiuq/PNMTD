using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Responsibilities;
using PNMTD.Services.Domain;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Write;

/// <summary>
/// Creating and changing responsibilities (docs/mcp-extensions-plan.md §8) with <i>Responsibilities: Edit</i>, through the same service
/// as the page: their fields and people, the colour as hex (docs/responsibilities-mcp-and-map-plan.md §6.3), the description and
/// restoring it from the history, links and Redmine combinations. Deleting them and the global people are <i>Manage</i>, which no
/// token has. A change says <i>via MCP</i> in the responsibility's history.
/// </summary>
public static class ResponsibilityWriteTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("responsibility_options", "Whom a responsibility can name",
            "For responsibility_create, _update and _redmine: the active users a responsibility can name as responsible, deputy or handler (name, " +
            "username, whether a Redmine user is linked; never contact details), the Redmine projects and trackers PNMT reads, and the colour " +
            "palette with how many responsibilities use each swatch and the first unused one (suggestedColor).",
            Schema(), Area.Responsibilities, Level.Edit, ToolKind.Read, OptionsAsync),
        new("responsibility_create", "Create a responsibility",
            "A new responsibility: who owns an area of work, such as Network or Backup, with a deputy. Information only: nobody is told. People are " +
            "users' ids from responsibility_options. Left out, color is none; responsibility_options suggests one. Credentials in the description " +
            "are refused (looks_like_credential).",
            Schema(Text("name", "1 to 100 characters, unique", true), Text("summary", "One line, at most 300 characters"),
                Id("responsibleId", "The responsible", required: false), Id("deputyId", "The deputy", required: false), ColorField,
                Text("description", "The long description, Markdown")),
            Area.Responsibilities, Level.Edit, ToolKind.Write, CreateAsync) { AuditEvents = [AuditEvents.ResponsibilityCreated] },
        new("responsibility_update", "Change a responsibility",
            "Changes the given fields of a responsibility; the others stay. color is a hex value, an empty text clears it. With description, its " +
            "long description in Markdown: baseRevision is the revision responsibility_get gave; if someone saved in between, the answer is " +
            "edit_conflict. restoreFrom puts back the description from before a change in responsibility_history. Credentials are refused " +
            "(looks_like_credential).",
            Schema(Id("id", "The responsibility"), Text("name", "1 to 100 characters"), Text("summary", "One line; an empty text clears it"),
                Id("responsibleId", "The responsible", required: false), Bool("clearResponsible", "true for nobody"),
                Id("deputyId", "The deputy", required: false), Bool("clearDeputy", "true for nobody"), ColorField,
                Text("description", "The long description, Markdown, the whole text"), Int("baseRevision", "With description: the revision you read"),
                Id("restoreFrom", "A history row's id from responsibility_history (a description change): that description back", required: false)),
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
        // docs/responsibilities-mcp-and-map-plan.md §6.4: the Links tab's changes of many links, and the Overview's notes.
        new("responsibility_links_change", "Change many links of responsibilities",
            $"As the Links tab: link (pages by number, locations, hosts) to one responsibility; unlink links; or move links' objects to another " +
            $"responsibility, with keep true linking them there too (Also link to…). At most {ResponsibilityService.MaxBulk} at once. Link ids come " +
            "from responsibility_links_list or responsibility_get. Answers how many were linked and unlinked, and what was skipped and why.",
            Schema(Choice("action", "link, unlink or move", ["link", "unlink", "move"], true),
                Id("responsibilityId", "link, move: the responsibility they go to", required: false),
                new ToolArgs.Field("pageNumbers", "array", "link: knowledge base pages, by number") { Extra = new System.Text.Json.Nodes.JsonObject { ["items"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "integer" } } },
                Ids("locationIds", "link: locations", required: false), Ids("hostIds", "link: hosts", required: false),
                Ids("linkIds", "unlink, move: the links", required: false),
                Bool("keep", "move: true keeps them where they are as well")),
            Area.Responsibilities, Level.Edit, ToolKind.Write, LinksChangeAsync)
        {
            AuditEvents = [AuditEvents.ResponsibilityLinked, AuditEvents.ResponsibilityUnlinked],
        },
        new("responsibility_note", "Write a note on the responsibilities' overview",
            $"A note on Admin → Responsibilities → Overview, free on the map: create (text; colour and width, Yellow and Medium when left out; x and " +
            "y, its top-left corner in the map's units, else right of everything placed) or update (noteId from responsibility_notes; what is left " +
            $"out stays, the place too). Markdown without images or HTML, 1 to {ResponsibilityMapService.MaxNoteLength:N0} characters. Credentials are " +
            $"refused (looks_like_credential). At most {ResponsibilityMapService.MaxNotes} notes.",
            Schema(Choice("action", "create or update", ["create", "update"], true), Id("noteId", "update: the note", required: false),
                Text("text", "Markdown"), Choice("colour", "The note's colour", Enum.GetNames<ResponsibilityNoteColour>()),
                Choice("width", "The note's width", Enum.GetNames<ResponsibilityNoteWidth>()),
                new ToolArgs.Field("x", "number", "create: the left edge, in the map's units"), new ToolArgs.Field("y", "number", "create: the top edge")),
            Area.Responsibilities, Level.Edit, ToolKind.Write, NoteAsync)
        {
            AuditEvents = [AuditEvents.ResponsibilityNoteCreated, AuditEvents.ResponsibilityNoteChanged],
        },
    ];

    private static ToolArgs.Field ColorField => Text("color", "A colour as #rrggbb (the # may be left out, any case), such as #2f8fd8");

    /// <summary>
    /// The colour given (docs/responsibilities-mcp-and-map-plan.md §6.3): <c>#rrggbb</c> or <c>rrggbb</c> in any case, stored in lower
    /// case; an empty text is none. Names such as "blue" and the short #28d are refused.
    /// </summary>
    internal static (bool Given, string? Color) ColorOf(JsonElement args)
    {
        if (OptionalText(args, "color", 16) is not { } text)
        {
            return (false, null);
        }
        if (text.Length == 0)
        {
            return (true, null);
        }
        var color = Colours.Normalize(text.StartsWith('#') ? text : "#" + text);
        return Colours.IsHex(color) ? (true, color)
            : throw ToolException.Invalid("color: a colour as #rrggbb, such as #2f8fd8. responsibility_options lists the palette.");
    }

    private static async Task<object> OptionsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var service = ctx.Services.GetRequiredService<ResponsibilityService>();
        var people = await service.PeopleAsync(ct);
        var queries = ctx.Services.GetRequiredService<ResponsibilityQueries>();
        var redmine = queries.RedmineConfigured ? await service.RedmineLookupsAsync(ct) : null;
        var used = await db.Responsibilities.AsNoTracking().Where(r => r.Color != null).Select(r => r.Color).ToListAsync(ct);
        ctx.Summary = $"{people.Users.Count} people a responsibility can name";
        return new
        {
            People = people.Users.Select(u => new { u.Id, u.Name, u.Username, u.RedmineLinked }),
            Redmine = redmine is null ? null : new { redmine.Configured, Projects = ToolSafety.Scanned(redmine.Projects), Trackers = redmine.Trackers, redmine.Error },
            Palette = Colours.Palette.Select(p => new { p.Name, p.Hex, UsedBy = used.Count(c => c == p.Hex) }),
            SuggestedColor = Colours.FirstUnused(used),
        };
    }

    /// <summary>The responsibility after a change, as responsibility_get says it, with the ids to change it further.</summary>
    private static object Result(ToolContext ctx, ResponsibilityDetail r)
    {
        ctx.Target("Responsibility", r.Id, r.Name);
        return new
        {
            r.Id, r.Name, r.Summary, r.Color, ColorName = Colours.NameOf(r.Color), r.Revision,
            Responsible = r.Responsible is { } p ? new { p.Id, p.Name, p.Active } : null,
            Deputy = r.Deputy is { } d ? new { d.Id, d.Name, d.Active } : null,
            r.Attention, Escalation = Read.ResponsibilityReadTools.Escalation(ctx, r.Escalation),
            Links = r.Links.Select(l => new { l.LinkId, Kind = l.Kind.ToString(), l.Id, l.Name, l.Number }),
            Redmine = r.Redmine.Select(c => new { c.Id, c.ProjectId, c.ProjectPath, c.TrackerId, c.TrackerName, HandledBy = c.User?.Name, c.Note, c.Warning }),
            Url = ctx.Link($"admin/responsibilities/{r.Id}"),
        };
    }

    private static async Task<object> CreateAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var service = ctx.Services.GetRequiredService<ResponsibilityService>();
        var (_, color) = ColorOf(args);
        var description = OptionalText(args, "description", 200_000);
        if (!string.IsNullOrEmpty(description))
        {
            // Refused before anything is made, so that a refusal leaves no responsibility without its text.
            LocationValues.CheckCredentials(description, null);
        }
        var r = await service.CreateAsync(new ResponsibilityRequest
        {
            Name = TextOf(args, "name", 1, 100),
            Summary = OptionalText(args, "summary", 300),
            Color = color,
            ResponsibleId = OptionalId(args, "responsibleId"),
            DeputyId = OptionalId(args, "deputyId"),
        }, ct);
        if (!string.IsNullOrEmpty(description))
        {
            r = await service.SetDescriptionAsync(r.Id, new ResponsibilityDescriptionRequest { Content = description, Revision = r.Revision }, ct);
        }
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
        var (colorGiven, color) = ColorOf(args);
        var description = OptionalText(args, "description", 200_000);
        var restoreFrom = OptionalId(args, "restoreFrom");
        if (!form && !colorGiven && description is null && restoreFrom is null)
        {
            throw ToolException.Invalid("Nothing to change: give a field, color, description with baseRevision, or restoreFrom.");
        }
        if (description is not null && restoreFrom is not null)
        {
            throw ToolException.Invalid("Either description or restoreFrom, not both.");
        }
        var r = current;
        if (form)
        {
            var summary = OptionalText(args, "summary", 300);
            r = await service.UpdateAsync(id, new ResponsibilityRequest
            {
                Name = OptionalText(args, "name", 100) ?? current.Name,
                Summary = summary is null ? current.Summary : summary.Length == 0 ? null : summary,
                // The whole form: without color, it stays as it is.
                Color = colorGiven ? color : current.Color,
                ResponsibleId = OptionalBool(args, "clearResponsible") == true ? null : OptionalId(args, "responsibleId") ?? current.Responsible?.Id,
                DeputyId = OptionalBool(args, "clearDeputy") == true ? null : OptionalId(args, "deputyId") ?? current.Deputy?.Id,
            }, ct);
        }
        else if (colorGiven)
        {
            // The colour alone, as the overview's side panel sets it: one history row.
            r = await service.SetColorAsync(id, new ResponsibilityColorRequest { Color = color }, ct);
        }
        if (description is not null)
        {
            var revision = Has(args, "baseRevision") ? IntOf(args, "baseRevision") : throw ToolException.Invalid("description needs baseRevision, from responsibility_get.");
            // Never acknowledged over MCP: a line that looks like a credential is refused, as on a page.
            r = await service.SetDescriptionAsync(id, new ResponsibilityDescriptionRequest { Content = description, Revision = revision }, ct);
        }
        else if (restoreFrom is { } row)
        {
            r = await service.RestoreAsync(id, row, ct);
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

    private static async Task<object> LinksChangeAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var service = ctx.Services.GetRequiredService<ResponsibilityService>();
        var action = OptionalText(args, "action", 8);
        ResponsibilityBulkResult result;
        switch (action)
        {
            case "link":
                var to = OptionalId(args, "responsibilityId") ?? throw ToolException.Invalid("responsibilityId: the responsibility they go to.");
                var targets = new List<ResponsibilityLinkRequest>();
                if (PageNumbers(args) is { Count: > 0 } numbers)
                {
                    // Only pages a machine may read (docs/locations-knowledge-plan.md §7.4): another number is no page here.
                    var pages = await MachineAccess.Pages(db).Where(p => numbers.Contains(p.Number)).Select(p => new { p.Id, p.Number }).ToListAsync(ct);
                    if (numbers.Except(pages.Select(p => p.Number)).FirstOrDefault() is var missing && pages.Count < numbers.Count)
                    {
                        throw ToolException.NotFound($"page {missing}");
                    }
                    targets.AddRange(pages.Select(p => new ResponsibilityLinkRequest { Kind = KbLinkKind.Page, Id = p.Id }));
                }
                targets.AddRange((OptionalIds(args, "locationIds", ResponsibilityService.MaxBulk) ?? []).Select(i => new ResponsibilityLinkRequest { Kind = KbLinkKind.Location, Id = i }));
                targets.AddRange((OptionalIds(args, "hostIds", ResponsibilityService.MaxBulk) ?? []).Select(i => new ResponsibilityLinkRequest { Kind = KbLinkKind.Host, Id = i }));
                if (targets.Count == 0)
                {
                    throw ToolException.Invalid("Give pageNumbers, locationIds or hostIds.");
                }
                result = await service.LinkManyAsync(new ResponsibilityBulkLinkRequest { ResponsibilityId = to, Targets = targets }, ct);
                ctx.Target("Responsibility", to, null);
                ctx.Summary = $"linked {result.Linked}, skipped {result.Skipped.Count}";
                break;
            case "unlink":
                AuditScope.Current?.UseEvent(AuditEvents.ResponsibilityUnlinked);
                result = await service.UnlinkManyAsync(new ResponsibilityBulkUnlinkRequest { LinkIds = [.. await MachineLinksAsync(args, db, ct)] }, ct);
                ctx.Summary = $"unlinked {result.Unlinked}";
                break;
            case "move":
                var target = OptionalId(args, "responsibilityId") ?? throw ToolException.Invalid("responsibilityId: the responsibility they go to.");
                result = await service.MoveLinksAsync(new ResponsibilityMoveLinksRequest
                {
                    LinkIds = [.. await MachineLinksAsync(args, db, ct)], ResponsibilityId = target, Keep = OptionalBool(args, "keep") == true,
                }, ct);
                ctx.Target("Responsibility", target, null);
                ctx.Summary = $"linked {result.Linked}, unlinked {result.Unlinked}";
                break;
            default:
                throw ToolException.Invalid("action is link, unlink or move.");
        }
        return new
        {
            result.Linked, result.Unlinked,
            Skipped = result.Skipped.Select(x => new { Kind = x.Kind.ToString(), x.Id, Name = ctx.Untrusted("name", x.Name, 200), x.Reason }),
        };
    }

    /// <summary>The page numbers given, or null.</summary>
    private static IReadOnlyList<int>? PageNumbers(JsonElement args)
    {
        if (!Has(args, "pageNumbers"))
        {
            return null;
        }
        if (args.GetProperty("pageNumbers") is not { ValueKind: JsonValueKind.Array } list)
        {
            throw ToolException.Invalid("pageNumbers must be a list of numbers.");
        }
        var numbers = list.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) ? n
            : throw ToolException.Invalid("pageNumbers must be a list of numbers.")).Distinct().ToList();
        return numbers.Count <= ResponsibilityService.MaxBulk ? numbers : throw ToolException.Invalid($"pageNumbers has at most {ResponsibilityService.MaxBulk} items.");
    }

    /// <summary>The link ids given, without links to pages a machine may not read: they are no links here.</summary>
    private static async Task<List<Guid>> MachineLinksAsync(JsonElement args, PnmtdDbContext db, CancellationToken ct)
    {
        var ids = IdsOf(args, "linkIds", ResponsibilityService.MaxBulk);
        var pageLinks = await db.ResponsibilityLinks.AsNoTracking().Where(l => ids.Contains(l.Id) && l.PageId != null)
            .Select(l => new { l.Id, PageId = l.PageId!.Value }).ToListAsync(ct);
        if (pageLinks.Count == 0)
        {
            return [.. ids];
        }
        var pageIds = pageLinks.Select(l => l.PageId).Distinct().ToList();
        var readable = await MachineAccess.Pages(db).Where(p => pageIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        var hidden = pageLinks.Where(l => !readable.Contains(l.PageId)).Select(l => l.Id).ToHashSet();
        var kept = ids.Where(i => !hidden.Contains(i)).ToList();
        return kept.Count > 0 ? kept : throw ToolException.NotFound("link");
    }

    /// <summary>A note's width in the map's units, as the overview draws it (overviewLayout.ts noteWidths).</summary>
    private static double NoteWidth(string width) => width switch
    {
        nameof(ResponsibilityNoteWidth.Small) => 160,
        nameof(ResponsibilityNoteWidth.Large) => 360,
        _ => 240,
    };

    private static async Task<object> NoteAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var map = ctx.Services.GetRequiredService<ResponsibilityMapService>();
        var text = OptionalText(args, "text", ResponsibilityMapService.MaxNoteLength);
        var colour = OptionalEnum<ResponsibilityNoteColour>(args, "colour");
        var width = OptionalEnum<ResponsibilityNoteWidth>(args, "width");
        ResponsibilityMapNote note;
        switch (OptionalText(args, "action", 8))
        {
            case "create":
                double? x = Has(args, "x") ? Number(args, "x") : null;
                double? y = Has(args, "y") ? Number(args, "y") : null;
                if (x is null || y is null)
                {
                    // Right of everything placed, at the top: the server doesn't know the automatic places, and the layout keeps clear of notes.
                    var places = await db.ResponsibilityMapPositions.AsNoTracking().Select(p => new { Right = p.X + 60, Top = p.Y - 60 }).ToListAsync(ct);
                    var notes = await db.ResponsibilityMapNotes.AsNoTracking().Select(n => new { n.X, n.Y, n.Width }).ToListAsync(ct);
                    var rights = places.Select(p => p.Right).Concat(notes.Select(n => n.X + NoteWidth(n.Width))).ToList();
                    var tops = places.Select(p => p.Top).Concat(notes.Select(n => n.Y)).ToList();
                    x ??= rights.Count > 0 ? Math.Round(rights.Max() + 80, 2) : 0;
                    y ??= tops.Count > 0 ? Math.Round(tops.Min(), 2) : 0;
                }
                // Never acknowledged over MCP: a line that looks like a credential is refused, as on the page.
                note = await map.CreateNoteAsync(new ResponsibilityNoteRequest { Text = text, Colour = colour, Width = width, X = x, Y = y }, ct);
                ctx.Summary = "wrote a note on the responsibilities' overview";
                break;
            case "update":
                var id = IdOf(args, "noteId");
                var current = await db.ResponsibilityMapNotes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id, ct) ?? throw ToolException.NotFound("note");
                AuditScope.Current?.UseEvent(AuditEvents.ResponsibilityNoteChanged);
                note = await map.UpdateNoteAsync(id, new ResponsibilityNoteRequest
                {
                    Text = text ?? current.Text,
                    Colour = colour ?? Enum.Parse<ResponsibilityNoteColour>(current.Colour),
                    Width = width ?? Enum.Parse<ResponsibilityNoteWidth>(current.Width),
                }, ct);
                ctx.Summary = "changed a note on the responsibilities' overview";
                break;
            default:
                throw ToolException.Invalid("action is create or update.");
        }
        ctx.Target("ResponsibilityNote", note.Id, null);
        return new
        {
            note.Id, Text = ctx.Untrusted("overview note", note.Text, ResponsibilityMapService.MaxNoteLength), note.Colour, note.Width, note.X, note.Y,
            Url = ctx.Link("admin/responsibilities/overview?focus=note:" + note.Id),
        };
    }

    private static double Number(JsonElement args, string name) =>
        args.GetProperty(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) && double.IsFinite(d) ? d
            : throw ToolException.Invalid($"{name} must be a number.");
}
