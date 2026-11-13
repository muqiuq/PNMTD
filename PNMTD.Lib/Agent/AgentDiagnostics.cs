using System.Text.Json.Serialization;

namespace PNMTD.Lib.Agent;

// Diagnostics (docs/windows-sensor-plan.md §5): sets of typed checks, assigned to an agent, run on
// a schedule or by hand. Nothing here is Windows-specific (§5.5): any agent that announces the
// check types (diag.ping, diag.dns) can run them. A check's type is an enum and its parameters are
// typed, so a set can't be turned into a command (§6).

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticCheckType>))]
public enum DiagnosticCheckType { Ping, Dns }

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticMode>))]
public enum DiagnosticMode { Scheduled, Manual }

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticOutcome>))]
public enum DiagnosticOutcome { Pass, Fail, Error }

[JsonConverter(typeof(JsonStringEnumConverter<IpFamily>))]
public enum IpFamily { Any, IPv4, IPv6 }

/// <summary>DNS record types a check may ask for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DnsRecordKind>))]
public enum DnsRecordKind { A, AAAA, CNAME, MX, TXT, SRV, PTR, NS, SOA }

/// <summary>
/// <c>ping</c>: passes when the loss is at most <see cref="MaxLossPercent"/> and, when set, the average round trip at most
/// <see cref="MaxAvgMs"/>. Capped on PNMTD: at most 10 pings, at most 5 s each (§5.1).
/// </summary>
public sealed record PingParameters(string Target, int Count = 4, int TimeoutMs = 1000, IpFamily Family = IpFamily.Any, int MaxLossPercent = 0, int? MaxAvgMs = null);

/// <summary>
/// <c>dns</c>: passes when the name resolves and, when set, an answer equals or contains <see cref="Expect"/>, and the
/// time is at most <see cref="MaxMs"/>. Without a server, the system's resolvers.
/// </summary>
public sealed record DnsParameters(string Name, DnsRecordKind RecordType = DnsRecordKind.A, string? Server = null, string? Expect = null, int? MaxMs = null);

/// <summary>A check of a set, as the agent receives it.</summary>
public sealed record DiagnosticCheck(Guid Id, string Name, DiagnosticCheckType Type, PingParameters? Ping = null, DnsParameters? Dns = null);

/// <summary>An assignment in the agent's configuration: a set, its schedule and its checks.</summary>
public sealed record DiagnosticsAssignment(Guid AssignmentId, Guid SetId, string SetName, DiagnosticMode Mode, int IntervalMinutes, bool Enabled,
    IReadOnlyList<DiagnosticCheck> Checks);

/// <summary>A ping's values: sent, received, loss, and the round trips.</summary>
public sealed record PingValues(int Sent, int Received, int LossPercent, double? MinMs, double? AvgMs, double? MaxMs, string? Address);

/// <summary>A DNS lookup's values: the answers, the resolver asked, the time, and the response code.</summary>
public sealed record DnsValues(IReadOnlyList<string> Answers, string? Resolver, double Ms, string? ResponseCode);

/// <summary>One check's result in a run.</summary>
public sealed record DiagnosticCheckResult(Guid CheckId, string Name, DiagnosticCheckType Type, DiagnosticOutcome Outcome, string Message,
    PingValues? Ping = null, DnsValues? Dns = null, double? DurationMs = null);

/// <summary>Agent → PNMTD after each run (§5.4); buffered while offline (§3.7).</summary>
public sealed record DiagnosticRunResult(Guid AssignmentId, Guid RunId, string Trigger, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    IReadOnlyList<DiagnosticCheckResult> Results);

/// <summary>Results buffered while offline arrive together.</summary>
public sealed record DiagnosticResultBatch(IReadOnlyList<DiagnosticRunResult> Runs);
