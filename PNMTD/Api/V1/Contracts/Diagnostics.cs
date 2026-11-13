using PNMTD.Lib.Agent;

namespace PNMTD.Api.V1.Contracts;

// Diagnostics (docs/windows-sensor-plan.md §5, §9.2).

/// <summary>A check: a name, a type, and the parameters of its type (the other is null).</summary>
public sealed record DiagnosticCheckRequest(string Name, DiagnosticCheckType Type, PingParameters? Ping, DnsParameters? Dns);

/// <summary>A set with its checks, in order: at most 50.</summary>
public sealed record DiagnosticSetRequest(string Name, string? Description, IReadOnlyList<DiagnosticCheckRequest> Checks);

public sealed record DiagnosticCheckItem(Guid Id, int Order, string Name, DiagnosticCheckType Type, PingParameters? Ping, DnsParameters? Dns);

public sealed record DiagnosticSetItem(Guid Id, string Name, string? Description, int Checks, int Assignments, DateTime UpdatedAt);

public sealed record DiagnosticSetDetail(Guid Id, string Name, string? Description, IReadOnlyList<DiagnosticCheckItem> Checks, int Assignments,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Assigns a set to one or more servers: each assignment gets a DIAGNOSTICS sensor of its own (§5.2).</summary>
public sealed record DiagnosticAssignmentRequest(Guid SetId, IReadOnlyList<Guid> TargetSensorIds, DiagnosticMode Mode, int IntervalMinutes, bool Enabled);

public sealed record DiagnosticAssignmentUpdate(DiagnosticMode Mode, int IntervalMinutes, bool Enabled);

public sealed record DiagnosticAssignmentItem(
    Guid Id,
    Ref Set,
    Ref Target,
    Ref Sensor,
    DiagnosticMode Mode,
    int IntervalMinutes,
    bool Enabled,
    SensorState State,
    bool AgentConnected,
    DiagnosticRunItem? LastRun);

public sealed class DiagnosticAssignmentQuery : PageQuery
{
    public Guid? TargetSensorId { get; set; }

    public Guid? SetId { get; set; }
}

public sealed record DiagnosticRunItem(
    Guid Id,
    Guid AssignmentId,
    string SetName,
    Ref Target,
    string Trigger,
    string? RequestedBy,
    DateTime StartedAt,
    DateTime? FinishedAt,
    DiagnosticOutcome? Outcome,
    string? Summary);

/// <summary>A run with each check's result: pass, fail or error, the values, and a message.</summary>
public sealed record DiagnosticRunDetail(DiagnosticRunItem Run, IReadOnlyList<DiagnosticCheckResult> Results);

public sealed class DiagnosticRunQuery : PageQuery
{
    public Guid? AssignmentId { get; set; }

    public Guid? SetId { get; set; }

    public Guid? TargetSensorId { get; set; }

    public DiagnosticOutcome? Outcome { get; set; }
}
