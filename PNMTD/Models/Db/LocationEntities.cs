using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace PNMTD.Models.Db;

// Locations (docs/locations-knowledge-plan.md §3, §4, §8.2): a location and its parent, the fields an Admin defines, their
// values per location, and each location's history. A host points to its location (HostEntity.LocationId). Authors are a
// user id plus their name at the time, with no foreign key (§8.2). Migration AddLocations (K2).

/// <summary>
/// A location (§3.1): a site, a building, a room, a cage. It may have a parent, at most 6 levels deep and without cycles
/// (§3.3); LocationTree checks both, the database only refuses to delete a parent. Its path and state are never stored.
/// </summary>
[Table("locations")]
public class LocationEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Null: a top-level location. Deleting a location that still has sub-locations is refused (RESTRICT).</summary>
    public Guid? ParentId { get; set; }

    public LocationEntity? Parent { get; set; }

    /// <summary>2–64 characters, as entered.</summary>
    [MaxLength(64)]
    public string Name { get; set; } = "";

    /// <summary>The name compared, regardless of case: unique among siblings, at the top level too (NULLS NOT DISTINCT).</summary>
    [MaxLength(64)]
    public string NormalizedName { get; set; } = "";

    /// <summary>One line, shown under the name.</summary>
    [MaxLength(256)]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    [MaxLength(200)]
    public string? CreatedByName { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [MaxLength(200)]
    public string? UpdatedByName { get; set; }
}

/// <summary>A location field's type (§4.1). Stored as its number: append, never reorder.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocationFieldType>))]
public enum LocationFieldType
{
    Markdown = 0,
    Text = 1,
    Number = 2,
    YesNo = 3,
    Date = 4,
    Choice = 5,
    Choices = 6,
    Link = 7,
    Email = 8,
    Phone = 9,
    User = 10,
    Host = 11,
}

/// <summary>
/// A field every location can fill (§4.2), defined by an Admin. One set for all locations; defining one needs no migration.
/// The migration creates <see cref="OverviewId"/> (§4.4).
/// </summary>
[Table("location_fields")]
public class LocationFieldEntity
{
    /// <summary>The field AddLocations creates: <i>Overview</i>, Markdown, on the overview, not inherited (§4.4).</summary>
    public static readonly Guid OverviewId = new("6f1c0d7e-2b4a-4c8e-9a35-0e7b5d1f4a01");

    public const string OverviewKey = "overview";

    [Key]
    public Guid Id { get; set; }

    /// <summary>A slug of the first label (<c>on-site-contact</c>), unique, never changed: for exports and later for MCP.</summary>
    [MaxLength(64)]
    public string Key { get; set; } = "";

    [MaxLength(64)]
    public string Label { get; set; } = "";

    public LocationFieldType Type { get; set; }

    /// <summary>Shown under the field while editing.</summary>
    [MaxLength(256)]
    public string? Help { get; set; }

    /// <summary>Groups the <i>Information</i> tab (<i>Access</i>, <i>Network</i>, <i>Contacts</i>).</summary>
    [MaxLength(64)]
    public string? Group { get; set; }

    public int Position { get; set; }

    /// <summary>Shown on the right side of the location's page (R3).</summary>
    public bool Overview { get; set; }

    /// <summary>Sub-locations without a value of their own show their nearest ancestor's (§3.3).</summary>
    public bool Inherited { get; set; }

    /// <summary>Its values never reach an AI or MCP (§7.4).</summary>
    public bool Sensitive { get; set; }

    /// <summary>The type's options as JSON: the choices, unit, minimum, maximum and decimals.</summary>
    [Column(TypeName = "jsonb")]
    public string? Options { get; set; }

    /// <summary>Archived: hidden everywhere, its values kept (§4.3). Only an archived field can be deleted.</summary>
    public DateTime? ArchivedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A location's value of one field (§2.3, §4.1): a typed JSON value, with its own revision for the conflict check (409
/// <c>edit_conflict</c>). Goes with its location and with its field.
/// </summary>
[Table("location_values")]
public class LocationValueEntity
{
    public Guid LocationId { get; set; }

    public Guid FieldId { get; set; }

    public LocationFieldEntity? Field { get; set; }

    /// <summary>The value as JSON, by the field's type (LocationValues).</summary>
    [Column(TypeName = "jsonb")]
    public string? Value { get; set; }

    /// <summary>Raised by every save; a save carries the revision it started from.</summary>
    public int Revision { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }

    [MaxLength(200)]
    public string? UpdatedByName { get; set; }
}

/// <summary>What a location's history row records (§3.5). Stored as its number: append, never reorder.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocationHistoryKind>))]
public enum LocationHistoryKind
{
    Created = 0,
    Renamed = 1,
    DescriptionChanged = 2,
    Moved = 3,
    /// <summary>A field's value, with the old and the new; restoring an old value is one too.</summary>
    ValueChanged = 4,
    HostsAdded = 5,
    HostsRemoved = 6,
}

/// <summary>
/// One change to a location (§3.5), newest first. A field's rows go with the field when an Admin deletes it (§4.3).
/// </summary>
[Table("location_history")]
public class LocationHistoryEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid LocationId { get; set; }

    public DateTime At { get; set; }

    public Guid? ByUserId { get; set; }

    [MaxLength(200)]
    public string ByName { get; set; } = "";

    public LocationHistoryKind Kind { get; set; }

    /// <summary>For <see cref="LocationHistoryKind.ValueChanged"/>.</summary>
    public Guid? FieldId { get; set; }

    [Column(TypeName = "jsonb")]
    public string? OldValue { get; set; }

    [Column(TypeName = "jsonb")]
    public string? NewValue { get; set; }

    /// <summary>What else the row says: the old and new name or path, the hosts added or removed.</summary>
    [MaxLength(2000)]
    public string? Detail { get; set; }
}
