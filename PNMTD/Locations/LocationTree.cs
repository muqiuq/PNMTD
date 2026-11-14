using Microsoft.EntityFrameworkCore;
using PNMTD.Data;

namespace PNMTD.Locations;

/// <summary>A location in the tree (docs/locations-knowledge-plan.md §3.3): its id, parent and name.</summary>
public sealed record LocationNode(Guid Id, Guid? ParentId, string Name);

/// <summary>
/// The locations' tree in memory (docs/locations-knowledge-plan.md §3.3): a team has hundreds of locations at most, so
/// (id, parent, name) is read whole and kept until a location changes. Paths, ancestors, descendants and depths come
/// from here, never from recursive queries. A singleton; LocationService calls <see cref="Invalidate"/> after every
/// change it commits. Writers that check a move or a new parent read the tree inside their transaction instead
/// (<see cref="LoadAsync"/>, under <see cref="WriteLock"/>), so two moves at once can't build a cycle.
/// </summary>
public sealed class LocationTree(IDbContextFactory<PnmtdDbContext> dbs)
{
    /// <summary>At most 6 levels: a site, a building, a floor, a room, a cage, a rack (§3.3).</summary>
    public const int MaxDepth = 6;

    /// <summary>Between the names of a path: <i>Zurich DC › Building A › Room 2.13</i> (§3.1).</summary>
    public const string PathSeparator = " › ";

    /// <summary>The advisory lock every change to the tree takes in its transaction ("PNMTLOC1").</summary>
    public const long WriteLock = 0x504E4D544C4F4331;

    private readonly object gate = new();
    private LocationTreeSnapshot? current;
    private int generation;

    /// <summary>The tree as last read; read again after <see cref="Invalidate"/>.</summary>
    public async Task<LocationTreeSnapshot> GetAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref current) is { } snapshot)
        {
            return snapshot;
        }
        int started;
        lock (gate)
        {
            started = generation;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var loaded = await LoadAsync(db, ct);
        lock (gate)
        {
            // A change committed while this read ran makes it stale: used once, not kept.
            if (generation == started)
            {
                current = loaded;
            }
        }
        return loaded;
    }

    /// <summary>After a location was created, renamed, moved or deleted: the next read goes to the database.</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            current = null;
            generation++;
        }
    }

    /// <summary>The tree as the context sees it now, inside its transaction if it has one.</summary>
    public static async Task<LocationTreeSnapshot> LoadAsync(PnmtdDbContext db, CancellationToken ct = default) =>
        LocationTreeSnapshot.From(await db.Locations.AsNoTracking()
            .Select(l => new LocationNode(l.Id, l.ParentId, l.Name))
            .ToListAsync(ct));
}

/// <summary>
/// One reading of the tree, never changed. Unknown ids have no ancestors, no descendants and an empty path; a cycle in
/// the data (the database doesn't refuse one, LocationService does) ends a walk instead of looping.
/// </summary>
public sealed class LocationTreeSnapshot
{
    private static readonly IReadOnlyList<LocationNode> None = [];

    private readonly Dictionary<Guid, LocationNode> nodes;
    private readonly Dictionary<Guid, List<LocationNode>> children = [];
    private readonly List<LocationNode> roots = [];

