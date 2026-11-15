using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>Triage's answer (docs/cookie-prompts.md §4): the overall state and why, and the cases that need attention now.</summary>
public sealed record TriageAnswer(
    [property: Description("all-fine, info, warning or alert")] OverallState State,
    [property: Description("Why, in one sentence of at most 160 characters")] string Reason,
    [property: Description("The cases that need attention in this round, most important first; ids from the brief only")] IReadOnlyList<TriageCase> Cases);

public sealed record TriageCase(
    [property: Description("The case's id, from the brief")] string Id,
    [property: Description("Why it needs attention, at most 200 characters")] string Why);

/// <summary>A case's work ends in a note, and the proposals it needs now (§21.1); most rounds need none.</summary>
public sealed record CaseNoteAnswer(
    [property: Description("What is going on, what should happen next and who should do it; at most 600 characters")] string Note,
    [property: Description("info, important or urgent")] Urgency Urgency,
    [property: Description("Who should look at it, in a few words; empty when nobody needs to")] string Attention,
    [property: Description("What you propose to do now, at most 3; empty when nothing is needed")] IReadOnlyList<ProposedAction>? Proposals = null,
    [property: Description("True when something is broken and needs a technical fix that nobody has found yet")] bool NeedsFixing = false);

/// <summary>Whether a person's message gives an instruction, filled in for them to confirm (§10.3, §21.1).</summary>
public sealed record InstructionAnswer(
    [property: Description("True when the person gives an instruction: mute something, suppress a kind of message, or a note on how you should work for a while")] bool IsInstruction,
    [property: Description("mute, suppress or note")] string Kind,
    [property: Description("For mute: host, sensor, location or case; for suppress: channel, sensor-type or case-kind; empty for a note")] string Scope,
    [property: Description("For mute: the id from your data; for suppress: pushover, email, ticket, redmine-note, a sensor type, problem or ticket; empty for a note")] string Value,
    [property: Description("What it is about, for people: the host's name, the channel")] string Label,
    [property: Description("The instruction, or why; at most 300 characters")] string Text,
    [property: Description("For how many days, 1 to 30")] int Days);

/// <summary>The status line (docs/cookie-prompts.md §5).</summary>
public sealed record StatusAnswer([property: Description("One sentence, at most 160 characters")] string Reason);

/// <summary>
/// Puts Cookie's prompts together (docs/cookie-prompts.md): identity, goals and rules every time, then the step's prompt or the
/// case's instructions, with the parts in braces filled in by code. The prompts come from Admin → Cookie, or their defaults.
/// </summary>
public sealed class CookieBrain(CookieSettings settings, Instructions instructions, PrivacyFilter privacy)
{
    /// <summary>What package nine adds to every case, fixed in code: Cookie can't act yet (§3.4).</summary>
    public const string WatchOnly =
        "This version of you only watches. You read with your tools and write case notes; you can't send, post, open or propose " +
        "anything yet. Where the instructions say to propose or send something, write in your note what should happen and who " +
        "should do it.";

    public const int StatusChars = 160;

    /// <summary>
    /// What Cookie may propose now (§6, §21.1), fixed in code from the levels on Admin → Cookie → Actions; with every action off,
    /// it only watches, as in package nine.
    /// </summary>
    public async Task<string> ActingAsync(CancellationToken ct)
    {
        var levels = (await settings.GetAsync<ActionSettings>(ct)).Levels;
        var kinds = new[] { CookieActionKind.Pushover, CookieActionKind.Email, CookieActionKind.Ticket, CookieActionKind.PrivateNote,
            CookieActionKind.PublicAnswer, CookieActionKind.ObjectNote };
        var on = kinds.Where(k => levels.Of(k).Level != ActionLevel.Off).Select(ProposedAction.Name).ToList();
        if (on.Count == 0)
        {
            return WatchOnly;
        }
        return "You may propose actions now: " + string.Join(", ", on) + ", and hand-to-person for anything else. Each proposal goes through " +
            "code (rights, limits, duplicates, mute and suppress) and Seraph, the reviewer; some wait for a person. Propose only what the case " +
            "needs now; most rounds need none, and a note is enough when someone is already on it. Name people only by the user ids in " +
            "PeopleYouMayAddress, tickets by their number, hosts and sensors by their ids. Write each text as it is to be sent, in the " +
            "requester's language for a public answer, and never with secrets.";
    }

    public async Task<string> PromptAsync(string key, CancellationToken ct)
    {
        var prompts = await settings.GetAsync<PromptSettings>(ct);
        return prompts.Texts.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text : CookiePrompts.Defaults[key].Text;
    }

