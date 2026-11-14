using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Auth.Rights;

namespace PNMTD.Models.Db;

/// <summary>
/// A group (docs/audit-redmine-zabbix-plan.md §5.1): a level per area for its members. A user's level in an area is the
/// highest any of their groups gives. The update creates Operators and Viewers once, as ordinary groups.
/// </summary>
[Table("groups")]
public class GroupEntity
{
    /// <summary>The groups the update made from the roles (§5.3). Nothing in the code relies on their names.</summary>
    public static readonly Guid Operators = new("6a7b0000-0000-4000-8000-000000000001");

    public static readonly Guid Viewers = new("6a7b0000-0000-4000-8000-000000000002");

    [Key]
    public Guid Id { get; set; }

    [MaxLength(100)]
    public required string Name { get; set; }

    /// <summary>Lower-case invariant, unique.</summary>
    [MaxLength(100)]
    public required string NormalizedName { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<GroupRightEntity> Rights { get; set; } = [];

    public List<UserGroupEntity> Members { get; set; } = [];
}

/// <summary>A group's level in one area; an area without a row is None.</summary>
[Table("group_rights")]
public class GroupRightEntity
{
    public Guid GroupId { get; set; }

    public Area Area { get; set; }

    public Level Level { get; set; }
}

/// <summary>A member of a group.</summary>
[Table("user_groups")]
public class UserGroupEntity
{
    public Guid UserId { get; set; }

    public Guid GroupId { get; set; }

    public GroupEntity Group { get; set; } = null!;
}
