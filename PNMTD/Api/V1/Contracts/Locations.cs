using System.Text.Json;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// Locations and their fields (docs/locations-knowledge-plan.md §3, §4, §9). A location's path is its ancestors' names and
// its own, joined by " › " (Zurich DC › Building A › Room 2.13); it is never stored. A location's state and host counts
// include its sub-locations' (§3.1, §3.3).
//
// A field's value is JSON, by the field's type (§4.1):
// - Markdown: a string, stored exactly as written; at most KnowledgeBase:MaxFieldBytes (256 KB) of UTF-8.
// - Text: a string of one line, at most 512 characters.
// - Number: a number, within the field's minimum and maximum, with at most its decimals. The unit is the field's.
// - YesNo: true or false.
// - Date: a string, "yyyy-MM-dd".
// - Choice: a string, one of the field's choices.
// - Choices: an array of strings, each one of the field's choices, in the choices' order.
// - Link: an object { "url": an absolute http(s) URL of at most 2,048 characters, "label": a string or absent }. A bare
//   URL string is taken as { "url": … }.
// - Email: a string, x@y.z.
// - Phone: a string as written, at most 32 characters: digits, +, spaces, ( ) - /.
// - User: a string, a PNMT user's id.
// - Host: a string, a host's id. Set to null when the host is deleted.
// null clears a location's own value: an inherited field then shows its nearest ancestor's again (§3.3).

/// <summary>A location as hosts, the dashboard and the search name it (§3.2): its id, its name and its path.</summary>
public sealed record LocationRef(Guid Id, string Name, string Path);

/// <summary>Hosts of a location and its sub-locations (§3.1): all, failing, and those assigned to it directly.</summary>
public sealed record LocationHostCounts(int Total, int Failing, int Own);

/// <summary>
/// <c>GET /locations</c> (§3.6): the whole tree, depth-first, siblings in the order of <c>sort</c>. With <c>search</c>, only
/// the matches, flat and with their paths. <see cref="Fields"/>: the fields not archived, for <i>6 of 9</i>.
/// </summary>
public sealed record LocationList(IReadOnlyList<LocationListItem> Items, int Fields);

/// <summary>
/// A row of the tree: the state is the worst of its and its sub-locations' enabled hosts (Failing, then Ok, then NoData
/// when none reports). <see cref="FieldsFilled"/> counts its own values and inherited ones; <see cref="InheritedFields"/>
/// names the inherited ones' labels.
/// </summary>
public sealed record LocationListItem(
    Guid Id,
    Guid? ParentId,
    string Name,
    string? Description,
    string Path,
    int Depth,
    HostState State,
    LocationHostCounts Hosts,
    int SubLocations,
    int FieldsFilled,
    IReadOnlyList<string> InheritedFields,
    DateTime UpdatedAt,
    string? UpdatedByName);

/// <summary><c>GET /locations</c>. Sort: name (default), state (failing first), updated; "-" first for descending.</summary>
public sealed class LocationQuery
{
    /// <summary>Names, descriptions and the values of Text and Markdown fields, ignoring case.</summary>
    public string? Search { get; set; }

    public string? Sort { get; set; }
}

/// <summary>
/// <c>GET /locations/{id}</c> (§3.4): the breadcrumb, the state and counts with sub-locations, the sub-locations, the hosts
/// as the dashboard's panel shows them (with sub-locations' unless <c>?own=true</c>; each names its location), and every
/// field's value that isn't archived, in the fields' order.
/// </summary>
public sealed record LocationDetail(
    Guid Id,
    Guid? ParentId,
    string Name,
    string? Description,
    string Path,
    IReadOnlyList<Ref> Ancestors,
    int Depth,
    HostState State,
    LocationHostCounts HostCounts,
    IReadOnlyList<LocationChild> SubLocations,
    IReadOnlyList<DashboardHost> Hosts,
    IReadOnlyList<LocationValueItem> Values,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime UpdatedAt,
    string? UpdatedByName);

/// <summary>A direct sub-location, with its own sub-locations' hosts counted.</summary>
public sealed record LocationChild(Guid Id, string Name, string? Description, HostState State, LocationHostCounts Hosts, int SubLocations);

/// <summary>
/// A field's value at a location (§4.1, §3.3). <see cref="Value"/> is what is shown: the location's own, or for an
/// inherited field without one, the nearest ancestor's (<see cref="InheritedFrom"/>). <see cref="Revision"/> is the
/// location's own value's, 0 if it never had one: a save sends it back (409 <c>edit_conflict</c>). <see cref="Target"/>
/// names a User value's user ("former user" once deleted) or a Host value's host.
/// </summary>
public sealed record LocationValueItem(
    Guid FieldId,
    JsonElement? Value,
    Ref? InheritedFrom,
    int Revision,
    DateTime? UpdatedAt,
    string? UpdatedByName,
    Ref? Target);

/// <summary>POST and PUT <c>/locations</c>: a name of 2–64 characters, unique among its siblings regardless of case; one line of description, at most 256 characters; the parent, or null for the top.</summary>
public sealed class LocationRequest
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public Guid? ParentId { get; init; }
}

/// <summary><c>POST /locations/{id}/move</c>: null moves it to the top. 409 <c>cycle</c> under itself or one of its sub-locations, <c>too_deep</c> past 6 levels.</summary>
public sealed class LocationMoveRequest
{
    public Guid? ParentId { get; init; }
}

