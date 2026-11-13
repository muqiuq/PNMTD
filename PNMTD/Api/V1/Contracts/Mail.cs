using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// A mail input rule: mail whose sender (and subject and body, when set) match these regular
/// expressions becomes an event on the target sensor, with OkCode, FailCode or DefaultCode.
/// ExtractMessageRegex's group <c>msg</c> becomes part of the event's message.
/// </summary>
public sealed record MailInputRule(
    Guid Id,
    string Name,
    bool Enabled,
    string? FromTest,
    string? SubjectTest,
    string? BodyTest,
    string? OkTest,
    int? OkCode,
    string? FailTest,
    int? FailCode,
    int DefaultCode,
    string? ExtractMessageRegex,
    SensorRef? Sensor,
    MailRuleAction Action = MailRuleAction.Event,
    bool AskAiWhenUnsure = false,
    bool VerifyAi = true);

public sealed record SensorRef(Guid Id, string Name, Guid HostId, string HostName);

/// <summary>POST and PUT <c>/mail-input-rules</c>. FromTest is required; every test must be a valid regular expression.</summary>
public sealed class MailInputRuleRequest
{
    public string? Name { get; init; }

    public bool Enabled { get; init; } = true;

    public string? FromTest { get; init; }

    public string? SubjectTest { get; init; }

    public string? BodyTest { get; init; }

    public string? OkTest { get; init; }

    public int? OkCode { get; init; }

    public string? FailTest { get; init; }

    public int? FailCode { get; init; }

    public int DefaultCode { get; init; } = 200;

    public string? ExtractMessageRegex { get; init; }

    public Guid? SensorId { get; init; }

    /// <summary>Event (default), AskAi (every matching mail to the AI), Ignore or Delete (docs/ai-operations-plan.md §6.4).</summary>
    public MailRuleAction Action { get; init; } = MailRuleAction.Event;

    public bool AskAiWhenUnsure { get; init; }

    public bool VerifyAi { get; init; } = true;
}

/// <summary>A mail the mailbox task read. Not visible to Viewers.</summary>
public sealed record MailLogItem(
    Guid Id,
    DateTime Created,
    DateTime? MessageDate,
    string From,
    string To,
    string Subject,
    bool Processed,
    Ref? ProcessedBy,
    MailDisposition Disposition = MailDisposition.None,
    MailAiState AiState = MailAiState.None,
    Guid? DecisionId = null);

public sealed record MailLogDetail(
    Guid Id,
    DateTime Created,
    DateTime? MessageDate,
    string From,
    string To,
    string Subject,
    bool Processed,
    Ref? ProcessedBy,
    string Content,
    string ProcessLog,
    MailDisposition Disposition = MailDisposition.None,
    MailAiState AiState = MailAiState.None,
    Guid? DecisionId = null);

/// <summary><c>GET /mail-log</c>: newest first.</summary>
public sealed class MailLogQuery : PageQuery
{
    public bool? Processed { get; set; }

    public MailAiState? AiState { get; set; }
}
