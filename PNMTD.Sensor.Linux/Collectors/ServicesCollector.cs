using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

/// <summary>A unit as <c>systemctl list-units</c> lists it.</summary>
internal sealed record UnitListing(string Name, string Load, string Active, string Sub, string? Description);

/// <summary>
/// <c>services</c> (docs/linux-sensor-plan.md §5.4 → <see cref="LinuxServicesSection"/>): every systemd service (<c>list-units --all</c>
/// and the unit files that aren't loaded), their details from one batched <c>systemctl show</c>, the program's package (<c>dpkg -S</c>,
/// cached per path, size and time) and its origin (<c>apt-cache policy</c>, every 15 minutes), snaps by their publisher, the class of
/// §5.4's table, the flags; the timers; and cron (<see cref="Cron"/>). Every minute; the packages' part every 15 minutes. PIDs and the
/// timers' times are volatile.
/// </summary>
internal sealed partial class ServicesCollector(IAgentRightsSource rights) : Collector
{
    public override string Name => SectionNames.Services;

    public override TimeSpan Interval => TimeSpan.FromMinutes(1);

    /// <summary>The first pass asks dpkg for every program, which takes seconds on a large system.</summary>
    public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

    private static readonly IReadOnlySet<string> VolatileNames = new HashSet<string> { "pid", "lastRun", "nextRun" };

    public override IReadOnlySet<string> Volatile => VolatileNames;

    private static readonly TimeSpan PackagesEvery = TimeSpan.FromMinutes(15);

