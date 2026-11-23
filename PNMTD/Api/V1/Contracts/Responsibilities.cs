namespace PNMTD.Api.V1.Contracts;

// Responsibilities (docs/responsibilities-plan.md §11): who owns an area of work, with a deputy, its links, the global
// responsible and deputy. Information only: nothing is sent to these people. Not the availability plan's responsible of the day.

/// <summary>A person a responsibility names: <see cref="Active"/> false once the account is deactivated.</summary>
public sealed record ResponsibilityPerson(Guid Id, string Name, bool Active);

/// <summary>
/// The global responsible and deputy (§5): they own what no responsibility covers, and stand in for a responsibility without an
/// active responsible or deputy.
/// </summary>
public sealed record ResponsibilityGlobal(ResponsibilityPerson? Responsible, ResponsibilityPerson? Deputy, DateTime UpdatedAt, string? UpdatedByName);

/// <summary>
/// A row of the list (§9.2). <see cref="GlobalStandsIn"/>: neither its responsible nor its deputy is set and active.
/// <see cref="Attention"/>: nobody set, or a person inactive.
/// </summary>
public sealed record ResponsibilityItem(
    Guid Id,
    string Name,
    string? Summary,
    ResponsibilityPerson? Responsible,
    ResponsibilityPerson? Deputy,
    bool GlobalStandsIn,
    bool Attention,
    int Links,
    DateTime UpdatedAt);

/// <summary>The page's list, with the global people on top (§9.2).</summary>
public sealed record ResponsibilityList(ResponsibilityGlobal Global, IReadOnlyList<ResponsibilityItem> Items);

/// <summary>A responsibility's link: the object as a page's links show it (<see cref="KbLinkItem"/>), and the link's own id.</summary>
public sealed record ResponsibilityLinkItem(Guid LinkId, KbLinkKind Kind, Guid Id, string Name, string? Detail, bool Exists, int? Number, bool Archived);

/// <summary>
/// A responsibility in full (§9.3). <see cref="Revision"/> is what a save of the description sends back; <see cref="Files"/>
/// are those its description references, for attachment chips.
/// </summary>
public sealed record ResponsibilityDetail(
    Guid Id,
    string Name,
    string? Summary,
    string Description,
    int Revision,
    ResponsibilityPerson? Responsible,
    ResponsibilityPerson? Deputy,
    bool GlobalStandsIn,
    bool Attention,
    ResponsibilityGlobal Global,
    IReadOnlyList<ResponsibilityLinkItem> Links,
    IReadOnlyList<FileItem> Files,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime UpdatedAt,
    string? UpdatedByName);

/// <summary>
/// A responsibility as a linked host, location or page shows it (§8): only when one links it, and only with
/// <i>Responsibilities: View</i>.
/// </summary>
public sealed record ResponsibilityRef(Guid Id, string Name, string? Summary, ResponsibilityPerson? Responsible, ResponsibilityPerson? Deputy,
    bool GlobalStandsIn, ResponsibilityPerson? GlobalResponsible, ResponsibilityPerson? GlobalDeputy);

/// <summary>Create and change (§11): the whole form. A null person means nobody.</summary>
public sealed class ResponsibilityRequest
{
    public string? Name { get; init; }

    public string? Summary { get; init; }

    public Guid? ResponsibleId { get; init; }

    public Guid? DeputyId { get; init; }
}

/// <summary>
/// The long description (§4.1): the Markdown, the revision it started from (409 <c>edit_conflict</c>), and whether lines that
/// look like credentials were acknowledged (422 <c>looks_like_credential</c>).
/// </summary>
public sealed class ResponsibilityDescriptionRequest
{
    public string? Content { get; init; }

    public int Revision { get; init; }

    public bool AcknowledgeCredentials { get; init; }
}

/// <summary>The global responsible and deputy: both, a null meaning nobody.</summary>
public sealed class ResponsibilityGlobalRequest
{
    public Guid? ResponsibleId { get; init; }

    public Guid? DeputyId { get; init; }
}

/// <summary>A link to a page, a location or a host (§4.1); a sensor is refused.</summary>
public sealed class ResponsibilityLinkRequest
{
    public KbLinkKind? Kind { get; init; }

    public Guid? Id { get; init; }
}

/// <summary>Whom a responsibility can name: the active users, with whether a Redmine user is linked (never contact details).</summary>
public sealed record ResponsibilityPeople(IReadOnlyList<ResponsibilityPersonOption> Users);

public sealed record ResponsibilityPersonOption(Guid Id, string Name, string Username, bool RedmineLinked);

/// <summary>A history row (§10.5); a description's texts come with <c>GET …/history/{rowId}</c>.</summary>
public sealed record ResponsibilityHistoryItem(Guid Id, DateTime At, string ByName, Models.Db.ResponsibilityHistoryKind Kind, string? Detail);

/// <summary>A history row with the description before and after it.</summary>
public sealed record ResponsibilityHistoryDetail(Guid Id, DateTime At, string ByName, Models.Db.ResponsibilityHistoryKind Kind, string? Detail,
    string? OldValue, string? NewValue);

/// <summary>The error codes of responsibilities (§11).</summary>
public static class ResponsibilityErrors
{
    public const string DuplicateName = "duplicate_name";
    public const string AlreadyLinked = "already_linked";
    public const string TooManyLinks = "too_many_links";
    public const string DuplicateCombination = "duplicate_combination";
    public const string RedmineNotConfigured = "redmine_not_configured";
}
