using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Core.Rights;

/// <summary>
/// What the server's own configuration allows PNMT (docs/linux-sensor-plan.md §3.2, §4.6): removing SSH keys, running scripts,
/// collecting beyond the defaults, and OS updates (docs/linux-os-updates-plan.md §3). Granted on the server by root, never by PNMT.
/// <see cref="Source"/> says where they come from: <c>config.yaml</c> (applied), <c>defaults</c> (no file: everything that changes
/// something off, except OS updates, which are on unless root turns them off, D1) or <c>refused</c> (an unsafe or broken file:
/// everything off, OS updates too; <see cref="Problem"/> says why). <see cref="Warnings"/> are the settings left off for an unknown
/// key or a wrong value.
/// </summary>
public sealed record AgentRights(
    string Source,
    string? Problem,
    KeyRights Keys,
    ScriptRights Scripts,
    CollectRights Collect,
    IReadOnlyList<string> Warnings,
    OsUpdateRights? OsUpdatesOrNull = null)
{
    /// <summary>OS updates (D1): on when no file or no <c>os_updates</c> block says otherwise; off for a refused file.</summary>
    public OsUpdateRights OsUpdates => OsUpdatesOrNull ?? (Source == Refused ? OsUpdateRights.Off : OsUpdateRights.On);

    public const string FromFile = "config.yaml";
    public const string FromDefaults = "defaults";
    public const string Refused = "refused";

    /// <summary>Everything that changes something off: no file, or before it was read.</summary>
    public static AgentRights Defaults { get; } = new(FromDefaults, null, KeyRights.Off, ScriptRights.Off, CollectRights.Off, [], OsUpdateRights.On);

    /// <summary>A file that grants nothing (§4.6): not root's, writable by others, unreadable, or not valid YAML. OS updates are off too.</summary>
    public static AgentRights RefusedBecause(string problem) => Defaults with { Source = Refused, Problem = problem, OsUpdatesOrNull = OsUpdateRights.Off };

    /// <summary>The hello's summary (<see cref="Hello.Rights"/>), which PNMT shows and uses to grey out what the server refuses.</summary>
    public AgentRightsSummary Summary() => new(Source, Problem, Keys.Remove, Keys.Users, Keys.Protect.Count, Keys.WritablePaths, Scripts.Enabled,
        Scripts.RunAs, Scripts.AllowedHashes.Count, Scripts.MaxTimeoutMinutes, Collect.SudoCommands, Collect.CronCommands,
        Warnings.Count == 0 ? null : Warnings, new OsUpdatesRightsSummary(OsUpdates.Update, OsUpdates.Upgrade, OsUpdates.FullUpgrade, OsUpdates.Changelogs));

    /// <summary>
    /// One line for the log, the journal and <c>pnmt-agent config check</c>, as PNMT's agent log words it: "key removal allowed for
    /// deploy, www; scripts off".
    /// </summary>
    public string Describe()
    {
        var keys = !Keys.Remove ? "key removal off"
            : $"key removal allowed for {(Keys.Users.Count == 0 ? "every account" : string.Join(", ", Keys.Users))}"
              + (Keys.Protect.Count > 0 ? $" ({Keys.Protect.Count} protected keys)" : "");
        var scripts = !Scripts.Enabled ? "scripts off"
            : $"scripts as {(Scripts.RunAs.Count == 0 ? "no account" : string.Join(", ", Scripts.RunAs))}"
              + (Scripts.AllowedHashes.Count > 0 ? $", only {Scripts.AllowedHashes.Count} allowed scripts" : ", any script of PNMT's library")
              + $", at most {Scripts.MaxTimeoutMinutes} min";
        var collect = new List<string>();
        if (Collect.SudoCommands)
        {
            collect.Add("sudo's command lines");
        }
        if (Collect.CronCommands)
        {
            collect.Add("crontab lines in full");
        }
        return $"{keys}; {scripts}" + (collect.Count > 0 ? $"; collects {string.Join(" and ", collect)}" : "") + $"; {OsUpdates.Describe()}";
    }
}

/// <summary>
/// <c>os_updates</c> (docs/linux-os-updates-plan.md §3): whether PNMT may run <c>apt-get update</c>, <c>upgrade</c> and
/// <c>full-upgrade</c>, and whether the agent fetches the pending packages' changelogs. On unless root says otherwise (D1).
/// </summary>
public sealed record OsUpdateRights(bool Update, bool Upgrade, bool FullUpgrade, bool Changelogs)
{
    public static OsUpdateRights On { get; } = new(true, true, true, true);

    public static OsUpdateRights Off { get; } = new(false, false, false, false);

    /// <summary>"OS updates: update, upgrade and full upgrade allowed", "OS updates off", "OS updates: update only".</summary>
    public string Describe()
    {
        var allowed = new List<string>();
        if (Update)
        {
            allowed.Add("update");
        }
        if (Upgrade)
        {
            allowed.Add("upgrade");
        }
        if (FullUpgrade)
        {
            allowed.Add("full upgrade");
        }
        var runs = allowed.Count == 0 ? "OS updates off"
            : $"OS updates: {(allowed.Count == 1 ? allowed[0] : string.Join(", ", allowed[..^1]) + " and " + allowed[^1])} allowed";
        return Changelogs || allowed.Count == 0 ? runs : runs + ", no changelogs";
    }
}

/// <summary>
/// <c>ssh_keys</c> (§4.6, §6.5): whether PNMT may remove keys, only from these accounts (empty: every account), never these
/// fingerprints, and the key files under <c>/etc</c> the agent may rewrite (the unit's <c>ReadWritePaths=</c>).
/// </summary>
public sealed record KeyRights(bool Remove, IReadOnlyList<string> Users, IReadOnlyList<string> Protect, IReadOnlyList<string> WritablePaths)
{
    public static KeyRights Off { get; } = new(false, [], [], []);
}

/// <summary>
/// <c>scripts</c> (§4.6, §8.5): whether PNMT may run scripts, as which accounts (root must be named), only these SHA-256 (empty: any
/// script of PNMT's library), and the longest run.
/// </summary>
public sealed record ScriptRights(bool Enabled, IReadOnlyList<string> RunAs, IReadOnlyList<string> AllowedHashes, int MaxTimeoutMinutes)
{
    public const int DefaultTimeoutMinutes = 60;

    public static ScriptRights Off { get; } = new(false, [], [], DefaultTimeoutMinutes);
}

/// <summary><c>collect</c> (§4.6, §5.4, §5.6): what is collected beyond the defaults.</summary>
public sealed record CollectRights(bool SudoCommands, bool CronCommands)
{
    public static CollectRights Off { get; } = new(false, false);
}

/// <summary>
/// Where features read the rights (docs/linux-sensor-plan.md §4.6): the effective rights now, and a number that grows with each
/// change. The service announces <see cref="AgentRights.Summary"/> in its hello, and reconnects when <see cref="Version"/> moved,
/// so the hello carries the new capabilities and rights. A platform without such a file (Windows today) registers none.
/// </summary>
public interface IAgentRightsSource
{
    AgentRights Current { get; }

    int Version { get; }
}
