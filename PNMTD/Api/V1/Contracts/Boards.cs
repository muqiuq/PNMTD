using System.Text.Json.Serialization;
using PNMTD.Lib.Models.Enum;
using PNMTD.Zabbix;

namespace PNMTD.Api.V1.Contracts;

// Boards (docs/boards-plan.md §6, §7, §8, §12): the layout, as the editor saves it and the board app draws it, and a board's data, as
// the board app and the admin preview get it. Every element's data is shaped for the board on the server (§11.4): no admin contract
// is passed on as it is.

// --- the layout (§6.2) -------------------------------------------------------------------------------------------------------------

/// <summary>An element's type (§7.1), in the catalog's groups: Redmine, Zabbix, the lounge, PNMT's monitoring, general.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BoardElementType>))]
public enum BoardElementType
{
    RedmineOverview, RedmineNewest, RedmineReceived, RedmineSolved,
    ZabbixCount, ZabbixHistory,
    Lounge,
    SensorSummary, FailingSensors, StatusTiles, HostBars, RecentChanges, Uplink,
    Clock, Note, OnDuty, Certificates,
}

/// <summary>What a status bar's rule counts (§8.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BoardMetric>))]
public enum BoardMetric
{
    SensorsFailing, HostsFailing, UplinkDown, LateTasks, DnsMismatches,
    ZabbixProblems,
    RedmineOpen, RedmineUrgent, RedmineUnassigned, RedmineOverdue, RedmineLinkedToProblem, RedmineReceivedToday,
    CertificatesExpiring,
}

/// <summary>The status bar's colours (§8.1): the worst matching rule wins; grey when a source can't be read and nothing worse matched.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BoardColour>))]
public enum BoardColour { Green, Yellow, Red, Grey }

/// <summary>How a board names people (§6.2): by name (the default), by initials, or not at all.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BoardPeople>))]
public enum BoardPeople { Names, Initials, Hidden }

[JsonConverter(typeof(JsonStringEnumConverter<BoardBarPosition>))]
public enum BoardBarPosition { Top, Bottom }

[JsonConverter(typeof(JsonStringEnumConverter<BoardBarSize>))]
public enum BoardBarSize { Small, Medium, Large }

/// <summary>The Redmine overview's tiles (§7.4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BoardRedmineTile>))]
public enum BoardRedmineTile { Open, Urgent, Unassigned, Overdue, Stale, ReceivedToday, SolvedToday }

/// <summary>The Redmine overview's lists (§7.4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BoardRedmineList>))]
public enum BoardRedmineList { Urgent, Unassigned, Overdue, LinkedToProblem }

[JsonConverter(typeof(JsonStringEnumConverter<BoardTargetKind>))]
public enum BoardTargetKind { Host, Sensor }

/// <summary>
/// A board's layout (§6.2), one JSON document: how often it polls, its look, the header line, the status bar and the columns.
/// <see cref="SchemaVersion"/> lets a later change migrate it when read.
/// </summary>
public sealed record BoardLayout(int SchemaVersion, int RefreshSeconds, BoardLook Look, BoardHeader Header, BoardBar Bar, IReadOnlyList<BoardColumn> Columns);

/// <summary>The look (§6.2, §9): the theme, fit to window, the text size (75–250 %), animations, and how people are named.</summary>
public sealed record BoardLook(string Theme, bool Fit, int TextScale, bool Motion, BoardPeople People);

/// <summary>
/// The header line (§6.4): the title (null: the board's name) and the clock. In the status bar when it is shown, else in a slim line of
/// its own; nothing when both are off.
/// </summary>
public sealed record BoardHeader(bool ShowTitle, string? Title, bool Clock, bool Date, bool Seconds);

/// <summary>The status bar (§8): where, how large, its green text, a flash on turning red, and its rules in order.</summary>
public sealed record BoardBar(bool Show, BoardBarPosition Position, BoardBarSize Size, string? GreenText, bool Flash, IReadOnlyList<BoardRule> Rules);

