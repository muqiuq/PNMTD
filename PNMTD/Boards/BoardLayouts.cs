using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Lib.Models.Enum;
using PNMTD.Zabbix;

namespace PNMTD.Boards;

/// <summary>An element type in the catalog (§7.1): its group, name, line, icon, the right that adds it, and the integration it needs.</summary>
public sealed record BoardTypeInfo(BoardElementType Type, string Group, string Name, string Line, string Icon, (Area Area, Level Level)? Right,
    string? Needs);

/// <summary>A measure in the catalog (§8.2): its name, the filters it reads, the right that adds it, and the integration it needs.</summary>
public sealed record BoardMetricInfo(BoardMetric Metric, string Name, string Filters, (Area Area, Level Level) Right, string? Needs);

/// <summary>
/// A board's layout (docs/boards-plan.md §6.2): its defaults, the catalog of element types and measures, the validation of a save (which
/// keeps only the settings a type reads), the rights a change needs (§7.2), and the summary of a change for the audit log (§13.2).
/// </summary>
public static partial class BoardLayouts
{
    public const int SchemaVersion = 1;
    public const int MaxColumns = 4;
    public const int MaxElementsPerColumn = 12;
    public const int MaxElements = 30;
    public const int MaxRules = 20;
    public const int MaxNote = 4000;
    public const int MaxTargets = 24;

    /// <summary>The layout's JSON in the database and in the audit's summary: the API's, enums by name.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static readonly IReadOnlyList<BoardTypeInfo> Types =
    [
        new(BoardElementType.RedmineOverview, "Redmine", "Redmine overview", "The overview's counts and its lists of the important tickets", "pi pi-ticket",
            (Area.Redmine, Level.View), "redmine"),
        new(BoardElementType.RedmineNewest, "Redmine", "Newest tickets", "The newest tickets first, the recent ones marked New", "pi pi-inbox",
            (Area.Redmine, Level.View), "redmine"),
        new(BoardElementType.RedmineReceived, "Redmine", "Tickets received today", "A large number, with yesterday's below it if wanted", "pi pi-download",
            (Area.Redmine, Level.View), "redmine"),
        new(BoardElementType.RedmineSolved, "Redmine", "Tickets solved today", "Tickets closed today that are still closed", "pi pi-check-circle",
            (Area.Redmine, Level.View), "redmine"),
        new(BoardElementType.ZabbixCount, "Zabbix", "Zabbix problems", "The open problems by severity, coloured by the worst", "pi pi-chart-bar",
            (Area.Zabbix, Level.View), "zabbix"),
        new(BoardElementType.ZabbixHistory, "Zabbix", "Latest Zabbix problems", "Newest first: the open ones and those resolved lately", "pi pi-history",
            (Area.Zabbix, Level.View), "zabbix"),
        new(BoardElementType.Lounge, "Lounge", "The lounge", "The lounge's chat, read-only. Anyone who opens the board reads it.", "pi pi-comments", null, null),
        new(BoardElementType.SensorSummary, "Monitoring", "Sensor summary", "How many sensors are OK, failing, without data", "pi pi-chart-pie",
            (Area.Monitoring, Level.View), null),
        new(BoardElementType.FailingSensors, "Monitoring", "Failing sensors", "The most important first, then the longest failing", "pi pi-exclamation-triangle",
            (Area.Monitoring, Level.View), null),
        new(BoardElementType.StatusTiles, "Monitoring", "Status tiles", "A large tile for each chosen host or sensor", "pi pi-th-large",
            (Area.Monitoring, Level.View), null),
        new(BoardElementType.HostBars, "Monitoring", "Host bars", "Hosts with their 30-day uptime and 90-day bar", "pi pi-server",
            (Area.Monitoring, Level.View), null),
        new(BoardElementType.RecentChanges, "Monitoring", "Recent changes", "State changes of the last 24 hours, newest first", "pi pi-list",
            (Area.Monitoring, Level.View), null),
        new(BoardElementType.Uplink, "Monitoring", "Uplink", "Whether PNMT's uplink is up, and since when", "pi pi-cloud", (Area.Monitoring, Level.View), null),
        new(BoardElementType.Clock, "General", "Clock", "The time, large, with the date and other time zones", "pi pi-clock", null, null),
        new(BoardElementType.Note, "General", "Note", "A text written on the board: numbers to call, a message for the room", "pi pi-pencil", null, null),
        new(BoardElementType.OnDuty, "General", "On duty today", "The availability plan's people of the day", "pi pi-user", (Area.Availability, Level.View), null),
        new(BoardElementType.Certificates, "General", "Expiring certificates", "SSL certificates that expire soon, the soonest first", "pi pi-verified",
            (Area.Certificates, Level.View), null),
    ];

