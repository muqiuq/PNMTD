using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Availability;
using PNMTD.Cookie.Lounge;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using PNMTD.Services.Domain;
using PNMTD.Zabbix;

namespace PNMTD.Boards;

/// <summary>
/// A board's data (docs/boards-plan.md §7, §8): each element's, shaped for the board, and the status bar's verdict, from the same reads.
/// What several elements need is read once per build: the monitoring snapshot, the dashboard, a Redmine project's overview, Zabbix's
/// snapshot. Redmine and Zabbix come from their workers' snapshots: a board asks neither of them anything. In the admin's preview,
/// <c>rights</c> withholds what the editor may not see (§10.4); the board itself shows everything its layout names (§7.2).
/// </summary>
public sealed class BoardData(PnmtdDbContext db, MonitoringReader reader, DashboardCache dashboards, LocationTree tree, IServiceProvider services,
    IOptionsMonitor<RedmineOptions> redmineOptions, ZabbixSnapshot zabbix, IOptionsMonitor<ZabbixOptions> zabbixOptions, BoardRuntime runtime,
    ILogger<BoardData> logger)
{
    /// <summary>A view of a layout; <paramref name="board"/> keeps the bar's "since" (none in a preview).</summary>
    public async Task<BoardView> BuildAsync(string name, BoardLayout layout, UserRights? rights, Guid? board, CancellationToken ct)
    {
        var read = new Reads(this, layout.Look.People);
        var elements = new List<BoardElementData>();
        foreach (var element in BoardLayouts.Elements(layout))
        {
            if (rights is not null && BoardLayouts.Of(element.Type).Right is { } right && !rights.Has(right.Area, right.Level))
            {
                elements.Add(new BoardElementData(element.Id, element.Type, BoardDataState.Withheld,
                    $"Needs {RightsCatalog.Of(right.Area).Label}: {right.Level} to preview. The board itself shows it."));
                continue;
            }
            elements.Add(await SafeAsync(element, () => ElementAsync(element, read, ct)));
        }
        var bar = await BarAsync(layout.Bar, read, rights, board, ct);
        return new BoardView(name, layout, bar, elements, Clock.UtcNow, Clock.TimeZone.Id, ServerInfo.Version);
    }

    /// <summary>The lounge element's lines, for its own faster poll (§7.6).</summary>
    public Task<BoardElementData> LoungeAsync(BoardElement element, BoardPeople people, CancellationToken ct) =>
        SafeAsync(element, () => Lounge(element, new Reads(this, people), ct));

    private async Task<BoardElementData> SafeAsync(BoardElement element, Func<Task<BoardElementData>> data)
    {
        try
        {
            return await data();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "A board's {Type} element could not be read", element.Type);
            return new BoardElementData(element.Id, element.Type, BoardDataState.Unavailable, "This element couldn't be read; PNMT's log says why.");
        }
    }

    private Task<BoardElementData> ElementAsync(BoardElement e, Reads read, CancellationToken ct) => e.Type switch
    {
        BoardElementType.SensorSummary => SensorSummary(e, read, ct),
        BoardElementType.FailingSensors => FailingSensors(e, read, ct),
        BoardElementType.StatusTiles => StatusTiles(e, read, ct),
        BoardElementType.HostBars => HostBars(e, read, ct),
        BoardElementType.SensorMessage => SensorMessage(e, read, ct),
        BoardElementType.RecentChanges => RecentChanges(e, read, ct),
        BoardElementType.Uplink => Uplink(e, read),
        BoardElementType.RedmineOverview => RedmineOverview(e, read, ct),
        BoardElementType.RedmineNewest => RedmineNewest(e, read, ct),
        BoardElementType.RedmineReceived or BoardElementType.RedmineSolved => RedmineNumber(e, read, ct),
        BoardElementType.ZabbixCount => ZabbixCount(e, ct),
        BoardElementType.ZabbixHistory => ZabbixHistory(e, ct),
        BoardElementType.Lounge => Lounge(e, read, ct),
        BoardElementType.OnDuty => OnDuty(e, read, ct),
        BoardElementType.Certificates => Certificates(e, ct),
        BoardElementType.Image => Image(e, ct),
        BoardElementType.Note => Task.FromResult(new BoardElementData(e.Id, e.Type, BoardDataState.Ok, Note: e.Settings.Text ?? "")),
        _ => Task.FromResult(new BoardElementData(e.Id, e.Type, BoardDataState.Ok)),
    };

    // --- shared reads ------------------------------------------------------------------------------------------------------------

    /// <summary>What a build reads at most once, on first use.</summary>
    private sealed class Reads(BoardData data, BoardPeople people)
    {
        private Task<Monitoring>? monitoring;
        private readonly Dictionary<int, Task<(RedmineOverview? Overview, string? Error)>> redmine = [];
        private Task<Dictionary<long, List<string>>>? zabbixGroups;

        public BoardPeople People => people;

        public Task<Monitoring> MonitoringAsync(CancellationToken ct) => monitoring ??= data.ReadMonitoringAsync(ct);

        public Task<(RedmineOverview? Overview, string? Error)> RedmineAsync(int? project, CancellationToken ct)
        {
            var key = project ?? 0;
            if (!redmine.TryGetValue(key, out var task))
            {
                redmine[key] = task = data.ReadRedmineAsync(project, ct);
            }
            return task;
        }

        public Task<Dictionary<long, List<string>>> ZabbixGroupsAsync(CancellationToken ct) => zabbixGroups ??= data.ReadZabbixGroupsAsync(ct);

        /// <summary>A person as the board names people (§6.2): the name, the initials, or nobody.</summary>
        public string? Person(string? name) => name is null ? null : people switch
        {
            BoardPeople.Hidden => null,
            BoardPeople.Initials => Initials(name),
            _ => name,
        };
    }

    /// <summary>"Anna Muster" → "AM"; a single word → its first two letters.</summary>
    public static string Initials(string name)
    {
        var words = name.Split([' ', '-', '.', '_'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0]))),
        };
    }

    private sealed record HostRow(Guid Id, string Name, bool Enabled, Guid? LocationId);

    private sealed record Monitoring(MonitoringSnapshot Snapshot, IReadOnlyDictionary<Guid, HostRow> Hosts, LocationTreeSnapshot Tree, Dashboard Dashboard)
    {
        /// <summary>The hosts in a location and its sub-locations; null for every host.</summary>
        public IReadOnlySet<Guid>? HostsIn(Guid? location)
        {
            if (location is not { } l)
            {
                return null;
            }
            var within = Tree.SelfAndDescendants(l).ToHashSet();
            return Hosts.Values.Where(h => h.LocationId is { } id && within.Contains(id)).Select(h => h.Id).ToHashSet();
        }

        /// <summary>Sensors (no containers) of at least an importance, in a location.</summary>
        public IEnumerable<SensorStatus> Sensors(Importance? importance, Guid? location)
        {
            var hosts = HostsIn(location);
            return Snapshot.Sensors.Values.Where(s => !s.IsContainer && (importance is not { } min || s.Info.EffectiveImportance >= min)
                && (hosts is null || hosts.Contains(s.Info.HostId)));
        }

        public IEnumerable<SensorStatus> Failing(Importance? importance, Guid? location) =>
            Sensors(importance, location).Where(s => s.IsActive && s.State == SensorState.Failing);
    }

    private async Task<Monitoring> ReadMonitoringAsync(CancellationToken ct)
    {
        var snapshot = await reader.SnapshotAsync(null, ct);
        var hosts = await db.Hosts.AsNoTracking().Select(h => new HostRow(h.Id, h.Name, h.Enabled, h.LocationId)).ToDictionaryAsync(h => h.Id, ct);
        return new Monitoring(snapshot, hosts, await tree.GetAsync(ct), await dashboards.GetAsync());
    }

    private async Task<(RedmineOverview? Overview, string? Error)> ReadRedmineAsync(int? project, CancellationToken ct)
    {
        try
        {
            var service = services.GetRequiredService<RedmineService>();
            return (await service.OverviewAsync(project, ct), null);
        }
        catch (RedmineException e)
        {
            return (null, $"Redmine can't be reached: {e.Message}");
        }
        catch (ApiException e)
        {
            return (null, e.Status == StatusCodes.Status400BadRequest ? "This Redmine project isn't one PNMT reads." : e.Message);
        }
    }

    private async Task<Dictionary<long, List<string>>> ReadZabbixGroupsAsync(CancellationToken ct) =>
        await db.ZabbixHosts.AsNoTracking().ToDictionaryAsync(h => h.HostId, h => h.Groups, ct);

    // --- PNMT's monitoring (§7.7) ------------------------------------------------------------------------------------------------

    private static BoardElementData Ok(BoardElement e) => new(e.Id, e.Type, BoardDataState.Ok);

    private async Task<BoardElementData> SensorSummary(BoardElement e, Reads read, CancellationToken ct)
    {
        var m = await read.MonitoringAsync(ct);
        var c = MonitoringSnapshot.Count(m.Sensors(e.Settings.Importance, e.Settings.LocationId));
        return Ok(e) with { Counts = new BoardSensorCounts(c.Ok, c.Failing, c.NoData, c.Ignored, c.Disabled), AsOf = m.Dashboard.GeneratedAt };
    }

    private async Task<BoardElementData> FailingSensors(BoardElement e, Reads read, CancellationToken ct)
    {
        var m = await read.MonitoringAsync(ct);
        var now = Clock.UtcNow;
        var failing = m.Failing(e.Settings.Importance, e.Settings.LocationId)
            .OrderByDescending(s => s.Info.EffectiveImportance).ThenBy(s => s.FailingSince ?? s.LastEventAt ?? now)
            .ThenBy(s => s.Info.HostName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = e.Settings.Rows ?? 12;
        return Ok(e) with
        {
            Sensors = failing.Take(rows).Select(s => new BoardSensorRow(s.Info.Id, s.Info.HostName, s.Info.Name, s.Info.EffectiveImportance,
                Utc(s.FailingSince ?? s.LastEventAt), e.Settings.Messages ? s.LastMessage : null)).ToList(),
            More = Math.Max(0, failing.Count - rows),
            AsOf = m.Dashboard.GeneratedAt,
        };
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? Clock.AsUtc(v) : null;

    private async Task<BoardElementData> StatusTiles(BoardElement e, Reads read, CancellationToken ct)
    {
        var m = await read.MonitoringAsync(ct);
        var tiles = new List<BoardTile>();
        foreach (var target in e.Settings.Targets ?? [])
        {
            if (target.Kind == BoardTargetKind.Host)
            {
                if (!m.Hosts.TryGetValue(target.Id, out var host))
                {
                    tiles.Add(new BoardTile(target.Kind, target.Id, "Removed host", BoardTileState.Missing, null));
                    continue;
                }
                var sensors = m.Snapshot.OfHost(host.Id).ToList();
                var state = MonitoringSnapshot.HostStateOf(host.Enabled, sensors);
                var since = state == HostState.Failing ? sensors.Where(s => s.IsActive && s.State == SensorState.Failing).Min(s => s.FailingSince) : null;
                tiles.Add(new BoardTile(target.Kind, target.Id, host.Name, state switch
                {
                    HostState.Ok => BoardTileState.Ok,
                    HostState.Failing => BoardTileState.Failing,
                    HostState.NoData => BoardTileState.NoData,
                    _ => BoardTileState.Off,
                }, Utc(since)));
            }
            else if (m.Snapshot.Sensors.TryGetValue(target.Id, out var sensor))
            {
                tiles.Add(new BoardTile(target.Kind, target.Id, $"{sensor.Info.HostName} / {sensor.Info.Name}", sensor.State switch
                {
                    SensorState.Ok => BoardTileState.Ok,
                    SensorState.Failing => BoardTileState.Failing,
                    SensorState.NoData => BoardTileState.NoData,
                    _ => BoardTileState.Off,
                }, sensor.State == SensorState.Failing ? Utc(sensor.FailingSince) : null));
            }
            else
            {
                tiles.Add(new BoardTile(target.Kind, target.Id, "Removed sensor", BoardTileState.Missing, null));
            }
        }
        return Ok(e) with { Tiles = tiles, AsOf = m.Dashboard.GeneratedAt };
    }

    public const int MaxSensorMessage = 2000;

    /// <summary>
    /// One sensor's last message (docs/board-messages-plan.md §9.3), as plain text cut at 2000 characters. An advanced sensor whose data
    /// names people has its messages without the values already (docs/advanced-sensor-plan.md §3).
    /// </summary>
    private async Task<BoardElementData> SensorMessage(BoardElement e, Reads read, CancellationToken ct)
    {
        if (e.Settings.Targets is not [{ } target, ..])
        {
            return new BoardElementData(e.Id, e.Type, BoardDataState.NotConfigured, "No sensor chosen: pick one in the element's settings.");
        }
        var m = await read.MonitoringAsync(ct);
        if (!m.Snapshot.Sensors.TryGetValue(target.Id, out var sensor) || sensor.IsContainer)
        {
            return Ok(e) with { SensorMessage = new BoardSensorMessage(target.Id, null, null, BoardTileState.Missing, null, null), AsOf = m.Dashboard.GeneratedAt };
        }
        var state = !sensor.IsActive ? BoardTileState.Off : sensor.State switch
        {
            SensorState.Ok => BoardTileState.Ok,
            SensorState.Failing => BoardTileState.Failing,
            SensorState.NoData => BoardTileState.NoData,
            _ => BoardTileState.Off,
        };
        var message = string.IsNullOrEmpty(sensor.LastMessage) ? null
            : sensor.LastMessage.Length <= MaxSensorMessage ? sensor.LastMessage : sensor.LastMessage[..(MaxSensorMessage - 1)] + "…";
        return Ok(e) with
        {
            SensorMessage = new BoardSensorMessage(sensor.Info.Id, sensor.Info.HostName, sensor.Info.Name, state, message, Utc(sensor.LastEventAt)),
            AsOf = m.Dashboard.GeneratedAt,
        };
    }

    private async Task<BoardElementData> HostBars(BoardElement e, Reads read, CancellationToken ct)
    {
        var m = await read.MonitoringAsync(ct);
        var within = e.Settings.LocationId is { } location ? m.Tree.SelfAndDescendants(location).ToHashSet() : null;
        var hosts = m.Dashboard.Hosts.Where(h => (within is null || (h.AssignedLocation?.Id is { } id && within.Contains(id)))
            && (!e.Settings.OnlyProblems || h.State == HostState.Failing)).ToList();
        var rows = e.Settings.Rows ?? 20;
        return Ok(e) with
        {
            Hosts = hosts.Take(rows).Select(h => new BoardHostRow(h.Id, h.Name, h.State, h.FailingCount, h.Uptime30Days, h.Days)).ToList(),
            More = Math.Max(0, hosts.Count - rows),
            AsOf = m.Dashboard.GeneratedAt,
        };
    }

    private async Task<BoardElementData> RecentChanges(BoardElement e, Reads read, CancellationToken ct)
    {
        var m = await read.MonitoringAsync(ct);
        var hosts = m.HostsIn(e.Settings.LocationId);
        var changes = m.Dashboard.RecentChanges.Where(c => hosts is null || hosts.Contains(c.HostId)).ToList();
        var rows = e.Settings.Rows ?? 10;
        return Ok(e) with
        {
            Changes = changes.Take(rows).Select(c => new BoardChangeRow(Clock.AsUtc(c.At), c.HostName, c.SensorName, c.Success, null)).ToList(),
            More = Math.Max(0, changes.Count - rows),
            AsOf = m.Dashboard.GeneratedAt,
        };
    }

    private async Task<BoardElementData> Uplink(BoardElement e, Reads read)
    {
        var m = await read.MonitoringAsync(CancellationToken.None);
        var u = m.Dashboard.Summary.Uplink;
        return Ok(e) with
        {
            Uplink = new BoardUplink(u.Configured, u.IsUp, u.IsUp ? null : Utc(u.LastSuccessfulRun), Utc(u.LastRun)),
            AsOf = m.Dashboard.GeneratedAt,
        };
    }

    // --- Redmine (§7.4) ----------------------------------------------------------------------------------------------------------

    /// <summary>The overview for an element, with its state: not set up, unreachable without data, or data that may be old.</summary>
    private async Task<(RedmineOverview? Overview, BoardElementData Data)> RedmineFor(BoardElement e, Reads read, CancellationToken ct)
    {
        if (!redmineOptions.CurrentValue.Enabled)
        {
            return (null, new BoardElementData(e.Id, e.Type, BoardDataState.NotConfigured, "Redmine isn't set up in PNMT."));
        }
        var (overview, error) = await read.RedmineAsync(e.Settings.ProjectId, ct);
        if (overview is null)
        {
            return (null, new BoardElementData(e.Id, e.Type, BoardDataState.Unavailable, error));
        }
        var f = overview.Freshness;
        var failing = f.Error is not null && f.FailedAt is { } failed && (f.RefreshedAt is not { } refreshed || failed > refreshed);
        return (overview, new BoardElementData(e.Id, e.Type, failing ? BoardDataState.Unavailable : BoardDataState.Ok,
            failing ? $"Redmine can't be reached since {Clock.ToLocal(f.FailedAt!.Value):HH:mm}." : null, AsOf: f.RefreshedAt));
    }

    private static BoardTicket Ticket(RedmineIssueItem i, BoardElementSettings s, Reads read, DateTime now) => new(i.Id, i.Tracker,
        i.IsPrivate || !s.Subjects ? null : i.Subject, i.ProjectName, i.Status, i.Priority, i.IsPrivate ? null : read.Person(i.AssignedTo), i.CreatedOn,
        Clock.AsUtc(i.UpdatedOn), s.NewMinutes is > 0 && i.CreatedOn is { } created && now - created <= TimeSpan.FromMinutes(s.NewMinutes.Value), i.IsPrivate);

    private async Task<BoardElementData> RedmineOverview(BoardElement e, Reads read, CancellationToken ct)
    {
        var (o, data) = await RedmineFor(e, read, ct);
        if (o is null)
        {
            return data;
        }
        var s = e.Settings;
        var t = o.Tiles;
        var tiles = (s.RedmineTiles ?? []).Select(tile => tile switch
        {
            BoardRedmineTile.Open => new BoardRedmineTileValue(tile, "Open", t.Open, null),
            BoardRedmineTile.Urgent => new BoardRedmineTileValue(tile, "Urgent", t.Urgent, t.Urgent > 0 ? "fail" : null),
            BoardRedmineTile.Unassigned => new BoardRedmineTileValue(tile, "New, no one assigned", t.Unassigned, t.Unassigned > 0 ? "warn" : null),
            BoardRedmineTile.Overdue => new BoardRedmineTileValue(tile, "Overdue", t.Overdue, t.Overdue > 0 ? "warn" : null),
            BoardRedmineTile.Stale => new BoardRedmineTileValue(tile, $"No update for {o.StaleDays} days", t.Stale, null),
            BoardRedmineTile.ReceivedToday => new BoardRedmineTileValue(tile, "Received today", t.ReceivedToday, null),
            _ => new BoardRedmineTileValue(tile, "Solved today", t.SolvedToday, null),
        }).ToList();
        var now = Clock.UtcNow;
        var rows = s.Rows ?? 5;
        var lists = (s.RedmineLists ?? []).Select(list =>
        {
            var (title, items) = list switch
            {
                BoardRedmineList.Urgent => ("Urgent", o.Urgent),
                BoardRedmineList.Unassigned => ("New, no one assigned", o.Unassigned),
                BoardRedmineList.Overdue => ("Overdue", o.Overdue),
                _ => ("Linked to a problem in PNMT", o.Problems),
            };
            return new BoardRedmineListValue(list, title, items.Take(rows).Select(i => Ticket(i, s, read, now)).ToList());
        }).ToList();
        return data with { Redmine = new BoardRedmine(tiles, lists) };
    }

    private async Task<BoardElementData> RedmineNewest(BoardElement e, Reads read, CancellationToken ct)
    {
        var (o, data) = await RedmineFor(e, read, ct);
        if (o is null)
        {
            return data;
        }
        var now = Clock.UtcNow;
        var rows = e.Settings.Rows ?? 8;
        return data with { Tickets = o.Newest.Take(rows).Select(i => Ticket(i, e.Settings, read, now)).ToList() };
    }

    private async Task<BoardElementData> RedmineNumber(BoardElement e, Reads read, CancellationToken ct)
    {
        var (o, data) = await RedmineFor(e, read, ct);
        if (o is null)
        {
            return data;
        }
        var received = e.Type == BoardElementType.RedmineReceived;
        var value = received ? o.Tiles.ReceivedToday : o.Tiles.SolvedToday;
        var yesterday = received ? o.Tiles.ReceivedYesterday : o.Tiles.SolvedYesterday;
        return data with { Number = new BoardNumber(value, e.Settings.Yesterday ? yesterday : null), Colour = Threshold(value, e.Settings) };
    }

    /// <summary>A number's colour from its element's thresholds: red from <c>Red</c>, yellow from <c>Yellow</c>; none without them.</summary>
    private static BoardColour? Threshold(int value, BoardElementSettings s) =>
        s.Red is { } red && value >= red ? BoardColour.Red
        : s.Yellow is { } yellow && value >= yellow ? BoardColour.Yellow
        : s.Red is not null || s.Yellow is not null ? BoardColour.Green
        : null;

    // --- Zabbix (§7.5) -----------------------------------------------------------------------------------------------------------

    private async Task<(ZabbixSnapshot.State? State, BoardElementData? NotOk)> ZabbixFor(BoardElement e, CancellationToken ct)
    {
        if (!zabbixOptions.CurrentValue.Enabled)
        {
            return (null, new BoardElementData(e.Id, e.Type, BoardDataState.NotConfigured, "Zabbix isn't set up in PNMT."));
        }
        await services.GetRequiredService<ZabbixPoller>().EnsureAsync(ct);
        var state = zabbix.Current;
        if (state.AsOf is null)
        {
            return (null, new BoardElementData(e.Id, e.Type, BoardDataState.Unavailable, state.Error ?? "Zabbix hasn't been read yet."));
        }
        return (state, null);
    }

    private bool ZabbixStale(ZabbixSnapshot.State state) => state.AsOf is not { } at || Clock.UtcNow - at > 3 * zabbixOptions.CurrentValue.Poll;

    private static bool InGroups(IEnumerable<long> hostIds, IReadOnlyList<string>? groups, Dictionary<long, List<string>> hostGroups) =>
        groups is not { Count: > 0 } || hostIds.Any(h => hostGroups.GetValueOrDefault(h)?.Any(g => groups.Contains(g, StringComparer.OrdinalIgnoreCase)) == true);

    private async Task<IReadOnlyList<ZabbixProblem>> ZabbixProblems(ZabbixSnapshot.State state, ZabbixSeverity min, IReadOnlyList<string>? groups,
        bool suppressed, Reads read, CancellationToken ct)
    {
        var problems = state.Problems.Where(p => p.Severity >= min && (suppressed || !p.Suppressed)).ToList();
        if (groups is { Count: > 0 })
        {
            var hostGroups = await read.ZabbixGroupsAsync(ct);
            problems = problems.Where(p => InGroups(p.Hosts.Select(h => ZabbixPoller.Int(h.Hostid)), groups, hostGroups)).ToList();
        }
        return problems;
    }

    private async Task<BoardElementData> ZabbixCount(BoardElement e, CancellationToken ct)
    {
        var (state, notOk) = await ZabbixFor(e, ct);
        if (state is null)
        {
            return notOk!;
        }
        var min = e.Settings.Severity ?? ZabbixSeverity.Warning;
        var problems = await ZabbixProblems(state, min, e.Settings.Groups, e.Settings.Suppressed, new Reads(this, BoardPeople.Names), ct);
        var severities = Enum.GetValues<ZabbixSeverity>().Where(s => s >= min).OrderByDescending(s => s)
            .Select(s => new BoardSeverityCount(s, problems.Count(p => p.Severity == s))).ToList();
        var stale = ZabbixStale(state);
        return new BoardElementData(e.Id, e.Type, stale ? BoardDataState.Unavailable : BoardDataState.Ok,
            stale ? state.Error ?? "Zabbix's problems are old: its poll hasn't answered lately." : null, state.AsOf,
            Zabbix: new BoardZabbix(problems.Count, severities, problems.Count == 0 ? null : problems.Max(p => p.Severity)));
    }

    private async Task<BoardElementData> ZabbixHistory(BoardElement e, CancellationToken ct)
    {
        var (state, notOk) = await ZabbixFor(e, ct);
        if (state is null)
        {
            return notOk!;
        }
        var s = e.Settings;
        var min = s.Severity ?? ZabbixSeverity.Warning;
        var since = Clock.UtcNow.AddHours(-(s.WindowHours ?? 24));
        var rows = await db.ZabbixProblemHistory.AsNoTracking()
            .Where(r => r.Severity >= min && (s.Suppressed || !r.Suppressed) && (r.ResolvedAt == null || r.ResolvedAt >= since))
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.EventId).Take(1000).ToListAsync(ct);
        if (s.Groups is { Count: > 0 })
        {
            var hostGroups = await ReadZabbixGroupsAsync(ct);
            rows = rows.Where(r => InGroups(r.HostIds, s.Groups, hostGroups)).ToList();
        }
        var take = s.Rows ?? 15;
        var stale = ZabbixStale(state);
        return new BoardElementData(e.Id, e.Type, stale ? BoardDataState.Unavailable : BoardDataState.Ok,
            stale ? state.Error ?? "Zabbix's problems are old: its poll hasn't answered lately." : null, state.AsOf,
            Problems: rows.Take(take).Select(r => new BoardProblem(r.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Severity,
                string.Join(", ", r.HostNames), r.Name, Clock.AsUtc(r.StartedAt), Utc(r.ResolvedAt), Utc(r.ResolvedFrom))).ToList(),
            More: Math.Max(0, rows.Count - take));
    }

    // --- the lounge (§7.6) -------------------------------------------------------------------------------------------------------

    private async Task<BoardElementData> Lounge(BoardElement e, Reads read, CancellationToken ct)
    {
        var lounge = services.GetRequiredService<LoungeService>();
        var room = await lounge.RoomAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, ct);
        var lines = room.Messages.Where(m => !m.Refused && !m.Removed && (e.Settings.Comments || m.Kind != "comment"))
            .TakeLast(e.Settings.Rows ?? 15)
            .Select(m =>
            {
                var person = m.From == "person";
                var name = person ? read.Person(m.Author) : m.Author;
                return new BoardLoungeLine(m.Id, Clock.AsUtc(m.At), m.From, m.Kind, name, person && read.People == BoardPeople.Hidden ? null : m.AuthorColor,
                    person ? read.People switch { BoardPeople.Names => m.AuthorCode, BoardPeople.Initials => name, _ => null } : m.AuthorCode, m.Text, m.OnId,
                    m.Important);
            }).ToList();
        return Ok(e) with { Lounge = new BoardLounge(lines, room.State.Speaking, room.CookieName) };
    }

    // --- general (§7.1) ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Who is on duty today (docs/board-messages-plan.md §9.5): the chain's first step, then up to the element's number of deputies from
    /// the next steps; where each works today, the day's comment, and the next open day's first step, each where the element shows it.
    /// </summary>
    private async Task<BoardElementData> OnDuty(BoardElement e, Reads read, CancellationToken ct)
    {
        var s = e.Settings;
        var plan = services.GetRequiredService<AvailabilityPlan>();
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var snapshot = await plan.ReadAsync(today, today.AddDays(s.Tomorrow ? 7 : 0), ct);
        var chain = snapshot.ChainOn(today);
        var duty = chain.Count > 0 ? chain[0] : Responsibility.Nobody;
        var deputies = chain.Skip(1).SelectMany(step => step.UserIds.Select(id => (Id: id, step.As))).Take(s.Deputies ?? 0).ToList();
        (DateOnly Day, Responsibility Who)? next = null;
        for (var day = today.AddDays(1); s.Tomorrow && read.People != BoardPeople.Hidden && day <= today.AddDays(7); day = day.AddDays(1))
        {
            if (!snapshot.Closed(day))
            {
                next = (day, snapshot.ChainOn(day) is { Count: > 0 } c ? c[0] : Responsibility.Nobody);
                break;
            }
        }
        var ids = duty.UserIds.Concat(deputies.Select(d => d.Id)).Concat(next?.Who.UserIds ?? []).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string Name(Guid id) => names.GetValueOrDefault(id) ?? "?";
        string? Where(Guid id)
        {
            if (!s.Where)
            {
                return null;
            }
            var type = snapshot.Cell(id, today) is { } cell ? snapshot.Type(cell.TypeId) : snapshot.DefaultType;
            return type is null ? null : type.Has(AvailabilityFlag.WorkTravel) ? "travelling" : type.Has(AvailabilityFlag.HomeOffice) ? "home office"
                : type.Has(AvailabilityFlag.OnSite) ? "on site" : null;
        }
        var people = duty.UserIds.OrderBy(Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(id => new BoardDutyPerson(read.Person(Name(id)), false, duty.As, Where(id)))
            .Concat(deputies.Select(d => new BoardDutyPerson(read.Person(Name(d.Id)), true, d.As, Where(d.Id))))
            .ToList();
        var comment = s.DayComment && snapshot.Row(today)?.Comment is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        var upcoming = next is { } n
            ? new BoardDutyNext(n.Day, n.Who.UserIds.Select(Name).Order(StringComparer.CurrentCultureIgnoreCase).Select(x => read.Person(x)!).ToList())
            : null;
        return Ok(e) with { OnDuty = new BoardOnDuty(people, duty.As, snapshot.Closed(today), comment, upcoming) };
    }

    /// <summary>An image element's file (docs/board-messages-plan.md §9.4): what it is, not its bytes.</summary>
    private async Task<BoardElementData> Image(BoardElement e, CancellationToken ct)
    {
        if (e.Settings.FileId is not { } id)
        {
            return new BoardElementData(e.Id, e.Type, BoardDataState.NotConfigured, "No image chosen: upload one in the element's settings.");
        }
        var file = await db.Files.AsNoTracking().Where(f => f.Id == id && f.Kind == FileKind.Image)
            .Select(f => new BoardImage(f.Id, f.ContentType, f.Width, f.Height, f.Size)).SingleOrDefaultAsync(ct);
        return file is null ? new BoardElementData(e.Id, e.Type, BoardDataState.Unavailable, "This image was removed.") : Ok(e) with { Image = file };
    }

    private async Task<BoardElementData> Certificates(BoardElement e, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var until = now.AddDays(e.Settings.Days ?? 30);
        var rows = e.Settings.Rows ?? 10;
        var found = await (from c in db.CertificateSensors.AsNoTracking()
                           join v in db.CertificateVersions.AsNoTracking() on c.CurrentVersionId equals v.Id
                           where c.Sensor.Enabled && c.Sensor.Parent.Enabled && v.NotAfter <= until
                           orderby v.NotAfter
                           select new { Host = c.Sensor.Parent.Name, Sensor = c.Sensor.Name, v.SubjectCommonName, v.NotAfter }).Take(rows + 100).ToListAsync(ct);
        return Ok(e) with
        {
            Certificates = found.Take(rows).Select(c => new BoardCertificate(c.Host, c.Sensor, c.SubjectCommonName, Clock.AsUtc(c.NotAfter),
                (int)Math.Floor((Clock.AsUtc(c.NotAfter) - now).TotalDays))).ToList(),
            More = Math.Max(0, found.Count - rows),
        };
    }

    // --- the status bar (§8) -----------------------------------------------------------------------------------------------------

    /// <summary>A rule's count, or why it can't be counted.</summary>
    private sealed record Measure(int? Count, string? Unknown);

    private async Task<Measure> MeasureAsync(BoardRule rule, Reads read, CancellationToken ct)
    {
        var f = rule.Filter ?? new BoardRuleFilter();
        switch (rule.Metric)
        {
            case BoardMetric.SensorsFailing:
                return new Measure((await read.MonitoringAsync(ct)).Failing(f.Importance, f.LocationId).Count(), null);
            case BoardMetric.HostsFailing:
                return new Measure((await read.MonitoringAsync(ct)).Failing(f.Importance, f.LocationId).Select(s => s.Info.HostId).Distinct().Count(), null);
            case BoardMetric.UplinkDown:
                var uplink = (await read.MonitoringAsync(ct)).Dashboard.Summary.Uplink;
                return new Measure(uplink.Configured && !uplink.IsUp ? 1 : 0, null);
            case BoardMetric.LateTasks:
                return new Measure((await read.MonitoringAsync(ct)).Dashboard.Summary.Tasks.Late, null);
            case BoardMetric.DnsMismatches:
                return new Measure((await read.MonitoringAsync(ct)).Dashboard.Summary.Dns.Mismatch, null);
            case BoardMetric.ZabbixProblems:
                if (!zabbixOptions.CurrentValue.Enabled)
                {
                    return new Measure(null, "Zabbix isn't set up");
                }
                await services.GetRequiredService<ZabbixPoller>().EnsureAsync(ct);
                var state = zabbix.Current;
                if (state.AsOf is null || ZabbixStale(state))
                {
                    return new Measure(null, "Zabbix can't be read");
                }
                return new Measure((await ZabbixProblems(state, f.Severity ?? ZabbixSeverity.Information, f.Groups, f.Suppressed, read, ct)).Count, null);
            case BoardMetric.CertificatesExpiring:
                var until = Clock.UtcNow.AddDays(f.Days ?? 30);
                return new Measure(await (from c in db.CertificateSensors join v in db.CertificateVersions on c.CurrentVersionId equals v.Id
                                          where c.Sensor.Enabled && c.Sensor.Parent.Enabled && v.NotAfter <= until select c.SensorId).CountAsync(ct), null);
            default:
                if (!redmineOptions.CurrentValue.Enabled)
                {
                    return new Measure(null, "Redmine isn't set up");
                }
                var (o, _) = await read.RedmineAsync(f.ProjectId, ct);
                if (o is null || (o.Freshness.Error is not null && o.Freshness.FailedAt is { } failed && (o.Freshness.RefreshedAt is not { } at || failed > at)))
                {
                    return new Measure(null, "Redmine can't be read");
                }
                return new Measure(rule.Metric switch
                {
                    BoardMetric.RedmineOpen => o.Tiles.Open,
                    BoardMetric.RedmineUrgent => o.Tiles.Urgent,
                    BoardMetric.RedmineUnassigned => o.Tiles.Unassigned,
                    BoardMetric.RedmineOverdue => o.Tiles.Overdue,
                    BoardMetric.RedmineLinkedToProblem => o.Problems.Count,
                    _ => o.Tiles.ReceivedToday,
                }, null);
        }
    }

    /// <summary>
    /// The bar's verdict (§8.1): red over yellow over grey over green. The text names what matched with its number, and what couldn't
    /// be read; green says the board's green text. Grey only when nothing matched and a source couldn't be read.
    /// </summary>
    private async Task<BoardBarView> BarAsync(BoardBar bar, Reads read, UserRights? rights, Guid? board, CancellationToken ct)
    {
        var parts = new List<BoardBarPart>();
        var unknown = new List<string>();
        foreach (var rule in bar.Show ? bar.Rules : [])
        {
            var info = BoardLayouts.Of(rule.Metric);
            if (rights is not null && !rights.Has(info.Right.Area, info.Right.Level))
            {
                continue;
            }
            Measure measure;
            try
            {
                measure = await MeasureAsync(rule, read, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "A board's rule {Metric} could not be counted", rule.Metric);
                measure = new Measure(null, $"{info.Name} can't be counted");
            }
            if (measure.Unknown is { } why)
            {
                if (!unknown.Contains(why))
                {
                    unknown.Add(why);
                }
            }
            else if (measure.Count >= rule.AtLeast)
            {
                parts.Add(new BoardBarPart(rule.Colour, rule.Label is { } label ? $"{measure.Count} {label}" : $"{info.Name}: {measure.Count}"));
            }
        }
        var colour = parts.Any(p => p.Colour == BoardColour.Red) ? BoardColour.Red
            : parts.Any(p => p.Colour == BoardColour.Yellow) ? BoardColour.Yellow
            : unknown.Count > 0 ? BoardColour.Grey
            : BoardColour.Green;
        // The worst first; a measure matched by a red and a yellow rule shows once, red.
        var shown = parts.OrderByDescending(p => p.Colour == BoardColour.Red).DistinctBy(p => p.Text).ToList();
        shown.AddRange(unknown.Select(u => new BoardBarPart(BoardColour.Grey, u)));
        var text = colour switch
        {
            BoardColour.Green => bar.GreenText ?? "All clear",
            BoardColour.Grey => $"Status unknown: {string.Join(" · ", unknown)}",
            _ => string.Join(" · ", shown.Select(p => p.Text)),
        };
        return new BoardBarView(colour, text, board is { } id ? runtime.Since(id, colour) : null, shown);
    }
}
