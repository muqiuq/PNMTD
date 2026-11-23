using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Audit;

namespace PNMTD.Models.Db;

/// <summary>
/// What came of a comment on a ticket whose assignee is away (docs/cookie-redmine-tab-plan.md §6): waiting for Cookie's verdict or
/// withheld by the screen; waiting to be told (for working hours, or the ticket's 4 hours); told, or told within another comment's
/// notice; and the ends: not important, only recorded, nobody to tell, the assignee back, expired, switched off.
/// </summary>
public enum AwayCommentState { Pending, Withheld, Waiting, Notified, Joined, NotImportant, Recorded, Nobody, Back, Expired, Off }

/// <summary>
/// A comment while the ticket's assignee is away (docs/cookie-redmine-tab-plan.md §6.3, §7): one row per Redmine journal, the case
/// book's; the verdict (Cookie's, or none) and whom it told. The comment's text goes after 30 days; the verdict stays.
/// </summary>
[Table("cookie_away_comments")]
[NotAudited("the away watch's book, kept by code; its settings and the links it relies on are audited")]
public class CookieAwayCommentEntity
{
    public long Id { get; set; }

    public Guid CaseId { get; set; }

    public int IssueId { get; set; }

    /// <summary>The Redmine journal: each is judged once.</summary>
    public int JournalId { get; set; }

    /// <summary>The away assignee's PNMT user.</summary>
    public Guid AssigneeUserId { get; set; }

    public int? AuthorRedmineId { get; set; }

    [MaxLength(200)]
    public string? AuthorName { get; set; }

    public bool IsPrivate { get; set; }

    public DateTime CommentAt { get; set; }

    /// <summary>The comment as Redmine has it, cut to 4 000 characters; removed after 30 days.</summary>
    [MaxLength(4000)]
    public string? Text { get; set; }

    /// <summary>The last day of the absence, as the plan had it when the comment came; null when it goes on beyond 60 days.</summary>
    public DateOnly? AwayUntil { get; set; }

    public AwayCommentState State { get; set; }

    /// <summary>high, normal or low: Cookie's verdict; null while nobody judged it.</summary>
    [MaxLength(8)]
    public string? Importance { get; set; }

    public double? Confidence { get; set; }

    [MaxLength(300)]
    public string? Reason { get; set; }

    /// <summary>cookie, or code when nobody could judge (stopped, withheld, too late).</summary>
    [MaxLength(16)]
    public string? JudgedBy { get; set; }

    public DateTime? JudgedAt { get; set; }

    /// <summary>Cookie's decision, when it proposed the notice.</summary>
    public long? DecisionId { get; set; }

    public DateTime? NotifiedAt { get; set; }

    /// <summary>Whom the notice reached: their names, comma-separated.</summary>
    [MaxLength(500)]
    public string? Recipients { get; set; }

    /// <summary>What happened, in a line: who was told and how, what held it back, why nobody.</summary>
    [MaxLength(500)]
    public string? Detail { get; set; }

    public DateTime CreatedAt { get; set; }
}