    public static readonly IReadOnlyList<BoardMetricInfo> Metrics =
    [
        new(BoardMetric.SensorsFailing, "Failing sensors", "importance, location", (Area.Monitoring, Level.View), null),
        new(BoardMetric.HostsFailing, "Hosts with a failing sensor", "importance, location", (Area.Monitoring, Level.View), null),
        new(BoardMetric.UplinkDown, "The uplink is down", "", (Area.Monitoring, Level.View), null),
        new(BoardMetric.LateTasks, "Late background tasks", "", (Area.Monitoring, Level.View), null),
        new(BoardMetric.DnsMismatches, "DNS zones that differ", "", (Area.Monitoring, Level.View), null),
        new(BoardMetric.ZabbixProblems, "Zabbix problems", "severity, host groups, suppressed", (Area.Zabbix, Level.View), "zabbix"),
        new(BoardMetric.RedmineOpen, "Redmine: open tickets", "project", (Area.Redmine, Level.View), "redmine"),
        new(BoardMetric.RedmineUrgent, "Redmine: urgent tickets", "project", (Area.Redmine, Level.View), "redmine"),
        new(BoardMetric.RedmineUnassigned, "Redmine: new, no one assigned", "project", (Area.Redmine, Level.View), "redmine"),
        new(BoardMetric.RedmineOverdue, "Redmine: overdue tickets", "project", (Area.Redmine, Level.View), "redmine"),
        new(BoardMetric.RedmineLinkedToProblem, "Redmine: tickets linked to a problem", "project", (Area.Redmine, Level.View), "redmine"),
        new(BoardMetric.RedmineReceivedToday, "Redmine: tickets received today", "project", (Area.Redmine, Level.View), "redmine"),
        new(BoardMetric.CertificatesExpiring, "Certificates expiring", "days", (Area.Certificates, Level.View), null),
    ];

    private static readonly Dictionary<BoardElementType, BoardTypeInfo> ByType = Types.ToDictionary(t => t.Type);
    private static readonly Dictionary<BoardMetric, BoardMetricInfo> ByMetric = Metrics.ToDictionary(m => m.Metric);

    public static BoardTypeInfo Of(BoardElementType type) => ByType[type];

    public static BoardMetricInfo Of(BoardMetric metric) => ByMetric[metric];

    // --- defaults ----------------------------------------------------------------------------------------------------------------

    public static readonly BoardLook DefaultLook = new(BoardThemes.Default, true, 100, true, BoardPeople.Names);

    public static readonly BoardHeader DefaultHeader = new(true, null, true, false, true);

    /// <summary>The rules of a new board (§8.2): Zabbix's only where Zabbix is configured.</summary>
    public static IReadOnlyList<BoardRule> DefaultRules(bool zabbix) =>
    [
        new(BoardMetric.SensorsFailing, BoardColour.Red, 1, "important sensors failing", new BoardRuleFilter(Importance: Importance.High)),
        .. zabbix ? new[] { new BoardRule(BoardMetric.ZabbixProblems, BoardColour.Red, 1, "Zabbix problems High or worse", new BoardRuleFilter(Severity: ZabbixSeverity.High)) } : [],
        new(BoardMetric.SensorsFailing, BoardColour.Yellow, 1, "sensors failing", new BoardRuleFilter()),
        new(BoardMetric.UplinkDown, BoardColour.Yellow, 1, "the uplink is down", new BoardRuleFilter()),
        .. zabbix ? new[] { new BoardRule(BoardMetric.ZabbixProblems, BoardColour.Yellow, 1, "Zabbix problems Average or worse", new BoardRuleFilter(Severity: ZabbixSeverity.Average)) } : [],
    ];

