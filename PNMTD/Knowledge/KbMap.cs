using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Knowledge;

/// <summary>
/// The knowledge map (docs/knowledge-base-extensions-plan.md §8): pages, locations, hosts and sensors as nodes; page links, links in
/// the texts, links to objects and the structure (sensor → host → location → parent) as edges. It reads everything in a few queries,
/// builds the graph in memory, and takes from it:
/// <list type="bullet">
/// <item>around a focus, everything within <c>Depth</c> steps over every edge, both ways;</item>
/// <item>without one, the pages with a link (or all, with <c>Unlinked</c>) that pass the filters, and what they link to.</item>
/// </list>
/// Structure is followed upwards always: every object brings its host, location and parents. Downwards it only ever leads to pages,
/// since only objects that pages link to, and what is above them, are in the graph. At most <c>KnowledgeBase:MapMaxNodes</c> nodes,
/// nearest to the focus (or most linked) first. Names are shown as the pages' links show them; states only with
/// <i>Monitoring: View</i>.
/// </summary>
public sealed class KbMap(PnmtdDbContext db, LocationTree tree, MonitoringReader monitoring, IHttpContextAccessor accessor,
    IOptionsMonitor<KnowledgeBaseOptions> kb)
{
    public const int MaxDepth = 4;

    private sealed record PageRow(Guid Id, int Number, string Title, KbPageStatus Status, bool Sensitive, bool WrittenWithAi, bool Archived, int Likes,
        int Uses, string TagsText);

    private sealed record HostRow(Guid Id, string Name, Guid? LocationId, string? Location, bool Enabled);

    private sealed record SensorRow(Guid Id, string Name, Guid HostId);

    public async Task<KbGraph> GraphAsync(KbMapQuery query, CancellationToken ct)
    {
        if (query.Depth is < 1 or > MaxDepth)
        {
            throw ApiException.Invalid("depth", $"1 to {MaxDepth} steps.");
        }
        var focusNumber = FocusNumber(query.Focus);
        var pages = await db.KbPages.AsNoTracking()
            .Where(p => query.Archived || p.ArchivedAt == null || p.Number == focusNumber)
            .Select(p => new PageRow(p.Id, p.Number, p.Title, p.Status, p.Sensitive, p.WrittenWithAi, p.ArchivedAt != null, p.Likes, p.Uses, p.TagsText))
            .ToListAsync(ct);
        var pageById = pages.ToDictionary(p => p.Id);
        var pageByNumber = pages.ToDictionary(p => p.Number, p => p.Id);
        var links = (await db.KbLinks.AsNoTracking().Select(l => new { l.PageId, l.LocationId, l.HostId, l.SensorId, l.TargetPageId }).ToListAsync(ct))
            .Where(l => pageById.ContainsKey(l.PageId)).ToList();
        var refs = await db.KbPageRefs.AsNoTracking().Select(r => new { r.FromPageId, r.ToNumber }).ToListAsync(ct);
        var locations = await tree.GetAsync(ct);

        var graph = new Graph();
        foreach (var p in pages)
        {
            graph.Node(PageId(p.Id));
        }
        // Page links: two rows per link, one edge.
        foreach (var l in links.Where(l => l.TargetPageId is { } t && pageById.ContainsKey(t)))
        {
            graph.Edge(PageId(l.PageId), PageId(l.TargetPageId!.Value), KbGraphEdgeKind.PageLink, undirected: true);
        }
        // Links in the texts: one-way, or folded into a page link between the same two pages.
        foreach (var r in refs.Where(r => pageById.ContainsKey(r.FromPageId)))
        {
            if (pageByNumber.TryGetValue(r.ToNumber, out var to) && to != r.FromPageId)
            {
                graph.TextLink(PageId(r.FromPageId), PageId(to));
            }
        }
        // Links to objects, and the objects with what is above them.
        var hostIds = links.Where(l => l.HostId is not null).Select(l => l.HostId!.Value).ToHashSet();
        var sensorIds = links.Where(l => l.SensorId is not null).Select(l => l.SensorId!.Value).ToHashSet();
        var (focus, focusObject) = await FocusAsync(query.Focus, focusNumber, pageByNumber, locations, ct);
        if (focusObject is { Kind: KbGraphNodeKind.Host } fh)
        {
            hostIds.Add(fh.Id);
        }
        if (focusObject is { Kind: KbGraphNodeKind.Sensor } fs)
        {
            sensorIds.Add(fs.Id);
        }
        var sensors = sensorIds.Count == 0 ? [] : await db.Sensors.AsNoTracking().Where(s => sensorIds.Contains(s.Id))
            .Select(s => new SensorRow(s.Id, s.Name, s.ParentId)).ToDictionaryAsync(s => s.Id, ct);
        hostIds.UnionWith(sensors.Values.Select(s => s.HostId));
        var hosts = hostIds.Count == 0 ? [] : await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id))
            .Select(h => new HostRow(h.Id, h.Name, h.LocationId, h.Location, h.Enabled)).ToDictionaryAsync(h => h.Id, ct);
        foreach (var l in links)
        {
            var target = l.LocationId is { } loc && locations.Contains(loc) ? LocationId(loc)
                : l.HostId is { } host && hosts.ContainsKey(host) ? HostId(host)
                : l.SensorId is { } sensor && sensors.ContainsKey(sensor) ? SensorId(sensor) : null;
            if (target is not null)
            {
                graph.Edge(PageId(l.PageId), target, KbGraphEdgeKind.ObjectLink);
            }
        }

        // What is above an object: the chain of structure edges, in order.
        List<string> Above(string id)
        {
            var chain = new List<string>();
            var (kind, guid) = Parse(id);
            Guid? location = null;
            if (kind == KbGraphNodeKind.Sensor && sensors.TryGetValue(guid, out var s) && hosts.ContainsKey(s.HostId))
            {
                graph.Edge(id, HostId(s.HostId), KbGraphEdgeKind.Structure);
                chain.Add(HostId(s.HostId));
                location = hosts[s.HostId].LocationId;
            }
            else if (kind == KbGraphNodeKind.Host && hosts.TryGetValue(guid, out var h))
            {
                location = h.LocationId;
            }
            else if (kind == KbGraphNodeKind.Location)
            {
                location = locations.Find(guid)?.ParentId;
                if (location is { } parent && locations.Contains(parent))
                {
                    graph.Edge(id, LocationId(parent), KbGraphEdgeKind.Structure);
                }
            }
            var below = chain.Count > 0 ? chain[^1] : id;
            if (kind != KbGraphNodeKind.Location && location is { } first && locations.Contains(first))
            {
                graph.Edge(below, LocationId(first), KbGraphEdgeKind.Structure);
            }
            for (var at = location; at is { } current && locations.Contains(current); at = locations.Find(current)!.ParentId)
            {
                chain.Add(LocationId(current));
                if (locations.Find(current)!.ParentId is { } parent && locations.Contains(parent))
                {
                    graph.Edge(LocationId(current), LocationId(parent), KbGraphEdgeKind.Structure);
                }
            }
            return chain;
        }
        foreach (var id in graph.Nodes.Keys.Where(n => !n.StartsWith("page:", StringComparison.Ordinal)).ToList())
        {
            Above(id);
        }
        if (focus is not null && !graph.Nodes.ContainsKey(focus))
        {
            graph.Node(focus);
            Above(focus);
        }

        // What is shown, in order of priority.
        var ordered = new List<string>();
        var seen = new HashSet<string>();
        void Take(string id)
        {
            if (seen.Add(id))
            {
                ordered.Add(id);
                if (!id.StartsWith("page:", StringComparison.Ordinal))
                {
                    foreach (var up in Above(id))
                    {
                        if (seen.Add(up))
                        {
                            ordered.Add(up);
                        }
                    }
                }
            }
        }
        if (focus is not null)
        {
            foreach (var id in graph.Around(focus, query.Depth))
            {
                Take(id);
            }
        }
        else
        {
            var tags = (query.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(KbTags.Normalize).OfType<string>().ToList();
            var within = query.Location is { } place ? locations.SelfAndDescendants(place).ToHashSet() : null;
            bool LinksWithin(Guid page) => links.Any(l => l.PageId == page && (
                (l.LocationId is { } loc && within!.Contains(loc))
                || (l.HostId is { } h && hosts.TryGetValue(h, out var host) && host.LocationId is { } hl && within!.Contains(hl))
                || (l.SensorId is { } s && sensors.TryGetValue(s, out var sensor) && hosts.TryGetValue(sensor.HostId, out var sh) && sh.LocationId is { } sl
                    && within!.Contains(sl))));
            var candidates = pages
                .Where(p => query.Status is null || p.Status == query.Status)
                .Where(p => query.Ai is null || p.WrittenWithAi == query.Ai)
                .Where(p => tags.All(t => p.TagsText.Split(' ').Contains(t)))
                .Where(p => within is null || LinksWithin(p.Id))
                .Select(p => (Page: p, Degree: graph.Degree(PageId(p.Id))))
                .Where(x => query.Unlinked || x.Degree > 0)
                .OrderByDescending(x => x.Degree).ThenBy(x => x.Page.Number);
            foreach (var (page, _) in candidates)
            {
                var id = PageId(page.Id);
                Take(id);
                foreach (var next in graph.Neighbours(id))
                {
                    Take(next);
                }
            }
        }
        var max = Math.Clamp(kb.CurrentValue.MapMaxNodes, 10, 5000);
        var shown = ordered.Take(max).ToHashSet();

        // The nodes, with names, states and groups.
        var withStates = accessor.HttpContext?.User is { } user && user.Has(Area.Monitoring, Level.View);
        var shownHosts = shown.Where(n => n.StartsWith("host:", StringComparison.Ordinal)).Select(n => Parse(n).Id).ToList();
        var shownSensors = shown.Where(n => n.StartsWith("sensor:", StringComparison.Ordinal)).Select(n => Parse(n).Id).ToList();
        Dictionary<string, string> states = [];
        if (withStates && (shownHosts.Count > 0 || shownSensors.Count > 0))
        {
            var snapshot = await monitoring.SnapshotAsync(s => shownHosts.Contains(s.ParentId) || shownSensors.Contains(s.Id), ct);
            foreach (var sensor in shownSensors)
            {
                states[SensorId(sensor)] = snapshot.Sensors.TryGetValue(sensor, out var status) ? status.State.ToString() : "NoData";
            }
            foreach (var host in shownHosts)
            {
                states[HostId(host)] = MonitoringSnapshot.HostStateOf(hosts[host].Enabled, snapshot.Sensors.Values.Where(s => s.Info.HostId == host)).ToString();
            }
        }
        Guid? RootOf(Guid? location) => location is { } l && locations.Contains(l) ? locations.Lineage(l).First().Id : null;
        var nodes = new List<KbGraphNode>();
        foreach (var id in ordered.Where(shown.Contains))
        {
            var (kind, guid) = Parse(id);
            switch (kind)
            {
                case KbGraphNodeKind.Page:
                    var p = pageById[guid];
                    nodes.Add(new KbGraphNode(id, kind, guid, p.Title, $"KB-{p.Number}", p.Number, p.Status, p.Sensitive, p.WrittenWithAi, p.Archived, p.Likes,
                        p.Uses, null, null));
                    break;
                case KbGraphNodeKind.Location:
                    var node = locations.Find(guid)!;
                    nodes.Add(Object(id, kind, guid, node.Name, locations.Path(guid), null, RootOf(guid)));
                    break;
                case KbGraphNodeKind.Host:
                    var h = hosts[guid];
                    nodes.Add(Object(id, kind, guid, h.Name, h.LocationId is { } hl && locations.Contains(hl) ? locations.Path(hl) : h.Location,
                        states.GetValueOrDefault(id), RootOf(h.LocationId)));
                    break;
                case KbGraphNodeKind.Sensor:
                    var s = sensors[guid];
                    nodes.Add(Object(id, kind, guid, s.Name, hosts.TryGetValue(s.HostId, out var sh) ? sh.Name : null, states.GetValueOrDefault(id),
                        RootOf(hosts.TryGetValue(s.HostId, out var host) ? host.LocationId : null)));
                    break;
            }
        }
        var edges = graph.Edges.Where(e => shown.Contains(e.Source) && shown.Contains(e.Target)).ToList();
        var groups = nodes.Where(n => n.Group is not null).Select(n => n.Group!).Distinct()
            .Select(g => new KbGraphGroup(g, locations.Find(Guid.Parse(g))!.Name)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new KbGraph(nodes, edges, groups, ordered.Count, ordered.Count > shown.Count, focus);
    }

    private static KbGraphNode Object(string id, KbGraphNodeKind kind, Guid guid, string name, string? detail, string? state, Guid? group) =>
        new(id, kind, guid, name, detail, null, null, false, false, false, 0, 0, state, group?.ToString());

    // --- the focus -------------------------------------------------------------------------------------------------------

    private static int? FocusNumber(string? focus) =>
        focus?.Trim() is { } f && f.StartsWith("page:", StringComparison.OrdinalIgnoreCase) && int.TryParse(f[5..], out var n) ? n : null;

    private sealed record FocusObject(KbGraphNodeKind Kind, Guid Id);

    /// <summary><c>page:42</c> by number, the others by id; 400 for anything else, 404 for what isn't there.</summary>
    private async Task<(string? Node, FocusObject? Object)> FocusAsync(string? focus, int? number, Dictionary<int, Guid> pages, LocationTreeSnapshot locations,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(focus))
        {
            return (null, null);
        }
        if (number is { } n)
        {
            return pages.TryGetValue(n, out var page) ? (PageId(page), null) : throw ApiException.NotFound("Page");
        }
        var parts = focus.Trim().Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var id))
        {
            throw ApiException.Invalid("focus", "page:42, or location:, host: or sensor: with an id.");
        }
        switch (parts[0].ToLowerInvariant())
        {
            case "location":
                return locations.Contains(id) ? (LocationId(id), new FocusObject(KbGraphNodeKind.Location, id)) : throw ApiException.NotFound("Location");
            case "host":
                return await db.Hosts.AnyAsync(h => h.Id == id, ct) ? (HostId(id), new FocusObject(KbGraphNodeKind.Host, id)) : throw ApiException.NotFound("Host");
            case "sensor":
                return await db.Sensors.AnyAsync(s => s.Id == id, ct)
                    ? (SensorId(id), new FocusObject(KbGraphNodeKind.Sensor, id))
                    : throw ApiException.NotFound("Sensor");
            default:
                throw ApiException.Invalid("focus", "page:42, or location:, host: or sensor: with an id.");
        }
    }

    // --- node ids ------------------------------------------------------------------------------------------------------

    private static string PageId(Guid id) => $"page:{id}";

    private static string LocationId(Guid id) => $"location:{id}";

    private static string HostId(Guid id) => $"host:{id}";

    private static string SensorId(Guid id) => $"sensor:{id}";

    private static (KbGraphNodeKind Kind, Guid Id) Parse(string node)
    {
        var parts = node.Split(':', 2);
        var kind = parts[0] switch
        {
            "page" => KbGraphNodeKind.Page,
            "location" => KbGraphNodeKind.Location,
            "host" => KbGraphNodeKind.Host,
            _ => KbGraphNodeKind.Sensor,
        };
        return (kind, Guid.Parse(parts[1]));
    }

    /// <summary>The graph in memory: nodes with their neighbours, and each edge once.</summary>
    private sealed class Graph
    {
        public Dictionary<string, HashSet<string>> Nodes { get; } = new(StringComparer.Ordinal);

        private readonly Dictionary<(string, string, KbGraphEdgeKind), KbGraphEdge> edges = [];

        public IEnumerable<KbGraphEdge> Edges => edges.Values;

        public void Node(string id)
        {
            if (!Nodes.ContainsKey(id))
            {
                Nodes[id] = new HashSet<string>(StringComparer.Ordinal);
            }
        }

        public void Edge(string a, string b, KbGraphEdgeKind kind, bool undirected = false)
        {
            var (source, target) = undirected && string.CompareOrdinal(a, b) > 0 ? (b, a) : (a, b);
            Node(source);
            Node(target);
            if (edges.ContainsKey((source, target, kind)))
            {
                return;
            }
            edges[(source, target, kind)] = new KbGraphEdge(source, target, kind);
            Nodes[source].Add(target);
            Nodes[target].Add(source);
        }

        /// <summary>A link in a text: folded into a page link between the same two pages, else an edge of its own.</summary>
        public void TextLink(string from, string to)
        {
            var (a, b) = string.CompareOrdinal(from, to) > 0 ? (to, from) : (from, to);
            if (edges.TryGetValue((a, b, KbGraphEdgeKind.PageLink), out var link))
            {
                edges[(a, b, KbGraphEdgeKind.PageLink)] = link with { AlsoInText = true };
                return;
            }
            Edge(from, to, KbGraphEdgeKind.TextLink);
        }

        public int Degree(string id) => Nodes.TryGetValue(id, out var n) ? n.Count : 0;

        public IEnumerable<string> Neighbours(string id) => Nodes.TryGetValue(id, out var n) ? n.Order(StringComparer.Ordinal) : [];

        /// <summary>Breadth first from <paramref name="start"/>, both ways over every edge, <paramref name="depth"/> steps at most.</summary>
        public IEnumerable<string> Around(string start, int depth)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { start };
            var frontier = new List<string> { start };
            yield return start;
            for (var step = 0; step < depth && frontier.Count > 0; step++)
            {
                var next = new List<string>();
                foreach (var node in frontier)
                {
                    foreach (var neighbour in Neighbours(node))
                    {
                        if (seen.Add(neighbour))
                        {
                            next.Add(neighbour);
                            yield return neighbour;
                        }
                    }
                }
                frontier = next;
            }
        }
    }
}
