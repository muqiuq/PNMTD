using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

/// <summary>A decision in the list (docs/ai-operations-plan.md §5.3).</summary>
public sealed record DecisionItem(
    Guid Id,
    string Kind,
    DecisionState State,
    string Title,
    string Summary,
    int Occurrences,
    bool Disputed,
    bool AdminOnly,
    DateTime CreatedAt,
    DateTime LastSeenAt,
    DateTime? ExpiresAt,
    DateTime? DecidedAt,
    string? DecidedByName,
    string? Choice);

/// <summary>
/// A decision with its options and evidence. By kind: <c>MailRule</c> (a proposed rule) and <c>Mail</c> for the mail
/// kinds, <c>Finding</c> for the analysis; <c>Ai</c> for every decision the AI raised.
/// </summary>
public sealed record DecisionDetail(
    DecisionItem Item,
    IReadOnlyList<DecisionOption> Options,
    MailRuleProposal? MailRule,
    MailEvidence? Mail,
    AiEvidence? Ai,
    FindingEvidence? Finding,
    IReadOnlyList<Guid> Subjects,
    string? Result,
    string? Error);

/// <summary>An option: <c>Edit</c> opens a form first; <c>Enabled</c> false explains itself in the description.</summary>
public sealed record DecisionOption(string Id, string Label, string Description, bool Edit, bool AdminOnly, bool Enabled);

/// <summary>
/// A rule the AI proposed (§6.3): the expressions code built from its template and phrases, the sensor, and what the
/// rule would match in the last mails. <c>ExistingRuleId</c>: an unsure rule whose tests it improves (trigger B).
/// </summary>
public sealed record MailRuleProposal(
    string Action,
    string Name,
    string FromTest,
    string? SubjectTest,
    string? OkTest,
    string? FailTest,
    string? ExtractMessageRegex,
    string DefaultOutcome,
    Guid? SensorId,
    string? SensorName,
    string? HostName,
    string? NewSensorName,
    string Kind,
    double Confidence,
    string Summary,
    string Reasoning,
    RulePreview Preview,
    Guid? ExistingRuleId,
    string? ExistingRuleName);

/// <summary>The proposed rule against the last mails: how many it matches, from how many other senders, and examples.</summary>
public sealed record RulePreview(int Checked, int Matches, int OtherSenders, bool TooBroad, IReadOnlyList<string> Examples);

/// <summary>The mail a decision is about (it was untrusted input: shown escaped).</summary>
public sealed record MailEvidence(Guid MailId, string From, string To, string Subject, string Body, DateTime? Date);

/// <summary>What the layers said (§3.4): the guard's probability, the scan's flags, the verifier's verdict.</summary>
public sealed record AiEvidence(double? Injection, string? BlockedBy, IReadOnlyList<string> Flags, string? Verdict, IReadOnlyList<string> Issues,
    IReadOnlyList<long> RequestIds);

/// <summary>An analysis finding (§9.3), with the real names mapped back from the pseudonyms.</summary>
public sealed record FindingEvidence(string Key, string Kind, string Severity, string Explanation, string Recommendation, double Confidence,
    IReadOnlyList<Ref> Sensors, IReadOnlyList<Ref> Hosts, bool Verified);

/// <summary><c>GET /decisions</c>: open first by default.</summary>
public sealed class DecisionQuery : PageQuery
{
    public DecisionState? State { get; set; }

    public string? Kind { get; set; }
}

/// <summary><c>POST /decisions/{id}/decide</c>: the option, and for an editing option the edited rule.</summary>
public sealed class DecideRequest
{
    public string? Choice { get; init; }

    public MailInputRuleRequest? Rule { get; init; }
}

public sealed record DecisionCount(int Open);
