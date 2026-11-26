using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Network;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;

namespace PNMTD.Network;

/// <summary>
/// What people decide on the network map (docs/network-map-plan.md §3, §5.4, §6.2): connections, suggestions and the
/// assignments of interfaces. Every write is audited by its endpoint's event; a change that affects the suggestions asks
/// for an analysis in a few seconds.
/// </summary>
public sealed class NetworkConnectionService(PnmtdDbContext db, LocationTree locations, NetworkAnalysis analysis, IHttpContextAccessor accessor)
{
    public const int MaxNotes = 4000;
    public const int MaxInterface = 200;

    private Task<NetworkDirectory> DirectoryAsync(CancellationToken ct) => NetworkDirectory.LoadAsync(db, locations, ct);

    // --- connections (§8.4) ----------------------------------------------------------------------------------------------

    public async Task<Paged<NetworkConnectionItem>> ListAsync(NetworkConnectionQuery query, CancellationToken ct) =>
        Paging.Page(await FilterAsync(query, ct), query);

    /// <summary>Every connection the query matches, sorted: the API pages it, MCP slices it by its cursor.</summary>
    public async Task<IReadOnlyList<NetworkConnectionItem>> FilterAsync(NetworkConnectionQuery query, CancellationToken ct)
    {
        var d = await DirectoryAsync(ct);
        var (field, descending) = Paging.Sort(query.Sort, "a", "a", "b", "type", "scan", "created", "updated");
        IEnumerable<NetworkConnectionEntity> rows = await db.NetworkConnections.AsNoTracking().ToListAsync(ct);
        if (query.LocationId is { } location)
        {
            rows = rows.Where(c => d.Within(c.A, location) || d.Within(c.B, location));
        }
        if (query.DeviceId is { } device)
        {
            rows = rows.Where(c => d.Is(c.A, device) || d.Is(c.B, device));
        }
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            rows = rows.Where(c => c.Type == query.Type.Trim());
        }
        if (query.Physical is { } physical)
        {
            rows = rows.Where(c => ConnectionTypes.IsPhysical(c.Type) == physical);
        }
        if (query.RoutersOnly == true)
        {
            rows = rows.Where(c => c.ASensorId is not null && c.BSensorId is not null);
        }
        if (query.Unconfirmed == true)
        {
            rows = rows.Where(c => c.UnconfirmedSince is not null);
        }
        if (query.OnVirtualPort == true)
        {
            rows = rows.Where(c => d.VirtualPortOf(c.A) is not null || d.VirtualPortOf(c.B) is not null);
        }
        var items = rows.Select(c => NetworkViews.Connection(c, d));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            items = items.Where(c => Text.Contains(c.A.Name, query.Search) || Text.Contains(c.B.Name, query.Search) || Text.Contains(c.A.Interface, query.Search)
                || Text.Contains(c.B.Interface, query.Search) || Text.Contains(c.Notes, query.Search) || Text.Contains(c.TypeLabel, query.Search));
        }
        items = field switch
        {
            "b" => Order(items, c => $"{c.B.Name} {c.B.Interface}", descending),
            "type" => Order(items, c => c.TypeLabel, descending),
            "scan" => Order(items, c => c.Scan.ToString(), descending),
            "created" => Order(items, c => c.CreatedAt.ToString("O"), descending),
            "updated" => Order(items, c => (c.UpdatedAt ?? c.CreatedAt).ToString("O"), descending),
            _ => Order(items, c => $"{c.A.Name} {c.A.Interface}", descending),
        };
        return items.ToList();
    }

    private static IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, string> key, bool descending) =>
        descending ? items.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : items.OrderBy(key, StringComparer.OrdinalIgnoreCase);

    public async Task<NetworkConnectionItem> GetAsync(Guid id, CancellationToken ct)
    {
        var c = await db.NetworkConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Connection");
        return NetworkViews.Connection(c, await DirectoryAsync(ct));
    }

    public async Task<NetworkConnectionItem> CreateAsync(NetworkConnectionRequest request, CancellationToken ct)
    {
        var d = await DirectoryAsync(ct);
        var v = new ApiValidator();
        var a = End(d, request.A, "a", v);
        var b = End(d, request.B, "b", v);
        v.Check(ConnectionTypes.IsKnown(request.Type?.Trim()), "type", "One of the connection types (GET /api/v1/network-map/connection-types).");
        v.Check((request.Notes?.Length ?? 0) <= MaxNotes, "notes", $"At most {MaxNotes} characters.");
        if (a is not null && b is not null && a.Device == b.Device)
        {
            v.Add("b", "The two ends are the same device.");
        }
        v.ThrowIfInvalid();
        var connection = await AddAsync(d, a!, b!, request.Type.Trim(), Text.Trimmed(request.Notes), NetworkConnectionSource.Manual, null, ct);
        return NetworkViews.Connection(connection, d);
    }

    /// <summary>A new connection in order (§3.1), with its audit target; 409 when the pair of ends exists.</summary>
    private async Task<NetworkConnectionEntity> AddAsync(NetworkDirectory d, ConnectionEnd a, ConnectionEnd b, string type, string? notes,
        NetworkConnectionSource source, string? evidence, CancellationToken ct)
    {
        var (first, second) = ConnectionEnd.Ordered(a, b);
        var key = ConnectionEnd.PairKey(first, second);
        if (await db.NetworkConnections.AnyAsync(c => c.Key == key, ct))
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, "This connection exists already.");
        }
        var who = AuditWho.From(accessor);
        var connection = new NetworkConnectionEntity
        {
            Id = Guid.NewGuid(),
            Key = key,
            AHostId = first.HostId,
            ASensorId = first.SensorId,
            AInterface = first.Interface,
            BHostId = second.HostId,
            BSensorId = second.SensorId,
            BInterface = second.Interface,
            Type = type,
            Notes = notes,
            Source = source,
            Evidence = evidence,
            CreatedAt = Clock.UtcNow,
            CreatedBy = who.UserId,
            CreatedByName = Name(who),
        };
        db.NetworkConnections.Add(connection);
        AuditLog.Target(Target(connection, d));
        await db.SaveChangesAsync(ct);
        analysis.RequestSoon();
        return connection;
    }

    public async Task<NetworkConnectionItem> UpdateAsync(Guid id, NetworkConnectionUpdate request, CancellationToken ct)
    {
        var c = await db.NetworkConnections.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Connection");
        var d = await DirectoryAsync(ct);
        new ApiValidator()
            .Check(request.Type is null || ConnectionTypes.IsKnown(request.Type.Trim()), "type", "One of the connection types.")
            .Check((request.Notes?.Length ?? 0) <= MaxNotes, "notes", $"At most {MaxNotes} characters.")
            .Check((request.AInterface?.Length ?? 0) <= MaxInterface, "aInterface", $"At most {MaxInterface} characters.")
            .Check((request.BInterface?.Length ?? 0) <= MaxInterface, "bInterface", $"At most {MaxInterface} characters.")
            .ThrowIfInvalid();
        var a = c.A with { Interface = request.AInterface is null ? c.AInterface : Text.Trimmed(request.AInterface) };
        var b = c.B with { Interface = request.BInterface is null ? c.BInterface : Text.Trimmed(request.BInterface) };
        var (first, second) = ConnectionEnd.Ordered(a, b);
        var key = ConnectionEnd.PairKey(first, second);
        if (key != c.Key && await db.NetworkConnections.AnyAsync(x => x.Key == key && x.Id != id, ct))
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, "A connection with these ends exists already.");
        }
        c.Key = key;
        (c.AHostId, c.ASensorId, c.AInterface) = (first.HostId, first.SensorId, first.Interface);
        (c.BHostId, c.BSensorId, c.BInterface) = (second.HostId, second.SensorId, second.Interface);
        if (request.Type is { } type)
        {
            c.Type = type.Trim();
        }
        if (request.Notes is not null)
        {
            c.Notes = Text.Trimmed(request.Notes);
        }
        var who = AuditWho.From(accessor);
        c.UpdatedAt = Clock.UtcNow;
        c.UpdatedBy = who.UserId;
        c.UpdatedByName = Name(who);
        AuditLog.Target(Target(c, d));
        await db.SaveChangesAsync(ct);
        analysis.RequestSoon();
        return NetworkViews.Connection(c, d);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var c = await db.NetworkConnections.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Connection");
        var d = await DirectoryAsync(ct);
        AuditLog.Target(Target(c, d));
        AuditLog.Summary($"Connection {d.Label(c.A)} ↔ {d.Label(c.B)} ({ConnectionTypes.Label(c.Type)}) deleted");
        db.NetworkConnections.Remove(c);
        await db.SaveChangesAsync(ct);
        analysis.RequestSoon();
    }

    private static AuditTarget Target(NetworkConnectionEntity c, NetworkDirectory d) =>
        AuditTarget.Of(AuditTargets.NetworkConnection, c.Id, $"{d.Label(c.A)} ↔ {d.Label(c.B)}", c.AHostId, c.BHostId, c.ASensorId, c.BSensorId);

    /// <summary>A request's end: a router (its host follows) or a host, and the interface.</summary>
    private static ConnectionEnd? End(NetworkDirectory d, NetworkEndRequest? end, string field, ApiValidator v)
    {
        var iface = Text.Trimmed(end?.Interface);
        if (iface is { Length: > MaxInterface })
        {
            v.Add($"{field}.interface", $"At most {MaxInterface} characters.");
        }
        if (end?.SensorId is { } sensorId)
        {
            if (d.Routers.TryGetValue(sensorId, out var r))
            {
                return new ConnectionEnd(r.HostId, sensorId, iface);
            }
            v.Add($"{field}.sensorId", "Not a MikroTik router.");
            return null;
        }
        if (end?.HostId is { } hostId)
        {
            if (d.Hosts.ContainsKey(hostId))
            {
                return new ConnectionEnd(hostId, null, iface);
            }
            v.Add($"{field}.hostId", "Not a host.");
            return null;
        }
        v.Add(field, "A router (sensorId) or a host (hostId).");
        return null;
    }

    private static string? Name(AuditWho who) => string.IsNullOrEmpty(who.Username) ? null : who.Username;

    // --- suggestions (§8.5) ----------------------------------------------------------------------------------------------

    public async Task<Paged<NetworkSuggestionItem>> SuggestionsAsync(NetworkSuggestionQuery query, CancellationToken ct) =>
        Paging.Page(await FilterSuggestionsAsync(query, ct), query);

    public async Task<IReadOnlyList<NetworkSuggestionItem>> FilterSuggestionsAsync(NetworkSuggestionQuery query, CancellationToken ct)
    {
        var d = await DirectoryAsync(ct);
        var (field, descending) = Paging.Sort(query.Sort, "-confidence", "confidence", "lastseen", "firstseen", "a", "b");
        var state = query.State ?? NetworkSuggestionState.Open;
        var rows = db.NetworkSuggestions.AsNoTracking().Where(s => s.State == state);
        if (query.Confidence is { } confidence)
        {
            rows = rows.Where(s => s.Confidence == confidence);
        }
        if (query.Kind is { } kind)
        {
            rows = rows.Where(s => s.Kind == kind);
        }
        IEnumerable<NetworkSuggestionEntity> list = await rows.ToListAsync(ct);
        if (query.DeviceId is { } device)
        {
            list = list.Where(s => d.Is(s.A, device) || d.Is(s.B, device));
        }
        if (query.LocationId is { } location)
        {
            list = list.Where(s => d.Within(s.A, location) || d.Within(s.B, location));
        }
        var items = list.Select(s => NetworkViews.Suggestion(s, d));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            items = items.Where(s => Text.Contains(s.A.Name, query.Search) || Text.Contains(s.B.Name, query.Search) || Text.Contains(s.A.Interface, query.Search)
                || Text.Contains(s.B.Interface, query.Search) || s.Evidence.Any(e => Text.Contains(e.Text, query.Search)));
        }
        items = field switch
        {
            "lastseen" => descending ? items.OrderByDescending(s => s.LastSeenAt) : items.OrderBy(s => s.LastSeenAt),
            "firstseen" => descending ? items.OrderByDescending(s => s.FirstSeenAt) : items.OrderBy(s => s.FirstSeenAt),
            "a" => Order(items, s => $"{s.A.Name} {s.A.Interface}", descending),
            "b" => Order(items, s => $"{s.B.Name} {s.B.Interface}", descending),
            _ => (descending ? items.OrderByDescending(s => s.Confidence) : items.OrderBy(s => s.Confidence))
                .ThenBy(s => s.A.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.A.Interface, StringComparer.OrdinalIgnoreCase),
        };
        return items.ToList();
    }

    /// <summary>
    /// Makes a suggestion a connection (§8.5): the proposal, or with another type, ports or notes. A <i>Differs</i> changes the
    /// connection it differs from instead.
    /// </summary>
    public async Task<NetworkConnectionItem> AcceptAsync(Guid id, NetworkSuggestionAccept request, CancellationToken ct)
    {
        var s = await db.NetworkSuggestions.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Suggestion");
        if (s.State != NetworkSuggestionState.Open)
        {
            throw ApiException.Conflict("suggestion_decided", $"The suggestion is {s.State.ToString().ToLowerInvariant()} already.");
        }
        var type = Text.Trimmed(request.Type) ?? s.Type;
        new ApiValidator()
            .Check(type is not null, "type", "The scan proposed no type: choose one.")
            .Check(type is null || ConnectionTypes.IsKnown(type), "type", "One of the connection types.")
            .Check((request.Notes?.Length ?? 0) <= MaxNotes, "notes", $"At most {MaxNotes} characters.")
            .Check((request.AInterface?.Length ?? 0) <= MaxInterface, "aInterface", $"At most {MaxInterface} characters.")
            .Check((request.BInterface?.Length ?? 0) <= MaxInterface, "bInterface", $"At most {MaxInterface} characters.")
            .ThrowIfInvalid();
        var d = await DirectoryAsync(ct);
        var a = s.A with { Interface = request.AInterface is null ? s.AInterface : Text.Trimmed(request.AInterface) };
        var b = s.B with { Interface = request.BInterface is null ? s.BInterface : Text.Trimmed(request.BInterface) };
        if (!d.Hosts.ContainsKey(a.HostId) || !d.Hosts.ContainsKey(b.HostId))
        {
            throw ApiException.Conflict("suggestion_stale", "A device of this suggestion no longer exists.");
        }
        NetworkConnectionEntity connection;
        var who = AuditWho.From(accessor);
        if (s.Kind == SuggestionKind.Differs && s.DiffersFrom is { } differs && await db.NetworkConnections.SingleOrDefaultAsync(c => c.Id == differs, ct) is { } old)
        {
            var (first, second) = ConnectionEnd.Ordered(a, b);
            old.Key = ConnectionEnd.PairKey(first, second);
            (old.AHostId, old.ASensorId, old.AInterface) = (first.HostId, first.SensorId, first.Interface);
            (old.BHostId, old.BSensorId, old.BInterface) = (second.HostId, second.SensorId, second.Interface);
            old.Type = type!;
            old.Notes = request.Notes is null ? old.Notes : Text.Trimmed(request.Notes);
            old.Evidence = s.Evidence;
            old.UpdatedAt = Clock.UtcNow;
            old.UpdatedBy = who.UserId;
            old.UpdatedByName = Name(who);
            connection = old;
        }
        else
        {
            var key = ConnectionEnd.PairKey(a, b);
            connection = await db.NetworkConnections.SingleOrDefaultAsync(c => c.Key == key, ct)
                ?? await AddAsync(d, a, b, type!, Text.Trimmed(request.Notes), NetworkConnectionSource.Suggestion, s.Evidence, ct);
        }
        s.State = NetworkSuggestionState.Accepted;
        s.ConnectionId = connection.Id;
        s.DecidedAt = Clock.UtcNow;
        s.DecidedBy = who.UserId;
        s.DecidedByName = Name(who);
        AuditLog.Target(AuditTarget.Of(AuditTargets.NetworkSuggestion, s.Id, $"{d.Label(a)} ↔ {d.Label(b)}", a.HostId, b.HostId, a.SensorId, b.SensorId));
        AuditLog.Summary($"Suggestion {d.Label(a)} ↔ {d.Label(b)} made a connection ({ConnectionTypes.Label(type!)})");
        await db.SaveChangesAsync(ct);
        analysis.RequestSoon();
        return NetworkViews.Connection(connection, d);
    }

    /// <summary>Many at once (§8.5), each with its proposed type; those without one are refused.</summary>
    public async Task<NetworkSuggestionsDecided> AcceptManyAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        Check(ids);
        var d = await DirectoryAsync(ct);
        var who = AuditWho.From(accessor);
        var rows = await db.NetworkSuggestions.Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        var refused = ids.Where(i => rows.All(r => r.Id != i)).Select(i => new NetworkSuggestionRefusal(i, "Not found.")).ToList();
        var done = new List<NetworkSuggestionEntity>();
        foreach (var s in rows)
        {
            if (s.State != NetworkSuggestionState.Open)
            {
                refused.Add(new NetworkSuggestionRefusal(s.Id, $"It is {s.State.ToString().ToLowerInvariant()} already."));
                continue;
            }
            if (s.Type is null || !ConnectionTypes.IsKnown(s.Type))
            {
                refused.Add(new NetworkSuggestionRefusal(s.Id, "The scan proposed no type: make it a connection with a type."));
                continue;
            }
            if (s.Kind == SuggestionKind.Differs)
            {
                refused.Add(new NetworkSuggestionRefusal(s.Id, "It would change a connection: accept it on its own."));
                continue;
            }
            var (first, second) = ConnectionEnd.Ordered(s.A, s.B);
            var key = ConnectionEnd.PairKey(first, second);
            var connection = await db.NetworkConnections.SingleOrDefaultAsync(c => c.Key == key, ct)
                ?? db.NetworkConnections.Local.FirstOrDefault(c => c.Key == key);
            if (connection is null)
            {
                connection = new NetworkConnectionEntity
                {
                    Id = Guid.NewGuid(), Key = key, AHostId = first.HostId, ASensorId = first.SensorId, AInterface = first.Interface,
                    BHostId = second.HostId, BSensorId = second.SensorId, BInterface = second.Interface, Type = s.Type,
                    Source = NetworkConnectionSource.Suggestion, Evidence = s.Evidence, CreatedAt = Clock.UtcNow, CreatedBy = who.UserId, CreatedByName = Name(who),
                };
                db.NetworkConnections.Add(connection);
            }
            s.State = NetworkSuggestionState.Accepted;
            s.ConnectionId = connection.Id;
            s.DecidedAt = Clock.UtcNow;
            s.DecidedBy = who.UserId;
            s.DecidedByName = Name(who);
            done.Add(s);
        }
        AuditLog.Target(AuditTarget.Many(AuditTargets.NetworkSuggestion, done.Select(s => (s.Id, $"{d.Label(s.A)} ↔ {d.Label(s.B)}")).ToList()));
        AuditLog.Summary($"{done.Count} suggestion(s) made connections");
        await db.SaveChangesAsync(ct);
        analysis.RequestSoon();
        return new NetworkSuggestionsDecided(done.Count, refused);
    }

    public Task<NetworkSuggestionsDecided> DismissAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        SetStateAsync(ids, NetworkSuggestionState.Open, NetworkSuggestionState.Dismissed, "dismissed", ct);

    public Task<NetworkSuggestionsDecided> RestoreAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        SetStateAsync(ids, NetworkSuggestionState.Dismissed, NetworkSuggestionState.Open, "restored", ct);

    private async Task<NetworkSuggestionsDecided> SetStateAsync(IReadOnlyList<Guid> ids, NetworkSuggestionState from, NetworkSuggestionState to, string verb,
        CancellationToken ct)
    {
        Check(ids);
        var d = await DirectoryAsync(ct);
        var who = AuditWho.From(accessor);
        var rows = await db.NetworkSuggestions.Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        var refused = ids.Where(i => rows.All(r => r.Id != i)).Select(i => new NetworkSuggestionRefusal(i, "Not found.")).ToList();
        var done = new List<NetworkSuggestionEntity>();
        foreach (var s in rows)
        {
            if (s.State != from)
            {
                refused.Add(new NetworkSuggestionRefusal(s.Id, $"It is {s.State.ToString().ToLowerInvariant()}."));
                continue;
            }
            s.State = to;
            s.DecidedAt = to == NetworkSuggestionState.Open ? null : Clock.UtcNow;
            s.DecidedBy = to == NetworkSuggestionState.Open ? null : who.UserId;
            s.DecidedByName = to == NetworkSuggestionState.Open ? null : Name(who);
            done.Add(s);
        }
        if (ids.Count == 1 && refused.Count == 1)
        {
            throw refused[0].Reason == "Not found." ? ApiException.NotFound("Suggestion") : ApiException.Conflict("suggestion_decided", refused[0].Reason);
        }
        AuditLog.Target(AuditTarget.Many(AuditTargets.NetworkSuggestion, done.Select(s => (s.Id, $"{d.Label(s.A)} ↔ {d.Label(s.B)}")).ToList()));
        AuditLog.Summary($"{done.Count} suggestion(s) {verb}");
        await db.SaveChangesAsync(ct);
        return new NetworkSuggestionsDecided(done.Count, refused);
    }

    private static void Check(IReadOnlyList<Guid> ids)
    {
        if (ids.Count is 0 or > 500)
        {
            throw ApiException.Invalid("ids", "Between 1 and 500 suggestions.");
        }
    }

    // --- network interfaces (§8.6) ---------------------------------------------------------------------------------------

    public async Task<Paged<NetworkInterfaceItem>> InterfacesAsync(NetworkInterfaceQuery query, CancellationToken ct)
    {
        var d = await DirectoryAsync(ct);
        var page = await Paging.PageAsync(InterfaceQuery(query, d), query, ct);
        return new Paged<NetworkInterfaceItem>(page.Items.Select(i => NetworkViews.Interface(i, d)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    /// <summary>A slice for MCP's cursor: <paramref name="take"/> rows from <paramref name="offset"/>, and the total.</summary>
    public async Task<(IReadOnlyList<NetworkInterfaceItem> Items, int Total)> InterfaceSliceAsync(NetworkInterfaceQuery query, int offset, int take, CancellationToken ct)
    {
        var d = await DirectoryAsync(ct);
        var rows = InterfaceQuery(query, d);
        var items = await rows.Skip(offset).Take(take).ToListAsync(ct);
        return (items.Select(i => NetworkViews.Interface(i, d)).ToList(), await rows.CountAsync(ct));
    }

    private IQueryable<NetworkInterfaceEntity> InterfaceQuery(NetworkInterfaceQuery query, NetworkDirectory d)
    {
        var (field, descending) = Paging.Sort(query.Sort, "-lastseen", "lastseen", "firstseen", "mac", "hostname", "port");
        var rows = db.NetworkInterfaces.AsNoTracking();
        if (query.ShowIgnored != true)
        {
            rows = rows.Where(i => !i.Ignored);
        }
        if (query.Unassigned == true)
        {
            rows = rows.Where(i => i.MatchKind == MatchKind.None || i.MatchKind == MatchKind.Ambiguous);
        }
        if (query.RouterId is { } router)
        {
            rows = rows.Where(i => i.AttachedSensorId == router);
        }
        if (query.LocationId is { } location)
        {
            var within = d.Tree.SelfAndDescendants(location).ToHashSet();
            var routers = d.Routers.Values.Where(r => r.LocationId is { } l && within.Contains(l)).Select(r => r.SensorId).ToList();
            rows = rows.Where(i => i.AttachedSensorId != null && routers.Contains(i.AttachedSensorId.Value));
        }
        if (query.HostId is { } host)
        {
            rows = rows.Where(i => i.MatchHostId == host || i.AssignedHostId == host);
        }
        if (query.Source is { Length: > 0 } source)
        {
            if (!Enum.TryParse<InterfaceSources>(source, true, out var flag) || flag == InterfaceSources.None)
            {
                throw ApiException.Invalid("source", "MacTable, Arp, Dhcp, Neighbor or Wifi.");
            }
            rows = rows.Where(i => (i.Sources & flag) != 0);
        }
        if (query.Vlan is { } vlan)
        {
            rows = rows.Where(i => i.AttachedVlan == vlan);
        }
        if (query.SeenInLastScan == true)
        {
            // An observation's time is when its table was read, a little before the snapshot's.
            rows = rows.Where(i => db.NetworkRouterSnapshots.Any(s => s.SensorId == i.AttachedSensorId && i.LastSeenAt >= s.ScannedAt.AddMinutes(-10)));
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            var term = query.Search!.Trim();
            var mac = Mac.Normalize(term);
            rows = rows.Where(i => EF.Functions.ILike(i.Mac, pattern) || (mac != null && i.Mac == mac) || EF.Functions.ILike(i.HostName ?? "", pattern)
                || EF.Functions.ILike(i.NeighborIdentity ?? "", pattern) || i.Ips.Any(ip => EF.Functions.ILike(ip, pattern)));
        }
        rows = field switch
        {
            "firstseen" => descending ? rows.OrderByDescending(i => i.FirstSeenAt) : rows.OrderBy(i => i.FirstSeenAt),
            "mac" => descending ? rows.OrderByDescending(i => i.Mac) : rows.OrderBy(i => i.Mac),
            "hostname" => descending ? rows.OrderByDescending(i => i.HostName) : rows.OrderBy(i => i.HostName),
            "port" => descending ? rows.OrderByDescending(i => i.AttachedPort) : rows.OrderBy(i => i.AttachedPort),
            _ => descending ? rows.OrderByDescending(i => i.LastSeenAt) : rows.OrderBy(i => i.LastSeenAt),
        };
        return ((IOrderedQueryable<NetworkInterfaceEntity>)rows).ThenBy(i => i.Mac);
    }

    public async Task<NetworkInterfaceItem> AssignAsync(string mac, NetworkInterfaceAssignment request, CancellationToken ct)
    {
        var row = await InterfaceAsync(mac, ct);
        var d = await DirectoryAsync(ct);
        if (!d.Hosts.ContainsKey(request.HostId))
        {
            throw ApiException.Invalid("hostId", "Not a host.");
        }
        if ((request.Interface?.Length ?? 0) > MaxInterface)
        {
            throw ApiException.Invalid("interface", $"At most {MaxInterface} characters.");
        }
        var who = AuditWho.From(accessor);
        row.AssignedHostId = request.HostId;
        row.AssignedInterface = Text.Trimmed(request.Interface);
        row.AssignedAt = Clock.UtcNow;
        row.AssignedBy = who.UserId;
        row.AssignedByName = Name(who);
        row.MatchKind = MatchKind.Manual;
        row.MatchHostId = request.HostId;
        row.MatchSensorId = null;
        row.MatchInterface = row.AssignedInterface;
        AuditLog.Target(AuditTarget.Of(AuditTargets.NetworkInterface, row.Mac, $"{row.Mac} → {d.Hosts[request.HostId].Name}"));
        AuditLog.Summary($"Interface {row.Mac} assigned to {d.Hosts[request.HostId].Name}");
        await db.SaveChangesAsync(ct);
        analysis.RequestSoon();
        return NetworkViews.Interface(row, d);
    }

    public async Task<NetworkInterfaceItem> ClearAsync(string mac, CancellationToken ct)
    {
        var row = await InterfaceAsync(mac, ct);
        var d = await DirectoryAsync(ct);
        var was = row.AssignedHostId is { } h && d.Hosts.TryGetValue(h, out var host) ? host.Name : "nobody";
        row.AssignedHostId = null;
        row.AssignedInterface = null;
        row.AssignedAt = null;
        row.AssignedBy = null;
        row.AssignedByName = null;
        if (row.MatchKind == MatchKind.Manual)
        {
            row.MatchKind = MatchKind.None;
            row.MatchHostId = null;
            row.MatchInterface = null;
        }
        AuditLog.Target(AuditTarget.Of(AuditTargets.NetworkInterface, row.Mac, row.Mac));
        AuditLog.Summary($"Interface {row.Mac}'s assignment to {was} cleared");
        await db.SaveChangesAsync(ct);
        analysis.RequestSoon();
        return NetworkViews.Interface(row, d);
    }

    public async Task<NetworkInterfaceItem> IgnoreAsync(string mac, bool ignore, CancellationToken ct)
    {
        var row = await InterfaceAsync(mac, ct);
        row.Ignored = ignore;
        AuditLog.Target(AuditTarget.Of(AuditTargets.NetworkInterface, row.Mac, row.Mac));
        AuditLog.Summary($"Interface {row.Mac} {(ignore ? "ignored" : "no longer ignored")}");
        await db.SaveChangesAsync(ct);
        analysis.RequestSoon();
        return NetworkViews.Interface(row, await DirectoryAsync(ct));
    }

    private async Task<NetworkInterfaceEntity> InterfaceAsync(string mac, CancellationToken ct)
    {
        var normalized = Mac.Normalize(mac) ?? throw ApiException.Invalid("mac", "A MAC address, as AA:BB:CC:DD:EE:FF.");
        return await db.NetworkInterfaces.SingleOrDefaultAsync(i => i.Mac == normalized, ct) ?? throw ApiException.NotFound("Network interface");
    }
}
