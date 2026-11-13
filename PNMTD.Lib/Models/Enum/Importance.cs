namespace PNMTD.Lib.Models.Enum;

/// <summary>
/// How much a host or sensor matters (docs/ai-operations-plan.md §11.1). A sensor without its own
/// takes its host's. Notification scopes cover sensors of at least an importance, and Pushover's
/// priority follows it.
/// </summary>
public enum Importance : short
{
    Low = 0,
    Normal = 1,
    High = 2,
}
