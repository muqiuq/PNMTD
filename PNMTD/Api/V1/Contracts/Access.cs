using PNMTD.Cookie.Access;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// The access watch (docs/cookie-access-plan.md §10.3).

public sealed record AccessSignalItem(string Key, SignalStrength Strength, string Text);

/// <summary>A reviewed login: what code read, the signals, the verdict, and <i>Was this you?</i>.</summary>
public sealed record AccessReviewItem(
    Guid Id,
    AccessTargetKind Target,
    Guid? SensorId,
    string TargetName,
    DateTime At,
    string Account,
    bool IsAdmin,
    string Way,
    string Group,
    string? Source,
    bool PublicSource,
    string? Device,
    string? KeyFingerprint,
    string? TargetUser,
    IReadOnlyList<AccessSignalItem> Signals,
    bool Strong,
    bool Late,
    bool Flagged,
    AccessReviewState State,
    double? Confidence,
    string? Reason,
    string? JudgedBy,
    DateTime? JudgedAt,
    string Text,
    string? Person,
    DateTime? AskedAt,
    string? PersonAnswer,
    DateTime? PersonAnsweredAt);

/// <summary>What a person can do about it (§7), as links into PNMT; Cookie never does any of them.</summary>
public sealed record AccessLinkItem(string Label, string Path);

/// <summary>An access case's own part (§10.4): its logins, its verdict and answer, the notices, and what the reader may do.</summary>
public sealed record AccessCaseDetail(
    Guid CaseId,
    AccessTargetKind Target,
    string TargetName,
    Guid? SensorId,
    string? Verdict,
    string? Answer,
    DateTime? AnsweredAt,
    string? AnsweredBy,
    string? NotifiedVerdict,
    DateTime? NotifiedAt,
    string? PendingVerdict,
    DateTime? TicketAskedAt,
    IReadOnlyList<AccessReviewItem> Logins,
    IReadOnlyList<AccessLinkItem> Links,
    IReadOnlyList<AccessNoticeItem> Notices,
    bool CanAnswer,
    bool CanAskTicket);

/// <summary>A notice of the case as the outbox kept it: to whom, how, and what came of it.</summary>
public sealed record AccessNoticeItem(DateTime At, CookieChannel Channel, string Recipient, string Subject, CookieOutboxState State, string? Detail, string Source);

/// <summary><i>Remember this</i> (§7): a known access entry for this account, its network or any source, its way or any.</summary>
public sealed record AccessRememberRequest(string Scope, bool AnySource, bool AnyWay, int? Days);

/// <summary><i>Expected</i> or <i>Not expected</i> (§7).</summary>
public sealed record AccessAnswerRequest(bool Expected, string? Comment, AccessRememberRequest? Remember);

/// <summary><i>This wasn't expected</i> on a login of the target's <i>Access</i> tab (§7).</summary>
public sealed record AccessNotExpectedRequest(string? Comment);

public sealed record AccessKnownItem(
    Guid Id,
    string Scope,
    AccessTargetKind? Target,
    Guid? SensorId,
    Guid? LocationId,
    string? Account,
    string? Source,
    IReadOnlyList<string> Groups,
    DateTime? Until,
    string Comment,
    string Label,
    DateTime CreatedAt,
    string CreatedByName,
    Guid? FromCaseId,
    DateTime? EndedAt,
    string? EndedByName);

public sealed record AccessKnownRequest(
    string Scope,
    AccessTargetKind? Target,
    Guid? SensorId,
    Guid? LocationId,
    string? Account,
    string? Source,
    IReadOnlyList<string>? Groups,
    DateTime? Until,
    string Comment);

/// <summary>A router and its switch (§4.2).</summary>
public sealed record AccessRouterItem(Guid SensorId, string Name, string HostName, Guid? LocationId, string? LocationName, bool Watch, DateTime? FirstReadAt,
    bool Gap);

public sealed record AccessRoutersRequest(IReadOnlyList<Guid> SensorIds, bool Watch);

public sealed record AccessWatchRequest(bool Watch);

/// <summary>A contact the tab warns about (§8): who, and what they lack.</summary>
public sealed record AccessContactWarning(string Name, string Problem);

/// <summary>Admin → Cookie → Access's warnings and counts (§8).</summary>
public sealed record AccessAdminState(
    bool CookieReadsLogins,
    bool CookieReadsSignIns,
    bool PublicBaseUrl,
    bool NightContact,
    bool NoticeAuto,
    bool PnmtOneAddress,
    int RoutersNotWatched,
    IReadOnlyList<string> RoutersWithGap,
    IReadOnlyList<AccessContactWarning> Contacts,
    int OpenCases,
    int ReviewsToday,
    int NoticesToday);

/// <summary>A router's <i>Access</i> tab (§10.4): its switch, its logins, its users, who is logged in now, and the gap.</summary>
public sealed record RouterAccessView(
    bool Watch,
    bool CanSwitch,
    DateTime? FirstReadAt,
    bool Gap,
    int Unmatched,
    string? PnmtAddress,
    IReadOnlyList<RouterUserItem> Users,
    IReadOnlyList<RouterActiveUser> Active,
    Paged<AgentLoginItem> Logins);

/// <summary>A login's review, on the target's <i>Access</i> tab: its verdict and case.</summary>
public sealed record AccessReviewRef(Guid ReviewId, Guid? CaseId, AccessReviewState State);

/// <summary>The <i>Was this you?</i> page's answer (§6.6).</summary>
public sealed record WasThisYouRequest(bool ItWasMe);

public sealed record WasThisYouResult(string Message);