    private LocationTreeSnapshot(IEnumerable<LocationNode> all)
    {
        nodes = all.ToDictionary(n => n.Id);
        foreach (var node in nodes.Values.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Id))
        {
            if (node.ParentId is { } parent && nodes.ContainsKey(parent))
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    children[parent] = list = [];
                }
                list.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }
    }

    public static LocationTreeSnapshot From(IEnumerable<LocationNode> nodes) => new(nodes);

    public static readonly LocationTreeSnapshot Empty = new([]);

    public int Count => nodes.Count;

    /// <summary>Every location, in no particular order.</summary>
    public IEnumerable<LocationNode> All => nodes.Values;

    public bool Contains(Guid id) => nodes.ContainsKey(id);

    public LocationNode? Find(Guid id) => nodes.GetValueOrDefault(id);

    /// <summary>The top-level locations, by name.</summary>
    public IReadOnlyList<LocationNode> Roots => roots;

    /// <summary>A location's direct sub-locations, by name; null: the top level.</summary>
    public IReadOnlyList<LocationNode> Children(Guid? id) =>
        id is { } parent ? children.TryGetValue(parent, out var list) ? list : None : roots;

    /// <summary>Its ancestors, the nearest first (parent, grandparent, …), without itself.</summary>
    public IReadOnlyList<LocationNode> Ancestors(Guid id)
    {
        var result = new List<LocationNode>();
        var seen = new HashSet<Guid> { id };
        var node = Find(id);
        while (node?.ParentId is { } parentId && seen.Add(parentId) && Find(parentId) is { } parent)
        {
            result.Add(parent);
            node = parent;
        }
        return result;
    }

    /// <summary>Its ancestors and itself, from the top: what <see cref="Path"/> joins, and the page's breadcrumb.</summary>
    public IReadOnlyList<LocationNode> Lineage(Guid id)
    {
        if (Find(id) is not { } node)
        {
            return None;
        }
        var lineage = Ancestors(id).Reverse().ToList();
        lineage.Add(node);
        return lineage;
    }

    /// <summary><i>Zurich DC › Building A › Room 2.13</i>; empty for an unknown id.</summary>
    public string Path(Guid id) => string.Join(LocationTree.PathSeparator, Lineage(id).Select(n => n.Name));

    /// <summary>1 for a top-level location, 2 for its sub-locations, …; 0 for an unknown id.</summary>
    public int Depth(Guid id) => Find(id) is null ? 0 : Ancestors(id).Count + 1;

    /// <summary>All locations below it, depth-first and by name, without itself.</summary>
    public IReadOnlyList<Guid> Descendants(Guid id)
    {
        var result = new List<Guid>();
        var seen = new HashSet<Guid> { id };
        var stack = new Stack<LocationNode>(Children(id).Reverse());
        while (stack.TryPop(out var node))
        {
            if (!seen.Add(node.Id))
            {
                continue;
            }
            result.Add(node.Id);
            foreach (var child in Children(node.Id).Reverse())
            {
                stack.Push(child);
            }
        }
        return result;
    }

    /// <summary>Itself and everything below it: what a location's hosts and state include (§3.3).</summary>
    public IReadOnlyList<Guid> SelfAndDescendants(Guid id) => Contains(id) ? [id, .. Descendants(id)] : [];

    /// <summary>The levels its subtree spans, itself included: 1 for a location without sub-locations.</summary>
    public int Height(Guid id)
    {
        if (!Contains(id))
        {
            return 0;
        }
        var depth = Depth(id);
        return Descendants(id).Select(Depth).DefaultIfEmpty(depth).Max() - depth + 1;
    }

    /// <summary>True when <paramref name="id"/> is <paramref name="ancestor"/> or lies below it.</summary>
    public bool IsWithin(Guid id, Guid ancestor) => id == ancestor ? Contains(id) : Ancestors(id).Any(a => a.Id == ancestor);

    /// <summary>
    /// Why the location can't go under <paramref name="parentId"/> (null: the top level): <c>cycle</c> under itself or one
    /// of its own sub-locations, <c>too_deep</c> past <see cref="LocationTree.MaxDepth"/> levels with its subtree;
    /// null when it can. A new location is checked with an id the tree doesn't know.
    /// </summary>
    public string? PlacementProblem(Guid id, Guid? parentId)
    {
        if (parentId is not { } parent)
        {
            return null;
        }
        if (IsWithin(parent, id))
        {
            return LocationErrors.Cycle;
        }
        var height = Math.Max(1, Height(id));
        return Depth(parent) + height > LocationTree.MaxDepth ? LocationErrors.TooDeep : null;
    }
}

/// <summary>The problem codes of the locations' API (docs/locations-knowledge-plan.md §9).</summary>
public static class LocationErrors
{
    public const string Cycle = "cycle";
    public const string TooDeep = "too_deep";
    public const string HasSubLocations = "has_sub_locations";
    public const string NotArchived = "not_archived";
    public const string ChoiceInUse = "choice_in_use";
    public const string TypeChange = "type_change";
}