    public static BoardElement NewElement(BoardElementType type) => new(NewId(), type, null, true, null, Normalize(type, new BoardElementSettings()));

    /// <summary>
    /// A new board's layout (§10.2): empty columns, or the starting layout (the sensor summary, failing sensors and a clock), with the
    /// rules of §8.2 either way.
    /// </summary>
    public static BoardLayout New(int columns, bool start, bool zabbix)
    {
        columns = Math.Clamp(columns, 1, MaxColumns);
        var elements = Enumerable.Range(0, columns).Select(_ => new List<BoardElement>()).ToList();
        if (start)
        {
            var summary = NewElement(BoardElementType.SensorSummary);
            var failing = NewElement(BoardElementType.FailingSensors) with { Height = 1 };
            var clock = NewElement(BoardElementType.Clock);
            switch (columns)
            {
                case 1:
                    elements[0].AddRange([summary, failing, clock]);
                    break;
                case 2:
                    elements[0].AddRange([summary, failing]);
                    elements[1].Add(clock);
                    break;
                default:
                    elements[0].Add(summary);
                    elements[1].Add(failing);
                    elements[2].Add(clock);
                    break;
            }
        }
        return new BoardLayout(SchemaVersion, 30, DefaultLook, DefaultHeader,
            new BoardBar(true, BoardBarPosition.Top, BoardBarSize.Medium, "All clear", true, DefaultRules(zabbix)),
            [.. elements.Select(e => new BoardColumn(1, e))]);
    }

