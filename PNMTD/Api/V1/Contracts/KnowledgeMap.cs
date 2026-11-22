using System.Text.Json.Serialization;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// The knowledge map (docs/knowledge-base-extensions-plan.md §8): pages, locations, hosts and sensors, and how they are connected.

[JsonConverter(typeof(JsonStringEnumConverter<KbGraphNodeKind>))]
public enum KbGraphNodeKind
{
    Page,
    Location,
    Host,
    Sensor,
}

/// <summary>
/// An edge (§8.1): a page link (mutual), a link in a page's text (one-way), a page's link to an object, or the structure
/// (sensor → host → location → parent).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KbGraphEdgeKind>))]
public enum KbGraphEdgeKind
{
    PageLink,
    TextLink,
    ObjectLink,
    Structure,
}

/// <summary>
/// A node: <see cref="Id"/> is <c>page:&lt;id&gt;</c>, <c>location:&lt;id&gt;</c>, <c>host:&lt;id&gt;</c> or <c>sensor:&lt;id&gt;</c>.
/// <see cref="Detail"/> is a page's <c>KB-42</c>, a location's path, a host's location, a sensor's host. <see cref="State"/> is a
/// host's or sensor's, only with <i>Monitoring: View</i>. <see cref="Group"/> is the top-level location of a location, host or
/// sensor, for the circles; null for pages.
/// </summary>
public sealed record KbGraphNode(
    string Id,
    KbGraphNodeKind Kind,
    Guid RefId,
    string Name,
    string? Detail,
    int? Number,
    KbPageStatus? Status,
    bool Sensitive,
    bool WrittenWithAi,
    bool Archived,
    int Likes,
    int Uses,
    string? State,
    string? Group);

/// <summary>
/// An edge between two nodes; a page link and a link in the text are drawn as one (<see cref="AlsoInText"/>). A link in the text
/// runs from the page whose text has it.
/// </summary>
public sealed record KbGraphEdge(string Source, string Target, KbGraphEdgeKind Kind, bool AlsoInText = false);

/// <summary>A top-level location with nodes on the map: its circle when the circles are on.</summary>
public sealed record KbGraphGroup(string Id, string Name);

/// <summary>
/// <c>GET /kb/map</c>: the nodes shown, their edges and groups; <see cref="Total"/> the nodes there would be without the limit, and
/// <see cref="Focus"/> the node it is around.
/// </summary>
public sealed record KbGraph(IReadOnlyList<KbGraphNode> Nodes, IReadOnlyList<KbGraphEdge> Edges, IReadOnlyList<KbGraphGroup> Groups, int Total, bool Truncated,
    string? Focus);

/// <summary>
/// <c>GET /kb/map</c> (§8.2). <see cref="Focus"/>: <c>page:42</c> (a number), or <c>location:</c>, <c>host:</c> or <c>sensor:</c> with an
/// id; around it <see cref="Depth"/> steps (1 to 4). Without a focus, the whole knowledge base: the pages with a link, narrowed by
/// the filters.
/// </summary>
public sealed class KbMapQuery
{
    public string? Focus { get; set; }

    public int Depth { get; set; } = 2;

    /// <summary>Comma-separated; all must match.</summary>
    public string? Tags { get; set; }

    /// <summary>Pages linked to the location, its sub-locations, or their hosts and sensors.</summary>
    public Guid? Location { get; set; }

    public KbPageStatus? Status { get; set; }

    public bool? Ai { get; set; }

    public bool Archived { get; set; }

    /// <summary>Pages without a link too.</summary>
    public bool Unlinked { get; set; }
}
