using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Linux.OsUpdates;

/// <summary>A line of apt's simulation: <c>Inst</c> (installed null for a new package) or <c>Remv</c>.</summary>
internal sealed record SimulatedChange(bool Remove, string Name, string? Arch, string? Installed, string? Candidate, string? Origins);

/// <summary>apt-get's simulation of one mode: its lines, the kept-back and phased packages.</summary>
internal sealed record Simulation(IReadOnlyList<SimulatedChange> Changes, IReadOnlyList<string> KeptBack, IReadOnlyList<string> Phased)
{
    public IEnumerable<SimulatedChange> Installs => Changes.Where(c => !c.Remove);

    public IEnumerable<SimulatedChange> Removals => Changes.Where(c => c.Remove);
}

/// <summary>What <c>apt-get update</c> said: the repositories reached and failed, the warnings and errors, and "Fetched …".</summary>
internal sealed record UpdateOutput(int Reached, int Failed, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, string? Fetched, bool Locked);

/// <summary>
/// apt's, dpkg's and needrestart's output (docs/linux-os-updates-plan.md §4), as pure functions, for the feature and the tests. The
/// formats are those of O0's fixtures from Debian 12 (apt 2.6, needrestart 3.6), and apt's documented ones.
/// </summary>
internal static partial class AptParsers
{
    // Inst libssl3 [3.0.20-1~deb12u2] (3.0.22-1~deb12u2 Debian-Security:12/oldstable-security [arm64]) []
    [GeneratedRegex(@"^Inst (?<name>\S+) (?:\[(?<installed>[^\]]*)\] )?\((?<candidate>\S+)(?: (?<origins>[^\[]*?))?\s*\[(?<arch>[^\]]+)\]\)")]
    private static partial Regex InstLine();

    // Remv foo [1.2-3] / Remv foo:arm64 [1.2-3]
    [GeneratedRegex(@"^Remv (?<name>\S+)(?: \[(?<installed>[^\]]*)\])?")]
    private static partial Regex RemvLine();

    // "Need to get 1,234 kB/5,246 kB of archives": the first is what is still to download (the rest is in apt's cache).
    [GeneratedRegex(@"Need to get (?<size>[\d.,]+) ?(?<unit>[kMG]?B)(?:/[\d.,]+ ?[kMG]?B)? of archives")]
    private static partial Regex NeedToGet();

    [GeneratedRegex(@"After this operation, (?<size>[\d.,]+) ?(?<unit>[kMG]?B) (?:of )?(?:additional )?disk space will be (?<way>used|freed)")]
    private static partial Regex DiskSpace();

    // Get:4 http://deb.debian.org/debian-security bookworm-security/main arm64 Packages [377 kB]: a numbered item of a repository (URL, suite).
    [GeneratedRegex(@"^(?<kind>Hit|Get|Ign|Err):(?<n>\d+) (?<url>\S+) (?<suite>[^/\s]+)")]
    private static partial Regex UpdateLine();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    // Configuration file '/etc/foo.conf'
    [GeneratedRegex(@"^Configuration file '(?<path>[^']+)'")]
    private static partial Regex ConfigFile();

    /// <summary>A security archive: <c>Debian-Security:12/oldstable-security</c>, <c>Ubuntu:24.04/noble-security</c>, ESM's <c>-infra-security</c>.</summary>
    public static bool IsSecurityOrigin(string? origins) => origins is not null
        && (origins.Contains("-security", StringComparison.OrdinalIgnoreCase) || origins.Contains("Security:", StringComparison.OrdinalIgnoreCase));

    // --- the simulations (§4.1) -----------------------------------------------------------------------------------------

