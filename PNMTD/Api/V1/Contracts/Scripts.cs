using System.Text.Json;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// PowerShell scripts (docs/windows-sensor-plan.md §7, W6). Admin only.

public sealed record WindowsScriptRequest(string Name, string? Description, string Text);

public sealed record WindowsScriptItem(Guid Id, string Name, string? Description, int CurrentVersion, ScriptParseState ParseState, int Parameters, int Findings,
    DateTime UpdatedAt);

public sealed record WindowsScriptVersionItem(Guid Id, int Number, string Hash, DateTime CreatedAt, string? CreatedByName, ScriptParseState ParseState,
    DateTime? ParsedAt, string? ParsedOn, string? ParseError);

/// <summary>A script with its current version's text and what the parser found, and its versions.</summary>
public sealed record WindowsScriptDetail(
    Guid Id,
    string Name,
    string? Description,
    int CurrentVersion,
    string Text,
    string Hash,
    ScriptParseState ParseState,
    ParsedScript? Parsed,
    string? ParseError,
    IReadOnlyList<WindowsScriptVersionItem> Versions,
    bool ParserAvailable);

public sealed record WindowsRunAsRequest(string Name, RunAsKind Kind, string? Account, string? Description);

public sealed record WindowsRunAsItem(Guid Id, string Name, RunAsKind Kind, string? Account, string? Description, DateTime CreatedAt);

/// <summary>
/// Run a script (§7.3) on servers, as a run-as account. <see cref="SecureArguments"/> (the [securestring] parameters) and
/// <see cref="Password"/> (for an account whose password is typed per run) are encrypted to each agent and never stored.
/// </summary>
public sealed record WindowsScriptRunRequest(
    IReadOnlyList<Guid> SensorIds,
    Guid RunAsId,
    IReadOnlyDictionary<string, JsonElement>? Arguments,
    IReadOnlyDictionary<string, string>? SecureArguments,
    string? Password,
    int? TimeoutSeconds,
    string? Shell);

/// <summary>
/// The scripts an administrator registered on a server, in the agent's window (§7.2), as the server last reported them; each
/// with the library script of the same text, when there is one. <see cref="CollectedAt"/> is null until the agent reported them.
/// </summary>
public sealed record WindowsLocalScripts(DateTime? CollectedAt, bool Allowed, bool AnyScript, IReadOnlyList<string> RunAs, IReadOnlyList<WindowsLocalScript> Scripts);

public sealed record WindowsLocalScript(RegisteredScript Script, string RunAsName, Guid? LibraryScriptId, string? LibraryScriptName);

/// <summary>Run a script registered on the server (§7.2), as the account it is registered with; secure arguments are encrypted to the agent.</summary>
public sealed record WindowsLocalScriptRunRequest(
    string Name,
    IReadOnlyDictionary<string, JsonElement>? Arguments,
    IReadOnlyDictionary<string, string>? SecureArguments,
    int? TimeoutSeconds,
    string? Shell);

public sealed record WindowsScriptRunItem(
    Guid Id,
    Guid BatchId,
    Guid? ScriptId,
    string ScriptName,
    int VersionNumber,
    Ref? Server,
    string ServerName,
    string RunAsName,
    RunAsKind RunAsKind,
    string? Account,
    string? RequestedBy,
    DateTime StartedAt,
    DateTime? FinishedAt,
    ScriptRunState State,
    int? ExitCode,
    string? Message,
    double? DurationMs,
    bool Redacted);

public sealed record WindowsScriptRunDetail(WindowsScriptRunItem Run, string? Arguments, string? Output, string Hash, int TimeoutSeconds);

public sealed class WindowsScriptRunQuery : PageQuery
{
    public Guid? ScriptId { get; set; }

    public Guid? SensorId { get; set; }

    public Guid? BatchId { get; set; }

    public ScriptRunState? State { get; set; }
}
