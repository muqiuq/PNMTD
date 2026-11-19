using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PNMTD.Lib.Agent;

// Scripts (docs/windows-sensor-plan.md §7, W6; bash: docs/linux-sensor-plan.md §8): parsed on an
// agent (never run for that), run only where an administrator allowed it on the server itself. On
// Windows as the service, an account stored on that server, an account whose password is typed per
// run, or a gMSA; on Linux as root (the service) or a local account by name (User).

[JsonConverter(typeof(JsonStringEnumConverter<RunAsKind>))]
public enum RunAsKind { Service, Stored, TypedPerRun, Gmsa, User }

/// <summary>A script's language: PowerShell runs on Windows servers, bash on Linux servers.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScriptLanguage>))]
public enum ScriptLanguage { PowerShell, Bash }

[JsonConverter(typeof(JsonStringEnumConverter<ScriptRunState>))]
public enum ScriptRunState { Sent, Running, Done, Failed, Refused, TimedOut, Offline }

/// <summary>A parameter as the parser found it: its type, whether it is mandatory, its default, and its validation.</summary>
public sealed record ScriptParameter(
    string Name,
    string Type,
    bool Mandatory,
    string? Default,
    int? Position,
    IReadOnlyList<string>? ValidateSet,
    double? RangeMin,
    double? RangeMax,
    string? HelpMessage,
    bool IsSwitch,
    bool IsSecure);

/// <summary>Comment-based help: .SYNOPSIS, .DESCRIPTION and .PARAMETER.</summary>
public sealed record ScriptHelp(string? Synopsis, string? Description, IReadOnlyDictionary<string, string> Parameters);

public sealed record ScriptSyntaxError(string Message, int Line, int Column);

/// <summary>A best-practice warning (§7.1): no [CmdletBinding()], an untyped parameter, Invoke-Expression, …</summary>
public sealed record ScriptFinding(string Rule, string Message, int? Line = null);

/// <summary>
/// What a parser found in a script. <see cref="Functions"/> (docs/connectors-plan.md §8): the functions defined at its top level, each
/// with its own parameters and help; null from a parser that doesn't list functions (an agent without <c>parse.functions.v1</c>).
/// </summary>
public sealed record ParsedScript(IReadOnlyList<ScriptParameter> Parameters, ScriptHelp? Help, IReadOnlyList<ScriptSyntaxError> Errors, IReadOnlyList<ScriptFinding> Findings,
    IReadOnlyList<ScriptFunction>? Functions = null)
{
    /// <summary>The parameters of the whole script, or of the function named; null when the parse has no such function.</summary>
    public IReadOnlyList<ScriptParameter>? ParametersOf(string? function) => function is null
        ? Parameters
        : Functions?.FirstOrDefault(f => string.Equals(f.Name, function, StringComparison.OrdinalIgnoreCase))?.Parameters;

    /// <summary>The function by its name as written in the script, ignoring case; null when there is none.</summary>
    public ScriptFunction? Function(string name) => Functions?.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A function defined at a script's top level, with its own parameters and help (docs/connectors-plan.md §8).</summary>
public sealed record ScriptFunction(string Name, int Line, IReadOnlyList<ScriptParameter> Parameters, ScriptHelp? Help);

/// <summary>PNMTD → agent (signed): parse this text; nothing is run.</summary>
public sealed record ParseScriptRequest(string Hash, string Text);

/// <summary>Agent → PNMTD: what the parser found, or why it couldn't.</summary>
public sealed record ParseResultMessage(string RequestId, string Hash, ParsedScript? Parsed, string? Error);

/// <summary>
/// A secret encrypted to the agent's ECDH key (§3.4): an ephemeral P-256 key's SPKI, a nonce, and AES-256-GCM ciphertext with its
/// tag, the key from HKDF-SHA256 over the shared secret and the run's id. A TLS-inspecting proxy can't read it.
/// </summary>
public sealed record EncryptedSecret(string EphemeralKey, string Nonce, string Ciphertext);

/// <summary>Who a script runs as: the kind, the account's name (not for the service), and a password typed for this run only.</summary>
public sealed record RunAsSpec(RunAsKind Kind, string? Account, EncryptedSecret? Password);

/// <summary>
/// PNMTD → agent (signed): run this script. The agent checks the local allowlists and the hash, parses the script again and
/// refuses arguments that don't match it, and splats them (never a command line). Secure arguments travel encrypted.
/// <see cref="Function"/> (docs/connectors-plan.md §8): a function defined at the script's top level to call instead of the whole
/// script; sent only to an agent that announces <c>scripts.functions.v1</c>, which checks it against its own parse.
/// </summary>
public sealed record RunScriptRequest(
    Guid RunId,
    string ScriptName,
    string Hash,
    string Text,
    IReadOnlyDictionary<string, JsonElement> Arguments,
    IReadOnlyDictionary<string, EncryptedSecret> SecureArguments,
    RunAsSpec RunAs,
    int TimeoutSeconds,
    string Shell,
    string? Function = null);

/// <summary>Agent → PNMTD: a run's end (or its refusal), with the output (at most 1 MiB), the exit code and the time.</summary>
public sealed record ScriptResultMessage(
    Guid RunId,
    ScriptRunState State,
    int? ExitCode,
    string? Output,
    string? Message,
    double? DurationMs,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Arguments against a script's parameters, the same on PNMTD and on the agent (§7.1).</summary>
public static class ScriptArguments
{
    /// <summary>
    /// The arguments against the parsed parameters (§7.1): known names, the mandatory ones, switches as booleans, sets, ranges and
    /// numbers. The agent checks them again against its own parse before it runs anything.
    /// </summary>
    public static IEnumerable<(string Field, string Message)> Check(ParsedScript parsed, IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyCollection<string> secure) => Check(parsed.Parameters, arguments, secure, "script");

    /// <summary>
    /// Against a function's parameters when <paramref name="function"/> names one (docs/connectors-plan.md §8.3), else the script's. A
    /// function the parse doesn't list is a problem of its own.
    /// </summary>
    public static IEnumerable<(string Field, string Message)> Check(ParsedScript parsed, string? function, IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyCollection<string> secure)
    {
        if (function is null)
        {
            return Check(parsed, arguments, secure);
        }
        return parsed.Function(function) is { } f
            ? Check(f.Parameters, arguments, secure, "function")
            : [("function", $"The script defines no function {function} at its top level.")];
    }

    /// <summary>The arguments against a list of parameters: the script's, or a function's.</summary>
    public static IEnumerable<(string Field, string Message)> Check(IReadOnlyList<ScriptParameter> declared, IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyCollection<string> secure, string owner = "script")
    {
        var parameters = declared.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in arguments.Keys.Concat(secure).Where(n => !parameters.ContainsKey(n)))
        {
            yield return ($"arguments.{name}", $"The {owner} has no parameter {name}.");
        }
        foreach (var name in secure.Where(n => parameters.TryGetValue(n, out var p) && !p.IsSecure))
        {
            yield return ($"arguments.{name}", $"{name} is not a [securestring]: send it as a plain argument.");
        }
        foreach (var name in arguments.Keys.Where(n => parameters.TryGetValue(n, out var p) && p.IsSecure))
        {
            yield return ($"arguments.{name}", $"{name} is a [securestring]: send it as a secure argument.");
        }
        foreach (var parameter in declared)
        {
            var given = arguments.TryGetValue(parameter.Name, out var value) || secure.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase);
            if (!given)
            {
                if (parameter.Mandatory)
                {
                    yield return ($"arguments.{parameter.Name}", $"{parameter.Name} is mandatory.");
                }
                continue;
            }
            if (parameter.IsSecure || !arguments.ContainsKey(parameter.Name))
            {
                continue;
            }
            if (parameter.IsSwitch && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                yield return ($"arguments.{parameter.Name}", $"{parameter.Name} is a switch: true or false.");
                continue;
            }
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            if (parameter.ValidateSet is { Count: > 0 } set && !set.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                yield return ($"arguments.{parameter.Name}", $"{parameter.Name} must be one of {string.Join(", ", set)}.");
            }
            var numeric = parameter.Type.ToLowerInvariant() is "int" or "int32" or "int64" or "long" or "double" or "decimal" or "byte" or "uint32" or "number";
            if (numeric && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                yield return ($"arguments.{parameter.Name}", $"{parameter.Name} must be a number.");
            }
            else if ((parameter.RangeMin is not null || parameter.RangeMax is not null)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && (number < parameter.RangeMin || number > parameter.RangeMax))
            {
                yield return ($"arguments.{parameter.Name}", $"{parameter.Name} must be between {parameter.RangeMin} and {parameter.RangeMax}.");
            }
        }
    }

}

