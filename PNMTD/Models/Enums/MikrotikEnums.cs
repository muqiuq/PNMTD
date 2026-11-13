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
    AfterUpdate = 6,
}

/// <summary>Whose tunnel a WireGuard peer is (docs/mikrotik-plan.md §9.2).</summary>
public enum WgPeerKind : short
{
    Router = 0,
    Custom = 1,
}

public enum MikrotikJobKind : short
{
    Update = 0,
    Command = 1,
}

/// <summary>Who a remote command runs as on the router (§8.1): PNMT's read-only user, or an admin with the password typed for the run.</summary>
public enum MikrotikRunAs
{
    Pnmt = 0,
    Admin = 1,
}

public enum MikrotikJobState : short
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Skipped = 3,
    Failed = 4,
    Cancelled = 5,

    /// <summary>A command that did not end within its timeout (§8.2); its output so far is kept.</summary>
    TimedOut = 6,
}

/// <summary>An update's steps (docs/mikrotik-plan.md §7.3), and a command's (§8.2).</summary>
public enum MikrotikJobStep : short
{
    Check = 0,
    Export = 1,
    BinaryBackup = 2,
    Download = 3,
    Reboot = 4,
    Wait = 5,
    Verify = 6,
    Firmware = 7,
    Done = 8,
    Run = 9,
    ExportAfter = 10,
}

public enum MikrotikJobOutcome : short
{
    Started = 0,
    Ok = 1,
    Failed = 2,
    Info = 3,
}
