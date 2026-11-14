using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PNMTD.Api;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Zabbix;

namespace PNMTD.Cookie;

/// <summary>A section of Admin → Cookie (docs/cookie-plan.md §9.3): its name, and its checks.</summary>
public interface ICookieSection
{
    static abstract string Section { get; }

    /// <summary>The problems with the values, by field; none when they can be saved.</summary>
    void Validate(ApiValidator v);
}

/// <summary>Identity (§9.3): who Cookie is. The prompts' {name}, {creator}, {organisation}, {tone} and {language}.</summary>
public sealed record IdentitySettings : ICookieSection
{
    public static string Section => "identity";

    public string Name { get; init; } = "Cookie";

    public string Creator { get; init; } = "Philipp";

    /// <summary>The organisation PNMT monitors for; "your organisation" when empty.</summary>
    public string Organisation { get; init; } = "";

    public string Tone { get; init; } = "warm, plain and to the point";

    public string Language { get; init; } = "English";

    public void Validate(ApiValidator v) => v
        .Check(Name.Trim().Length is > 0 and <= 40, "name", "1 to 40 characters.")
        .Check(Creator.Length <= 100, "creator", "At most 100 characters.")
        .Check(Organisation.Length <= 200, "organisation", "At most 200 characters.")
        .Check(Tone.Length <= 200, "tone", "At most 200 characters.")
        .Check(Language.Trim().Length is > 0 and <= 40, "language", "1 to 40 characters.");
}

/// <summary>Prompts (§9.3, docs/cookie-prompts.md): only the ones changed; the others are the defaults.</summary>
public sealed record PromptSettings : ICookieSection
{
    public static string Section => "prompts";

    /// <summary>A prompt's key (<see cref="CookiePrompts"/>) → its text.</summary>
    public Dictionary<string, string> Texts { get; init; } = [];

    public void Validate(ApiValidator v)
    {
        foreach (var (key, text) in Texts)
        {
            v.Check(CookiePrompts.Defaults.ContainsKey(key), $"texts.{key}", "Not one of Cookie's prompts.");
            v.Check(text.Length <= 12_000, $"texts.{key}", "At most 12,000 characters.");
            foreach (var placeholder in CookiePrompts.Required(key))
            {
                v.Check(text.Contains(placeholder, StringComparison.Ordinal), $"texts.{key}", $"{placeholder} can be moved, but not removed.");
            }
        }
    }
}

/// <summary>Where a narrower instruction applies (§9.3, Cases).</summary>
public enum InstructionScope { Location, Host, Project, SensorType }

public sealed record ScopedInstruction(Guid Id, InstructionScope Scope, string Value, string Label, string Text);

/// <summary>Cases (§9.3): what opens a case, when a problem is unresolved, and an instruction for each kind of case.</summary>
public sealed record CaseSettings : ICookieSection
{
    public static string Section => "cases";

    /// <summary>A Zabbix problem joins a host's case from this severity on (§4.1).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ZabbixSeverity ZabbixMinSeverity { get; init; } = ZabbixSeverity.Average;

    /// <summary>A problem without progress for this long gets a ticket (§7.1): 2 days.</summary>
    public double UnresolvedHours { get; init; } = 48;

    /// <summary>A kind's key (<see cref="CookiePrompts.CaseKinds"/>) → its instruction, when changed.</summary>
    public Dictionary<string, string> Instructions { get; init; } = [];

    public List<ScopedInstruction> Scoped { get; init; } = [];

    public void Validate(ApiValidator v)
    {
        v.Check(UnresolvedHours is >= 1 and <= 24 * 60, "unresolvedHours", "1 hour to 60 days.");
        foreach (var (key, text) in Instructions)
        {
            v.Check(CookiePrompts.CaseKinds.ContainsKey(key), $"instructions.{key}", "Not a kind of case.");
            v.Check(text.Length <= 6000, $"instructions.{key}", "At most 6,000 characters.");
        }
        v.Check(Scoped.Count <= 200, "scoped", "At most 200.");
        foreach (var s in Scoped)
        {
            v.Check(s.Value.Trim().Length is > 0 and <= 100, $"scoped.{s.Id}", "Choose what it applies to.");
            v.Check(s.Text.Trim().Length is > 0 and <= 6000, $"scoped.{s.Id}", "1 to 6,000 characters.");
        }
    }
}

