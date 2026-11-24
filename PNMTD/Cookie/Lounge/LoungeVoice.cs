using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Tools;

namespace PNMTD.Cookie.Lounge;

/// <summary>
/// One message of a voice (docs/lounge-plan.md §4): the scene, its ground and seed, the code's direction, the comments to take up and the
/// person's message to answer, with the lounge's rights and the scene's masking.
/// </summary>
public sealed record VoiceCall(
    string Voice,
    LoungeSceneEntity Scene,
    LoungeGroundResult Ground,
    string? Seed,
    IReadOnlyList<string> Directions,
    bool RepeatDirection,
    LoungeMessageEntity? ReplyTo,
    IReadOnlyList<LoungeMessageEntity> Comments,
    UserRights Rights,
    Pseudonyms Privacy,
    LoungePeople People,
    LoungeSettings Settings,
    Dictionary<string, Dictionary<string, double>> Positions,
    string Awake,
    IReadOnlyDictionary<Guid, string> MayEmail,
    bool OfferCase,
    string? Yesterday = null,
    LoungeLength Length = LoungeLength.Short,
    int ImportantLeft = 0,
    IReadOnlyList<object>? MayBoard = null,
    string? Asker = null);

/// <summary>How long a message may be (docs/lounge-plan.md §4.3): chosen by code for each, short most of the time.</summary>
public enum LoungeLength { Short, Medium, Long }

/// <summary>What a voice's turn came to: the message stored, or none (it passed, or named someone who opted out twice), and its cost.</summary>
public sealed record VoiceResult(LoungeMessageEntity? Message, decimal Cost, int Calls, int ToolCalls, string? Why);