    public async Task<string> CaseInstructionAsync(string kind, CancellationToken ct)
    {
        var cases = await settings.GetAsync<CaseSettings>(ct);
        return cases.Instructions.TryGetValue(kind, out var text) && !string.IsNullOrWhiteSpace(text) ? text : CookiePrompts.CaseKinds[kind].Text;
    }

    /// <summary>The values every prompt may use.</summary>
    public async Task<Dictionary<string, string>> ValuesAsync(CancellationToken ct)
    {
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        var redmine = await settings.GetAsync<CookieRedmineSettings>(ct);
        // People's instructions and the lessons go into every prompt: masked like everything else that leaves PNMT (§13.2).
        var p = await privacy.OpenAsync(ct);
        var active = (await settings.GetAsync<LessonSettings>(ct)).Lessons.Where(l => l.Active).ToList();
        var instructionText = p.Mask(Instructions.PromptText(await instructions.ActiveAsync(ct)));
        var lessonText = active.Count > 0 ? p.Mask(string.Join("\n", active.Select(l => $"- {l.Text}"))) : "None yet.";
        await p.SaveAsync(ct);
        var first = redmine.Projects.FirstOrDefault() ?? WatchedProject.Default(0);
        return new Dictionary<string, string>
        {
            ["name"] = identity.Name,
            ["creator"] = identity.Creator,
            ["organisation"] = string.IsNullOrWhiteSpace(identity.Organisation) ? "your organisation" : identity.Organisation,
            ["tone"] = identity.Tone,
            ["language"] = identity.Language,
            ["first_answer_hours"] = first.FirstAnswerHours.ToString("0.#", CultureInfo.InvariantCulture),
            ["update_days"] = first.UpdateDays.ToString(CultureInfo.InvariantCulture),
            // Mute, suppress and instructions (§10.3), and the lessons (§11).
            ["instructions"] = instructionText,
            ["lessons"] = lessonText,
            ["max_chars"] = StatusChars.ToString(CultureInfo.InvariantCulture),
            ["now"] = Clock.LocalNow.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Prompts 1 to 3: who Cookie is, its goals, its rules.</summary>
    public async Task<string> BaseAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct) => string.Join("\n\n",
        CookiePrompts.Fill(await PromptAsync("identity", ct), values),
        CookiePrompts.Fill(await PromptAsync("goals", ct), values),
        CookiePrompts.Fill(await PromptAsync("rules", ct), values));

    /// <summary>
    /// The kind of case for its instructions (§9.3, Cases): a ticket waits for its first answer or for an update; a problem is
    /// unresolved, recurring, a Zabbix problem or a bad sensor. Several failing at once is an extra instruction.
    /// </summary>
    public static string KindOf(CookieCaseEntity c, IReadOnlyList<CaseMember> members, int recurrences, double unresolvedHours, DateTime now) => c.Kind switch
    {
        CookieCaseKind.Ticket => c.FirstAnswerAt is null && !c.AuthorIsStaff ? "firstAnswer" : "noUpdate",
        CookieCaseKind.Request => "request",
        _ when now - c.ProgressAt >= TimeSpan.FromHours(unresolvedHours) => "unresolved",
        _ when recurrences >= 3 => "recurring",
        _ when members.Count > 0 && members.All(m => m.Type == CaseMember.Zabbix) => "zabbixProblem",
        _ => "badSensor",
    };

    /// <summary>The instructions for a case: its kind's, then the narrower ones whose scope it is in.</summary>
    public async Task<string> InstructionsAsync(CookieCaseEntity c, string kind, IReadOnlyList<CaseMember> members, bool manyAtOnce,
        IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var cases = await settings.GetAsync<CaseSettings>(ct);
        var parts = new List<string> { CookiePrompts.Fill(await CaseInstructionAsync(kind, ct), values) };
        if (manyAtOnce && kind != "manyAtOnce")
        {
            parts.Add(CookiePrompts.Fill(await CaseInstructionAsync("manyAtOnce", ct), values));
        }
        var types = members.Select(m => m.SensorType).Where(t => t is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in cases.Scoped)
        {
            var applies = s.Scope switch
            {
                InstructionScope.Location => c.LocationId?.ToString() == s.Value.Trim(),
                InstructionScope.Host => c.HostId?.ToString() == s.Value.Trim(),
                InstructionScope.Project => c.ProjectId?.ToString(CultureInfo.InvariantCulture) == s.Value.Trim(),
                InstructionScope.SensorType => types.Contains(s.Value.Trim()),
                _ => false,
            };
            if (applies)
            {
                parts.Add($"For {s.Label}:\n{s.Text}");
            }
        }
        return string.Join("\n\n", parts);
    }

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Ai.AiJson.Options);
}
