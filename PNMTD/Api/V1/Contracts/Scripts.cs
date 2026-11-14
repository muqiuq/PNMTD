using System.Text.Json;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// Scripts (docs/windows-sensor-plan.md §7, W6; docs/linux-sensor-plan.md §8): the library and the runs are every platform's
// (/api/v1/agents/), PowerShell for Windows servers and bash for Linux servers (L7); the run-as accounts and the scripts registered
// on a server are Windows' (/api/v1/windows/), a Linux server's accounts come from its system section (…/run-as-accounts). Admin only.

/// <summary>
/// A script's name, description and text, and at creation its <see cref="Language"/> (PowerShell when missing; it can't change
/// later). A new bash script without a text starts from <see cref="BashScriptHeader.Template"/>.
/// </summary>
public sealed record AgentScriptRequest(string Name, string? Description, string Text, ScriptLanguage? Language = null);

/// <summary><c>GET /api/v1/agents/scripts?language=</c>: the library, of one language.</summary>
public sealed class AgentScriptQuery : PageQuery
{
    public ScriptLanguage? Language { get; set; }
}

/// <summary>What a new script of a language starts from (docs/linux-sensor-plan.md §8.1, §11.1).</summary>
public sealed record AgentScriptTemplate(ScriptLanguage Language, string Text);

public sealed record AgentScriptItem(Guid Id, string Name, string? Description, ScriptLanguage Language, int CurrentVersion, ScriptParseState ParseState,
    int Parameters, int Findings, DateTime UpdatedAt);

public sealed record AgentScriptVersionItem(Guid Id, int Number, string Hash, DateTime CreatedAt, string? CreatedByName, ScriptParseState ParseState,
    DateTime? ParsedAt, string? ParsedOn, string? ParseError);

/// <summary>
/// A script with its current version's text and what the parser found, and its versions. For bash (docs/linux-sensor-plan.md §8.2,
/// §8.3) <see cref="Parsed"/> is there from the save on (PNMTD reads the header itself: parameters, help, header errors and the checks
/// of §8.3), and <see cref="ParseState"/> is the syntax check of a Linux agent (<c>bash -n</c>, shellcheck), whose errors and findings
/// are merged into <see cref="Parsed"/>: Pending until one with parse.v1 is connected. <see cref="ParserAvailable"/>: an agent of the
/// script's platform that parses is connected.
/// </summary>
public sealed record AgentScriptDetail(
    Guid Id,
    string Name,
    string? Description,
    ScriptLanguage Language,
    int CurrentVersion,
    string Text,
    string Hash,
    ScriptParseState ParseState,
    ParsedScript? Parsed,
    string? ParseError,
    IReadOnlyList<AgentScriptVersionItem> Versions,
    bool ParserAvailable);

/// <summary>A Windows run-as account (§7.4).</summary>
public sealed record WindowsRunAsRequest(string Name, RunAsKind Kind, string? Account, string? Description);

public sealed record WindowsRunAsItem(Guid Id, string Name, RunAsKind Kind, string? Account, string? Description, DateTime CreatedAt);

/// <summary>
/// Run a script (§7.3) on servers of its platform. PowerShell on Windows servers as a run-as account (<see cref="RunAsId"/>);
/// <see cref="Password"/> for an account whose password is typed per run. Bash on Linux servers (docs/linux-sensor-plan.md §8.4) as
/// <see cref="RunAs"/> <c>Service</c> (root, the default) or <c>User</c> with <see cref="Account"/>, a local account; no passwords.
/// <see cref="SecureArguments"/> ([securestring], bash's <c>secret</c>) and the password are encrypted to each agent and never stored.
/// </summary>
public sealed record AgentScriptRunRequest(
    IReadOnlyList<Guid> SensorIds,
    Guid? RunAsId,
    IReadOnlyDictionary<string, JsonElement>? Arguments,
    IReadOnlyDictionary<string, string>? SecureArguments,
    string? Password,
    int? TimeoutSeconds,
    string? Shell,
    RunAsKind? RunAs = null,
    string? Account = null);

/// <summary>
/// A Linux server's account for the run dialog (docs/linux-sensor-plan.md §8.4): root as the service, or a local account from the
/// system section, with whether the server's config.yaml allows scripts as it (scripts.run_as), and if not, the line that would.
/// </summary>
public sealed record LinuxRunAsAccount(string Name, RunAsKind Kind, int? Uid, string? Home, string? Shell, bool LoginShell, bool SystemAccount, bool Allowed,
    string? Refused);

/// <summary>
/// <c>GET /api/v1/agents/servers/{id}/run-as-accounts</c> (Linux, Admin): whether the server allows scripts at all (scripts.v1, else
/// <see cref="ScriptsRefused"/> with the line of config.yaml), its scripts.run_as, the longest run it allows, how many script hashes it
/// allows (0: any of PNMT's library), and the accounts to offer.
/// </summary>
public sealed record LinuxRunAsAccounts(Guid SensorId, bool ScriptsAllowed, string? ScriptsRefused, IReadOnlyList<string> RunAs, int MaxTimeoutMinutes,
    int AllowedScriptHashes, IReadOnlyList<LinuxRunAsAccount> Accounts);

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

public sealed record AgentScriptRunItem(
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

public sealed record AgentScriptRunDetail(AgentScriptRunItem Run, string? Arguments, string? Output, string Hash, int TimeoutSeconds);

public sealed class AgentScriptRunQuery : PageQuery
{
    public Guid? ScriptId { get; set; }

    public Guid? SensorId { get; set; }

    public Guid? BatchId { get; set; }

    public ScriptRunState? State { get; set; }
}
