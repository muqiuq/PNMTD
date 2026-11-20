using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Lounge;

/// <summary>A voice's night (docs/lounge-plan.md §7.3, §17.5): what to refresh, add, forget and keep, and how its positions moved.</summary>
public sealed record LoungeNightAnswer(
    [property: Description("The ids of the memories today's conversation touched, like m41")] IReadOnlyList<string> Refreshed,
    [property: Description("At most 3 new memories, one sentence each; usually one or two")] IReadOnlyList<LoungeNewMemory> New,
    [property: Description("The ids of memories that are no longer true; never a keepsake")] IReadOnlyList<string> Forget,
    [property: Description("A keepsake to keep; Keep empty when nothing changes")] LoungeKeepsakeSwap Keepsake,
    [property: Description("How your positions moved, usually not at all: an empty list")] IReadOnlyList<LoungeOpinionMove> Opinions);

public sealed record LoungeNewMemory(
    [property: Description("One sentence of at most 200 characters, people as PERSON_n")] string Text,
    [property: Description("moment, person, joke or lesson")] string Kind);

public sealed record LoungeKeepsakeSwap(
    [property: Description("The memory to keep forever: an id like m41, or new:0 for the first of tonight's new ones; empty for none")] string Keep,
    [property: Description("With three keepsakes already: the one you let go, like k12; empty otherwise")] string LetGo);

public sealed record LoungeOpinionMove(
    [property: Description("The conviction's key")] string Conviction,
    [property: Description("How far you moved, within the limit; negative towards the first pole")] double Shift,
    [property: Description("Why, in one sentence")] string Why);

/// <summary>What a night came to, for the scene's row.</summary>
public sealed record NightResult(decimal Cost, int Calls, string Summary);

