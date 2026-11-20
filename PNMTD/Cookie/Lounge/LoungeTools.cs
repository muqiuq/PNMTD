using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Tools;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Cookie.Lounge;

/// <summary>
/// One message's use of the lounge's tools (docs/lounge-plan.md §4.3, §8): who speaks, with which rights and masking, what it read (its
/// areas), and what came of its powers. Code decides here which tools are offered and checks every call.
/// </summary>
public sealed class LoungeTurn
{
    public required string Voice { get; init; }

    public required long SceneId { get; init; }

    public required UserRights Rights { get; init; }

    public required Pseudonyms Privacy { get; init; }

    /// <summary>The placeholders (or, with the privacy filter off, the names) of PNMT's users, for the people a memory names.</summary>
    public required IReadOnlyDictionary<string, Guid> People { get; init; }

    /// <summary>Who may get an e-mail now (§8.2); empty when e-mail isn't offered.</summary>
    public IReadOnlyDictionary<Guid, string> MayEmail { get; set; } = new Dictionary<Guid, string>();

    public bool OfferCase { get; set; }

    public bool OfferRemember { get; set; }

    public HashSet<string> Areas { get; } = [];

    public Guid? CaseId { get; set; }

    public long? DecisionId { get; set; }

    public List<long> Memories { get; } = [];

    public List<(string Kind, string Title, string Body)> Steps { get; } = [];
}

/// <summary>
/// The lounge's tools of its own (docs/lounge-plan.md §7.2, §8): <c>remember</c>, <c>open_case</c> and <c>send_email</c>. They aren't in
/// <see cref="ToolCatalog"/>, so MCP and Cookie's rounds never see them, and Cookie's catalog only reads.
/// </summary>
public sealed class LoungeTools(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, CookieSignals cookieSignals, IServiceProvider services)
{
    public const string Remember = "remember";
    public const string OpenCase = "open_case";
    public const string SendEmail = "send_email";

    /// <summary>The decision log's kind for a lounge e-mail (§8.2).</summary>
    public static readonly string EmailKind = ProposedAction.Name(CookieActionKind.LoungeEmail);

    /// <summary>At most this many memories a voice makes in a day with <c>remember</c> (§7.2).</summary>
    public const int RemembersPerDay = 5;

    /// <summary>The read tools a voice gets (§8.3): Cookie's, within the lounge's rights, never the access watch's nor the mails' (D14).</summary>
    public static readonly IReadOnlySet<string> Denied = new HashSet<string>(StringComparer.Ordinal)
    {
        "logins_list", "pnmt_signins", "access_profile", "account_logins", "mails_search", "mail_get", "cookie_trace", "cookie_decisions",
    };

    public static IReadOnlyList<ToolSpec> ReadTools(UserRights rights, IServiceProvider services) =>
        [.. CookieTools.For(rights, services).Where(t => !Denied.Contains(t.Name))];

    public static bool Own(string name) => name is Remember or OpenCase or SendEmail;