    public static Simulation ParseSimulation(string output)
    {
        var changes = new List<SimulatedChange>();
        var keptBack = new List<string>();
        var phased = new List<string>();
        List<string>? block = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (block is not null)
            {
                if (line.StartsWith("  ", StringComparison.Ordinal))
                {
                    block.AddRange(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    continue;
                }
                block = null;
            }
            if (line.StartsWith("The following packages have been kept back:", StringComparison.Ordinal))
            {
                block = keptBack;
                continue;
            }
            if (line.StartsWith("The following upgrades have been deferred due to phasing:", StringComparison.Ordinal))
            {
                block = phased;
                continue;
            }
            if (InstLine().Match(line) is { Success: true } inst)
            {
                var (name, arch) = NameAndArch(inst.Groups["name"].Value, inst.Groups["arch"].Value);
                changes.Add(new SimulatedChange(false, name, arch, NullIfEmpty(inst.Groups["installed"].Value), inst.Groups["candidate"].Value,
                    NullIfEmpty(inst.Groups["origins"].Value.Trim())));
            }
            else if (RemvLine().Match(line) is { Success: true } remv)
            {
                var (name, arch) = NameAndArch(remv.Groups["name"].Value, null);
                changes.Add(new SimulatedChange(true, name, arch, NullIfEmpty(remv.Groups["installed"].Value), null, null));
            }
        }
        // A package can appear twice (Inst, then a later Inst after a Remv in a complex plan): the last one counts.
        var distinct = changes.GroupBy(c => (c.Remove, c.Name, c.Arch)).Select(g => g.Last()).ToList();
        return new Simulation(distinct, keptBack.Distinct(StringComparer.Ordinal).ToList(), phased.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary><c>libssl3:arm64</c> → (libssl3, arm64); the architecture of Inst's bracket otherwise (<c>all</c>, <c>arm64</c>).</summary>
    private static (string Name, string? Arch) NameAndArch(string name, string? bracketArch)
    {
        var colon = name.IndexOf(':');
        return colon > 0 ? (name[..colon], name[(colon + 1)..]) : (name, NullIfEmpty(bracketArch?.Trim()));
    }

    /// <summary><c>apt-get --assume-no</c>'s sizes: what it needs to download, and the extra disk space (negative: freed). In bytes, apt's SI units.</summary>
    public static (long? Download, long? Disk) ParseSizes(string output)
    {
        long? download = NeedToGet().Match(output) is { Success: true } n ? Bytes(n.Groups["size"].Value, n.Groups["unit"].Value) : null;
        long? disk = null;
        if (DiskSpace().Match(output) is { Success: true } d && Bytes(d.Groups["size"].Value, d.Groups["unit"].Value) is { } bytes)
        {
            disk = d.Groups["way"].Value == "freed" ? -bytes : bytes;
        }
        return (download, disk);
    }

    private static long? Bytes(string size, string unit)
    {
        // apt prints its own locale-free format, but a thousands separator can appear in other builds: "5,246 kB".
        var text = size.Replace(",", "", StringComparison.Ordinal);
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }
        var factor = unit switch { "kB" => 1_000d, "MB" => 1_000_000d, "GB" => 1_000_000_000d, _ => 1d };
        return (long)Math.Round(value * factor);
    }

    /// <summary>
    /// <c>apt-cache show --no-all-versions pkg=ver …</c>: each stanza's package and version, and its source package and version (the
    /// <c>Source:</c> field, <c>name</c> or <c>name (version)</c>; none: the package itself and its own version).
    /// </summary>
    public static Dictionary<(string Package, string Version), (string Source, string SourceVersion)> ParseAptCacheShow(string output)
    {
        var result = new Dictionary<(string, string), (string, string)>();
        foreach (var stanza in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? package = null, version = null, source = null, sourceVersion = null;
            foreach (var line in stanza.Split('\n'))
            {
                if (line.StartsWith("Package: ", StringComparison.Ordinal))
                {
                    package = line[9..].Trim();
                }
                else if (line.StartsWith("Version: ", StringComparison.Ordinal))
                {
                    version = line[9..].Trim();
                }
                else if (line.StartsWith("Source: ", StringComparison.Ordinal))
                {
                    var value = line[8..].Trim();
                    var paren = value.IndexOf(" (", StringComparison.Ordinal);
                    if (paren > 0 && value.EndsWith(')'))
                    {
                        source = value[..paren];
                        sourceVersion = value[(paren + 2)..^1];
                    }
                    else
                    {
                        source = value;
                    }
                }
            }
            if (package is not null && version is not null)
            {
                result[(package, version)] = (source ?? package, sourceVersion ?? version);
            }
        }
        return result;
    }

    /// <summary>
    /// <c>dpkg-query -W -f='${Package}:${Architecture} ${Version} ${source:Package} ${source:Version}\n'</c>: installed packages by
    /// name and architecture, with their source and its version.
    /// </summary>
    public static Dictionary<(string Name, string Arch), (string Version, string Source, string SourceVersion)> ParseDpkgQuery(string output)
    {
        var result = new Dictionary<(string, string), (string, string, string)>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }
            var colon = parts[0].LastIndexOf(':');
            var (name, arch) = colon > 0 ? (parts[0][..colon], parts[0][(colon + 1)..]) : (parts[0], "");
            result[(name, arch)] = (parts[1], parts[2], parts[3]);
        }
        return result;
    }

    // --- an update's output (§4.4) ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>apt-get update</c>: the repositories (URL and suite; apt numbers each file it fetches, O0) reached (Hit, Get) and failed (Err; Ign
    /// then Err is one failure), the <c>W:</c> and <c>E:</c> lines, "Fetched …", and whether another apt held the lists' lock (apt 2.6
    /// doesn't wait for it, O0).
    /// </summary>
    public static UpdateOutput ParseUpdate(string output)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var errors = new List<string>();
        string? fetched = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (UpdateLine().Match(line) is { Success: true } m)
            {
                var n = $"{m.Groups["url"].Value} {m.Groups["suite"].Value}";
                switch (m.Groups["kind"].Value)
                {
                    case "Hit" or "Get":
                        reached.Add(n);
                        break;
                    case "Err":
                        failed.Add(n);
                        break;
                }
            }
            else if (line.StartsWith("W: ", StringComparison.Ordinal))
            {
                warnings.Add(line[3..].Trim());
            }
            else if (line.StartsWith("E: ", StringComparison.Ordinal))
            {
                errors.Add(line[3..].Trim());
            }
            else if (line.StartsWith("Fetched ", StringComparison.Ordinal))
            {
                fetched = line.Trim();
            }
        }
        // A repository whose InRelease is "Get" and whose Packages fail is both; Err wins.
        reached.ExceptWith(failed);
        var locked = errors.Any(e => e.StartsWith("Could not get lock", StringComparison.Ordinal) || e.StartsWith("Unable to lock", StringComparison.Ordinal));
        return new UpdateOutput(reached.Count, failed.Count, warnings.Distinct().Take(50).ToList(), errors.Distinct().Take(50).ToList(), fetched, locked);
    }

    /// <summary>
    /// An update's state from its exit code and output (§4.4, O0): exit 0 without warnings or failed repositories is Succeeded; some
    /// repositories failed or apt warned, with at least one reached, is Warnings (apt exits 100 for a 401 repository while the others
    /// were refreshed); nothing reached, or another failure, is Failed.
    /// </summary>
    public static OsUpdateState UpdateState(int? exitCode, UpdateOutput output)
    {
        if (exitCode == 0 && output.Failed == 0 && output.Warnings.Count == 0 && output.Errors.Count == 0)
        {
            return OsUpdateState.Succeeded;
        }
        if (output.Reached > 0 && !output.Locked && (output.Failed > 0 || output.Errors.Count > 0 || output.Warnings.Count > 0))
        {
            return OsUpdateState.Warnings;
        }
        return exitCode == 0 ? OsUpdateState.Warnings : OsUpdateState.Failed;
    }

    // --- an upgrade's output (§4.4) ----------------------------------------------------------------------------------------

    /// <summary>
    /// What an upgrade's output says besides history.log: the configuration files where the local version was kept (dpkg's
    /// "Configuration file '…'" followed by "Keeping old config file"), needrestart's list of services (D4: only reported), and the
    /// <c>W:</c>/<c>E:</c> lines.
    /// </summary>
    public static (List<string> ConfigFilesKept, List<string> Services, List<string> Warnings, List<string> Errors) ParseUpgrade(string output)
    {
        var kept = new List<string>();
        var services = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();
        string? configFile = null;
        var inServices = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (ConfigFile().Match(line) is { Success: true } c)
            {
                configFile = c.Groups["path"].Value;
                continue;
            }
            if (configFile is not null && line.Contains("Keeping old config file", StringComparison.Ordinal))
            {
                kept.Add(configFile);
                configFile = null;
                continue;
            }
            if (line.StartsWith("Services to be restarted:", StringComparison.Ordinal) || line.StartsWith("Service restarts being deferred:", StringComparison.Ordinal))
            {
                inServices = true;
                continue;
            }
            if (inServices)
            {
                var item = line.Trim();
                if (item.Length == 0)
                {
                    inServices = false;
                    continue;
                }
                // " systemctl restart ssh.service", or " /etc/needrestart/restart.d/systemd-manager".
                var service = item.StartsWith("systemctl restart ", StringComparison.Ordinal) ? item["systemctl restart ".Length..].Trim() : Path.GetFileName(item);
                services.AddRange(service.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                continue;
            }
            if (line.StartsWith("W: ", StringComparison.Ordinal))
            {
                warnings.Add(line[3..].Trim());
            }
            else if (line.StartsWith("E: ", StringComparison.Ordinal))
            {
                errors.Add(line[3..].Trim());
            }
        }
        return (kept.Distinct().ToList(), services.Distinct().ToList(), warnings.Distinct().Take(50).ToList(), errors.Distinct().Take(50).ToList());
    }

    // --- needrestart (D4) -----------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>needrestart -b -r l</c>: <c>NEEDRESTART-SVC: ssh.service</c> per service, and the kernel (<c>KCUR</c>, <c>KEXP</c>,
    /// <c>KSTA</c>: 2 an ABI-compatible upgrade, 3 a version upgrade pending).
    /// </summary>
    public static NeedsRestartInfo ParseNeedrestart(string output)
    {
        var services = new List<string>();
        string? current = null, expected = null, status = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var key = line[..colon];
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "NEEDRESTART-SVC":
                    services.Add(value);
                    break;
                case "NEEDRESTART-KCUR":
                    current = value;
                    break;
                case "NEEDRESTART-KEXP":
                    expected = value;
                    break;
                case "NEEDRESTART-KSTA":
                    status = value;
                    break;
            }
        }
        return new NeedsRestartInfo(services.Distinct().Take(200).ToList(), status is "2" or "3", current, expected);
    }

    // --- history.log (§4.4, §4.5) -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>/var/log/apt/history.log</c>: entries separated by blank lines, each with <c>Start-Date</c> and <c>End-Date</c> (the server's
    /// local time, two spaces between date and time), <c>Commandline</c>, <c>Requested-By</c>, and package lists such as
    /// <c>Upgrade: name:arch (from, to), …</c>, <c>Install: name:arch (to, automatic)</c>, <c>Remove: name:arch (from)</c>.
    /// </summary>
    public static List<AptHistoryEntry> ParseHistory(string text, TimeZoneInfo zone)
    {
        var entries = new List<AptHistoryEntry>();
        foreach (var block in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            DateTimeOffset? start = null, end = null;
            string? commandline = null, requestedBy = null, error = null;
            var upgraded = new List<PackageChange>();
            var installed = new List<PackageChange>();
            var removed = new List<PackageChange>();
            foreach (var line in block.Split('\n'))
            {
                var colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }
                var key = line[..colon];
                var value = line[(colon + 2)..].Trim();
                switch (key)
                {
                    case "Start-Date":
                        start = LocalTime(value, zone);
                        break;
                    case "End-Date":
                        end = LocalTime(value, zone);
                        break;
                    case "Commandline":
                        commandline = value;
                        break;
                    case "Requested-By":
                        requestedBy = value;
                        break;
                    case "Error":
                        error = value;
                        break;
                    case "Upgrade" or "Downgrade":
                        upgraded.AddRange(Packages(value, two: true));
                        break;
                    case "Install" or "Reinstall":
                        installed.AddRange(Packages(value, two: false).Select(p => p with { To = p.From, From = null }));
                        break;
                    case "Remove" or "Purge":
                        removed.AddRange(Packages(value, two: false));
                        break;
                }
            }
            if (start is null)
            {
                continue;
            }
            var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{start:O}\n{commandline}")), 0, 8).ToLowerInvariant();
            entries.Add(new AptHistoryEntry(id, start.Value, end, commandline, requestedBy, upgraded, installed, removed, error));
        }
        return entries;
    }

    /// <summary>"2026-11-21  09:56:32" in the server's zone.</summary>
    private static DateTimeOffset? LocalTime(string value, TimeZoneInfo zone)
    {
        var normal = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!DateTime.TryParseExact(normal, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }
        var offset = zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>"libssl3:arm64 (3.0.20-1~deb12u2, 3.0.22-1~deb12u2), tzdata:all (2026b, 2026f)": the list's packages, split on "), ".</summary>
    private static IEnumerable<PackageChange> Packages(string value, bool two)
    {
        foreach (var item in value.Split("), ", StringSplitOptions.RemoveEmptyEntries))
        {
            var text = item.TrimEnd(')');
            var paren = text.IndexOf(" (", StringComparison.Ordinal);
            if (paren <= 0)
            {
                continue;
            }
            var (name, arch) = NameAndArch(text[..paren].Trim(), null);
            var versions = text[(paren + 2)..].Split(", ", StringSplitOptions.TrimEntries);
            // "automatic" is a mark, not a version.
            var list = versions.Where(v => v != "automatic").ToList();
            yield return two
                ? new PackageChange(name, arch, list.ElementAtOrDefault(0), list.ElementAtOrDefault(1))
                : new PackageChange(name, arch, list.ElementAtOrDefault(0), null);
        }
    }

    // --- changelogs (§6.2) -----------------------------------------------------------------------------------------------

    /// <summary>The changelog's part newer than the installed version, with its CVEs and urgencies (<see cref="DebianChangelog.Since"/>).</summary>
    public static (string Text, List<string> Cves, List<string> Urgencies) ChangelogSince(string changelog, string? installed) =>
        DebianChangelog.Since(changelog, installed);

    // --- logs -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A log as it is stored and shown: dpkg's progress (lines overwritten with a carriage return) reduced to what was left on the line,
    /// ANSI escapes and other control characters removed, tabs and newlines kept.
    /// </summary>
    public static string Clean(string text)
    {
        var withoutEscapes = AnsiEscape().Replace(text, "");
        var builder = new StringBuilder(withoutEscapes.Length);
        foreach (var raw in withoutEscapes.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw;
            var cr = line.LastIndexOf('\r');
            if (cr >= 0)
            {
                // What the terminal showed last: after the final carriage return, unless that is empty.
                var after = line[(cr + 1)..];
                line = after.Length > 0 ? after : line[..cr][(line[..cr].LastIndexOf('\r') + 1)..];
            }
            foreach (var c in line)
            {
                if (c == '\t' || !char.IsControl(c))
                {
                    builder.Append(c);
                }
            }
            builder.Append('\n');
        }
        if (builder.Length > 0 && !text.EndsWith('\n'))
        {
            builder.Length--;
        }
        return builder.ToString();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
