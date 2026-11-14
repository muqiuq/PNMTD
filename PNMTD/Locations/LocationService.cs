using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Locations;

/// <summary>
/// Locations in the API (docs/locations-knowledge-plan.md §3, §9): the tree with state and counts, a location's page,
/// creating, renaming, moving and deleting, assigning hosts, <i>From the hosts' texts…</i>, and the history. A location's
/// hosts and state include its sub-locations' (§3.1, §3.3). Every change to the tree takes <see cref="LocationTree.WriteLock"/>
/// and checks against the tree as its transaction sees it, then invalidates the cached tree and the dashboard (whose
/// hosts carry their locations' paths).
/// </summary>
public sealed partial class LocationService(
    PnmtdDbContext db,
    LocationTree tree,
    LocationValues values,
    DashboardService dashboard,
    DashboardCache dashboardCache,
    IHttpContextAccessor accessor)
{
    public const int MaxHostsPerRequest = 5000;
    public const int MaxTextsPerRequest = 500;

    private static readonly string[] SortFields = ["name", "state", "updated"];

    // --- The tree (§3.6) ------------------------------------------------------------------------------------------------

    public async Task<LocationList> ListAsync(LocationQuery query, CancellationToken ct)
    {
        var (sort, descending) = Paging.Sort(query.Sort, "name", SortFields);
        var snapshot = await tree.GetAsync(ct);
        var rows = await db.Locations.AsNoTracking()
            .Select(l => new { l.Id, l.Description, l.UpdatedAt, l.UpdatedByName })
            .ToDictionaryAsync(l => l.Id, ct);
        var hosts = (await dashboardCache.GetAsync()).Hosts.Where(h => h.AssignedLocation is not null)
            .ToLookup(h => h.AssignedLocation!.Id);
        var fields = await values.FieldsAsync(ct);
        var fieldIds = fields.Select(f => f.Id).ToList();
        var filled = (await db.LocationValues.AsNoTracking()
            .Where(v => v.Value != null && fieldIds.Contains(v.FieldId))
            .Select(v => new { v.LocationId, v.FieldId })
            .ToListAsync(ct)).Select(v => (v.LocationId, v.FieldId)).ToHashSet();

        HashSet<Guid>? matches = null;
        if (Text.Pattern(query.Search) is { } pattern)
        {
            var search = query.Search!.Trim();
            matches = snapshot.All
                .Where(n => n.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (rows.GetValueOrDefault(n.Id)?.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                .Select(n => n.Id)
                .ToHashSet();
            // The values of Text and Markdown fields, as their text (#>> '{}' unquotes a JSON string).
            matches.UnionWith(await db.Database.SqlQuery<Guid>($$"""
                SELECT DISTINCT v."LocationId" AS "Value"
                FROM location_values v JOIN location_fields f ON f."Id" = v."FieldId"
                WHERE f."ArchivedAt" IS NULL AND f."Type" IN (0, 1) AND v."Value" IS NOT NULL AND (v."Value" #>> '{}') ILIKE {{pattern}}
                """).ToListAsync(ct));
        }

        LocationListItem Item(LocationNode node)
        {
            var subtree = snapshot.SelfAndDescendants(node.Id);
            var inSubtree = subtree.SelectMany(id => hosts[id]).ToList();
            var (count, inherited) = LocationValues.Filled(snapshot, fields, filled, node.Id);
            var row = rows.GetValueOrDefault(node.Id);
            return new LocationListItem(node.Id, node.ParentId, node.Name, row?.Description, snapshot.Path(node.Id), snapshot.Depth(node.Id),
                StateOf(inSubtree), Counts(inSubtree, node.Id), snapshot.Children(node.Id).Count, count, inherited.Select(f => f.Label).ToList(),
                row?.UpdatedAt ?? default, row?.UpdatedByName);
        }

        var items = new List<LocationListItem>();
        void Walk(Guid? parentId)
        {
            var level = snapshot.Children(parentId).Select(Item).ToList();
            IEnumerable<LocationListItem> ordered = sort switch
            {
                "state" => level.OrderBy(i => HostService.StateOrder(i.State)).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
                "updated" => level.OrderBy(i => i.UpdatedAt),
                _ => level.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            };
            foreach (var item in descending ? ordered.Reverse() : ordered)
            {
                if (matches is null || matches.Contains(item.Id))
                {
                    items.Add(item);
                }
                Walk(item.Id);
            }
        }
        Walk(null);
        return new LocationList(items, fields.Count);
    }

    /// <summary>Failing when an enabled host fails, Ok when one is OK, otherwise NoData (§3.1): as a host is to its sensors.</summary>
    public static HostState StateOf(IEnumerable<DashboardHost> hosts)
    {
        var enabled = hosts.Where(h => h.Enabled).ToList();
        return enabled.Any(h => h.State == HostState.Failing) ? HostState.Failing
            : enabled.Any(h => h.State == HostState.Ok) ? HostState.Ok
            : HostState.NoData;
    }

    private static LocationHostCounts Counts(IReadOnlyCollection<DashboardHost> hosts, Guid locationId) =>
        new(hosts.Count, hosts.Count(h => h.State == HostState.Failing), hosts.Count(h => h.AssignedLocation?.Id == locationId));

    // --- A location's page (§3.4) -------------------------------------------------------------------------------------

    /// <summary>The page: hosts with sub-locations' unless <paramref name="own"/>; counts and state always with them.</summary>
    public async Task<LocationDetail> GetAsync(Guid id, bool own, CancellationToken ct)
    {
        var location = await db.Locations.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw ApiException.NotFound("Location");
        var snapshot = await tree.GetAsync(ct);
        if (!snapshot.Contains(id))
        {
            // Created by another context since the tree was read.
            tree.Invalidate();
            snapshot = await tree.GetAsync(ct);
        }
        var subtree = snapshot.SelfAndDescendants(id).ToList();
        var hosts = await dashboard.HostsAsync(h => h.LocationId != null && subtree.Contains(h.LocationId.Value), ct);
        var children = snapshot.Children(id).Select(child =>
        {
            var ids = snapshot.SelfAndDescendants(child.Id).ToHashSet();
            var inChild = hosts.Where(h => ids.Contains(h.AssignedLocation!.Id)).ToList();
            return new LocationChild(child.Id, child.Name, null, StateOf(inChild), Counts(inChild, child.Id), snapshot.Children(child.Id).Count);
        }).ToList();
        if (children.Count > 0)
        {
            var childIds = children.Select(c => c.Id).ToList();
            var descriptions = await db.Locations.AsNoTracking().Where(l => childIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, l => l.Description, ct);
            children = children.Select(c => c with { Description = descriptions.GetValueOrDefault(c.Id) }).ToList();
        }
        var resolved = await values.ResolveAsync(id, ct);
        var fieldValues = await values.ItemsAsync(resolved, ct);
        return new LocationDetail(location.Id, location.ParentId, location.Name, location.Description, snapshot.Path(id),
            snapshot.Ancestors(id).Reverse().Select(a => new Ref(a.Id, a.Name)).ToList(), snapshot.Depth(id),
            StateOf(hosts), Counts(hosts, id), children,
            own ? hosts.Where(h => h.AssignedLocation?.Id == id).ToList() : hosts,
            fieldValues, await FilesAsync(resolved, ct), location.CreatedAt, location.CreatedByName, location.UpdatedAt, location.UpdatedByName);
    }

    /// <summary>The files the Markdown values shown reference, inherited ones included, for attachment chips (§5.6).</summary>
    private async Task<IReadOnlyList<FileItem>> FilesAsync(IReadOnlyList<EffectiveValue> resolved, CancellationToken ct)
    {
        var ids = resolved.Where(v => v.Field.Type == LocationFieldType.Markdown && v.Value is not null)
            .SelectMany(v => MarkdownText.Read(LocationValues.ReadString(v.Value) ?? "").FileIds)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }
        var files = await db.Files.AsNoTracking().Where(f => ids.Contains(f.Id) && f.DeletedAt == null).ToListAsync(ct);
        return files.Select(FileNames.Item).ToList();
    }

    // --- Creating, changing, moving, deleting (§3.1, §3.3) ---------------------------------------------------------------

    public async Task<LocationDetail> CreateAsync(LocationRequest request, CancellationToken ct)
    {
        var (name, description) = Validate(request);
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        var location = new LocationEntity
        {
            Id = Guid.NewGuid(), ParentId = request.ParentId, Name = name, NormalizedName = NormalizeName(name), Description = description,
            CreatedAt = now, CreatedBy = by.Id, CreatedByName = by.Name, UpdatedAt = now, UpdatedBy = by.Id, UpdatedByName = by.Name,
        };
        await using (var transaction = await BeginTreeChangeAsync(ct))
        {
            var snapshot = await LocationTree.LoadAsync(db, ct);
            CheckPlacement(snapshot, location.Id, request.ParentId);
            await EnsureUniqueAsync(snapshot, location.Id, request.ParentId, name, ct);
            db.Locations.Add(location);
            History(location.Id, by, now, LocationHistoryKind.Created, detail: PathWith(snapshot, request.ParentId, name));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        tree.Invalidate();
        return await GetAsync(location.Id, false, ct);
    }

    /// <summary>The name, the description and the parent; each change is a history row (§3.5).</summary>
    public async Task<LocationDetail> UpdateAsync(Guid id, LocationRequest request, CancellationToken ct)
    {
        var (name, description) = Validate(request);
        await using (var transaction = await BeginTreeChangeAsync(ct))
        {
            var location = await db.Locations.SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw ApiException.NotFound("Location");
            var snapshot = await LocationTree.LoadAsync(db, ct);
            var by = LocationActor.Of(accessor);
            var now = Clock.UtcNow;
            var changed = false;
            if (location.Name != name || location.ParentId != request.ParentId)
            {
                await EnsureUniqueAsync(snapshot, id, request.ParentId, name, ct);
            }
            if (location.ParentId != request.ParentId)
            {
                Move(snapshot, location, request.ParentId, by, now);
                changed = true;
            }
            if (location.Name != name)
            {
                History(id, by, now, LocationHistoryKind.Renamed, LocationValues.StringValue(location.Name), LocationValues.StringValue(name));
                location.Name = name;
                location.NormalizedName = NormalizeName(name);
                changed = true;
            }
            if (location.Description != description)
            {
                History(id, by, now, LocationHistoryKind.DescriptionChanged, Json(location.Description), Json(description));
                location.Description = description;
                changed = true;
            }
            if (changed)
            {
                location.UpdatedAt = now;
                location.UpdatedBy = by.Id;
                location.UpdatedByName = by.Name;
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        AfterTreeChange();
        return await GetAsync(id, false, ct);
    }

    /// <summary><c>POST /locations/{id}/move</c>: under another parent, or to the top (§3.3).</summary>
    public async Task<LocationDetail> MoveAsync(Guid id, LocationMoveRequest request, CancellationToken ct)
    {
        await using (var transaction = await BeginTreeChangeAsync(ct))
        {
            var location = await db.Locations.SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw ApiException.NotFound("Location");
            if (location.ParentId != request.ParentId)
            {
                var snapshot = await LocationTree.LoadAsync(db, ct);
                var by = LocationActor.Of(accessor);
                var now = Clock.UtcNow;
                CheckPlacement(snapshot, id, request.ParentId);
                await EnsureUniqueAsync(snapshot, id, request.ParentId, location.Name, ct);
                Move(snapshot, location, request.ParentId, by, now);
                location.UpdatedAt = now;
                location.UpdatedBy = by.Id;
                location.UpdatedByName = by.Name;
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        AfterTreeChange();
        return await GetAsync(id, false, ct);
    }

    private void Move(LocationTreeSnapshot snapshot, LocationEntity location, Guid? parentId, LocationActor by, DateTime now)
    {
        CheckPlacement(snapshot, location.Id, parentId);
        History(location.Id, by, now, LocationHistoryKind.Moved, LocationValues.StringValue(snapshot.Path(location.Id)),
            LocationValues.StringValue(PathWith(snapshot, parentId, location.Name)));
        location.ParentId = parentId;
    }

    /// <summary>
    /// Admins only (§8.1). Refused while it has sub-locations (409 <c>has_sub_locations</c>, naming them), so nothing goes by
    /// accident (§3.3). Its hosts keep their texts and have no location afterwards; its values, history and links go with it.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await using (var transaction = await BeginTreeChangeAsync(ct))
        {
            if (!await db.Locations.AnyAsync(l => l.Id == id, ct))
            {
                throw ApiException.NotFound("Location");
            }
            var snapshot = await LocationTree.LoadAsync(db, ct);
            var children = snapshot.Children(id);
            if (children.Count > 0)
            {
                throw new ApiException(StatusCodes.Status409Conflict, LocationErrors.HasSubLocations,
                    $"Move or delete its sub-locations first: {string.Join(", ", children.Select(c => c.Name))}.",
                    extensions: new Dictionary<string, object?> { ["subLocations"] = children.Select(c => new Ref(c.Id, c.Name)).ToList() });
            }
            var hostCount = await db.Hosts.CountAsync(h => h.LocationId == id, ct);
            var valueCount = await db.LocationValues.CountAsync(v => v.LocationId == id && v.Value != null, ct);
            UdpViews.Audit(db, accessor, AuthEvents.LocationDeleted,
                Cuts.Cut($"{snapshot.Path(id)}: {Plural(hostCount, "host")} without a location now, {Plural(valueCount, "value")} deleted", 1000));
            await db.SaveChangesAsync(ct);
            // Hosts are set to none (ON DELETE SET NULL); values, history, file uses and page links cascade.
            await db.Locations.Where(l => l.Id == id).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        }
        AfterTreeChange();
    }

    // --- Hosts (§3.2) -----------------------------------------------------------------------------------------------------

    /// <summary><c>PUT /hosts/location</c>: many hosts' location at once, null for none; how many changed.</summary>
    public async Task<int> SetHostsAsync(HostLocationRequest request, CancellationToken ct)
    {
        new ApiValidator()
            .Check(request.Ids.Count is >= 1 and <= MaxHostsPerRequest, "ids", $"Between 1 and {MaxHostsPerRequest} hosts.")
            .ThrowIfInvalid();
        if (request.LocationId is { } locationId && !await db.Locations.AnyAsync(l => l.Id == locationId, ct))
        {
            throw ApiException.Invalid("locationId", "No such location.");
        }
        var ids = request.Ids.Distinct().ToList();
        var hosts = await db.Hosts.Where(h => ids.Contains(h.Id) && h.LocationId != request.LocationId).ToListAsync(ct);
        if (hosts.Count == 0)
        {
            return 0;
        }
        HostsMoved(db, LocationActor.Of(accessor), hosts.Select(h => (h.Name, h.LocationId, request.LocationId)));
        foreach (var host in hosts)
        {
            host.LocationId = request.LocationId;
        }
        await db.SaveChangesAsync(ct);
        dashboardCache.Invalidate();
        return hosts.Count;
    }

    /// <summary>
    /// The history rows of hosts moving between locations (§3.5): per location, the hosts removed and those added. The
    /// caller saves. Also for the host's form (HostService).
    /// </summary>
    public static void HostsMoved(PnmtdDbContext db, LocationActor by, IEnumerable<(string Host, Guid? From, Guid? To)> moves)
    {
        var list = moves.Where(m => m.From != m.To).ToList();
        var now = Clock.UtcNow;
        foreach (var group in list.Where(m => m.From is not null).GroupBy(m => m.From!.Value))
        {
            db.LocationHistory.Add(HistoryRow(group.Key, by, now, LocationHistoryKind.HostsRemoved, null, null, Names(group.Select(m => m.Host))));
        }
        foreach (var group in list.Where(m => m.To is not null).GroupBy(m => m.To!.Value))
        {
            db.LocationHistory.Add(HistoryRow(group.Key, by, now, LocationHistoryKind.HostsAdded, null, null, Names(group.Select(m => m.Host))));
        }
    }

    private static string Names(IEnumerable<string> hosts) => Cuts.Cut(string.Join(", ", hosts.Order(StringComparer.OrdinalIgnoreCase)), 2000);

    // --- From the hosts' texts… (§3.2) -------------------------------------------------------------------------------------

    /// <summary>The texts of hosts without a location, matched ignoring case and whitespace, with the locations of that name.</summary>
    public async Task<IReadOnlyList<LocationSuggestion>> SuggestionsAsync(CancellationToken ct)
    {
        var texts = await db.Hosts.AsNoTracking().Where(h => h.LocationId == null && h.Location != null).Select(h => h.Location!).ToListAsync(ct);
        var snapshot = await tree.GetAsync(ct);
        var byName = snapshot.All.ToLookup(n => NormalizeName(n.Name));
        return texts
            .GroupBy(NormalizeName)
            .Where(g => g.Key.Length > 0)
            .Select(g => new LocationSuggestion(
                g.GroupBy(t => t.Trim()).OrderByDescending(s => s.Count()).ThenBy(s => s.Key, StringComparer.Ordinal).First().Key,
                g.Count(),
                byName[g.Key].Select(n => new LocationRef(n.Id, n.Name, snapshot.Path(n.Id))).OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(s => s.Hosts)
            .ThenBy(s => s.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Per text, assigns the hosts without a location that use it to a location: one picked, or one created under the
    /// parent given (an existing sibling of that name is used instead). <see cref="LocationFromTextRequest.ClearText"/>
    /// clears their texts too.
    /// </summary>
    public async Task<LocationFromTextResult> FromTextAsync(LocationFromTextRequest request, CancellationToken ct)
    {
        var v = new ApiValidator()
            .Check(request.Items.Count is >= 1 and <= MaxTextsPerRequest, "items", $"Between 1 and {MaxTextsPerRequest} texts.");
        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            v.Check(!string.IsNullOrWhiteSpace(item.Text), $"items[{i}].text", "The text the hosts use.");
            if (item.LocationId is null)
            {
                var name = CollapseSpaces(item.Name ?? item.Text ?? "");
                v.Check(name.Length is >= 2 and <= 64 && !name.Any(char.IsControl), $"items[{i}].name", "The name must have 2 to 64 characters.");
            }
        }
        v.ThrowIfInvalid();

        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        var created = 0;
        var assigned = 0;
        await using (var transaction = await BeginTreeChangeAsync(ct))
        {
            var snapshot = await LocationTree.LoadAsync(db, ct);
            var hosts = (await db.Hosts.Where(h => h.LocationId == null && h.Location != null).ToListAsync(ct))
                .ToLookup(h => NormalizeName(h.Location!));
            // Locations this request creates, by parent and name: two texts can name the same new one.
            var made = new Dictionary<(Guid?, string), LocationEntity>();
            for (var i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                Guid target;
                if (item.LocationId is { } picked)
                {
                    if (!snapshot.Contains(picked))
                    {
                        throw ApiException.Invalid($"items[{i}].locationId", "No such location.");
                    }
                    target = picked;
                }
                else
                {
                    var name = CollapseSpaces(item.Name ?? item.Text!);
                    var normalized = NormalizeName(name);
                    if (item.ParentId is { } parent && !snapshot.Contains(parent))
                    {
                        throw ApiException.Invalid($"items[{i}].parentId", "No such location.");
                    }
                    if (snapshot.Children(item.ParentId).FirstOrDefault(c => NormalizeName(c.Name) == normalized) is { } sibling)
                    {
                        target = sibling.Id;
                    }
                    else if (made.TryGetValue((item.ParentId, normalized), out var earlier))
                    {
                        target = earlier.Id;
                    }
                    else
                    {
                        var location = new LocationEntity
                        {
                            Id = Guid.NewGuid(), ParentId = item.ParentId, Name = name, NormalizedName = normalized,
                            CreatedAt = now, CreatedBy = by.Id, CreatedByName = by.Name, UpdatedAt = now, UpdatedBy = by.Id, UpdatedByName = by.Name,
                        };
                        CheckPlacement(snapshot, location.Id, item.ParentId);
                        db.Locations.Add(location);
                        History(location.Id, by, now, LocationHistoryKind.Created, detail: PathWith(snapshot, item.ParentId, name));
                        made[(item.ParentId, normalized)] = location;
                        created++;
                        target = location.Id;
                    }
                }
                var matching = hosts[NormalizeName(item.Text!)].Where(h => h.LocationId is null).ToList();
                if (matching.Count == 0)
                {
                    continue;
                }
                HostsMoved(db, by, matching.Select(h => (h.Name, (Guid?)null, (Guid?)target)));
                foreach (var host in matching)
                {
                    host.LocationId = target;
                    if (request.ClearText)
                    {
                        host.Location = null;
                    }
                }
                assigned += matching.Count;
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        AfterTreeChange();
        return new LocationFromTextResult(created, assigned);
    }

    // --- History (§3.5) -----------------------------------------------------------------------------------------------------

    public async Task<Paged<LocationHistoryItem>> HistoryAsync(Guid id, PageQuery page, CancellationToken ct)
    {
        if (!await db.Locations.AnyAsync(l => l.Id == id, ct))
        {
            throw ApiException.NotFound("Location");
        }
        var query =
            from h in db.LocationHistory.AsNoTracking()
            where h.LocationId == id
            join f in db.LocationFields.AsNoTracking() on h.FieldId equals f.Id into fields
            from f in fields.DefaultIfEmpty()
            orderby h.At descending, h.Id
            select new { h, Label = f == null ? null : f.Label, Type = f == null ? (LocationFieldType?)null : f.Type };
        var (number, size) = Paging.Normalize(page);
        var total = await query.CountAsync(ct);
        var rows = await query.Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Paged<LocationHistoryItem>(rows.Select(r => new LocationHistoryItem(r.h.Id, r.h.At, r.h.ByName, r.h.Kind, r.h.FieldId, r.Label, r.Type,
            LocationValues.Element(r.h.OldValue), LocationValues.Element(r.h.NewValue), r.h.Detail)).ToList(), number, size, total);
    }

    private void History(Guid locationId, LocationActor by, DateTime at, LocationHistoryKind kind, string? oldValue = null, string? newValue = null,
        string? detail = null) =>
        db.LocationHistory.Add(HistoryRow(locationId, by, at, kind, oldValue, newValue, detail));

    private static LocationHistoryEntity HistoryRow(Guid locationId, LocationActor by, DateTime at, LocationHistoryKind kind, string? oldValue,
        string? newValue, string? detail) =>
        new()
        {
            Id = Guid.NewGuid(), LocationId = locationId, At = at, ByUserId = by.Id, ByName = by.Name, Kind = kind,
            OldValue = oldValue, NewValue = newValue, Detail = detail is null ? null : Cuts.Cut(detail, 2000),
        };

    // --- Rules --------------------------------------------------------------------------------------------------------------

    /// <summary>Names and texts compared: trimmed, runs of whitespace as one space, ignoring case (§3.1, §3.2).</summary>
    public static string NormalizeName(string value) => CollapseSpaces(value).ToLowerInvariant();

    private static string CollapseSpaces(string value) => Spaces().Replace(value.Trim(), " ");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Spaces();

    private static (string Name, string? Description) Validate(LocationRequest request)
    {
        var name = CollapseSpaces(request.Name ?? "");
        var description = Text.Trimmed(request.Description);
        new ApiValidator()
            .Check(name.Length is >= 2 and <= 64 && !name.Any(char.IsControl), "name", "The name must have 2 to 64 characters.")
            .Check(description is null || (description.Length <= 256 && !description.Any(char.IsControl)), "description", "One line, at most 256 characters.")
            .ThrowIfInvalid();
        return (name, description);
    }

    /// <summary>The parent exists, and the location with its subtree fits under it: no cycle, at most 6 levels (§3.3).</summary>
    private static void CheckPlacement(LocationTreeSnapshot snapshot, Guid id, Guid? parentId)
    {
        if (parentId is { } parent && !snapshot.Contains(parent))
        {
            throw ApiException.Invalid("parentId", "No such location.");
        }
        switch (snapshot.PlacementProblem(id, parentId))
        {
            case LocationErrors.Cycle:
                throw ApiException.Conflict(LocationErrors.Cycle, "A location can't move under itself or one of its own sub-locations.");
            case LocationErrors.TooDeep:
                throw ApiException.Conflict(LocationErrors.TooDeep, $"Locations nest at most {LocationTree.MaxDepth} levels deep, its sub-locations included.");
        }
    }

    /// <summary>Unique among its siblings regardless of case, at the top level too (409 <c>duplicate_key</c>).</summary>
    private async Task EnsureUniqueAsync(LocationTreeSnapshot snapshot, Guid id, Guid? parentId, string name, CancellationToken ct)
    {
        var normalized = NormalizeName(name);
        if (await db.Locations.AnyAsync(l => l.ParentId == parentId && l.NormalizedName == normalized && l.Id != id, ct))
        {
            var where = parentId is { } parent ? snapshot.Path(parent) : "The top level";
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, $"{where} has a location named {name} already.");
        }
    }

    private static string PathWith(LocationTreeSnapshot snapshot, Guid? parentId, string name) =>
        parentId is { } parent ? snapshot.Path(parent) + LocationTree.PathSeparator + name : name;

    /// <summary>A transaction holding <see cref="LocationTree.WriteLock"/>: one change to the tree at a time.</summary>
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginTreeChangeAsync(CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [LocationTree.WriteLock], ct);
        return transaction;
    }

    /// <summary>Paths changed: the tree is read again, and the dashboard's hosts carry their paths.</summary>
    private void AfterTreeChange()
    {
        tree.Invalidate();
        dashboardCache.Invalidate();
    }

    private static string? Json(string? value) => value is null ? null : LocationValues.StringValue(value);

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