/// <summary><c>PUT /locations/{id}/values/{fieldId}</c>: the value by the field's type (above), and the revision the edit started from.</summary>
public sealed class LocationValueRequest
{
    public JsonElement? Value { get; init; }

    public int Revision { get; init; }
}

/// <summary>A row of a location's history (§3.5), newest first. Values are JSON by the field's type; the field's label is its current one.</summary>
public sealed record LocationHistoryItem(
    Guid Id,
    DateTime At,
    string ByName,
    LocationHistoryKind Kind,
    Guid? FieldId,
    string? FieldLabel,
    LocationFieldType? FieldType,
    JsonElement? OldValue,
    JsonElement? NewValue,
    string? Detail);

/// <summary>
/// <c>GET /locations/suggestions</c> (§3.2): a text hosts use without a location, as most of them write it, with how many
/// hosts use it (matched ignoring case and whitespace), and the locations already named so.
/// </summary>
public sealed record LocationSuggestion(string Text, int Hosts, IReadOnlyList<LocationRef> Matches);

/// <summary><c>POST /locations/from-text</c>: per text, a location to assign its hosts to; optionally clearing their texts.</summary>
public sealed class LocationFromTextRequest
{
    public IReadOnlyList<LocationFromTextItem> Items { get; init; } = [];

    /// <summary><i>Clear the text on these hosts</i>, off by default.</summary>
    public bool ClearText { get; init; }
}

/// <summary>
/// One text: <see cref="LocationId"/> picks an existing location; otherwise a location named <see cref="Name"/> (the text
/// by default) is created under <see cref="ParentId"/>, or the sibling of that name is used if there is one.
/// </summary>
public sealed class LocationFromTextItem
{
    public string? Text { get; init; }

    public Guid? LocationId { get; init; }

    public string? Name { get; init; }

    public Guid? ParentId { get; init; }
}

/// <summary>What <i>From the hosts' texts…</i> did.</summary>
public sealed record LocationFromTextResult(int LocationsCreated, int HostsAssigned);

/// <summary><c>PUT /hosts/location</c> (§3.2): many hosts' location at once; null: none. The answer is how many changed.</summary>
public sealed class HostLocationRequest
{
    public IReadOnlyList<Guid> Ids { get; init; } = [];

    public Guid? LocationId { get; init; }
}

/// <summary>
/// A field's type options (§4.1, §4.2): <see cref="Choices"/> for Choice and Choices (1–50, each at most 64 characters,
/// unique ignoring case); <see cref="Unit"/> (at most 16 characters), <see cref="Min"/>, <see cref="Max"/> and
/// <see cref="Decimals"/> (0–6) for Number. Other types have none.
/// </summary>
public sealed record LocationFieldOptions
{
    public IReadOnlyList<string>? Choices { get; init; }

    public string? Unit { get; init; }

    public decimal? Min { get; init; }

    public decimal? Max { get; init; }

    public int? Decimals { get; init; }
}

/// <summary>
/// <c>GET /location-fields</c> (§4.2, §10.5): every field in order, archived ones included, and the checklist of
/// <i>Add typical fields…</i> (§4.4), each created through <c>POST /location-fields</c>.
/// </summary>
public sealed record LocationFieldList(IReadOnlyList<LocationFieldItem> Items, IReadOnlyList<LocationFieldTemplate> Typical);

/// <summary>A field's definition (§4.2), and how many locations have a value of their own (<see cref="Filled"/>).</summary>
public sealed record LocationFieldItem(
    Guid Id,
    string Key,
    string Label,
    LocationFieldType Type,
    string? Help,
    string? Group,
    int Position,
    bool Overview,
    bool Inherited,
    bool Sensitive,
    LocationFieldOptions? Options,
    DateTime? ArchivedAt,
    DateTime CreatedAt,
    int Filled);

/// <summary>A typical field (§4.4); <see cref="Exists"/> once a field has its key.</summary>
public sealed record LocationFieldTemplate(
    string Key,
    string Label,
    LocationFieldType Type,
    bool Overview,
    bool Inherited,
    bool Sensitive,
    string? Help,
    bool Exists);

/// <summary>
/// POST and PUT <c>/location-fields</c> (§4.2): a label of 1–64 characters; help at most 256, a group at most 64. The key
/// is made from the first label and never changes. The type changes only while no location has a value, or along Text →
/// Markdown, Choice → Choices and Choice → Text (409 <c>type_change</c>). A choice still used can't be removed (409
/// <c>choice_in_use</c>, naming the locations); <see cref="Renames"/> renames one in every value.
/// </summary>
public sealed class LocationFieldRequest
{
    public string? Label { get; init; }

    public LocationFieldType Type { get; init; }

    public string? Help { get; init; }

    public string? Group { get; init; }

    public bool Overview { get; init; }

    public bool Inherited { get; init; }

    public bool Sensitive { get; init; }

    public LocationFieldOptions? Options { get; init; }

    public IReadOnlyList<LocationChoiceRename>? Renames { get; init; }
}

/// <summary>A choice renamed: <see cref="From"/> was one of the field's choices, <see cref="To"/> is one of the new ones.</summary>
public sealed class LocationChoiceRename
{
    public string? From { get; init; }

    public string? To { get; init; }
}

/// <summary><c>PUT /location-fields/order</c>: the fields in their new order; fields not named follow, in their order.</summary>
public sealed class LocationFieldOrderRequest
{
    public IReadOnlyList<Guid> Ids { get; init; } = [];
}
