namespace PNMTD.Models.Enums;

/// <summary>The Setup tab's steps (docs/mikrotik-plan.md §4.3): waiting for the router → tunnel up → logged in (ready).</summary>
public enum MikrotikSetupState : short
{
    AwaitingRouter = 0,
    TunnelUp = 1,
    Ready = 2,
    /// <summary>Logged in once, now refused (441): a changed host key, the key or the group gone.</summary>
    NeedsAttention = 3,
}

public enum MikrotikBackupKind : short
{
    Export = 0,
    Binary = 1,
}

/// <summary>Why a backup was taken (§6.1).</summary>
public enum MikrotikBackupTrigger : short
{
    FirstLogin = 0,
    Schedule = 1,
    Manual = 2,
    BeforeUpdate = 3,
    BeforeCommand = 4,
    AfterCommand = 5,
}