/// <summary>Working hours (§7.3): each weekday's periods, and holidays with their own.</summary>
public sealed record HoursSettings : ICookieSection
{
    public static string Section => "hours";

    public List<DayHours> Week { get; init; } = [.. WorkingCalendar.DefaultWeek];

    public List<Holiday> Holidays { get; init; } = [];

    public WorkingCalendar Calendar() => new(Week, Holidays);

    public void Validate(ApiValidator v)
    {
        v.Check(Week.Select(d => d.Day).Distinct().Count() == Week.Count, "week", "Each day once.");
        foreach (var day in Week)
        {
            Periods(v, $"week.{day.Day}", day.Periods);
        }
        v.Check(Holidays.Select(h => h.Date).Distinct().Count() == Holidays.Count, "holidays", "Each date once.");
        v.Check(Holidays.Count <= 500, "holidays", "At most 500.");
        foreach (var holiday in Holidays)
        {
            v.Check(holiday.Name.Length <= 100, $"holidays.{holiday.Date:yyyy-MM-dd}", "At most 100 characters.");
            Periods(v, $"holidays.{holiday.Date:yyyy-MM-dd}", holiday.Periods);
        }
    }

    private static void Periods(ApiValidator v, string field, IReadOnlyList<HoursPeriod> periods)
    {
        v.Check(periods.Count <= 6, field, "At most 6 periods a day.");
        var parsed = new List<(TimeOnly From, TimeOnly To)>();
        foreach (var p in periods)
        {
            if (!p.TryParse(out var from, out var to))
            {
                v.Add(field, $"{p.From}–{p.To}: from and to as HH:mm, from before to.");
                continue;
            }
            parsed.Add((from, to));
        }
        var ordered = parsed.OrderBy(p => p.From).ToList();
        v.Check(ordered.Zip(ordered.Skip(1)).All(pair => pair.First.To <= pair.Second.From), field, "Periods may not overlap.");
    }
}

/// <summary>How urgent something is (§8): info, important, urgent.</summary>
public enum Urgency { Info, Important, Urgent }

/// <summary>When a recipient rule applies (§8).</summary>
public enum RecipientWhen { Always, WorkingHours, OutsideWorkingHours }

/// <summary>What a recipient rule covers (§8).</summary>
public enum RecipientScope { Everything, Location, Host, Project, CaseKind }

/// <summary>Where a person is told (§8).</summary>
public enum RecipientChannel { Dashboard, Email, Pushover }

/// <summary>A person's contact details (§8): Cookie's own, since PNMT's users have neither an address nor a Pushover key.</summary>
public sealed record CookieContact(Guid UserId, string? Email, string? PushoverKey, string? RedmineName, string? QuietFrom, string? QuietTo)
{
    /// <summary>Whether <paramref name="localTime"/> is in the person's quiet hours (which may span midnight).</summary>
    public bool IsQuiet(TimeOnly localTime)
    {
        if (!TimeOnly.TryParse(QuietFrom, System.Globalization.CultureInfo.InvariantCulture, out var from)
            || !TimeOnly.TryParse(QuietTo, System.Globalization.CultureInfo.InvariantCulture, out var to) || from == to)
        {
            return false;
        }
        return from < to ? localTime >= from && localTime < to : localTime >= from || localTime < to;
    }
}

/// <summary>Who gets what (§8): a scope, an urgency from which it applies, when, who, and by which channels.</summary>
public sealed record RecipientRule(
    Guid Id,
    string Name,
    RecipientScope Scope,
    [property: Description("The location's or host's id, the Redmine project's number, or the kind of case (problem, ticket)")] string? Value,
    Urgency MinUrgency,
    RecipientWhen When,
    List<Guid> Users,
    List<Guid> Groups,
    List<RecipientChannel> Channels);