/// <summary>A rule (§8.2): a measure with its filter, at least how many, the colour it gives, and an optional label.</summary>
public sealed record BoardRule(BoardMetric Metric, BoardColour Colour, int AtLeast, string? Label, BoardRuleFilter? Filter = null);

/// <summary>A rule's filter; each measure reads the parts it has (§8.2).</summary>
public sealed record BoardRuleFilter(Importance? Importance = null, Guid? LocationId = null, ZabbixSeverity? Severity = null,
    IReadOnlyList<string>? Groups = null, bool Suppressed = false, int? ProjectId = null, int? Days = null);

/// <summary>A column (§6.2): its width in shares (1–4), and its elements in order.</summary>
public sealed record BoardColumn(int Width, IReadOnlyList<BoardElement> Elements);

/// <summary>
/// An element (§6.2): a stable id (8 characters), its type, a title (null: the type's), whether the title shows, its height (null: its own;
/// 1–6: a share of what is left in fit mode), and the type's settings.
/// </summary>
public sealed record BoardElement(string Id, BoardElementType Type, string? Title, bool ShowTitle, int? Height, BoardElementSettings Settings);

/// <summary>
/// An element's settings (§7.1). One record for every type: each type reads the parts it has, and a save keeps only those.
/// </summary>
public sealed record BoardElementSettings(
    int? Rows = null,
    Importance? Importance = null,
    Guid? LocationId = null,
    bool Messages = false,
    bool Ignored = false,
    bool OnlyProblems = false,
    bool Tiles = false,
    IReadOnlyList<BoardTarget>? Targets = null,
    int? PerRow = null,
    int? ProjectId = null,
    bool Subjects = true,
    IReadOnlyList<BoardRedmineTile>? RedmineTiles = null,
    IReadOnlyList<BoardRedmineList>? RedmineLists = null,
    int? NewMinutes = null,
    bool Yesterday = false,
    int? Yellow = null,
    int? Red = null,
    ZabbixSeverity? Severity = null,
    IReadOnlyList<string>? Groups = null,
    bool Suppressed = false,
    int? WindowHours = null,
    bool Comments = true,
    bool Seconds = false,
    bool Date = true,
    IReadOnlyList<BoardZone>? Zones = null,
    string? Text = null,
    bool Center = false,
    bool Large = false,
    int? Days = null);

/// <summary>A status tile's host or sensor (§7.1).</summary>
public sealed record BoardTarget(BoardTargetKind Kind, Guid Id);

/// <summary>A clock's extra time zone: an IANA name and its label.</summary>
public sealed record BoardZone(string Zone, string Label);

// --- the admin's API (§12.1) -------------------------------------------------------------------------------------------------------

/// <summary>A board in the list (§10.2).</summary>
public sealed record BoardItem(Guid Id, string Name, string? Description, bool Enabled, int Columns, int Elements, string Theme, int Networks,
    bool Anywhere, DateTime? LastSeenAt, int ScreensNow);

/// <summary>A network a board opens from, as entered or normalised; <see cref="Inside"/> names the entry it lies inside.</summary>
public sealed record BoardNetworkItem(string Cidr, string? Label, string? Inside);

/// <summary>Who opened it last, and how many screens show it now (§5.4).</summary>
public sealed record BoardSeen(DateTime? At, string? Address, string? Agent, int ScreensNow);

/// <summary>A board in full (§10.3), without its token: only <c>GET /boards/{id}/url</c> shows the URL.</summary>
public sealed record BoardDetail(Guid Id, string Name, string? Description, bool Enabled, string? TokenHint, IReadOnlyList<BoardNetworkItem> Networks,
    bool Anywhere, BoardLayout Layout, int Revision, BoardSeen Seen, DateTime CreatedAt, string? CreatedByName, DateTime UpdatedAt, string? UpdatedByName);

public sealed record BoardNetworkRequest(string Cidr, string? Label);

/// <summary><c>POST /boards</c> (§10.2): a name, the columns, and what it starts from: empty, the starting layout, or a copy of a board.</summary>
public sealed class BoardCreateRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public int Columns { get; init; } = 3;

    /// <summary><c>empty</c>, <c>start</c> (the default) or <c>copy</c> with <see cref="CopyOf"/>.</summary>
    public string? From { get; init; }

    public Guid? CopyOf { get; init; }
}

