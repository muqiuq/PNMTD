namespace PNMTD.Models.Db;

/// <summary>What a mail rule does with a matching mail (docs/ai-operations-plan.md §6.4).</summary>
public enum MailRuleAction : short
{
    /// <summary>An event on the rule's sensor, as before.</summary>
    Event = 0,

    /// <summary>Every matching mail goes to the AI, which says OK, failure or unknown.</summary>
    AskAi = 1,

    /// <summary>Processed and archived, without an event; only when no event or AI rule matched.</summary>
    Ignore = 2,

    /// <summary>Processed and deleted from the mailbox; only when no event or AI rule matched.</summary>
    Delete = 3,
}

/// <summary>What became of a mail (§6.5).</summary>
public enum MailDisposition : short
{
    None = 0,
    Event = 1,
    AiEvent = 2,
    Ignored = 3,
    Deleted = 4,
}

/// <summary>The AI's part in a mail (§6.5).</summary>
public enum MailAiState : short
{
    None = 0,
    Queued = 1,
    Analysed = 2,
    Blocked = 3,
    Failed = 4,
}