/// <summary>
/// The night (docs/lounge-plan.md §7.3): after the evening scene, or at the next wake, each voice gets one call without tools on its own
/// model, with today's conversation, the day's scenes in one line each, its memories and keepsakes, and both convictions. Code applies the
/// answer: refreshes, adds, forgets, swaps keepsakes (§7.4), moves positions within the drift (§6.2); then it sweeps the weak memories and
/// those naming people who opted out (§9.4).
/// </summary>
public sealed class LoungeNight(IDbContextFactory<PnmtdDbContext> dbs, CookieModel model, Seraph seraph, LoungeVoice voices, CookieBrain brain,
    IOptionsMonitor<CookieOptions> options, ILogger<LoungeNight> logger)
{
    public const int DayMessages = 120;

    public async Task<NightResult> RunAsync(string voice, LoungeSettings settings, Pseudonyms p, LoungePeople people, DateTime since,
        Dictionary<string, Dictionary<string, double>> positions, long sceneId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var values = await voices.ValuesAsync(voice, people, ct);
        var me = LoungeVoice.Name(voice, values["name"]);
        var other = values["other"];
        var now = Clock.UtcNow;
        string Time(DateTime at) => Clock.ToLocal(at).ToString("HH:mm", CultureInfo.InvariantCulture);

        // Today: the conversation (the last 120 lines), and every scene in one line, so that a continuous day is seen whole (§7.3).
        var lines = (await db.LoungeMessages.AsNoTracking().Where(m => m.At >= since && m.DeletedAt == null && m.RefusedWhy == null && m.From != "system")
            .OrderByDescending(m => m.Id).Take(DayMessages).ToListAsync(ct)).OrderBy(m => m.Id)
            .Select(m => $"{Time(m.At)} {(m.From == voice ? "you" : LoungeVoices.Known(m.From) ? other : people.As(m.UserId, p))}" +
                $"{(m.Kind == "comment" ? " (a comment)" : "")}: {p.Mask(InputSanitizer.Clean(m.Text, 600).Text)}").ToList();
        var scenes = await db.LoungeScenes.AsNoTracking().Where(s => s.StartedAt >= since && s.Kind != "night").OrderBy(s => s.Id)
            .Select(s => new { s.StartedAt, s.Kind, s.Seed, s.Opener, s.Messages }).ToListAsync(ct);
        var memories = await db.LoungeMemories.Where(m => m.Voice == voice).ToListAsync(ct);
        var raised = await db.CookieCases.AsNoTracking().Where(c => c.Kind == CookieCaseKind.Raised && c.OpenedAt >= since)
            .Select(c => new { c.Id, c.Title, c.Open, c.CookieNote }).ToListAsync(ct);
        var emails = await db.CookieDecisions.AsNoTracking().Where(d => d.Kind == LoungeTools.EmailKind && d.At >= since).Select(d => new { d.Title, d.Outcome })
            .ToListAsync(ct);
        var day = string.Join("\n\n",
            "Today's scenes:\n" + (scenes.Count == 0 ? "none" : string.Join("\n", scenes.Select(s =>
                $"- {Time(s.StartedAt)} {s.Kind}, opened by {(s.Opener == voice ? "you" : other)}, {s.Messages} messages: {(s.Seed is null ? "free talk" : p.Mask(s.Seed))}"))),
            "Today's conversation:\n" + (lines.Count == 0 ? "nothing" : string.Join("\n", lines)),
            "Your memories (id, strength now):\n" + (memories.Count(m => !m.Keepsake) == 0 ? "none" : string.Join("\n", memories.Where(m => !m.Keepsake)
                .OrderByDescending(m => LoungeMemory.Strength(m, now, settings.HalfLifeDays))
                .Select(m => FormattableString.Invariant($"- {LoungeMemory.Ref(m)} ({LoungeMemory.Strength(m, now, settings.HalfLifeDays):0.00}): {m.Text}")))),
            "Your keepsakes:\n" + (memories.Count(m => m.Keepsake) == 0 ? "none yet: you may keep up to three" : string.Join("\n", memories.Where(m => m.Keepsake)
                .Select(m => $"- {LoungeMemory.Ref(m)}: {m.Text}"))),
            "The convictions (keys " + string.Join(", ", settings.Convictions.Select(c => c.Key)) + "):\n" + LoungeVoice.Convictions(settings, positions, voice, other, p),
            "Cases you raised today:\n" + (raised.Count == 0 ? "none" : string.Join("\n", raised.Select(c =>
                $"- {p.Mask(c.Title)}: {(c.Open ? "open" : "closed")}{(c.CookieNote is null ? "" : $", Cookie's note: {p.Mask(c.CookieNote)}")}"))),
            "E-mails today:\n" + (emails.Count == 0 ? "none" : string.Join("\n", emails.Select(e => $"- {p.Mask(e.Title)}: {e.Outcome}"))));
        values["drift"] = settings.MaxDriftPerDay.ToString("0.00", CultureInfo.InvariantCulture);
        values["day"] = people.Hide(day);
        var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = people.Hide(await voices.SystemAsync(voice, values, ct)) },
            new JsonObject { ["role"] = "user", ["content"] = CookiePrompts.Fill(await brain.PromptAsync("loungeNight", ct), values) });
        var choice = voice == LoungeVoices.Seraph ? await seraph.ChoiceAsync(ct) ?? throw new LoungePausedException("Seraph has no model: it takes two to talk.") : null;
        var turn = await model.CompleteAsync($"lounge.night.{voice}", messages, null, (nameof(LoungeNightAnswer), AiJson.SchemaFor<LoungeNightAnswer>()),
            settings.Effort, null, options.CurrentValue.Tokens.LoungeNight, ct, choice);
        LoungeNightAnswer answer;
        try
        {
            answer = JsonSerializer.Deserialize<LoungeNightAnswer>(turn.Content ?? "", AiJson.Options) ?? throw new JsonException("empty");
        }
        catch (JsonException e)
        {
            throw new AiRequestException(AiOutcome.InvalidOutput, $"The night's answer does not match the schema: {e.Message}");
        }
        var summary = Apply(db, voice, answer, memories, settings, positions, people, now);
        if (!string.IsNullOrWhiteSpace(turn.Reasoning))
        {
            db.LoungeSteps.Add(Step(sceneId, "reasoning", $"{me}'s night: thinking", turn.Reasoning!));
        }
        db.LoungeSteps.Add(Step(sceneId, "memory", $"{me}'s night", summary));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("The lounge's night for {Voice}: {Summary}", voice, summary);
        return new NightResult(turn.Cost, 1, summary);
    }

    /// <summary>Code applies a night's answer (§7.3): unknown ids are ignored, each change is in the summary.</summary>
    public static string Apply(PnmtdDbContext db, string voice, LoungeNightAnswer answer, List<LoungeMemoryEntity> memories, LoungeSettings settings,
        Dictionary<string, Dictionary<string, double>> positions, LoungePeople people, DateTime now)
    {
        var done = new List<string>();
        foreach (var reference in (answer.Refreshed ?? []).Distinct())
        {
            if (LoungeMemory.Find(memories, reference) is { } m)
            {
                LoungeMemory.Touch(m, now, settings.HalfLifeDays);
                done.Add($"refreshed {LoungeMemory.Ref(m)}");
            }
        }
        var added = new List<LoungeMemoryEntity>();
        foreach (var n in (answer.New ?? []).Where(n => !string.IsNullOrWhiteSpace(n.Text)).Take(3))
        {
            var text = CaseBook.Cut(n.Text.Trim(), LoungeMemory.MaxChars)!;
            var memory = new LoungeMemoryEntity
            {
                Voice = voice, Text = text, Kind = LoungeMemory.Kinds.Contains(n.Kind) ? n.Kind : "moment", Strength0 = 1, RefreshedAt = now, CreatedAt = now,
                People = [.. LoungeTools.PeopleIn(text, people.ByToken)],
            };
            db.LoungeMemories.Add(memory);
            memories.Add(memory);
            added.Add(memory);
            done.Add($"new: {text}");
        }
        foreach (var reference in (answer.Forget ?? []).Distinct())
        {
            if (LoungeMemory.Find(memories, reference) is { Keepsake: false } m && m.Id != 0)
            {
                db.LoungeMemories.Remove(m);
                memories.Remove(m);
                done.Add($"forgot {LoungeMemory.Ref(m)}");
            }
        }
        if (answer.Keepsake is { Keep.Length: > 0 } swap)
        {
            var keep = swap.Keep.StartsWith("new:", StringComparison.OrdinalIgnoreCase) && int.TryParse(swap.Keep.AsSpan(4), out var i) && i >= 0 && i < added.Count
                ? added[i] : LoungeMemory.Find(memories, swap.Keep);
            var letGo = LoungeMemory.Find(memories, swap.LetGo);
            if (keep is not null && LoungeMemory.Swap(memories, keep, letGo, now))
            {
                done.Add($"keeps \"{keep.Text}\"{(letGo is { Keepsake: false } ? $", lets go of \"{letGo.Text}\"" : "")}");
            }
        }
        foreach (var move in answer.Opinions ?? [])
        {
            if (settings.Convictions.FirstOrDefault(c => c.Key == move.Conviction) is not { } conviction || !positions.TryGetValue(conviction.Key, out var held))
            {
                continue;
            }
            var before = held[voice];
            var after = LoungeOpinions.Move(before, move.Shift, settings.MaxDriftPerDay, conviction.Owner == voice, conviction.StartOf(conviction.Owner));
            if (Math.Abs(after - before) < 0.0001)
            {
                continue;
            }
            held[voice] = after;
            db.LoungeOpinions.Add(new LoungeOpinionEntity
            {
                At = now, Conviction = conviction.Key, Voice = voice, Before = before, After = after, Why = CaseBook.Cut(move.Why?.Trim() ?? "", 300)!,
            });
            done.Add(FormattableString.Invariant($"{conviction.Key}: {before:+0.00;-0.00} → {after:+0.00;-0.00}"));
        }
        // The sweep (§7.3), and the memories of people who opted out (§9.4).
        var hidden = people.Users.Where(u => !u.Value.Mentions || !u.Value.Active).Select(u => u.Key).ToHashSet();
        foreach (var m in memories.Where(m => m.People.Any(hidden.Contains)).ToList())
        {
            if (m.Id != 0)
            {
                db.LoungeMemories.Remove(m);
            }
            else
            {
                db.Entry(m).State = EntityState.Detached;
            }
            memories.Remove(m);
            done.Add($"forgot {LoungeMemory.Ref(m)}: it names someone who opted out");
        }
        foreach (var m in LoungeMemory.Swept(memories, now, settings.HalfLifeDays))
        {
            if (m.Id != 0)
            {
                db.LoungeMemories.Remove(m);
                done.Add($"faded {LoungeMemory.Ref(m)}");
            }
        }
        return done.Count == 0 ? "Nothing changed." : string.Join("; ", done);
    }

    private static LoungeStepEntity Step(long sceneId, string kind, string title, string body) =>
        new() { SceneId = sceneId, At = Clock.UtcNow, Kind = kind, Title = CaseBook.Cut(title, 300)!, Body = CaseBook.Cut(body, 20_000)! };
}