/// <summary>
/// The SHA-256 of a script's text, hex: what registrations name and runs check. Of the text without a BOM and with LF line endings,
/// so a file saved on Windows (CRLF, perhaps a BOM) and the same text pasted into PNMT's editor have the same hash.
/// </summary>
public static class ScriptHashes
{
    public static string Of(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.TrimStart('\uFEFF').Replace("\r\n", "\n")))).ToLowerInvariant();
}

/// <summary>Secrets encrypted end to end to an agent (§3.4, §7.4): ECDH P-256, HKDF-SHA256, AES-256-GCM.</summary>
public static class AgentSecrets
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("pnmt-run-as-v1");

    public static EncryptedSecret Encrypt(string agentEncryptionKey, string plaintext, Guid context)
    {
        using var agent = ECDiffieHellman.Create();
        agent.ImportSubjectPublicKeyInfo(Convert.FromBase64String(agentEncryptionKey), out _);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var key = Key(ephemeral.DeriveRawSecretAgreement(agent.PublicKey), context);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, data, cipher, tag, context.ToByteArray());
        }
        CryptographicOperations.ZeroMemory(key);
        return new EncryptedSecret(Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(nonce),
            Convert.ToBase64String([.. cipher, .. tag]));
    }

    public static string Decrypt(ECDiffieHellman agentKey, EncryptedSecret secret, Guid context)
    {
        using var ephemeral = ECDiffieHellman.Create();
        ephemeral.ImportSubjectPublicKeyInfo(Convert.FromBase64String(secret.EphemeralKey), out _);
        var key = Key(agentKey.DeriveRawSecretAgreement(ephemeral.PublicKey), context);
        var all = Convert.FromBase64String(secret.Ciphertext);
        if (all.Length < 16)
        {
            throw new CryptographicException("The secret is too short.");
        }
        var plain = new byte[all.Length - 16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Decrypt(Convert.FromBase64String(secret.Nonce), all.AsSpan(0, all.Length - 16), all.AsSpan(all.Length - 16), plain, context.ToByteArray());
        }
        CryptographicOperations.ZeroMemory(key);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Key(byte[] shared, Guid context)
    {
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, context.ToByteArray(), Info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }
}