/// <summary>Notifications (§8, §9.3): the contact details, and who gets what.</summary>
public sealed record NotificationSettings : ICookieSection
{
    public static string Section => "notifications";

    public List<CookieContact> Contacts { get; init; } = [];

    public List<RecipientRule> Rules { get; init; } = [];

    public CookieContact? ContactOf(Guid userId) => Contacts.FirstOrDefault(c => c.UserId == userId);

    public void Validate(ApiValidator v)
    {
        v.Check(Contacts.Select(c => c.UserId).Distinct().Count() == Contacts.Count, "contacts", "One contact per user.");
        foreach (var c in Contacts)
        {
            var field = $"contacts.{c.UserId}";
            v.Check(string.IsNullOrWhiteSpace(c.Email) || System.Net.Mail.MailAddress.TryCreate(c.Email.Trim(), out _), field, "Not an e-mail address.");
            v.Check(string.IsNullOrWhiteSpace(c.PushoverKey) || System.Text.RegularExpressions.Regex.IsMatch(c.PushoverKey.Trim(), "^[A-Za-z0-9]{30}$"),
                field, "A Pushover user key has 30 letters and digits.");
            v.Check((c.RedmineName ?? "").Length <= 200, field, "At most 200 characters.");
            v.Check(Quiet(c.QuietFrom) && Quiet(c.QuietTo), field, "Quiet hours as HH:mm.");
        }
        v.Check(Rules.Count <= 200, "rules", "At most 200.");
        foreach (var r in Rules)
        {
            var field = $"rules.{r.Id}";
            v.Check(r.Name.Trim().Length is > 0 and <= 100, field, "A name of 1 to 100 characters.");
            v.Check(r.Scope == RecipientScope.Everything || !string.IsNullOrWhiteSpace(r.Value), field, "Choose what the rule covers.");
            v.Check(r.Users.Count + r.Groups.Count > 0, field, "Choose at least one user or group.");
            v.Check(r.Channels.Count > 0, field, "Choose at least one channel.");
        }
    }

    private static bool Quiet(string? value) =>
        string.IsNullOrWhiteSpace(value) || TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);
}

/// <summary>A watched Redmine project and its ticket rules (§7.2, §9.3).</summary>
public sealed record WatchedProject(
    int ProjectId,
    double FirstAnswerHours,
    double FirstAnswerReminderHours,
    int UpdateDays,
    int UpdateReminderDays,
    Guid? EscalationGroupId,
    List<int> StaffRoleIds,
    List<int> StopStatusIds)
{
    public static WatchedProject Default(int projectId) => new(projectId, 4, 3, 14, 12, null, [], []);
}

/// <summary>Redmine (§9.3): the watched projects, and where Cookie's own tickets go.</summary>
public sealed record CookieRedmineSettings : ICookieSection
{
    public static string Section => "redmine";

    public List<WatchedProject> Projects { get; init; } = [];

    /// <summary>The project for the tickets of §7.1 (the ticket after 2 days).</summary>
    public int? TicketProjectId { get; init; }

    public int? TicketTrackerId { get; init; }

    public WatchedProject? Of(int projectId) => Projects.FirstOrDefault(p => p.ProjectId == projectId);

    public void Validate(ApiValidator v)
    {
        v.Check(Projects.Select(p => p.ProjectId).Distinct().Count() == Projects.Count, "projects", "Each project once.");
        foreach (var p in Projects)
        {
            var field = $"projects.{p.ProjectId}";
            v.Check(p.FirstAnswerHours is > 0 and <= 200, field, "The first answer: 0 to 200 working hours.");
            v.Check(p.FirstAnswerReminderHours > 0 && p.FirstAnswerReminderHours < p.FirstAnswerHours, field, "The reminder comes before the deadline.");
            v.Check(p.UpdateDays is >= 1 and <= 365, field, "Updates: 1 to 365 days.");
            v.Check(p.UpdateReminderDays >= 1 && p.UpdateReminderDays < p.UpdateDays, field, "The reminder comes before the deadline.");
        }
    }
}