    public static string NewId() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));

    [GeneratedRegex("^[a-z0-9]{8}$")]
    private static partial Regex IdPattern();

    // --- reading and writing ------------------------------------------------------------------------------------------------------

    public static string Write(BoardLayout layout) => JsonSerializer.Serialize(layout, Json);

    /// <summary>The stored layout; a document that doesn't read (it never should) becomes an empty board rather than an error.</summary>
    public static BoardLayout Read(string json)
    {
        try
        {
            var layout = JsonSerializer.Deserialize<BoardLayout>(json, Json);
            if (layout?.Look is not null && layout.Header is not null && layout.Bar is not null && layout.Columns is not null)
            {
                return layout;
            }
        }
        catch (JsonException)
        {
        }
        return New(1, false, false);
    }

    // --- validation --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A layout as saved (§6.2): every range checked, with the field's path for the editor; the settings each type doesn't read dropped,
    /// and the ones it reads defaulted. Errors go into <paramref name="v"/> under <c>layout.…</c>.
    /// </summary>
    public static BoardLayout Validate(BoardLayout? layout, ApiValidator v)
    {
        if (layout is null || layout.Look is null || layout.Header is null || layout.Bar is null || layout.Columns is null)
        {
            v.Add("layout", "A layout: look, header, bar and columns.");
            return New(1, false, false);
        }
        v.Check(layout.RefreshSeconds is >= 10 and <= 300, "layout.refreshSeconds", "10 to 300 seconds.");
        var look = layout.Look;
        v.Check(BoardThemes.Ids.Contains(look.Theme), "layout.look.theme", "Not one of the themes.");
        v.Check(look.TextScale is >= 75 and <= 250, "layout.look.textScale", "75 to 250 %.");
        v.Check(Enum.IsDefined(look.People), "layout.look.people", "Names, initials or hidden.");
        var header = layout.Header with { Title = Trimmed(layout.Header.Title) };
        v.Check(header.Title is null || header.Title.Length <= 100, "layout.header.title", "At most 100 characters.");

        var bar = layout.Bar;
        var rules = bar.Rules ?? [];
        v.Check(rules.Count <= MaxRules, "layout.bar.rules", $"At most {MaxRules} rules.");
        v.Check(Enum.IsDefined(bar.Position) && Enum.IsDefined(bar.Size), "layout.bar", "A position and a size.");
        var greenText = Trimmed(bar.GreenText);
        v.Check(greenText is null || greenText.Length <= 60, "layout.bar.greenText", "At most 60 characters.");
        var cleanRules = rules.Select((r, i) => Rule(r, $"layout.bar.rules[{i}]", v)).ToList();

        v.Check(layout.Columns.Count is >= 1 and <= MaxColumns, "layout.columns", $"1 to {MaxColumns} columns.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        var columns = new List<BoardColumn>();
        foreach (var (column, c) in layout.Columns.Take(MaxColumns).Select((col, i) => (col, i)))
        {
            var path = $"layout.columns[{c}]";
            v.Check(column.Width is >= 1 and <= 4, $"{path}.width", "1 to 4 shares.");
            var elements = column.Elements ?? [];
            v.Check(elements.Count <= MaxElementsPerColumn, $"{path}.elements", $"At most {MaxElementsPerColumn} elements in a column.");
            total += elements.Count;
            var clean = new List<BoardElement>();
            foreach (var (element, e) in elements.Select((el, i) => (el, i)))
            {
                var at = $"{path}.elements[{e}]";
                v.Check(element.Id is not null && IdPattern().IsMatch(element.Id), $"{at}.id", "8 letters and digits.");
                v.Check(element.Id is null || ids.Add(element.Id), $"{at}.id", "Two elements have this id.");
                v.Check(Enum.IsDefined(element.Type), $"{at}.type", "Not an element type.");
                var title = Trimmed(element.Title);
                v.Check(title is null || title.Length <= 60, $"{at}.title", "At most 60 characters.");
                v.Check(element.Height is null or (>= 1 and <= 6), $"{at}.height", "Its own height, or 1 to 6 shares.");
                var settings = Enum.IsDefined(element.Type) ? Settings(element.Type, element.Settings ?? new BoardElementSettings(), $"{at}.settings", v)
                    : new BoardElementSettings();
                clean.Add(element with { Title = title, Settings = settings });
            }
            columns.Add(new BoardColumn(column.Width, clean));
        }
        v.Check(total <= MaxElements, "layout.columns", $"At most {MaxElements} elements on a board.");
        return new BoardLayout(SchemaVersion, layout.RefreshSeconds, look, header, bar with { GreenText = greenText, Rules = cleanRules }, columns);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static BoardRule Rule(BoardRule rule, string path, ApiValidator v)
    {
        v.Check(Enum.IsDefined(rule.Metric), $"{path}.metric", "Not a measure.");
        v.Check(rule.Colour is BoardColour.Yellow or BoardColour.Red, $"{path}.colour", "Yellow or red.");
        v.Check(rule.AtLeast is >= 1 and <= 9999, $"{path}.atLeast", "1 to 9999.");
        var label = Trimmed(rule.Label);
        v.Check(label is null || label.Length <= 60, $"{path}.label", "At most 60 characters.");
        var f = rule.Filter ?? new BoardRuleFilter();
        var filter = rule.Metric switch
        {
            BoardMetric.SensorsFailing or BoardMetric.HostsFailing => new BoardRuleFilter(Importance: f.Importance, LocationId: f.LocationId),
            BoardMetric.ZabbixProblems => new BoardRuleFilter(Severity: f.Severity ?? ZabbixSeverity.Information, Groups: Groups(f.Groups, $"{path}.filter.groups", v),
                Suppressed: f.Suppressed),
            BoardMetric.RedmineOpen or BoardMetric.RedmineUrgent or BoardMetric.RedmineUnassigned or BoardMetric.RedmineOverdue
                or BoardMetric.RedmineLinkedToProblem or BoardMetric.RedmineReceivedToday => new BoardRuleFilter(ProjectId: f.ProjectId),
            BoardMetric.CertificatesExpiring => new BoardRuleFilter(Days: Range(f.Days, 1, 90, 30, $"{path}.filter.days", v)),
            _ => new BoardRuleFilter(),
        };
        return rule with { Label = label, Filter = filter };
    }

    private static int Range(int? value, int min, int max, int fallback, string path, ApiValidator v)
    {
        v.Check(value is null || (value >= min && value <= max), path, $"{min} to {max}.");
        return value is { } x && x >= min && x <= max ? x : fallback;
    }

    private static IReadOnlyList<string>? Groups(IReadOnlyList<string>? groups, string path, ApiValidator v)
    {
        var clean = (groups ?? []).Select(g => g?.Trim() ?? "").Where(g => g.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        v.Check(clean.Count <= 20 && clean.All(g => g.Length <= 255), path, "At most 20 host groups.");
        return clean.Count == 0 ? null : clean;
    }

    /// <summary>A type's settings with its defaults (§7.1), for a new element and for a save that left something out.</summary>
    public static BoardElementSettings Normalize(BoardElementType type, BoardElementSettings s) => Settings(type, s, "", new ApiValidator());

    private static BoardElementSettings Settings(BoardElementType type, BoardElementSettings s, string path, ApiValidator v)
    {
        int Rows(int min, int max, int fallback) => Range(s.Rows, min, max, fallback, $"{path}.rows", v);
        int? Threshold(int? value, string name)
        {
            v.Check(value is null or (>= 0 and <= 9999), $"{path}.{name}", "0 to 9999.");
            return value is >= 0 and <= 9999 ? value : null;
        }
        switch (type)
        {
            case BoardElementType.RedmineOverview:
                var tiles = (s.RedmineTiles ?? [BoardRedmineTile.Open, BoardRedmineTile.Urgent, BoardRedmineTile.Unassigned, BoardRedmineTile.Overdue,
                    BoardRedmineTile.ReceivedToday, BoardRedmineTile.SolvedToday]).Where(Enum.IsDefined).Distinct().ToList();
                var lists = (s.RedmineLists ?? [BoardRedmineList.Urgent, BoardRedmineList.Unassigned, BoardRedmineList.Overdue, BoardRedmineList.LinkedToProblem])
                    .Where(Enum.IsDefined).Distinct().ToList();
                return new BoardElementSettings(Rows: Rows(1, 10, 5), ProjectId: s.ProjectId, Subjects: s.Subjects, RedmineTiles: tiles, RedmineLists: lists);
            case BoardElementType.RedmineNewest:
                return new BoardElementSettings(Rows: Rows(1, 20, 8), ProjectId: s.ProjectId, Subjects: s.Subjects,
                    NewMinutes: Range(s.NewMinutes, 0, 240, 30, $"{path}.newMinutes", v));
            case BoardElementType.RedmineReceived or BoardElementType.RedmineSolved:
                var yellow = Threshold(s.Yellow, "yellow");
                var red = Threshold(s.Red, "red");
                v.Check(yellow is null || red is null || red >= yellow, $"{path}.red", "Red from at least yellow's number.");
                return new BoardElementSettings(ProjectId: s.ProjectId, Yesterday: s.Yesterday, Yellow: yellow, Red: red);
            case BoardElementType.ZabbixCount:
                return new BoardElementSettings(Severity: Severity(s.Severity, ZabbixSeverity.Warning), Groups: Groups(s.Groups, $"{path}.groups", v),
                    Suppressed: s.Suppressed);
            case BoardElementType.ZabbixHistory:
                return new BoardElementSettings(Rows: Rows(1, 50, 15), Severity: Severity(s.Severity, ZabbixSeverity.Warning),
                    Groups: Groups(s.Groups, $"{path}.groups", v), Suppressed: s.Suppressed,
                    WindowHours: Range(s.WindowHours, 1, 72, 24, $"{path}.windowHours", v));
            case BoardElementType.Lounge:
                return new BoardElementSettings(Rows: Rows(5, 50, 15), Comments: s.Comments);
            case BoardElementType.SensorSummary:
                return new BoardElementSettings(Importance: Known(s.Importance), LocationId: s.LocationId, Ignored: s.Ignored);
            case BoardElementType.FailingSensors:
                return new BoardElementSettings(Rows: Rows(1, 50, 12), Importance: Known(s.Importance), LocationId: s.LocationId, Messages: s.Messages);
            case BoardElementType.StatusTiles:
                var targets = (s.Targets ?? []).Where(t => t is not null && Enum.IsDefined(t.Kind) && t.Id != Guid.Empty).Distinct().ToList();
                v.Check(targets.Count <= MaxTargets, $"{path}.targets", $"At most {MaxTargets} hosts and sensors.");
                return new BoardElementSettings(Targets: targets.Take(MaxTargets).ToList(), PerRow: Range(s.PerRow, 1, 8, 3, $"{path}.perRow", v));
            case BoardElementType.HostBars:
                return new BoardElementSettings(Rows: Rows(1, 200, 20), LocationId: s.LocationId, OnlyProblems: s.OnlyProblems, Tiles: s.Tiles);
            case BoardElementType.RecentChanges:
                // The dashboard keeps the newest 15 changes of the last 24 hours.
                return new BoardElementSettings(Rows: Rows(1, 15, 10), LocationId: s.LocationId);
            case BoardElementType.Clock:
                var zones = (s.Zones ?? []).Where(z => z is not null).Select(z => new BoardZone(z.Zone?.Trim() ?? "", z.Label?.Trim() ?? "")).ToList();
                v.Check(zones.Count <= 3, $"{path}.zones", "At most three more time zones.");
                foreach (var (zone, i) in zones.Select((z, i) => (z, i)))
                {
                    v.Check(IsZone(zone.Zone), $"{path}.zones[{i}].zone", "A time zone such as Europe/London or America/New_York.");
                    v.Check(zone.Label.Length is >= 1 and <= 30, $"{path}.zones[{i}].label", "1 to 30 characters.");
                }
                return new BoardElementSettings(Seconds: s.Seconds, Date: s.Date, Zones: zones.Take(3).ToList());
            case BoardElementType.Note:
                var text = (s.Text ?? "").Replace("\0", "").Trim();
                v.Check(text.Length <= MaxNote, $"{path}.text", $"At most {MaxNote} characters.");
                return new BoardElementSettings(Text: text, Center: s.Center, Large: s.Large);
            case BoardElementType.Certificates:
                return new BoardElementSettings(Rows: Rows(1, 50, 10), Days: Range(s.Days, 1, 90, 30, $"{path}.days", v));
            default:
                return new BoardElementSettings();
        }
    }

    private static ZabbixSeverity Severity(ZabbixSeverity? value, ZabbixSeverity fallback) => value is { } x && Enum.IsDefined(x) ? x : fallback;

    private static Importance? Known(Importance? value) => value is { } x && Enum.IsDefined(x) ? x : null;

    private static bool IsZone(string name)
    {
        try
        {
            return name.Length > 0 && TimeZoneInfo.FindSystemTimeZoneById(name) is not null;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    public static IEnumerable<BoardElement> Elements(BoardLayout layout) => layout.Columns.SelectMany(c => c.Elements);

    // --- rights (§7.2) ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The elements and rules a save adds or changes that need a right the editor lacks: an element that is new or whose type, settings
    /// or title changed, and a rule that wasn't on the board. What someone else added and stays as it was needs nothing.
    /// </summary>
    public static IReadOnlyList<(string What, Area Area, Level Level)> Missing(BoardLayout? before, BoardLayout after, UserRights rights)
    {
        var old = before is null ? new Dictionary<string, BoardElement>() : Elements(before).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        var missing = new List<(string, Area, Level)>();
        foreach (var element in Elements(after))
        {
            if (Of(element.Type).Right is not { } right || rights.Has(right.Area, right.Level))
            {
                continue;
            }
            if (old.TryGetValue(element.Id, out var was) && Same(was, element))
            {
                continue;
            }
            missing.Add((Of(element.Type).Name, right.Area, right.Level));
        }
        var oldRules = (before?.Bar.Rules ?? []).Select(Key).ToList();
        foreach (var rule in after.Bar.Rules)
        {
            var key = Key(rule);
            if (oldRules.Remove(key))
            {
                continue;
            }
            var right = Of(rule.Metric).Right;
            if (!rights.Has(right.Area, right.Level))
            {
                missing.Add(($"the rule \"{Of(rule.Metric).Name}\"", right.Area, right.Level));
            }
        }
        return missing;
    }

    private static bool Same(BoardElement a, BoardElement b) => a.Type == b.Type && Key(a.Settings) == Key(b.Settings);

    private static string Key<T>(T value) => JsonSerializer.Serialize(value, Json);

    // --- the summary of a change (§13.2) -------------------------------------------------------------------------------------------

    /// <summary>"Column 2: added Newest tickets; status bar: 1 rule changed; theme: dark → terminal", or null when nothing changed.</summary>
    public static string? Diff(BoardLayout before, BoardLayout after)
    {
        var parts = new List<string>();
        if (before.Columns.Count != after.Columns.Count)
        {
            parts.Add($"{before.Columns.Count} → {after.Columns.Count} columns");
        }
        var old = before.Columns.SelectMany((c, i) => c.Elements.Select(e => (Column: i, Element: e))).ToDictionary(x => x.Element.Id);
        var now = after.Columns.SelectMany((c, i) => c.Elements.Select(e => (Column: i, Element: e))).ToDictionary(x => x.Element.Id);
        string Name(BoardElement e) => e.Title ?? Of(e.Type).Name;
        foreach (var (id, (column, element)) in now)
        {
            if (!old.TryGetValue(id, out var was))
            {
                parts.Add($"column {column + 1}: added {Name(element)}");
            }
            else if (Key(was.Element) != Key(element))
            {
                parts.Add($"{Name(element)} changed");
            }
            else if (was.Column != column)
            {
                parts.Add($"{Name(element)} moved to column {column + 1}");
            }
        }
        foreach (var (id, (column, element)) in old.Where(o => !now.ContainsKey(o.Key)))
        {
            parts.Add($"column {column + 1}: removed {Name(element)}");
        }
        var order = before.Columns.Select(c => string.Join(",", c.Elements.Select(e => e.Id))).ToList();
        var newOrder = after.Columns.Select(c => string.Join(",", c.Elements.Select(e => e.Id))).ToList();
        if (parts.Count == 0 && !order.SequenceEqual(newOrder))
        {
            parts.Add("elements reordered");
        }
        if (before.Columns.Select(c => c.Width).SequenceEqual(after.Columns.Select(c => c.Width)) is false && before.Columns.Count == after.Columns.Count)
        {
            parts.Add("column widths changed");
        }
        if (Key(before.Bar) != Key(after.Bar))
        {
            var rules = Key(before.Bar.Rules) != Key(after.Bar.Rules) ? $"{after.Bar.Rules.Count} rule(s)" : null;
            parts.Add(after.Bar.Show != before.Bar.Show ? (after.Bar.Show ? "status bar shown" : "status bar hidden")
                : rules is not null ? $"status bar: rules changed, {rules}" : "status bar changed");
        }
        if (before.Look.Theme != after.Look.Theme)
        {
            parts.Add($"theme: {before.Look.Theme} → {after.Look.Theme}");
        }
        if (Key(before.Look with { Theme = "" }) != Key(after.Look with { Theme = "" }) || Key(before.Header) != Key(after.Header)
            || before.RefreshSeconds != after.RefreshSeconds)
        {
            parts.Add("look changed");
        }
        return parts.Count == 0 ? null : Cut(string.Join("; ", parts), 900);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>A layout's contents in one line for the audit log of a new or deleted board: "3 columns, 7 elements, 5 rules, theme dark".</summary>
    public static string Describe(BoardLayout layout)
    {
        var builder = new StringBuilder();
        builder.Append(layout.Columns.Count == 1 ? "1 column" : $"{layout.Columns.Count} columns");
        var count = Elements(layout).Count();
        builder.Append(count == 1 ? ", 1 element" : $", {count} elements");
        builder.Append(layout.Bar.Show ? $", {layout.Bar.Rules.Count} rule(s)" : ", no status bar");
        builder.Append($", theme {layout.Look.Theme}");
        return builder.ToString();
    }
}

/// <summary>The themes' ids (§9.2), in the picker's order; <c>src/boards/themes/registry.ts</c> holds the same, and a test compares them.</summary>
public static class BoardThemes
{
    public const string Default = "default";

    public static readonly IReadOnlyList<string> Ids =
    [
        "default", "dark", "high-contrast", "high-contrast-dark", "slay", "switzerland", "terminal", "brasil", "atomic-age", "wtf", "suse", "win95",
        "winxp", "ubuntu", "amiga",
    ];
}