    /// <summary>The origins (apt's Release files' <c>o=</c>) that are the distribution's own.</summary>
    private static readonly HashSet<string> DistributionOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "Debian", "Ubuntu", "UbuntuESM", "UbuntuESMApps", "Canonical",
    };

    /// <summary>Interpreters: the script they run is the program that counts (<c>/usr/bin/python3 /opt/app/main.py</c>).</summary>
    private static readonly string[] Interpreters = ["python", "python2", "python3", "perl", "ruby", "node", "nodejs", "bash", "sh", "dash", "php", "java", "env", "dotnet"];

    private readonly Lock gate = new();
    private readonly Dictionary<string, (long Size, DateTimeOffset? Modified, string? Package)> owners = new(StringComparer.Ordinal);
    private Dictionary<string, string?> origins = new(StringComparer.Ordinal);
    private DateTimeOffset originsAt = DateTimeOffset.MinValue;
    private int packages;
    private DateTimeOffset packagesAt = DateTimeOffset.MinValue;

    [GeneratedRegex(@"path=([^ ;]+)")]
    private static partial Regex ExecPath();

    [GeneratedRegex(@"argv\[\]=([^;]*)")]
    private static partial Regex ExecArgv();

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var listed = Isolated.Part(errors, "systemctl list-units", ListUnits) ?? [];
        var files = Isolated.Part(errors, "systemctl list-unit-files", ListUnitFiles) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        // The unit files that aren't loaded (disabled, never started) are services too; templates and aliases are not.
        var names = listed.Where(u => u.Load != "not-found").Select(u => u.Name)
            .Concat(files.Where(f => f.Value is not ("alias" or "transient") && !f.Key.Contains('@')).Select(f => f.Key))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var details = Isolated.Part(errors, "systemctl show", () => Show(names,
            ["Id", "Description", "LoadState", "ActiveState", "SubState", "UnitFileState", "User", "MainPID", "ExecStart", "FragmentPath", "Type",
             "TriggeredBy", "ConditionResult", "ConditionTimestampMonotonic"])) ?? [];
        var snaps = Isolated.Part(errors, "snaps", Snaps) ?? new Dictionary<string, string?>(StringComparer.Ordinal);

        var units = details.Where(d => d.GetValueOrDefault("LoadState") is not (null or "not-found" or "")).ToList();
        var programs = units.ToDictionary(u => u["Id"], Program, StringComparer.Ordinal);
        var paths = programs.Values.Where(p => p is not null && p.StartsWith('/')).Select(p => p!)
            .Concat(units.Select(u => u.GetValueOrDefault("FragmentPath")).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!))
            .Distinct(StringComparer.Ordinal).ToList();
        var packageOf = Isolated.Part(errors, "dpkg -S", () => Owners(paths)) ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        var originOf = Isolated.Part(errors, "apt-cache policy", () => Origins(packageOf.Values.Where(p => p is not null).Select(p => p!).Distinct().ToList()))
            ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        var proxmox = LinuxOs.IsProxmox();

        var services = units.Select(u =>
        {
            var name = u["Id"];
            var program = programs.GetValueOrDefault(name);
            var unitFile = Blank(u.GetValueOrDefault("FragmentPath"));
            var package = program is not null ? packageOf.GetValueOrDefault(program) : null;
            package ??= unitFile is not null ? packageOf.GetValueOrDefault(unitFile) : null;
            var origin = package is not null ? originOf.GetValueOrDefault(package) : null;
            string? snap = null;
            string? publisher = null;
            if (name.StartsWith("snap.", StringComparison.Ordinal) && name.Split('.') is { Length: >= 3 } parts)
            {
                snap = parts[1];
                publisher = snaps.GetValueOrDefault(snap);
            }
            var active = u.GetValueOrDefault("ActiveState") ?? "unknown";
            var fileState = Blank(u.GetValueOrDefault("UnitFileState")) ?? Blank(files.GetValueOrDefault(name));
            var type = Blank(u.GetValueOrDefault("Type"));
            var triggered = Blank(u.GetValueOrDefault("TriggeredBy")) is not null;
            var enabledNotRunning = fileState is "enabled" or "enabled-runtime" && active is not ("active" or "activating" or "reloading")
                && !(type == "oneshot" && active != "failed") && !(type == "dbus" && active != "failed") && !triggered
                // A start whose conditions weren't met (ConditionResult=no once they were checked) is no fault; never started is.
                && !(u.GetValueOrDefault("ConditionResult") == "no" && u.GetValueOrDefault("ConditionTimestampMonotonic") is { } checkedAt && checkedAt is not ("" or "0"));
            var pid = int.TryParse(u.GetValueOrDefault("MainPID"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : (int?)null;
            return new LinuxServiceEntry(
                name,
                Blank(u.GetValueOrDefault("Description")),
                u.GetValueOrDefault("LoadState") ?? "unknown",
                active,
                u.GetValueOrDefault("SubState") ?? "unknown",
                fileState,
                Blank(u.GetValueOrDefault("User")) ?? (pid is not null || active == "active" ? "root" : null),
                pid,
                program,
                unitFile,
                UnitSource(unitFile),
                snap is null ? package : null,
                snap is not null ? publisher : origin,
                snap,
                Classify(name, program, package, origin, snap, publisher, proxmox),
                enabledNotRunning,
                active == "failed",
                type);
        })
        // The special ones first, as on Windows (§5.4): the unpackaged, then the third-party's.
        .OrderBy(s => s.Class switch { LinuxServiceClass.Unpackaged => 0, LinuxServiceClass.ThirdParty => 1, _ => 2 })
        .ThenBy(s => s.Name, StringComparer.Ordinal)
        .ToList();

        var timers = Isolated.Part(errors, "timers", Timers) ?? [];
        var cronCommands = rights.Current.Collect.CronCommands;
        var cron = Isolated.Part(errors, "cron", () => Cron.Read(cronCommands)) ?? [];
        var count = Isolated.Part(errors, "dpkg-query", () => (int?)PackageCount()) ?? 0;
        return new LinuxServicesSection(services, timers, cron, count, cronCommands);
    }

    /// <summary>§5.4's table: the agent itself; a snap by its publisher; a package of the distribution's archive; another package; none.</summary>
    public static LinuxServiceClass Classify(string name, string? program, string? package, string? origin, string? snap, string? publisher, bool proxmox)
    {
        if (name == AgentInfo.Unit || program == AgentInfo.Program)
        {
            return LinuxServiceClass.Pnmt;
        }
        if (snap is not null)
        {
            return publisher is null ? LinuxServiceClass.Unknown
                : publisher.Equals("canonical", StringComparison.OrdinalIgnoreCase) ? LinuxServiceClass.Distribution : LinuxServiceClass.ThirdParty;
        }
        if (package is not null)
        {
            var archive = origin?.Split(' ')[0];
            return archive is not null && (DistributionOrigins.Contains(archive) || proxmox && archive.Equals("Proxmox", StringComparison.OrdinalIgnoreCase))
                ? LinuxServiceClass.Distribution
                : LinuxServiceClass.ThirdParty;
        }
        return program is null ? LinuxServiceClass.Unknown : LinuxServiceClass.Unpackaged;
    }

    /// <summary>Where the unit file is: a package's (/usr/lib, /lib), the admin's (/etc, /usr/local), or generated at runtime (/run).</summary>
    public static string? UnitSource(string? unitFile) => unitFile switch
    {
        null => null,
        _ when unitFile.StartsWith("/run/", StringComparison.Ordinal) => "runtime",
        _ when unitFile.StartsWith("/etc/", StringComparison.Ordinal) || unitFile.StartsWith("/usr/local/", StringComparison.Ordinal) => "admin",
        _ when unitFile.StartsWith("/usr/lib/", StringComparison.Ordinal) || unitFile.StartsWith("/lib/", StringComparison.Ordinal) => "package",
        _ => null,
    };

    /// <summary>
    /// The program of <c>ExecStart</c> (<c>{ path=/usr/sbin/sshd ; argv[]=/usr/sbin/sshd -D $SSHD_OPTS ; … }</c>); for an interpreter the script
    /// it runs, when that is a path.
    /// </summary>
    public static string? Program(IReadOnlyDictionary<string, string> unit)
    {
        var exec = unit.GetValueOrDefault("ExecStart");
        if (string.IsNullOrEmpty(exec) || ExecPath().Match(exec) is not { Success: true } path)
        {
            return null;
        }
        var program = path.Groups[1].Value;
        if (!program.StartsWith('/'))
        {
            // systemd's own units name some tools without a folder (path=systemctl).
            program = Tool.Find(program) ?? program;
        }
        var baseName = Path.GetFileName(program);
        var interpreter = Interpreters.Contains(baseName) || Interpreters.Contains(Regex.Replace(baseName, @"[0-9.]+$", ""));
        if (interpreter && ExecArgv().Match(exec) is { Success: true } arguments)
        {
            var argv = arguments.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (argv.Skip(1).FirstOrDefault(a => a.StartsWith('/')) is { } script)
            {
                return script;
            }
        }
        return program;
    }

    // --- systemctl --------------------------------------------------------------------------------------------------

    /// <summary><c>systemctl list-units --type=service --all</c>: JSON from systemd 249 on (L0), the plain table otherwise.</summary>
    public static List<UnitListing> ListUnits()
    {
        var json = Tool.Run("systemctl", ["list-units", "--type=service", "--all", "--output=json", "--no-pager"], TimeSpan.FromSeconds(20));
        if (json.Ok && json.Output.TrimStart().StartsWith('[') && ParseUnitsJson(json.Output) is { } units)
        {
            return units;
        }
        var plain = Tool.Text("systemctl", ["list-units", "--type=service", "--all", "--plain", "--no-legend", "--no-pager"], TimeSpan.FromSeconds(20));
        return ParseUnitsTable(plain);
    }

    public static List<UnitListing>? ParseUnitsJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateArray().Select(e => new UnitListing(Str(e, "unit") ?? "?", Str(e, "load") ?? "", Str(e, "active") ?? "",
                Str(e, "sub") ?? "", Str(e, "description"))).ToList();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static List<UnitListing> ParseUnitsTable(string text, params string[] suffixes) => text.Split('\n')
        .Select(l => l.Trim().TrimStart('●', '*', ' ').Split((char[])[' ', '\t'], 5, StringSplitOptions.RemoveEmptyEntries))
        .Where(f => f.Length >= 4 && (suffixes.Length == 0 ? [".service"] : suffixes).Any(x => f[0].EndsWith(x, StringComparison.Ordinal)))
        .Select(f => new UnitListing(f[0], f[1], f[2], f[3], f.Length > 4 ? f[4] : null))
        .ToList();

    /// <summary><c>systemctl list-unit-files --type=service</c>: each file's state (enabled, disabled, static, masked, alias, …).</summary>
    public static Dictionary<string, string> ListUnitFiles()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = Tool.Run("systemctl", ["list-unit-files", "--type=service", "--output=json", "--no-pager"], TimeSpan.FromSeconds(20));
        if (json.Ok && json.Output.TrimStart().StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(json.Output);
                foreach (var e in document.RootElement.EnumerateArray())
                {
                    if (Str(e, "unit_file") is { } name && Str(e, "state") is { } state)
                    {
                        result[name] = state;
                    }
                }
                return result;
            }
            catch (JsonException)
            {
            }
        }
        foreach (var line in Tool.Text("systemctl", ["list-unit-files", "--type=service", "--plain", "--no-legend", "--no-pager"], TimeSpan.FromSeconds(20)).Split('\n'))
        {
            var f = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 2 && f[0].EndsWith(".service", StringComparison.Ordinal))
            {
                result[f[0]] = f[1];
            }
        }
        return result;
    }

    /// <summary>One <c>systemctl show</c> for many units (at most 400 per call): their properties, a block each, split at the blank lines.</summary>
    public static List<Dictionary<string, string>> Show(IReadOnlyList<string> units, IReadOnlyList<string> properties,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = new List<Dictionary<string, string>>();
        foreach (var chunk in units.Chunk(400))
        {
            var output = Tool.Text("systemctl", ["show", "--no-pager", "-p", string.Join(',', properties), .. chunk], TimeSpan.FromSeconds(30), environment: environment);
            result.AddRange(ParseShow(output));
        }
        return result;
    }

    public static List<Dictionary<string, string>> ParseShow(string output)
    {
        var blocks = new List<Dictionary<string, string>>();
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                if (current.Count > 0)
                {
                    blocks.Add(current);
                    current = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                continue;
            }
            var equals = line.IndexOf('=');
            if (equals > 0)
            {
                current[line[..equals]] = line[(equals + 1)..].Trim();
            }
        }
        if (current.Count > 0)
        {
            blocks.Add(current);
        }
        return blocks.Where(b => b.ContainsKey("Id")).ToList();
    }

    // --- timers -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The timers: <c>list-timers --all --output=json</c> (next and last in µs), with each timer's unit file state; else <c>show</c> for each
    /// timer with UTC times (systemd without JSON for timers).
    /// </summary>
    public static List<TimerEntry> Timers()
    {
        var json = Tool.Run("systemctl", ["list-timers", "--all", "--output=json", "--no-pager"], TimeSpan.FromSeconds(15));
        if (json.Ok && json.Output.TrimStart().StartsWith('[') && ParseTimersJson(json.Output) is { } timers)
        {
            var states = timers.Count == 0 ? [] : Show(timers.Select(t => t.Name).ToList(), ["Id", "UnitFileState"]);
            return timers.Select(t => t with { UnitFileState = Blank(states.FirstOrDefault(s => s["Id"] == t.Name)?.GetValueOrDefault("UnitFileState")) })
                .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        }
        var names = Tool.Text("systemctl", ["list-units", "--type=timer", "--all", "--plain", "--no-legend", "--no-pager"], TimeSpan.FromSeconds(15)).Split('\n')
            .Select(l => l.Trim().TrimStart('●', '*', ' ').Split(' ', 2)[0]).Where(n => n.EndsWith(".timer", StringComparison.Ordinal)).ToList();
        if (names.Count == 0)
        {
            return [];
        }
        return Show(names, ["Id", "Unit", "LastTriggerUSec", "NextElapseUSecRealtime", "UnitFileState"], new Dictionary<string, string> { ["TZ"] = "UTC" })
            .Select(t => new TimerEntry(t["Id"], Blank(t.GetValueOrDefault("Unit")), UtcTime(t.GetValueOrDefault("LastTriggerUSec")),
                UtcTime(t.GetValueOrDefault("NextElapseUSecRealtime")), Blank(t.GetValueOrDefault("UnitFileState"))))
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    public static List<TimerEntry>? ParseTimersJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateArray().Select(e => new TimerEntry(Str(e, "unit") ?? "?", Blank(Str(e, "activates")), Microseconds(e, "last"),
                Microseconds(e, "next"), null)).ToList();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static DateTimeOffset? Microseconds(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var us) && us > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(us / 1000)
            : null;

    /// <summary><c>Sat 2026-11-14 02:11:22 UTC</c> (systemctl show with TZ=UTC), or null for <c>n/a</c> and empty.</summary>
    public static DateTimeOffset? UtcTime(string? text) =>
        text is not null && Regex.Match(text, @"(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)") is { Success: true } m
        && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? new DateTimeOffset(at, TimeSpan.Zero)
            : null;

    // --- packages ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The package of each path (<c>dpkg -S</c>, one call for those not cached; a path dpkg doesn't know is tried in /usr and out of it, as
    /// merged /usr has both). Cached per path, size and modification time: a program replaced by hand is asked again.
    /// </summary>
    private Dictionary<string, string?> Owners(IReadOnlyList<string> paths)
    {
        if (!Tool.Exists("dpkg"))
        {
            throw new NotAvailableException("dpkg is not installed");
        }
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var ask = new List<string>();
        lock (gate)
        {
            foreach (var path in paths)
            {
                var facts = Native.StatFull(path, followLinks: true);
                if (owners.TryGetValue(path, out var known) && known.Size == (facts?.Size ?? -1) && known.Modified == facts?.ModifiedAt)
                {
                    result[path] = known.Package;
                }
                else
                {
                    ask.Add(path);
                }
            }
        }
        if (ask.Count == 0)
        {
            return result;
        }
        var candidates = ask.SelectMany(p => Alternatives(p).Select(a => (Path: p, Candidate: a))).ToList();
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in candidates.Select(c => c.Candidate).Distinct(StringComparer.Ordinal).Chunk(200))
        {
            // Exit 1 when some path is no package's: the found ones are still on stdout.
            foreach (var (package, file) in ParseDpkgS(Tool.Run("dpkg", ["-S", .. chunk], TimeSpan.FromSeconds(60)).Output))
            {
                found.TryAdd(file, package);
            }
        }
        lock (gate)
        {
            foreach (var path in ask)
            {
                var package = Alternatives(path).Select(a => found.GetValueOrDefault(a)).FirstOrDefault(p => p is not null);
                var facts = Native.StatFull(path, followLinks: true);
                owners[path] = (facts?.Size ?? -1, facts?.ModifiedAt, package);
                result[path] = package;
            }
        }
        return result;
    }

    /// <summary>A path as dpkg may list it: itself, with or without /usr (merged /usr), and where its symlink points.</summary>
    public static IEnumerable<string> Alternatives(string path)
    {
        yield return path;
        if (path.StartsWith("/usr/", StringComparison.Ordinal) && path[4..].Split('/')[1] is "bin" or "sbin" or "lib" or "lib64")
        {
            yield return path[4..];
        }
        else if (path.Split('/') is [_, "bin" or "sbin" or "lib" or "lib64", ..])
        {
            yield return "/usr" + path;
        }
        string? target = null;
        try
        {
            target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
        }
        if (target is not null && target != path)
        {
            yield return target;
        }
    }

    /// <summary><c>dpkg -S</c>'s lines: <c>openssh-server: /usr/sbin/sshd</c>, <c>libc6:arm64: /lib/…</c>, diversions skipped.</summary>
    public static IEnumerable<(string Package, string Path)> ParseDpkgS(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf(": /", StringComparison.Ordinal);
            if (separator <= 0 || line.StartsWith("diversion ", StringComparison.Ordinal))
            {
                continue;
            }
            var path = line[(separator + 2)..].Trim();
            foreach (var package in line[..separator].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                yield return (package.Split(':')[0], path);
            }
        }
    }

    /// <summary>
    /// Each package's origin, every 15 minutes: <c>apt-cache policy</c> maps each source to its Release file's origin (<c>o=Debian</c>),
    /// <c>apt-cache policy &lt;packages&gt;</c> names the sources of each installed version. Only <c>/var/lib/dpkg/status</c>: installed by
    /// hand, <c>local</c>.
    /// </summary>
    private Dictionary<string, string?> Origins(IReadOnlyList<string> wanted)
    {
        lock (gate)
        {
            if (DateTimeOffset.UtcNow - originsAt < PackagesEvery && wanted.All(origins.ContainsKey))
            {
                return origins;
            }
        }
        if (!Tool.Exists("apt-cache"))
        {
            throw new NotAvailableException("apt-cache is not installed");
        }
        var sources = ParseSources(Tool.Text("apt-cache", ["policy"], TimeSpan.FromSeconds(30)));
        var next = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var chunk in wanted.Chunk(200))
        {
            foreach (var (package, origin) in ParsePackagePolicy(Tool.Run("apt-cache", ["policy", .. chunk], TimeSpan.FromSeconds(60)).Output, sources))
            {
                next[package] = origin;
            }
        }
        foreach (var package in wanted.Where(w => !next.ContainsKey(w)))
        {
            next[package] = null;
        }
        lock (gate)
        {
            origins = next;
            originsAt = DateTimeOffset.UtcNow;
            return origins;
        }
    }

    /// <summary><c>apt-cache policy</c>'s package files: <c>500 http://deb.debian.org/debian bookworm/main arm64 Packages</c> → <c>o=Debian</c>.</summary>
    public static Dictionary<string, string> ParseSources(string output)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (SourceLine(line) is { } source)
            {
                current = source;
            }
            else if (current is not null && line.StartsWith("release ", StringComparison.Ordinal)
                     && Regex.Match(line, @"(?:^release |,)o=([^,]+)") is { Success: true } origin)
            {
                sources[current] = origin.Groups[1].Value;
            }
        }
        return sources;
    }

    /// <summary>
    /// <c>apt-cache policy pkg…</c>: per package, the sources under the installed version (<c> *** 1:9.2p1-2 500</c>); the first archive's
    /// origin. When only dpkg's status knows the installed version: an archive that has other versions of it (the installed one was
    /// superseded, not yet upgraded) as <c>Ubuntu (superseded)</c>; none at all is <c>local</c>, installed by hand.
    /// </summary>
    public static IEnumerable<(string Package, string? Origin)> ParsePackagePolicy(string output, IReadOnlyDictionary<string, string> sources)
    {
        string? package = null;
        var installed = false;
        var found = new List<string>();
        var others = new List<string>();
        var local = false;
        (string, string?) Result(string name) =>
            (name, found.FirstOrDefault() ?? (others.Count > 0 ? $"{others[0]} (superseded)" : local ? "local" : null));
        foreach (var raw in output.Split('\n'))
        {
            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]) && raw.TrimEnd().EndsWith(':'))
            {
                if (package is not null)
                {
                    yield return Result(package);
                }
                package = raw.TrimEnd()[..^1].Split(':')[0];
                installed = false;
                found.Clear();
                others.Clear();
                local = false;
                continue;
            }
            var line = raw.Trim();
            if (line.StartsWith("*** ", StringComparison.Ordinal))
            {
                installed = true;
                continue;
            }
            if (SourceLine(line) is { } name)
            {
                if (name == "/var/lib/dpkg/status")
                {
                    local |= installed;
                }
                else
                {
                    (installed ? found : others).Add(sources.GetValueOrDefault(name) ?? Host(name));
                }
                continue;
            }
            if (installed && Regex.IsMatch(line, @"^\S+ -?\d+$"))
            {
                // The next version's line: the installed one's sources are done.
                installed = false;
            }
        }
        if (package is not null)
        {
            yield return Result(package);
        }
    }

    /// <summary>A source's line (<c>500 http://… bookworm/main arm64 Packages</c>, <c>100 /var/lib/dpkg/status</c>): the source, or null.</summary>
    private static string? SourceLine(string line) =>
        Regex.Match(line, @"^-?\d+ (\S.*)$") is { Success: true } m && m.Groups[1].Value.Contains('/') ? m.Groups[1].Value.Trim() : null;

    private static string Host(string source) => Uri.TryCreate(source.Split(' ')[0], UriKind.Absolute, out var uri) ? uri.Host : source.Split(' ')[0];

    /// <summary>The installed packages (<c>dpkg-query -W</c>'s <c>ii</c> lines), every 15 minutes.</summary>
    private int PackageCount()
    {
        lock (gate)
        {
            if (DateTimeOffset.UtcNow - packagesAt < PackagesEvery)
            {
                return packages;
            }
        }
        if (!Tool.Exists("dpkg-query"))
        {
            throw new NotAvailableException("dpkg-query is not installed");
        }
        var count = Tool.Text("dpkg-query", ["-W", "-f", "${db:Status-Abbrev}\n"], TimeSpan.FromSeconds(30)).Split('\n').Count(l => l.StartsWith("ii", StringComparison.Ordinal));
        lock (gate)
        {
            packages = count;
            packagesAt = DateTimeOffset.UtcNow;
        }
        return count;
    }

    /// <summary>The snaps and their publishers (<c>snap list</c>), when snapd is installed: <c>canonical✓</c> is Canonical.</summary>
    private static Dictionary<string, string?> Snaps()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!Tool.Exists("snap"))
        {
            return result;
        }
        var list = Tool.Run("snap", ["list", "--unicode=never", "--color=never"], TimeSpan.FromSeconds(20));
        foreach (var line in list.Output.Split('\n').Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 5)
            {
                result[f[0]] = f[4].TrimEnd('*', '✓', '✪', '!');
            }
        }
        return result;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