/// <summary><c>PUT /boards/{id}</c> (§12.1): the whole page at once, from the revision it was loaded at.</summary>
public sealed class BoardRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<BoardNetworkRequest>? Networks { get; init; }

    public required BoardLayout Layout { get; init; }

    public int Revision { get; init; }
}

/// <summary>A board's URL (§5.1): shown with <i>Boards: Manage</i>, audited.</summary>
public sealed record BoardUrl(string Url);

/// <summary>The address PNMT sees for the admin's browser (§5.2), with a hint when it is a Docker bridge's, and whether Proxy is set.</summary>
public sealed record BoardAddress(string? Address, string? Hint, bool ProxySet);

/// <summary><c>POST /boards/preview</c> (§10.4): a draft's layout, and the board it is for (its name; null for a new one).</summary>
public sealed record BoardPreviewRequest(BoardLayout Layout, string? Name = null);

/// <summary>An element type in the catalog (§10.3): its group, name and line, and whether this editor may add it, with why not.</summary>
public sealed record BoardCatalogType(BoardElementType Type, string Group, string Name, string Line, string Icon, bool Available, string? Reason);

/// <summary>A status bar's measure in the catalog, with whether this editor may use it.</summary>
public sealed record BoardCatalogMetric(BoardMetric Metric, string Name, string Filters, bool Available, string? Reason);

/// <summary>A location for the filters: its path.</summary>
public sealed record BoardLocationOption(Guid Id, string Path);

/// <summary>A Redmine project for the filters, subprojects after their parent.</summary>
public sealed record BoardProjectOption(int Id, string Name, int? ParentId);

/// <summary>A host or a sensor for a status tile: its name, a sensor's as "host / sensor".</summary>
public sealed record BoardTargetOption(BoardTargetKind Kind, Guid Id, string Name);

/// <summary><c>GET /boards/catalog</c> (§12.1): the types and measures, and the lists the editor's pickers need.</summary>
public sealed record BoardCatalog(IReadOnlyList<BoardCatalogType> Types, IReadOnlyList<BoardCatalogMetric> Metrics, IReadOnlyList<string> Themes,
    IReadOnlyList<BoardLocationOption> Locations, IReadOnlyList<BoardProjectOption> RedmineProjects, IReadOnlyList<string> ZabbixGroups,
    string TimeZone);

// --- a board's data (§12.2) --------------------------------------------------------------------------------------------------------

/// <summary>An element's state (§7.3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BoardDataState>))]
public enum BoardDataState { Ok, Unavailable, NotConfigured, Withheld }

/// <summary>
/// <c>GET /pnmt-board/api/view</c> and the admin's preview (§12.2): the layout, the status bar, each element's data, and what the board
/// app needs to keep itself current: the time zone its times are shown in, and the version it reloads for.
/// </summary>
public sealed record BoardView(string Name, BoardLayout Layout, BoardBarView Bar, IReadOnlyList<BoardElementData> Elements, DateTime GeneratedAt,
    string TimeZone, string AppVersion);

/// <summary>The status bar's verdict (§8.1): the colour, its text, since when, and each matched part.</summary>
public sealed record BoardBarView(BoardColour Colour, string Text, DateTime? Since, IReadOnlyList<BoardBarPart> Parts);

public sealed record BoardBarPart(BoardColour Colour, string Text);

