namespace PNMTD.Lib.Network;

/// <summary>A connection as the path search sees it: its two nodes ("router:{id}", "host:{id}") and ports, and its type.</summary>
public sealed record PathEdge(Guid ConnectionId, string A, string? APort, string B, string? BPort, string Type);

/// <summary>One hop of a path, oriented from where the path comes to where it goes.</summary>
public sealed record PathHop(PathEdge Edge, string From, string? FromPort, string To, string? ToPort);

/// <summary>
/// A path between two devices (docs/network-map-plan.md §10.2): its hops, the slowest known speed, how many hops are virtual,
/// and the connections without which there is no other path (single points of failure).
/// </summary>
public sealed record TopologyPath(IReadOnlyList<PathHop> Hops, int? SlowestMbps, int VirtualHops, IReadOnlyList<Guid> SinglePoints);

/// <summary>
/// The path between two devices over the connections (never over suggestions, §10.2): breadth-first, at most
/// <see cref="MaxHops"/> hops; among the shortest, the one whose slowest hop is fastest. For the single points, each hop is
/// left out in turn and the search is run again, without the hop limit.
/// </summary>
public static class TopologyPaths
{
    public const int MaxHops = 12;

    /// <summary>The path, or null when there is none within <see cref="MaxHops"/> (or <paramref name="from"/> is <paramref name="to"/>).</summary>
    public static TopologyPath? Find(IReadOnlyList<PathEdge> edges, string from, string to, bool physicalOnly = false)
    {
        if (from == to)
        {
            return null;
        }
        var usable = edges.Where(e => e.A != e.B && (!physicalOnly || ConnectionTypes.IsPhysical(e.Type)))
            .OrderBy(e => e.ConnectionId).ToList();
        var adjacency = Adjacency(usable);
        if (!adjacency.ContainsKey(from) || !adjacency.ContainsKey(to))
        {
            return null;
        }

        // Breadth-first distances from the start, then the best bottleneck along the shortest paths.
        var distance = new Dictionary<string, int> { [from] = 0 };
        var queue = new Queue<string>([from]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (distance[node] >= MaxHops)
            {
                continue;
            }
            foreach (var (_, next) in adjacency[node])
            {
                if (distance.TryAdd(next, distance[node] + 1))
                {
                    queue.Enqueue(next);
                }
            }
        }
        if (!distance.TryGetValue(to, out var hops))
        {
            return null;
        }
        var best = new Dictionary<string, (int Bottleneck, PathEdge? Via, string? Previous)> { [from] = (int.MaxValue, null, null) };
        foreach (var layer in distance.Where(d => d.Value > 0 && d.Value <= hops).GroupBy(d => d.Value).OrderBy(g => g.Key))
        {
            foreach (var node in layer.Select(d => d.Key).OrderBy(n => n, StringComparer.Ordinal))
            {
                (int Bottleneck, PathEdge? Via, string? Previous)? chosen = null;
                foreach (var (edge, previous) in adjacency[node])
                {
                    if (!distance.TryGetValue(previous, out var d) || d != layer.Key - 1 || !best.TryGetValue(previous, out var before))
                    {
                        continue;
                    }
                    var bottleneck = Math.Min(before.Bottleneck, ConnectionTypes.Find(edge.Type)?.SpeedMbps ?? int.MaxValue);
                    if (chosen is null || bottleneck > chosen.Value.Bottleneck)
                    {
                        chosen = (bottleneck, edge, previous);
                    }
                }
                if (chosen is { } c)
                {
                    best[node] = c;
                }
            }
        }

        var path = new List<PathHop>();
        for (var node = to; node != from;)
        {
            var (_, via, previous) = best[node];
            path.Add(Orient(via!, previous!));
            node = previous!;
        }
        path.Reverse();

        var speeds = path.Select(h => ConnectionTypes.Find(h.Edge.Type)?.SpeedMbps).OfType<int>().ToList();
        var virtualHops = path.Count(h => !ConnectionTypes.IsPhysical(h.Edge.Type));
        var singlePoints = path.Where(h => !Reachable(usable.Where(e => e.ConnectionId != h.Edge.ConnectionId).ToList(), from, to))
            .Select(h => h.Edge.ConnectionId).ToList();
        return new TopologyPath(path, speeds.Count > 0 ? speeds.Min() : null, virtualHops, singlePoints);
    }

    /// <summary>Whether any path joins the two, however long.</summary>
    public static bool Reachable(IReadOnlyList<PathEdge> edges, string from, string to)
    {
        var adjacency = Adjacency(edges);
        if (!adjacency.ContainsKey(from))
        {
            return false;
        }
        var seen = new HashSet<string> { from };
        var queue = new Queue<string>([from]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node == to)
            {
                return true;
            }
            foreach (var (_, next) in adjacency[node])
            {
                if (seen.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }
        return false;
    }

    private static Dictionary<string, List<(PathEdge Edge, string Other)>> Adjacency(IEnumerable<PathEdge> edges)
    {
        var adjacency = new Dictionary<string, List<(PathEdge, string)>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            Add(edge.A, edge, edge.B);
            Add(edge.B, edge, edge.A);
        }
        return adjacency;

        void Add(string node, PathEdge edge, string other)
        {
            if (!adjacency.TryGetValue(node, out var list))
            {
                adjacency[node] = list = [];
            }
            list.Add((edge, other));
        }
    }

    /// <summary>The edge as a hop that arrives from <paramref name="from"/>.</summary>
    private static PathHop Orient(PathEdge edge, string from) => edge.A == from
        ? new PathHop(edge, edge.A, edge.APort, edge.B, edge.BPort)
        : new PathHop(edge, edge.B, edge.BPort, edge.A, edge.APort);
}