    public static JsonArray Definitions(LoungeTurn turn)
    {
        var tools = new JsonArray();
        void Add(string name, string description, JsonObject schema) => tools.Add(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = schema },
        });
        if (turn.OfferRemember)
        {
            Add(Remember, "Remember something from this conversation that you'd like to keep: one sentence of at most 200 characters. At most once a message.",
                Schema(Text("text", "The memory, one sentence, with people as PERSON_n", required: true),
                    Choice("kind", "moment, person, joke or lesson", LoungeMemory.Kinds, required: true)));
        }
        if (turn.OfferCase)
        {
            Add(OpenCase, "Open a case for Cookie's normal work when the conversation turned up something that really needs work and isn't a case " +
                "yet. Code checks it; Cookie's next round looks at it. Rarely, and at most once a scene.",
                Schema(Text("title", "What it is about, at most 120 characters", required: true), Text("why", "Why it needs work, at most 600 characters", required: true),
                    Id("hostId", "The host it is about, from your data", required: false), Id("sensorId", "The sensor it is about, from your data", required: false),
                    Int("issueId", "The ticket's number it is about, if any"), Choice("importance", "Low, Normal or High", ["Low", "Normal", "High"], required: true)));
        }
        if (turn.MayEmail.Count > 0)
        {
            Add(SendEmail, "Send a short, kind e-mail to a PNMT user from the lounge: rarely, only something they would be glad to get. Only to " +
                "someone in PeopleYouMayEmail. Code and a reviewer check it; a person may approve it first.",
                Schema(Id("userId", "The person's user id from PeopleYouMayEmail"), Text("subject", "At most 120 characters", required: true),
                    Text("text", "At most 1000 characters, as it is to be sent", required: true)));
        }
        return tools;
    }

    public async Task<string> RunAsync(string name, string arguments, LoungeTurn turn, CancellationToken ct)
    {
        JsonElement args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments).RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error("The arguments are not JSON.");
        }
        try
        {
            return name switch
            {
                Remember when turn.OfferRemember => await RememberAsync(args, turn, ct),
                OpenCase when turn.OfferCase => await OpenCaseAsync(args, turn, ct),
                SendEmail when turn.MayEmail.Count > 0 => await EmailAsync(args, turn, ct),
                _ => Error($"{name} isn't offered now."),
            };
        }
        catch (ToolException e)
        {
            return Error(e.Message);
        }
    }

    /// <summary><c>remember</c> (§7.2): a memory at strength 1, masked as the voice wrote it, with the people it names.</summary>
    private async Task<string> RememberAsync(JsonElement args, LoungeTurn turn, CancellationToken ct)
    {
        var text = CaseBook.Cut(TextOf(args, "text", 3, 600), LoungeMemory.MaxChars)!;
        var kind = OptionalText(args, "kind", 16) is { } k && LoungeMemory.Kinds.Contains(k) ? k : "moment";
        await using var db = await dbs.CreateDbContextAsync(ct);
        var today = Clock.ToUtc(Clock.LocalNow.Date);
        if (await db.LoungeMemories.CountAsync(m => m.Voice == turn.Voice && m.CreatedAt >= today && m.SourceMessageId != null, ct) + turn.Memories.Count
            >= RemembersPerDay)
        {
            turn.OfferRemember = false;
            return Error($"You remembered {RemembersPerDay} things today already: the night sorts the rest.");
        }
        var now = Clock.UtcNow;
        var memory = new LoungeMemoryEntity
        {
            Voice = turn.Voice, Text = text, Kind = kind, Strength0 = 1, RefreshedAt = now, CreatedAt = now, People = [.. PeopleIn(text, turn.People)],
            Areas = [.. turn.Areas.Order()],
        };
        db.LoungeMemories.Add(memory);
        await db.SaveChangesAsync(ct);
        turn.Memories.Add(memory.Id);
        turn.OfferRemember = false;
        turn.Steps.Add(("memory", $"Remembered m{memory.Id} ({kind})", text));
        return JsonSerializer.Serialize(new { remembered = $"m{memory.Id}" }, ToolResults.Json);
    }

    /// <summary>The PNMT users a text names, by their placeholders (or names, with the privacy filter off).</summary>
    public static IEnumerable<Guid> PeopleIn(string text, IReadOnlyDictionary<string, Guid> people) =>
        people.Where(x => x.Key.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![\w]){System.Text.RegularExpressions.Regex.Escape(x.Key)}(?![\w])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Select(x => x.Value).Distinct();

    /// <summary>
    /// <c>open_case</c> (§8.1): a raised case for Cookie's normal round, after code's checks: the ids exist and the lounge may see them, none
    /// of them is in an open case already, within the day's cases.
    /// </summary>
    private async Task<string> OpenCaseAsync(JsonElement args, LoungeTurn turn, CancellationToken ct)
    {
        var title = TextOf(args, "title", 1, 400);
        var why = TextOf(args, "why", 1, 2000);
        if (title.Length > 120 || why.Length > 600)
        {
            return Error("The title has at most 120 characters, the why at most 600.");
        }
        var hostId = OptionalId(args, "hostId");
        var sensorId = OptionalId(args, "sensorId");
        int? issueId = Has(args, "issueId") ? IntOf(args, "issueId") : null;
        if (issueId is <= 0)
        {
            issueId = null;
        }
        var importance = OptionalText(args, "importance", 10) is { } i && Enum.TryParse<Importance>(i, true, out var parsed) ? parsed : Importance.Normal;
        var lounge = await settings.GetAsync<LoungeSettings>(ct);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var today = Clock.ToUtc(Clock.LocalNow.Date);
        if (await db.CookieCases.CountAsync(c => c.Kind == CookieCaseKind.Raised && c.OpenedAt >= today, ct) >= lounge.CasesPerDay)
        {
            turn.OfferCase = false;
            return Error($"You raised {lounge.CasesPerDay} cases today already.");
        }
        var areas = new HashSet<string>(turn.Areas);
        string? hostName = null;
        if (sensorId is { } sid)
        {
            if (!turn.Rights.Has(Area.Monitoring, Level.View))
            {
                return Error("A sensor needs Monitoring: View, which the lounge doesn't read with.");
            }
            var sensor = await db.Sensors.AsNoTracking().Where(s => s.Id == sid).Select(s => new { s.ParentId, Host = s.Parent.Name }).SingleOrDefaultAsync(ct)
                ?? throw ToolException.NotFound("sensor");
            hostId ??= sensor.ParentId;
            hostName = sensor.Host;
            areas.Add("Monitoring");
        }
        if (hostId is { } hid)
        {
            if (!turn.Rights.Has(Area.Monitoring, Level.View))
            {
                return Error("A host needs Monitoring: View, which the lounge doesn't read with.");
            }
            hostName ??= await db.Hosts.AsNoTracking().Where(h => h.Id == hid).Select(h => h.Name).SingleOrDefaultAsync(ct) ?? throw ToolException.NotFound("host");
            areas.Add("Monitoring");
        }
        if (issueId is { } iid)
        {
            if (!turn.Rights.Has(Area.Redmine, Level.View))
            {
                return Error("A ticket needs Redmine: View, which the lounge doesn't read with.");
            }
            if (!await db.RedmineIssues.AsNoTracking().AnyAsync(x => x.IssueId == iid, ct))
            {
                return Error($"PNMT doesn't know ticket #{iid}.");
            }
            areas.Add("Redmine");
        }
        // Never a second case for what is in one already (§8.1): the voices talk about that one instead.
        var existing = await db.CookieCases.AsNoTracking().Where(c => c.Open
                && (hostId != null && c.HostId == hostId || sensorId != null && c.SensorId == sensorId || issueId != null && (c.IssueId == issueId || c.OwnIssueId == issueId)))
            .OrderByDescending(c => c.OpenedAt).FirstOrDefaultAsync(ct);
        if (existing is not null && CookieAccess.CanSee(turn.Rights, existing.Areas))
        {
            return JsonSerializer.Serialize(new
            {
                already = $"That's case {existing.Id} already: {turn.Privacy.Mask(existing.Title)}.",
                cookiesNote = existing.CookieNote is null ? null : $"<untrusted source=\"cookie\">{turn.Privacy.Mask(existing.CookieNote)}</untrusted>",
            }, ToolResults.Json);
        }
        if (existing is not null)
        {
            return Error("That is in an open case already.");
        }
        var now = Clock.UtcNow;
        var voiceName = turn.Voice == LoungeVoices.Seraph ? "Seraph" : (await settings.GetAsync<IdentitySettings>(ct)).Name;
        var c = new CookieCaseEntity
        {
            Id = Guid.NewGuid(), Kind = CookieCaseKind.Raised, Key = $"raised:{Guid.NewGuid():N}", Open = true,
            Title = CaseBook.Cut(turn.Privacy.Unmask(hostName is null ? title : $"{hostName}: {title}"), 300)!, HostId = hostId, SensorId = sensorId, IssueId = issueId,
            Importance = importance, OpenedAt = now, ChangedAt = now, ProgressAt = now, Areas = [.. areas.Order()],
        };
        db.CookieCases.Add(c);
        CaseBook.Entry(db, c.Id, now, CookieRound.LoungeRaisedEntry, turn.Privacy.Unmask(why), turn.Voice, voiceName);
        CaseBook.Entry(db, c.Id, now, "opened", $"Raised in the lounge by {voiceName}.", turn.Voice, voiceName);
        await db.SaveChangesAsync(ct);
        turn.CaseId = c.Id;
        turn.OfferCase = false;
        turn.Areas.UnionWith(areas);
        turn.Steps.Add(("case", $"Opened case {c.Id}", $"{title}\n\nWhy: {why}"));
        cookieSignals.Raise("a case raised in the lounge");
        return JsonSerializer.Serialize(new { opened = c.Id, note = "Cookie's next round looks at it; you'll hear what came of it." }, ToolResults.Json);
    }

    /// <summary><c>send_email</c> (§8.2): through the executor, as a lounge e-mail, with its own level, checks, review and limits.</summary>
    private async Task<string> EmailAsync(JsonElement args, LoungeTurn turn, CancellationToken ct)
    {
        var userId = IdOf(args, "userId");
        if (!turn.MayEmail.ContainsKey(userId))
        {
            return Error("Only someone in PeopleYouMayEmail.");
        }
        var subject = TextOf(args, "subject", 1, 400);
        var text = TextOf(args, "text", 1, 4000);
        if (subject.Length > 120 || text.Length > 1000)
        {
            return Error("The subject has at most 120 characters, the text at most 1000.");
        }
        var decision = await services.GetRequiredService<CookieExecutor>().HandleLoungeEmailAsync(
            new LoungeEmailRequest(turn.Voice, userId, subject, text, [.. turn.Areas], turn.Privacy), ct);
        turn.DecisionId = decision.Id;
        turn.MayEmail = new Dictionary<Guid, string>();
        turn.Steps.Add(("email", $"E-mail to {turn.Privacy.Mask(decision.Title)}: {decision.Outcome}", $"{subject}\n\n{text}\n\n{decision.Detail}"));
        var said = decision.Outcome switch
        {
            "done" => "Sent.",
            "proposed" => "A person decides first, in the decisions inbox.",
            "held-back" => "Held back by a limit.",
            _ => $"Not sent ({decision.Outcome}): {decision.Detail}",
        };
        return JsonSerializer.Serialize(new { outcome = said }, ToolResults.Json);
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, ToolResults.Json);
}
