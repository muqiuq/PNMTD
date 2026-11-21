using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.OsUpdates;

/// <summary>
/// apt's view of a server, shared by the os-updates and system sections (docs/linux-os-updates-plan.md §4.1): the simulations of both
/// modes (never <c>apt update</c> itself; cached for a minute, so one collection simulates once), when the lists were refreshed, and
/// the distribution as the trackers name it.
/// </summary>
internal static class AptState
{
    private static readonly Lock gate = new();
    private static readonly Dictionary<string, (DateTimeOffset At, string Output)> cache = [];

    /// <summary><c>apt-get -s</c> for a mode: <c>upgrade --with-new-pkgs</c> or <c>full-upgrade</c>, without the lock.</summary>
    public static string Simulate(OsUpgradeMode mode) => Cached($"sim-{mode}", () => Tool.Text("apt-get",
        ["-s", "-q", "-o", "Debug::NoLocking=1", .. ModeArguments(mode)], TimeSpan.FromSeconds(120), Environment()));

    /// <summary><c>apt-get --assume-no</c> for a mode: its sizes ("Need to get …", "After this operation, …"); it answers no and aborts.</summary>
    public static string Sizes(OsUpgradeMode mode) => Cached($"sizes-{mode}", () => Tool.Run("apt-get",
        ["--assume-no", "-q", "-o", "Debug::NoLocking=1", .. ModeArguments(mode)], TimeSpan.FromSeconds(120), environment: Environment()).Output);

    public static void Forget()
    {
        lock (gate)
        {
            cache.Clear();
        }
    }

    private static string[] ModeArguments(OsUpgradeMode mode) => mode == OsUpgradeMode.FullUpgrade ? ["full-upgrade"] : ["upgrade", "--with-new-pkgs"];

    public static IReadOnlyDictionary<string, string> Environment() => new Dictionary<string, string> { ["LC_ALL"] = "C.UTF-8" };

    private static string Cached(string key, Func<string> read)
    {
        lock (gate)
        {
            if (cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(1))
            {
                return hit.Output;
            }
        }
        var output = read();
        lock (gate)
        {
            cache[key] = (DateTimeOffset.UtcNow, output);
        }
        return output;
    }

    /// <summary>
    /// When the lists were last refreshed (O0): apt leaves the Release files with the mirror's dates, so an unchanged mirror's lists look
    /// old; <c>/var/lib/apt/lists/partial</c> is touched by every update. The newer of the two.
    /// </summary>
    public static DateTimeOffset? ListsUpdatedAt()
    {
        const string lists = "/var/lib/apt/lists";
        if (!Directory.Exists(lists))
        {
            return null;
        }
        var newest = Directory.GetFiles(lists).Where(f => f.EndsWith("Release", StringComparison.Ordinal))
            .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        var partial = Path.Combine(lists, "partial");
        if (Directory.Exists(partial))
        {
            var touched = Directory.GetLastWriteTimeUtc(partial);
            newest = touched > newest ? touched : newest;
        }
        return newest == default ? null : new DateTimeOffset(newest, TimeSpan.Zero);
    }

    /// <summary>The distribution as the trackers name it (§6.3): <c>debian:bookworm</c> (Proxmox VE too), <c>ubuntu:noble</c>; null for others.</summary>
    public static string? Distribution(LinuxOsInfo os)
    {
        var debianLike = os.Id == "debian" || os.Id == "proxmox" || (os.Id != "ubuntu" && os.IdLike.Contains("debian") && !os.IdLike.Contains("ubuntu"));
        if (os.Id == "ubuntu" && os.Codename is { Length: > 0 } ubuntu)
        {
            return $"ubuntu:{ubuntu}";
        }
        if (debianLike && os.Codename is { Length: > 0 } codename)
        {
            return $"debian:{codename}";
        }
        // Proxmox VE's os-release is Debian's; a derivative's codename is not Debian's, and isn't guessed.
        return os.Id == "debian" && os.DebianVersion is { } point ? $"debian:{CodenameOf(point)}" : null;
    }

    private static string? CodenameOf(string debianVersion) => debianVersion.Split('.')[0] switch
    {
        "12" => "bookworm",
        "13" => "trixie",
        "14" => "forky",
        _ => null,
    };
}

