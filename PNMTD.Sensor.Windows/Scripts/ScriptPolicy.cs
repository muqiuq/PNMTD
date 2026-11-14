using System.Security.Cryptography;
using System.Text;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Scripts;

/// <summary>
/// What an administrator allowed on this server (docs/windows-sensor-plan.md §7.2), written only by the window, which needs a local
/// administrator; PNMT cannot change it from the outside. Scripts from PNMT at all (<see cref="Enabled"/>); then the scripts
/// registered here (<see cref="LocalScripts"/>), each only as the account it is registered with; and only if
/// <see cref="AnyScript"/>, other scripts too, as the accounts in <see cref="RunAs"/> ("service" for the agent itself must be
/// named; empty: any account but the service).
/// </summary>
internal sealed record ScriptPolicy(bool Enabled, bool AnyScript, IReadOnlyList<string> RunAs)
{
    public const string File = "scripts-policy.json";

    public static ScriptPolicy Off { get; } = new(false, false, []);

    public static ScriptPolicy Read() => AgentFiles.ReadState<ScriptPolicy>(File) ?? Off;

    public void Save() => AgentFiles.WriteState(File, this);

    /// <summary>Why a run is refused, or null.</summary>
    public string? Refuses(string hash, Lib.Agent.RunAsSpec runAs)
    {
        if (!Enabled)
        {
            return "Scripts from PNMT are not allowed on this server (the agent's window turns them on).";
        }
        if (LocalScripts.ByHash(hash) is { } local)
        {
            return local.RunsAs(runAs) ? null : $"This server runs {local.Name} only as {local.RunAsText}.";
        }
        if (!AnyScript)
        {
            return "This script is not registered on the server (the agent's window: Scripts, Add script…).";
        }
        var name = runAs.Kind == Lib.Agent.RunAsKind.Service ? "service" : runAs.Account ?? "";
        if (runAs.Kind == Lib.Agent.RunAsKind.Service && !RunAs.Contains("service", StringComparer.OrdinalIgnoreCase))
        {
            return "Running as the service itself must be allowed by name (\"service\") on the server.";
        }
        if (RunAs.Count > 0 && !RunAs.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return $"{name} is not on the server's list of allowed accounts.";
        }
        return null;
    }
}

/// <summary>
/// Accounts whose passwords are stored on this server (§7.4, R20): entered in the window, encrypted with DPAPI for the machine
/// and an extra random secret, in state\credentials.json, which only SYSTEM and Administrators can read. They never reach PNMT.
/// A local administrator can still recover them, as any secret on their machine.
/// </summary>
internal static class StoredAccounts
{
    private const string File = "credentials.json";
    private const string EntropyFile = "credentials.key";

    public static IReadOnlyList<string> Names() => (AgentFiles.ReadState<Dictionary<string, string>>(File) ?? []).Keys.Order().ToList();

    public static void Set(string account, string password)
    {
        var all = AgentFiles.ReadState<Dictionary<string, string>>(File) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        all[account] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy(), DataProtectionScope.LocalMachine));
        AgentFiles.WriteState(File, all);
    }

    public static void Remove(string account)
    {
        var all = AgentFiles.ReadState<Dictionary<string, string>>(File) ?? [];
        if (all.Remove(account))
        {
            AgentFiles.WriteState(File, all);
        }
    }

    public static string? Password(string account)
    {
        var all = AgentFiles.ReadState<Dictionary<string, string>>(File) ?? [];
        var match = all.FirstOrDefault(p => string.Equals(p.Key, account, StringComparison.OrdinalIgnoreCase));
        return match.Value is null ? null : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(match.Value), Entropy(), DataProtectionScope.LocalMachine));
    }

    private static byte[] Entropy()
    {
        var path = Path.Combine(AgentInfo.StateFolder, EntropyFile);
        if (System.IO.File.Exists(path))
        {
            return System.IO.File.ReadAllBytes(path);
        }
        var entropy = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(AgentInfo.StateFolder);
        System.IO.File.WriteAllBytes(path, entropy);
        return entropy;
    }
}