/// <summary>The code's duties (§19.1): each off until an admin turns it on.</summary>
public sealed record DutySwitches(
    bool FirstAnswerReminder = false,
    bool FirstAnswerMissed = false,
    bool UpdateReminder = false,
    bool UpdateMissed = false,
    bool ProblemTicket = false,
    bool ClosingNote = false)
{
    public bool On(CookieDutyKind kind) => kind switch
    {
        CookieDutyKind.FirstAnswerReminder => FirstAnswerReminder,
        CookieDutyKind.FirstAnswerMissed => FirstAnswerMissed,
        CookieDutyKind.UpdateReminder => UpdateReminder,
        CookieDutyKind.UpdateMissed => UpdateMissed,
        CookieDutyKind.ProblemTicket => ProblemTicket,
        CookieDutyKind.ClosingNote => ClosingNote,
        _ => false,
    };
}

/// <summary>The output limits of §6.1: what may go out, per day (and per hour for Pushover), up to <see cref="Ceilings"/>.</summary>
public sealed record OutputLimits(
    int PushoverPerPersonDay = 3,
    int PushoverPerPersonHour = 1,
    int EmailPerPersonDay = 10,
    int MessagesPerDay = 30,
    int TicketsPerDay = 5,
    int NotesPerTicketDay = 2,
    int RedmineNotesPerDay = 30,
    int PublicAnswersPerDay = 10,
    int ObjectNotesPerDay = 50,
    int BigBrotherPerDay = 10,
    int BigBrotherPerCaseDay = 2)
{
    /// <summary>The ceilings in code: only a new release raises them.</summary>
    public static readonly OutputLimits Ceilings = new(10, 5, 50, 200, 20, 10, 200, 50, 200, 30, 5);
}

/// <summary>Actions (§6, §9.3): the duties' switches, and the output limits.</summary>
public sealed record ActionSettings : ICookieSection
{
    public static string Section => "actions";

    public DutySwitches Duties { get; init; } = new();

    public OutputLimits Limits { get; init; } = new();

    public void Validate(ApiValidator v)
    {
        var values = Limits.GetType().GetProperties().Where(p => p.PropertyType == typeof(int));
        foreach (var p in values)
        {
            var value = (int)p.GetValue(Limits)!;
            var ceiling = (int)p.GetValue(OutputLimits.Ceilings)!;
            v.Check(value >= 0 && value <= ceiling, $"limits.{JsonNamingPolicy.CamelCase.ConvertName(p.Name)}", $"0 to {ceiling}.");
        }
    }
}

/// <summary>Models (§9.3): what overrides the .env defaults, and the limits per round.</summary>
public sealed record ModelSettings : ICookieSection
{
    public static string Section => "models";

    /// <summary>Empty: Cookie__Model.</summary>
    public string? Model { get; init; }

    /// <summary>Empty: Cookie__Providers.</summary>
    public string? Providers { get; init; }

    public string TriageEffort { get; init; } = "low";

    public string WorkEffort { get; init; } = "medium";

    public int SleepMinutes { get; init; } = 5;

    public int MaxCasesPerRound { get; init; } = 5;

    public int MaxToolCalls { get; init; } = 30;

    /// <summary>Empty: Cookie__BudgetUsd.</summary>
    public decimal? BudgetUsd { get; init; }

    public static readonly string[] Efforts = ["minimal", "low", "medium", "high"];

    public void Validate(ApiValidator v) => v
        .Check(Model is null || Model.Length <= 200, "model", "At most 200 characters.")
        .Check(Providers is null || Providers.Length <= 300, "providers", "At most 300 characters.")
        .Check(Efforts.Contains(TriageEffort), "triageEffort", "minimal, low, medium or high.")
        .Check(Efforts.Contains(WorkEffort), "workEffort", "minimal, low, medium or high.")
        .Check(SleepMinutes is >= 1 and <= 120, "sleepMinutes", "1 to 120 minutes.")
        .Check(MaxCasesPerRound is >= 1 and <= 20, "maxCasesPerRound", "1 to 20.")
        .Check(MaxToolCalls is >= 0 and <= 60, "maxToolCalls", "0 to 60.")
        .Check(BudgetUsd is null or >= 0 and <= 10_000, "budgetUsd", "0 to 10,000 USD.");
}