/// <summary>
/// The <c>os-updates</c> section (docs/linux-os-updates-plan.md §4.1): every pending package with its versions, source package and origin,
/// what <c>upgrade</c> and <c>full-upgrade</c> would do (with their sizes and removals), whether dpkg was interrupted, needrestart's list
/// (only reported, D4), a reboot required, apt settings that weaken its checks, and the feature's state (the run in progress, the last
/// update, the next slot). Collected every 15 minutes, after each run, and every 6 hours.
/// </summary>
internal sealed class OsUpdatesCollector : Collector
{
    public override string Name => SectionNames.OsUpdates;

    public override TimeSpan Interval => TimeSpan.FromMinutes(15);

    /// <summary>Four simulations and their lookups take some seconds on a large system.</summary>
    public override TimeSpan Timeout => TimeSpan.FromMinutes(4);

    /// <summary>The sizes are kept per plan (its hash of lines), so an unchanged plan isn't asked again.</summary>
    private readonly Dictionary<OsUpgradeMode, (string Key, long? Download, long? Disk)> sizes = [];

    /// <summary>The feature's state for the section; null without the feature (<c>pnmt-agent collect</c>).</summary>
    public static Func<(OsUpdateRunRef? Current, OsUpdateRunRef? LastUpdate, DateTimeOffset? NextUpdateAt, int RepositoriesFailed)>? FeatureState { get; set; }

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        if (!Tool.Exists("apt-get"))
        {
            throw new NotAvailableException("apt-get is not installed");
        }
        AptState.Forget();
        var os = LinuxOs.Read();
        var upgrade = AptParsers.ParseSimulation(AptState.Simulate(OsUpgradeMode.Upgrade));
        var full = AptParsers.ParseSimulation(AptState.Simulate(OsUpgradeMode.FullUpgrade));
        var inUpgrade = upgrade.Installs.Select(c => (c.Name, c.Arch)).ToHashSet();
        var installs = full.Installs.Concat(upgrade.Installs).GroupBy(c => (c.Name, c.Arch)).Select(g => g.First()).ToList();
        var installed = Isolated.Part(errors, "dpkg-query", () => Installed(installs)) ?? [];
        var sources = Isolated.Part(errors, "apt-cache show", () => Sources(installs)) ?? [];
        var packages = installs.Select(c =>
        {
            var source = sources.GetValueOrDefault((c.Name, c.Candidate!));
            var mine = installed.GetValueOrDefault((c.Name, c.Arch ?? ""));
            if (mine == default)
            {
                // dpkg names arch: all packages with the native architecture in history.log, and "all" in dpkg-query.
                mine = installed.FirstOrDefault(p => p.Key.Name == c.Name).Value;
            }
            return new PendingPackage(c.Name, c.Arch, c.Installed, c.Candidate!, source == default ? null : source.Source,
                source == default ? null : source.SourceVersion, mine == default ? null : mine.SourceVersion, c.Origins,
                AptParsers.IsSecurityOrigin(c.Origins), inUpgrade.Contains((c.Name, c.Arch)));
        }).ToList();
        var phased = upgrade.Phased.Concat(full.Phased).Distinct(StringComparer.Ordinal).ToList();
        packages.AddRange(phased.Where(p => packages.All(x => x.Name != p)).Select(p => new PendingPackage(p, null, null, "", null, null, null, null, false, false, true)));
        var state = FeatureState?.Invoke();
        var warnings = Isolated.Part(errors, "apt-config", Warnings) ?? [];
        if (state?.RepositoriesFailed > 0)
        {
            warnings.Add($"The last update reached some repositories, but {state.Value.RepositoriesFailed} failed: see its log.");
        }
        var reboot = Isolated.Part(errors, "reboot required", RebootPackages);
        return new OsUpdatesSection(
            AptState.Distribution(os),
            AptState.ListsUpdatedAt(),
            state?.NextUpdateAt,
            packages.OrderBy(p => p.Name, StringComparer.Ordinal).Take(OsUpdatesSection.MaxPackages).ToList(),
            packages.Count,
            Isolated.Part(errors, "upgrade plan", () => Plan(OsUpgradeMode.Upgrade, upgrade)),
            Isolated.Part(errors, "full-upgrade plan", () => Plan(OsUpgradeMode.FullUpgrade, full)),
            Isolated.Part(errors, "dpkg --audit", Interrupted),
            warnings,
            Isolated.Part(errors, "needrestart", NeedsRestart),
            reboot is not null,
            reboot ?? [],
            state?.Current,
            state?.LastUpdate,
            phased);
    }

    private AptPlan Plan(OsUpgradeMode mode, Simulation simulation)
    {
        var key = string.Join('\n', simulation.Changes.Select(c => $"{c.Remove} {c.Name} {c.Candidate}"));
        if (!sizes.TryGetValue(mode, out var known) || known.Key != key)
        {
            var (download, disk) = simulation.Changes.Count == 0 ? (0L, 0L) : AptParsers.ParseSizes(AptState.Sizes(mode));
            known = (key, download, disk);
            sizes[mode] = known;
        }
        var removals = simulation.Removals.Select(r => r.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var upgrades = simulation.Installs.Count(c => c.Installed is not null);
        return new AptPlan(upgrades, simulation.Installs.Count() - upgrades, removals.Count, simulation.KeptBack.Count, known.Download, known.Disk, removals,
            AptPlan.HashOf(removals));
    }

    private static Dictionary<(string Name, string Arch), (string Version, string Source, string SourceVersion)> Installed(List<SimulatedChange> installs)
    {
        var names = installs.Where(c => c.Installed is not null).Select(c => c.Name).Distinct().ToList();
        if (names.Count == 0)
        {
            return [];
        }
        var result = new Dictionary<(string, string), (string, string, string)>();
        foreach (var batch in names.Chunk(200))
        {
            var output = Tool.Run("dpkg-query", ["-W", "--showformat=${Package}:${Architecture} ${Version} ${source:Package} ${source:Version}\n", .. batch],
                TimeSpan.FromSeconds(30)).Output;
            foreach (var (key, value) in AptParsers.ParseDpkgQuery(output))
            {
                result[key] = value;
            }
        }
        return result;
    }

    private static Dictionary<(string Package, string Version), (string Source, string SourceVersion)> Sources(List<SimulatedChange> installs)
    {
        var result = new Dictionary<(string, string), (string, string)>();
        foreach (var batch in installs.Where(c => c.Candidate is not null).Select(c => $"{c.Name}={c.Candidate}").Distinct().Chunk(200))
        {
            var output = Tool.Run("apt-cache", ["show", "--no-all-versions", .. batch], TimeSpan.FromSeconds(60), environment: AptState.Environment()).Output;
            foreach (var (key, value) in AptParsers.ParseAptCacheShow(output))
            {
                result[key] = value;
            }
        }
        return result;
    }

    /// <summary><c>dpkg --audit</c> prints nothing when every package is configured.</summary>
    private static bool Interrupted() => Tool.Run("dpkg", ["--audit"], TimeSpan.FromSeconds(60)).Output.Trim().Length > 0;

    private static NeedsRestartInfo? NeedsRestart() =>
        Tool.Exists("needrestart") ? AptParsers.ParseNeedrestart(Tool.Run("needrestart", ["-b", "-r", "l"], TimeSpan.FromSeconds(90)).Output) : null;

    /// <summary>/run/reboot-required and the packages that asked for it, or null when no reboot is required.</summary>
    private static List<string>? RebootPackages() => File.Exists("/run/reboot-required")
        ? (Tool.ReadFile("/run/reboot-required.pkgs") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(50).ToList()
        : null;

    /// <summary>apt settings that let unsigned packages in (§4.3): shown, not refused.</summary>
    private static List<string> Warnings()
    {
        var dump = Tool.Text("apt-config", ["dump"], TimeSpan.FromSeconds(15));
        var warnings = new List<string>();
        foreach (var (setting, text) in new[]
                 {
                     ("APT::Get::AllowUnauthenticated", "apt allows unauthenticated packages (APT::Get::AllowUnauthenticated)"),
                     ("Acquire::AllowInsecureRepositories", "apt allows insecure repositories (Acquire::AllowInsecureRepositories)"),
                     ("Acquire::AllowDowngradeToInsecureRepositories", "apt allows downgrading to insecure repositories"),
                 })
        {
            if (dump.Split('\n').Any(l => l.StartsWith(setting + " ", StringComparison.Ordinal) && (l.Contains("\"1\"", StringComparison.Ordinal) || l.Contains("\"true\"", StringComparison.OrdinalIgnoreCase))))
            {
                warnings.Add(text);
            }
        }
        return warnings;
    }
}
