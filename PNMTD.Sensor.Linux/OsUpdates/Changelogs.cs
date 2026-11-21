using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.OsUpdates;

/// <summary>
/// The changelogs of pending source versions (docs/linux-os-updates-plan.md §6.2), to find the CVEs an update fixes. First
/// <c>apt-get changelog</c>, which fetches only the changelog; O0 found that it answers 404 for Debian's security uploads (their
/// changelogs reach metadata.ftp-master only with a point release), so then the candidate .deb itself is downloaded into a folder of
/// the agent's and its <c>changelog.Debian.gz</c> read. Only code reads the text (the CVE and urgency patterns); it never reaches an
/// AI. At most <see cref="PerDay"/> source versions a day, one at a time; each is tried once.
/// </summary>
internal static class Changelogs
{
    public const int PerDay = 50;

    /// <summary>A .deb larger than this isn't downloaded for its changelog.</summary>
    private const long MaxDebBytes = 60L * 1024 * 1024;

    /// <summary>One binary per pending source version not fetched yet: the source, its candidate and installed versions, and a binary to read.</summary>
    public static List<(string Source, string Version, string? Installed, string Binary, string Candidate)> Wanted(IEnumerable<PendingPackage> packages,
        IReadOnlyDictionary<string, DateOnly> fetched) => packages
        .Where(p => p.Installed is not null && p.Candidate.Length > 0 && !p.Phased)
        .Select(p => (Source: p.Source ?? p.Name, Version: p.SourceVersion ?? p.Candidate, Installed: p.InstalledSourceVersion ?? p.Installed, Binary: p.Name, p.Candidate))
        .Where(p => !fetched.ContainsKey(Key(p.Source, p.Version)))
        .GroupBy(p => (p.Source, p.Version)).Select(g => g.OrderBy(p => p.Binary.Length).First())
        // Security archives first: their CVEs matter most.
        .OrderBy(p => packages.Any(x => x.Name == p.Binary && x.Security) ? 0 : 1).ThenBy(p => p.Source, StringComparer.Ordinal)
        .ToList();

    public static string Key(string source, string version) => $"{source}={version}";

    /// <summary>The changelog part newer than the installed version, or null when neither way found one.</summary>
    public static PackageChangelog? Fetch(string distribution, string source, string version, string? installed, string binary, string candidate, string workFolder)
    {
        var (text, from) = ViaAptGetChangelog(binary, candidate) is { } direct ? (direct, "apt-get changelog") : (ViaDeb(binary, candidate, workFolder), "the .deb");
        if (text is null)
        {
            return null;
        }
        var (since, cves, urgencies) = AptParsers.ChangelogSince(text, installed);
        return since.Length == 0 && cves.Count == 0 ? null
            : new PackageChangelog(distribution, source, version, installed, cves.Take(500).ToList(), urgencies, since, from);
    }

    private static string? ViaAptGetChangelog(string binary, string candidate)
    {
        try
        {
            var result = Tool.Run("apt-get", ["changelog", "-q", $"{binary}={candidate}"], TimeSpan.FromSeconds(60), environment: AptState.Environment());
            return result.Ok && result.Output.Contains(" urgency=", StringComparison.Ordinal) ? result.Output : null;
        }
        catch (Exception e) when (e is NotAvailableException or TimeoutException)
        {
            return null;
        }
    }

    private static string? ViaDeb(string binary, string candidate, string workFolder)
    {
        var size = DebSize(binary, candidate);
        if (size is null or > MaxDebBytes)
        {
            return null;
        }
        var folder = Path.Combine(workFolder, "changelog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            // apt-get download writes into the working folder; the names are apt's own, passed as arguments, never into the script.
            var download = Tool.Run("sh", ["-c", "cd \"$1\" && exec apt-get download -q -o APT::Sandbox::User=root \"$2\"", "sh", folder, $"{binary}={candidate}"],
                TimeSpan.FromSeconds(180), environment: AptState.Environment());
            var deb = Directory.GetFiles(folder, "*.deb").FirstOrDefault();
            if (!download.Ok || deb is null)
            {
                return null;
            }
            var read = Tool.Run("sh", ["-c", "dpkg-deb --fsys-tarfile \"$1\" | tar -xO --wildcards './usr/share/doc/*/changelog.Debian.gz' 2>/dev/null | gzip -dc", "sh", deb],
                TimeSpan.FromSeconds(60));
            return read.Output.Contains(" urgency=", StringComparison.Ordinal) ? read.Output : null;
        }
        catch (Exception e) when (e is NotAvailableException or TimeoutException or IOException)
        {
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The .deb's size from <c>apt-cache show</c>'s <c>Size:</c>.</summary>
    private static long? DebSize(string binary, string candidate)
    {
        var output = Tool.Run("apt-cache", ["show", "--no-all-versions", $"{binary}={candidate}"], TimeSpan.FromSeconds(30), environment: AptState.Environment()).Output;
        var line = output.Split('\n').FirstOrDefault(l => l.StartsWith("Size: ", StringComparison.Ordinal));
        return line is not null && long.TryParse(line[6..].Trim(), out var size) ? size : null;
    }
}