/// <summary>Rights (§9.3): the group whose rights Cookie reads with. Never the admin flag.</summary>
public sealed record CookieRightsSettings : ICookieSection
{
    public static string Section => "rights";

    public Guid? GroupId { get; init; }

    public void Validate(ApiValidator v)
    {
    }
}

/// <summary>A saved version of a section: who, when, why.</summary>
public sealed record CookieSettingsVersion(int Version, DateTime ChangedAt, string ChangedBy, string? Note);

/// <summary>
/// The sections of Admin → Cookie (docs/cookie-plan.md §9.3, §19.3): the newest version of each, or its defaults; every save a
/// new version; a rollback saves an old version again. Cookie's tools can't reach them (§13.1).
/// </summary>
public sealed class CookieSettings(IDbContextFactory<PnmtdDbContext> dbs, IMemoryCache cache)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly IReadOnlyList<string> Sections =
    [
        IdentitySettings.Section, PromptSettings.Section, CaseSettings.Section, HoursSettings.Section, NotificationSettings.Section,
        CookieRedmineSettings.Section, ActionSettings.Section, ModelSettings.Section, CookieRightsSettings.Section,
    ];

    private static string Key(string section) => $"cookie-settings:{section}";

    public async Task<T> GetAsync<T>(CancellationToken ct) where T : class, ICookieSection, new() =>
        (await cache.GetOrCreateAsync(Key(T.Section), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            await using var db = await dbs.CreateDbContextAsync(ct);
            var section = T.Section;
            var json = await db.CookieSettings.AsNoTracking().Where(s => s.Section == section).OrderByDescending(s => s.Version)
                .Select(s => s.Json).FirstOrDefaultAsync(ct);
            return Parse<T>(json) ?? new T();
        }))!;

    /// <summary>Checks and saves a new version; returns it.</summary>
    public async Task<int> SaveAsync<T>(T value, Guid? userId, string userName, string? note, CancellationToken ct) where T : class, ICookieSection, new()
    {
        var v = new ApiValidator();
        value.Validate(v);
        v.ThrowIfInvalid();
        return await SaveJsonAsync(T.Section, JsonSerializer.Serialize(value, Json), userId, userName, note, ct);
    }

    public async Task<IReadOnlyList<CookieSettingsVersion>> VersionsAsync(string section, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await db.CookieSettings.AsNoTracking().Where(s => s.Section == section).OrderByDescending(s => s.Version)
            .Select(s => new CookieSettingsVersion(s.Version, s.ChangedAt, s.ChangedByName, s.Note)).Take(200).ToListAsync(ct);
    }

    public async Task<string?> JsonAsync(string section, int version, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await db.CookieSettings.AsNoTracking().Where(s => s.Section == section && s.Version == version).Select(s => s.Json)
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>The old version's values as a new version.</summary>
    public async Task<int> RollbackAsync(string section, int version, Guid? userId, string userName, CancellationToken ct)
    {
        var json = await JsonAsync(section, version, ct) ?? throw ApiException.NotFound("Version");
        return await SaveJsonAsync(section, json, userId, userName, $"Rolled back to version {version}", ct);
    }

    private async Task<int> SaveJsonAsync(string section, string json, Guid? userId, string userName, string? note, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var last = await db.CookieSettings.Where(s => s.Section == section).MaxAsync(s => (int?)s.Version, ct) ?? 0;
        db.CookieSettings.Add(new CookieSettingEntity
        {
            Section = section, Version = last + 1, Json = json, ChangedAt = Clock.UtcNow, ChangedById = userId, ChangedByName = userName,
            Note = note is { Length: > 300 } n ? n[..300] : note,
        });
        await db.SaveChangesAsync(ct);
        cache.Remove(Key(section));
        return last + 1;
    }

    public static T? Parse<T>(string? json) where T : class
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
}