/// <summary>
/// A voice speaks (docs/lounge-plan.md §4): one call per message with that voice's model (D13), the system prompt first so that a scene's
/// later messages are mostly a cache hit, the room's last 30 lines as the conversation, the read tools within the lounge's rights and the
/// lounge's own tools. Code checks the answer before it is stored (§4.3).
/// </summary>
public sealed partial class LoungeVoice(IDbContextFactory<PnmtdDbContext> dbs, IServiceProvider services, CookieSettings settings, CookieModel model,
    CookieBrain brain, Seraph seraph, LoungeTools tools, IOptionsMonitor<CookieOptions> options)
{
    public const int History = 30;
    /// <summary>The hard cut, at a sentence's end where there is one: a long message is the exception, and still a chat line.</summary>
    public const int MaxChars = 500;

    /// <summary>
    /// The length a message aims at (§4.3), at random: one sentence two times in three, two in one in four, up to three now and then. A reply to a
    /// person may take two; the morning's and the evening's are short.
    /// </summary>
    public static LoungeLength LengthFor(string sceneKind, bool reply, Random random)
    {
        if (sceneKind is "morning" or "evening")
        {
            return LoungeLength.Short;
        }
        var roll = random.NextDouble();
        var length = roll < 0.65 ? LoungeLength.Short : roll < 0.9 ? LoungeLength.Medium : LoungeLength.Long;
        return reply && length == LoungeLength.Short ? LoungeLength.Medium : length;
    }

    /// <summary>An important message's limit (§4.3): about six sentences, for something that really matters.</summary>
    public const int MaxImportantChars = 1500;

    /// <summary>What a voice writes first to say a message really matters.</summary>
    public const string ImportantMarker = "[important]";

    /// <summary>The length line of a call: the message's length, and, while the day's allowance lasts, how to say more when it really matters.</summary>
    public static string LengthLine(LoungeLength length, int importantLeft) => LengthText(length) + (importantLeft <= 0 ? ""
        : $" Only when something really matters, something that should be done or a risk nobody sees: start with {ImportantMarker} and say it " +
          $"fully, up to six sentences ({importantLeft} left today). That is rare.");

    /// <summary>The marker taken off: the text, and whether the voice marked it important.</summary>
    public static (string Text, bool Marked) Marked(string text)
    {
        var m = ImportantPattern().Match(text);
        return m.Success ? (text[m.Length..].Trim(), true) : (text, false);
    }

    /// <summary>The length as the voice is told, last in every call, where it weighs most.</summary>
    public static string LengthText(LoungeLength length) => length switch
    {
        LoungeLength.Short => "Keep it short: one sentence, as in a chat.",
        LoungeLength.Medium => "One or two short sentences.",
        _ => "You may take up to three sentences this time, if you have something to tell.",
    };
    public const string Pass = "(pass)";

    public static string Name(string voice, string cookieName) => voice == LoungeVoices.Seraph ? "Seraph" : cookieName;

    /// <summary>The system prompt (§4.2): Cookie's identity or Seraph's line, the lounge's rules, the voice's character.</summary>
    public async Task<string> SystemAsync(string voice, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var first = voice == LoungeVoices.Cookie
            ? CookiePrompts.Fill(await brain.PromptAsync("identity", ct), values)
            : $"You are Seraph, {values["name"]}'s reviewer.";
        // Their schedule, fixed in code after the rules (§4.2): an edited prompt can't lose it.
        return string.Join("\n\n", first, CookiePrompts.Fill(await brain.PromptAsync("loungeRules", ct), values),
            LoungePacing.Schedule(await settings.GetAsync<LoungeSettings>(ct)),
            CookiePrompts.Fill(await brain.PromptAsync(voice == LoungeVoices.Cookie ? "loungeCookie" : "loungeSeraph", ct), values));
    }

    /// <summary>The values every lounge prompt may use, besides the identity's.</summary>
    public async Task<Dictionary<string, string>> ValuesAsync(string voice, LoungePeople people, CancellationToken ct)
    {
        var values = await brain.ValuesAsync(ct);
        values["other"] = Name(LoungeVoices.Other(voice), values["name"]);
        values["not_mentioned"] = people.OptedOutTokens.Count == 0 ? "nobody" : string.Join(", ", people.OptedOutTokens);
        return values;
    }

    /// <summary>Both convictions with both positions in words (§6.1), the voice's own first.</summary>
    public static string Convictions(LoungeSettings settings, Dictionary<string, Dictionary<string, double>> positions, string voice, string otherName, Pseudonyms p)
    {
        var lines = settings.Convictions.Select(c =>
        {
            var mine = positions[c.Key][voice];
            var theirs = positions[c.Key][LoungeVoices.Other(voice)];
            var own = c.Owner == voice ? " This is your very strong opinion: you may soften a little, never give it up." : "";
            return $"- {p.Mask(c.Question)} You are {p.Mask(LoungeOpinions.Words(c, mine))}. {otherName} is {p.Mask(LoungeOpinions.Words(c, theirs))}.{own}";
        });
        return string.Join("\n", lines);
    }

    public async Task<VoiceResult> SpeakAsync(VoiceCall c, CancellationToken ct)
    {
        var p = c.Privacy;
        var values = await ValuesAsync(c.Voice, c.People, ct);
        var me = Name(c.Voice, values["name"]);
        var other = values["other"];
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);

        // What the voice remembers (§7): its keepsakes, its strongest memories with ids, and one that resurfaces.
        var memories = await db.LoungeMemories.AsNoTracking().Where(m => m.Voice == c.Voice).ToListAsync(ct);
        var keepsakes = memories.Where(m => m.Keepsake).OrderBy(m => m.Id).ToList();
        var ranked = memories.Where(m => !m.Keepsake).OrderByDescending(m => LoungeMemory.Strength(m, now, c.Settings.HalfLifeDays)).ToList();
        var strongest = ranked.Take(c.Settings.MemoriesInPrompt).ToList();
        var rest = ranked.Skip(c.Settings.MemoriesInPrompt).ToList();
        var random = new Random(HashCode.Combine(c.Scene.Id, c.Voice));
        LoungeMemoryEntity? resurfaced = null;
        if (rest.Count > 0)
        {
            var total = rest.Sum(m => LoungeMemory.Strength(m, now, c.Settings.HalfLifeDays));
            var pick = random.NextDouble() * total;
            resurfaced = rest.FirstOrDefault(m => (pick -= LoungeMemory.Strength(m, now, c.Settings.HalfLifeDays)) <= 0) ?? rest[0];
        }
        values["now"] = Clock.LocalNow.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        values["awake"] = c.Awake;
        values["convictions"] = Convictions(c.Settings, c.Positions, c.Voice, other, p);
        values["keepsakes"] = keepsakes.Count == 0 ? "Nothing yet: the night lets you choose three." : string.Join("\n", keepsakes.Select(m => $"- {LoungeMemory.Ref(m)}: {m.Text}"));
        values["memories"] = strongest.Count == 0 ? "Nothing yet." : string.Join("\n", strongest.Select(m => $"- {LoungeMemory.Ref(m)}: {m.Text}"));
        values["ground"] = c.Ground.Json
            + (resurfaced is null ? "" : $"\nSomething that comes back to you: {LoungeMemory.Ref(resurfaced)}: {resurfaced.Text}")
            + (c.Yesterday is null ? "" : $"\nYesterday, as you remember it:\n{c.Yesterday}");
        values["staffing"] = c.Ground.Staffing ?? "not known to the lounge";
        values["seed"] = c.Seed is null ? "No particular topic: talk about whatever you like: your convictions, your memories, each other." : $"The topic: {c.Seed}";
        values["direction"] = string.Join(" ", c.Directions);

        var context = new ToolContext(services, c.Rights, p);
        var messages = new JsonArray(Message("system", c.People.Hide(await SystemAsync(c.Voice, values, ct))),
            Message("user", c.People.Hide(CookiePrompts.Fill(await brain.PromptAsync("loungeScene", ct), values))));
        foreach (var line in await ConversationAsync(db, c, me, other, context, ct))
        {
            messages.Add(line);
        }
        var turnText = $"Your turn, {me}." + (c.RepeatDirection ? " " + values["direction"] : "");
        if (c.MayEmail.Count > 0)
        {
            turnText += $"\n\nPeopleYouMayEmail:\n{CookieBrain.Json(c.MayEmail.Select(x => new { UserId = x.Key, Name = x.Value }))}\n\n";
        }
        else
        {
            turnText += " ";
        }
        // docs/board-messages-plan.md §8.6: only when the person this voice answers may send board messages, and asks for one.
        if (c.MayBoard is { Count: > 0 } boards)
        {
            turnText += $"\n\nBoardsYouMayMessage (only if {c.Asker ?? "the person"} asks you to put something on a board):\n{CookieBrain.Json(boards)}\n\n";
        }
        // The length last, where it weighs most (§4.3), and how to say more when it really matters.
        turnText += LengthLine(c.Length, c.ImportantLeft);
        messages.Add(Message("user", c.People.Hide(turnText)));

        var turn = new LoungeTurn
        {
            Voice = c.Voice, SceneId = c.Scene.Id, Rights = c.Rights, Privacy = p, People = c.People.ByToken, MayEmail = c.MayEmail, OfferCase = c.OfferCase,
            OfferRemember = true, MayBoard = c.MayBoard is { Count: > 0 } && c.Asker is not null, Asker = c.Asker,
        };
        turn.Areas.UnionWith(c.Ground.Areas);
        var readTools = c.Settings.ToolCallsPerMessage > 0 ? CookieTools.Definitions(LoungeTools.ReadTools(c.Rights, services)) : new JsonArray();
        var choice = c.Voice == LoungeVoices.Seraph ? await seraph.ChoiceAsync(ct) ?? throw new LoungePausedException("Seraph has no model: it takes two to talk.") : null;
        var modelName = choice?.Model ?? await CookieModelAsync(ct);
        var o = options.CurrentValue;
        int calls = 0, toolCalls = 0;
        decimal cost = 0;
        string? provider = null;
        string? text = null;
        for (var step = 0; step <= c.Settings.ToolCallsPerMessage + 1; step++)
        {
            var allowTools = toolCalls < c.Settings.ToolCallsPerMessage;
            JsonArray? definitions = null;
            if (allowTools)
            {
                definitions = new JsonArray([.. readTools.Select(t => t!.DeepClone()), .. LoungeTools.Definitions(turn).Select(t => t!.DeepClone())]);
            }
            var reply = await model.CompleteAsync($"lounge.{c.Voice}", messages, definitions is { Count: > 0 } ? definitions : null, null, c.Settings.Effort, provider,
                o.Tokens.Lounge, ct, choice);
            calls++;
            cost += reply.Cost;
            provider ??= reply.Provider;
            if (!string.IsNullOrWhiteSpace(reply.Reasoning))
            {
                turn.Steps.Add(("reasoning", $"{me} thinks", reply.Reasoning!));
            }
            if (reply.ToolCalls.Count == 0)
            {
                text = reply.Content;
                break;
            }
            messages.Add(reply.AssistantMessage);
            foreach (var call in reply.ToolCalls)
            {
                toolCalls++;
                string result;
                if (LoungeTools.Own(call.Name))
                {
                    result = await tools.RunAsync(call.Name, call.Arguments, turn, ct);
                }
                else if (LoungeTools.Denied.Contains(call.Name))
                {
                    result = JsonSerializer.Serialize(new { error = $"{call.Name} isn't for the lounge." }, ToolResults.Json);
                }
                else
                {
                    result = await CookieTools.RunAsync(call.Name, call.Arguments, context, ct);
                    turn.Areas.UnionWith(context.Areas);
                }
                turn.Steps.Add(("tool", $"{call.Name}({CaseBook.Cut(call.Arguments, 200)})", CaseBook.Cut(result, 4000)!));
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = result });
            }
            if (toolCalls >= c.Settings.ToolCallsPerMessage)
            {
                messages.Add(Message("user", "No more looking: say it."));
            }
        }

        // Code's checks (§4.3): a pass ends the scene; too long is cut, an important one later; someone who opted out gets one retry, then
        // the message is dropped.
        text = Clean(text, me, MaxImportantChars);
        if (text is not null && c.People.Names(text) is { } named)
        {
            turn.Steps.Add(("refused", $"Named {named}, who opted out: one retry", text));
            messages.Add(Message("assistant", text));
            messages.Add(Message("user", $"Don't mention {named}: they asked not to be talked about. Say it without them, or answer {Pass}."));
            var again = await model.CompleteAsync($"lounge.{c.Voice}", messages, null, null, c.Settings.Effort, provider, o.Tokens.Lounge, ct, choice);
            calls++;
            cost += again.Cost;
            text = Clean(again.Content, me, MaxImportantChars);
            if (text is not null && c.People.Names(text) is { } still)
            {
                turn.Steps.Add(("refused", $"Named {still} again: dropped", text));
                text = null;
            }
        }
        // Important (§4.3): marked by the voice, within the day's allowance, or a message that raised a case; it may be long. Otherwise the
        // marker goes and the usual limit applies.
        var important = false;
        if (text is not null)
        {
            var (plain, marked) = Marked(text);
            important = marked && c.ImportantLeft > 0 || turn.CaseId is not null;
            if (marked && !important)
            {
                turn.Steps.Add(("refused", $"Marked important, but none left today: cut to {MaxChars} characters", plain));
            }
            text = plain.Length == 0 ? null : Shorten(plain, important ? MaxImportantChars : MaxChars);
        }
        LoungeMessageEntity? stored = null;
        if (text is not null)
        {
            stored = new LoungeMessageEntity
            {
                At = Clock.UtcNow, From = c.Voice, Kind = "message", Text = CaseBook.Cut(p.Unmask(text), 2000)!, SceneId = c.Scene.Id, ReplyToId = c.ReplyTo?.Id,
                Areas = [.. turn.Areas.Order()], Model = CaseBook.Cut(modelName, 200), Cost = cost, ToolCalls = toolCalls, CaseId = turn.CaseId,
                DecisionId = turn.DecisionId, Answered = true, Important = important,
            };
            db.LoungeMessages.Add(stored);
            await db.SaveChangesAsync(ct);
            var commentIds = c.Comments.Select(x => x.Id).ToList();
            if (commentIds.Count > 0)
            {
                await db.LoungeMessages.Where(m => commentIds.Contains(m.Id)).ExecuteUpdateAsync(u => u.SetProperty(m => m.TakenById, stored.Id), ct);
            }
            if (turn.Memories.Count > 0)
            {
                await db.LoungeMemories.Where(m => turn.Memories.Contains(m.Id)).ExecuteUpdateAsync(u => u.SetProperty(m => m.SourceMessageId, stored.Id), ct);
            }
            if (turn.CaseId is { } caseId)
            {
                await db.CookieCases.Where(x => x.Id == caseId).ExecuteUpdateAsync(u => u.SetProperty(x => x.LoungeMessageId, stored.Id), ct);
            }
        }
        if (c.ReplyTo is { } asked)
        {
            // Answered, or passed on: either way it doesn't wait again.
            await db.LoungeMessages.Where(m => m.Id == asked.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.Answered, true), ct);
        }
        foreach (var (kind, title, body) in turn.Steps)
        {
            db.LoungeSteps.Add(new LoungeStepEntity
            {
                SceneId = c.Scene.Id, MessageId = stored?.Id, At = Clock.UtcNow, Kind = kind, Title = CaseBook.Cut(title, 300)!, Body = CaseBook.Cut(body, 20_000)!,
                Areas = [.. turn.Areas.Order()],
            });
        }
        await db.SaveChangesAsync(ct);
        return new VoiceResult(stored, cost, calls, toolCalls, stored is null ? "passed" : null);
    }

    /// <summary>
    /// The room's last 30 lines as the voice reads them (§4.2): its own as <c>assistant</c>, the other voice's and people's as <c>user</c>,
    /// untrusted. A comment says what it is on; the message it is on comes along when it is older than the 30.
    /// </summary>
    private async Task<List<JsonObject>> ConversationAsync(PnmtdDbContext db, VoiceCall c, string me, string other, ToolContext context, CancellationToken ct)
    {
        var p = c.Privacy;
        var recent = await db.LoungeMessages.AsNoTracking().Where(m => m.DeletedAt == null && m.RefusedWhy == null && m.From != "system")
            .OrderByDescending(m => m.Id).Take(History).ToListAsync(ct);
        var wanted = recent.Where(m => m.OnId is not null).Select(m => m.OnId!.Value)
            .Concat(c.Comments.Select(x => x.Id)).Concat(c.Comments.Where(x => x.OnId is not null).Select(x => x.OnId!.Value))
            .Concat(c.ReplyTo is null ? [] : [c.ReplyTo.Id]).Distinct().Except(recent.Select(m => m.Id)).ToList();
        var older = wanted.Count == 0 ? []
            : await db.LoungeMessages.AsNoTracking().Where(m => wanted.Contains(m.Id) && m.DeletedAt == null && m.RefusedWhy == null).ToListAsync(ct);
        var all = older.Concat(recent).DistinctBy(m => m.Id).OrderBy(m => m.Id).ToList();
        var byId = all.ToDictionary(m => m.Id);
        string Time(DateTime at) => Clock.ToLocal(at).ToString("HH:mm", CultureInfo.InvariantCulture);
        string Who(LoungeMessageEntity m) => m.From switch
        {
            LoungeVoices.Cookie or LoungeVoices.Seraph => m.From == c.Voice ? "you" : other,
            _ => c.People.As(m.UserId, p),
        };
        var lines = new List<JsonObject>();
        foreach (var m in all)
        {
            if (m.From == c.Voice)
            {
                lines.Add(Message("assistant", c.People.Hide(p.Mask(m.Text))));
                continue;
            }
            string text;
            if (LoungeVoices.Known(m.From))
            {
                text = $"{Time(m.At)} {other}: {context.Untrusted(m.From, m.Text, 2000)}";
            }
            else if (m.Kind == "comment")
            {
                var on = m.OnId is { } onId && byId.TryGetValue(onId, out var target)
                    ? target.From == c.Voice ? $"your message of {Time(target.At)}" : $"{Who(target)}'s message of {Time(target.At)}"
                    : "your conversation";
                text = $"{Time(m.At)} {Who(m)}, a comment on {on}: {context.Untrusted("comment", m.Text, 500)}";
            }
            else
            {
                text = $"{Time(m.At)} {Who(m)}: {context.Untrusted("person", m.Text, 1000)}";
            }
            lines.Add(Message("user", c.People.Hide(text)));
        }
        return lines;
    }

    private async Task<string> CookieModelAsync(CancellationToken ct)
    {
        var models = await settings.GetAsync<ModelSettings>(ct);
        return string.IsNullOrWhiteSpace(models.Model) ? options.CurrentValue.Model : models.Model.Trim();
    }

    /// <summary>The answer as a chat line: no name in front, no echoed tags, at most <paramref name="max"/>; null for nothing to add.</summary>
    public static string? Clean(string? text, string me, int max = MaxChars)
    {
        var t = text?.Trim() ?? "";
        t = Untrusted().Replace(t, "").Trim();
        foreach (var prefix in new[] { $"{me}:", $"**{me}:**", $"**{me}**:" })
        {
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                t = t[prefix.Length..].Trim();
            }
        }
        t = TimePrefix().Replace(t, "").Trim();
        if (t.Length == 0 || t.Trim('.', ' ').Equals(Pass.Trim('(', ')'), StringComparison.OrdinalIgnoreCase) || t.Equals(Pass, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Shorten(t, max);
    }

    /// <summary>Over <paramref name="max"/>: cut after the last sentence that fits, or at the limit when no sentence ends before it.</summary>
    public static string Shorten(string text, int max = MaxChars)
    {
        if (text.Length <= max)
        {
            return text;
        }
        var head = text[..max];
        var end = Math.Max(Math.Max(head.LastIndexOf(". ", StringComparison.Ordinal), head.LastIndexOf("! ", StringComparison.Ordinal)),
            head.LastIndexOf("? ", StringComparison.Ordinal));
        return end >= max / 3 ? head[..(end + 1)] : CaseBook.Cut(text, max)!;
    }

    private static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };

    /// <summary>[important] at the start, bold or not, with what may follow it (a colon, a dash).</summary>
    [GeneratedRegex(@"^\s*(\*\*)?\[important\](\*\*)?\s*[:\-–—]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ImportantPattern();

    [GeneratedRegex(@"</?untrusted[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Untrusted();

    [GeneratedRegex(@"^\d{1,2}:\d{2}\s+")]
    private static partial Regex TimePrefix();
}

/// <summary>The lounge can't talk until something is set up or fixed: the reason is shown, and the loop looks again.</summary>
public sealed class LoungePausedException(string reason) : Exception(reason);