/// <summary>
/// An element's data (§7): its state with a message, the age of its source, a colour for a number with thresholds, and the part its
/// type fills. <see cref="More"/> counts what the list left out.
/// </summary>
public sealed record BoardElementData(
    string Id,
    BoardElementType Type,
    BoardDataState State,
    string? Message = null,
    DateTime? AsOf = null,
    BoardColour? Colour = null,
    int More = 0,
    BoardSensorCounts? Counts = null,
    IReadOnlyList<BoardSensorRow>? Sensors = null,
    IReadOnlyList<BoardTile>? Tiles = null,
    IReadOnlyList<BoardHostRow>? Hosts = null,
    IReadOnlyList<BoardChangeRow>? Changes = null,
    BoardUplink? Uplink = null,
    BoardRedmine? Redmine = null,
    IReadOnlyList<BoardTicket>? Tickets = null,
    BoardNumber? Number = null,
    BoardZabbix? Zabbix = null,
    IReadOnlyList<BoardProblem>? Problems = null,
    BoardLounge? Lounge = null,
    string? Note = null,
    BoardOnDuty? OnDuty = null,
    IReadOnlyList<BoardCertificate>? Certificates = null);

public sealed record BoardSensorCounts(int Ok, int Failing, int NoData, int Ignored, int Disabled);

/// <summary>A failing sensor (§7.7): never its token or details beyond the message, and the message only where the element says so.</summary>
public sealed record BoardSensorRow(Guid Id, string Host, string Sensor, Importance Importance, DateTime? Since, string? Message);

[JsonConverter(typeof(JsonStringEnumConverter<BoardTileState>))]
public enum BoardTileState { Ok, Failing, NoData, Off, Missing }

/// <summary>A status tile (§7.1): a host or sensor by name, its state and since when.</summary>
public sealed record BoardTile(BoardTargetKind Kind, Guid Id, string Name, BoardTileState State, DateTime? Since);

public sealed record BoardHostRow(Guid Id, string Name, HostState State, int Failing, double? Uptime30Days, UptimeSeries Days);

public sealed record BoardChangeRow(DateTime At, string Host, string Sensor, bool Success, string? Message);

public sealed record BoardUplink(bool Configured, bool Up, DateTime? Since, DateTime? LastRun);

public sealed record BoardRedmineTileValue(BoardRedmineTile Tile, string Label, int Value, string? Tone);

public sealed record BoardRedmineListValue(BoardRedmineList List, string Title, IReadOnlyList<BoardTicket> Tickets);

public sealed record BoardRedmine(IReadOnlyList<BoardRedmineTileValue> Tiles, IReadOnlyList<BoardRedmineListValue> Lists);

/// <summary>
/// A ticket on a board (§7.4): a private one has no subject or assignee, and subjects can be switched off. <see cref="New"/> marks one
/// younger than the element's minutes.
/// </summary>
public sealed record BoardTicket(int Id, string? Tracker, string? Subject, string Project, string? Status, string? Priority, string? AssignedTo,
    DateTime? CreatedOn, DateTime UpdatedOn, bool New, bool Private);

/// <summary>A count (§7.1): today's, and yesterday's where the element shows it.</summary>
public sealed record BoardNumber(int Value, int? Yesterday);

public sealed record BoardSeverityCount(ZabbixSeverity Severity, int Count);

public sealed record BoardZabbix(int Total, IReadOnlyList<BoardSeverityCount> Severities, ZabbixSeverity? Worst);

/// <summary>
/// A Zabbix problem in the history (§7.5): open, or resolved at <see cref="Resolved"/>; an estimate between <see cref="ResolvedFrom"/> and
/// <see cref="Resolved"/> when its recovery wasn't seen.
/// </summary>
public sealed record BoardProblem(string EventId, ZabbixSeverity Severity, string Host, string Name, DateTime Started, DateTime? Resolved,
    DateTime? ResolvedFrom);

/// <summary>A line of the lounge on a board (§7.6): never a refused or removed one, names as the board names people.</summary>
public sealed record BoardLoungeLine(long Id, DateTime At, string From, string Kind, string? Author, string? AuthorColor, string? AuthorCode,
    string Text, long? OnId, bool Important);

public sealed record BoardLounge(IReadOnlyList<BoardLoungeLine> Lines, string? Typing, string CookieName);

/// <summary>Who is on duty today (§7.1): the availability plan's people of the day, and as what.</summary>
public sealed record BoardOnDuty(IReadOnlyList<string> People, string As, bool Closed);

public sealed record BoardCertificate(string Host, string Sensor, string? Name, DateTime NotAfter, int Days);
