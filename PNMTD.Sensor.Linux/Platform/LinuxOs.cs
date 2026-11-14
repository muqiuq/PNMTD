using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Linux.Platform;

/// <summary>
/// The version check (docs/linux-sensor-plan.md §4.8), at every start: <c>/etc/os-release</c>, <c>/etc/debian_version</c>, and for
/// Proxmox VE <c>pveversion</c> and <c>/etc/pve</c>. Built for Debian 12 and 13; supported too: Proxmox VE 8 and 9, Ubuntu 22.04,
/// 24.04 and 26.04. Anything else with systemd and glibc is logged, reported, flagged and runs anyway; musl, no systemd and 32-bit
/// are refused by <c>join</c>, with the reason.
/// </summary>
internal static partial class LinuxOs
{
    private static readonly string[] SupportedUbuntu = ["22.04", "24.04", "26.04"];

    [GeneratedRegex(@"pve-manager/(\d+(?:\.\d+)*)")]
    private static partial Regex PveManager();

    /// <summary>Reads the distribution, its point release, the kernel, Proxmox VE when it is one, and systemd's version.</summary>
    public static LinuxOsInfo Read()
    {
        var release = ParseOsRelease(Tool.ReadFile("/etc/os-release") ?? Tool.ReadFile("/usr/lib/os-release") ?? "");
        var id = release.GetValueOrDefault("ID")?.ToLowerInvariant() ?? "linux";
        var idLike = (release.GetValueOrDefault("ID_LIKE") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var versionId = release.GetValueOrDefault("VERSION_ID");
        var codename = release.GetValueOrDefault("VERSION_CODENAME") ?? release.GetValueOrDefault("UBUNTU_CODENAME");
        var pretty = release.GetValueOrDefault("PRETTY_NAME") ?? release.GetValueOrDefault("NAME") ?? "Linux";
        var debian = Tool.ReadValue("/etc/debian_version");
        var proxmox = IsProxmox();
        var pve = proxmox ? ProxmoxVersion() : null;
        string? systemd = null;
        try
        {
            systemd = SystemdVersion(Tool.Run("systemctl", ["--version"], TimeSpan.FromSeconds(10)).Output);
        }
        catch (Exception e) when (e is Core.Collectors.NotAvailableException or TimeoutException or InvalidOperationException)
        {
        }
        return new LinuxOsInfo(pretty, id, idLike, versionId, codename, debian, Tool.ReadValue("/proc/sys/kernel/osrelease") ?? Environment.OSVersion.Version.ToString(),
            Tool.ReadValue("/proc/sys/kernel/version"), OsArchitecture(), Supported(id, versionId, proxmox, pve), Distribution(id, pretty, debian, proxmox, pve), systemd);
    }

    /// <summary>The hello's and the join's OS, as <see cref="AgentOs"/> documents it for Linux: the edition is <c>proxmox</c> on Proxmox VE.</summary>
    public static AgentOs ToAgentOs(LinuxOsInfo os) =>
        new(os.PrettyName, os.Distribution?.StartsWith("Proxmox VE", StringComparison.Ordinal) == true ? "proxmox" : os.Id, os.VersionId, os.KernelRelease,
            os.Codename, os.Supported);

    public static bool IsProxmox() => Directory.Exists("/etc/pve") || Tool.Exists("pveversion");

    /// <summary>Proxmox VE's version from <c>pveversion</c> (a Perl script: about a second), or null.</summary>
    public static string? ProxmoxVersion()
    {
        try
        {
            return ParseProxmoxVersion(Tool.Run("pveversion", [], TimeSpan.FromSeconds(20)).Output);
        }
        catch (Exception e) when (e is Core.Collectors.NotAvailableException or TimeoutException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Why <c>join</c> refuses this machine (§4.8), or null: 32-bit, musl, or no systemd.</summary>
    public static string? JoinRefusal()
    {
        if (RuntimeInformation.OSArchitecture is Architecture.X86 or Architecture.Arm or Architecture.Armv6)
        {
            return "32-bit systems are not supported (docs/linux-sensor-plan.md §4.8): the agent is built for x64 and arm64.";
        }
        if (File.Exists("/lib/ld-musl-x86_64.so.1") || File.Exists("/lib/ld-musl-aarch64.so.1"))
        {
            return "musl (Alpine) is not supported (§4.8): the agent needs glibc.";
        }
        if (!Directory.Exists("/run/systemd/system"))
        {
            return "systemd is not running here (no /run/systemd/system): the agent is a systemd service (§4.8).";
        }
        return null;
    }

    // --- pure parsing -----------------------------------------------------------------------------------------------

    /// <summary>os-release's <c>KEY=value</c> lines (os-release(5)): values quoted with " or ', backslash escapes inside double quotes.</summary>
    public static IReadOnlyDictionary<string, string> ParseOsRelease(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || equals <= 0)
            {
                continue;
            }
            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                var unescaped = new StringBuilder();
                for (var i = 1; i < value.Length - 1; i++)
                {
                    if (value[i] == '\\' && i + 1 < value.Length - 1)
                    {
                        i++;
                    }
                    unescaped.Append(value[i]);
                }
                value = unescaped.ToString();
            }
            else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                value = value[1..^1];
            }
            values[line[..equals].Trim()] = value;
        }
        return values;
    }

    /// <summary><c>pve-manager/8.2.4/faa83925c9641325 (running kernel: 6.8.8-2-pve)</c> → <c>8.2.4</c>.</summary>
    public static string? ParseProxmoxVersion(string? output) => output is not null && PveManager().Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary><c>systemd 252 (252.30-1~deb12u2)</c> → <c>252 (252.30-1~deb12u2)</c>.</summary>
    public static string? SystemdVersion(string output) =>
        output.Split('\n')[0].Trim() is { } first && first.StartsWith("systemd ", StringComparison.Ordinal) ? first["systemd ".Length..].Trim() : null;

    /// <summary>
    /// §4.8's list: Debian 12 and 13; Proxmox VE 8 and 9 (by pveversion, or by the Debian underneath when it can't be asked);
    /// Ubuntu 22.04, 24.04 and 26.04.
    /// </summary>
    public static bool Supported(string id, string? versionId, bool proxmox, string? proxmoxVersion)
    {
        if (proxmox)
        {
            var major = int.TryParse(proxmoxVersion?.Split('.')[0], out var m) ? m : versionId switch { "12" => 8, "13" => 9, _ => 0 };
            return major is 8 or 9;
        }
        return id switch
        {
            "debian" => versionId is "12" or "13",
            "ubuntu" => SupportedUbuntu.Contains(versionId),
            _ => false,
        };
    }

    /// <summary>A label for PNMT: <c>Debian 12.7</c>, <c>Proxmox VE 8.2.4</c>, or the pretty name.</summary>
    public static string Distribution(string id, string pretty, string? debianVersion, bool proxmox, string? proxmoxVersion) =>
        proxmox ? $"Proxmox VE {proxmoxVersion ?? "?"}".TrimEnd()
        : id == "debian" && debianVersion is { Length: > 0 } && char.IsDigit(debianVersion[0]) ? $"Debian {debianVersion}"
        : pretty;

    private static string OsArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        var other => other.ToString().ToLowerInvariant(),
    };
}
