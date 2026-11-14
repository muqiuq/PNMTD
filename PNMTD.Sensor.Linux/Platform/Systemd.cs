using PNMTD.Sensor.Core;
using PNMTD.Sensor.Linux.Rights;

namespace PNMTD.Sensor.Linux.Platform;

/// <summary>
/// The unit and systemd (docs/linux-sensor-plan.md §4.3, §4.4 step 9): <c>/etc/systemd/system/pnmt-agent.service</c> from the
/// template in the binary, with <c>ReadWritePaths=</c> from config.yaml's <c>ssh_keys.writable_paths</c> when it names any;
/// <c>daemon-reload</c>, <c>enable --now</c> or a restart; and the program copied to <c>/usr/local/sbin/pnmt-agent</c>. Each
/// <c>systemctl</c> call with a timeout.
/// </summary>
internal sealed class Systemd : IServiceManager
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// join's steps 8 and 9: the template config.yaml once, the program, the unit, and the service (re)started. A running service is
    /// stopped before its program is replaced: it loads its assemblies from that file.
    /// </summary>
    public void InstallAndStart()
    {
        ConfigYaml.WriteTemplateIfMissing();
        if (IsActive())
        {
            Systemctl("stop", AgentInfo.Unit);
        }
        InstallProgram();
        WriteUnit();
        Systemctl("daemon-reload");
        Systemctl("enable", AgentInfo.Unit);
        Systemctl("start", AgentInfo.Unit);
    }

    /// <summary>The unit as the template has it, with one <c>ReadWritePaths=-…</c> line per writable path (the dash: a missing path doesn't stop the unit).</summary>
    public static string UnitText(IReadOnlyList<string> writablePaths)
    {
        var template = Resource("pnmt-agent.service");
        if (writablePaths.Count == 0)
        {
            return template;
        }
        const string marker = "# ReadWritePaths=/etc/pve/priv\n";
        var lines = string.Concat(writablePaths.Select(p => $"ReadWritePaths=-{p}\n"));
        return template.Contains(marker, StringComparison.Ordinal) ? template.Replace(marker, marker + lines, StringComparison.Ordinal)
            : template.Replace("\n[Install]", $"\n{lines}\n[Install]", StringComparison.Ordinal);
    }

    /// <summary>Writes the unit (0644) with the writable paths config.yaml grants now; a refused config.yaml grants none.</summary>
    public static void WriteUnit()
    {
        var rights = ConfigYaml.Read().Rights;
        var text = UnitText(rights.Keys.WritablePaths);
        var temporary = AgentInfo.UnitFile + ".tmp";
        File.WriteAllText(temporary, text);
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temporary, AgentInfo.UnitFile, overwrite: true);
    }

    /// <summary>Copies the running program to <c>/usr/local/sbin/pnmt-agent</c> (root, 0755) when it runs from elsewhere: next to it, then renamed.</summary>
    public static void InstallProgram()
    {
        var current = AgentInfo.CurrentExecutable;
        if (string.Equals(Path.GetFullPath(current), AgentInfo.Program, StringComparison.Ordinal))
        {
            return;
        }
        Replace(AgentInfo.Program, current);
    }

    /// <summary>Puts <paramref name="source"/> in place of <paramref name="target"/> atomically: the running program keeps its old file until it exits.</summary>
    public static void Replace(string target, string source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.new");
        File.Copy(source, temporary, overwrite: true);
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Native.ChownToRoot(temporary);
        File.Move(temporary, target, overwrite: true);
    }

    public static bool IsInstalled => File.Exists(AgentInfo.UnitFile);

    /// <summary><c>systemctl is-active</c>: active, inactive, failed, activating…; null when systemd can't be asked.</summary>
    public static string? State()
    {
        try
        {
            return Tool.Run("systemctl", ["is-active", AgentInfo.Unit], TimeSpan.FromSeconds(15)).Output.Trim() is { Length: > 0 } state ? state : null;
        }
        catch (Exception e) when (e is Core.Collectors.NotAvailableException or TimeoutException)
        {
            return null;
        }
    }

    public static bool IsActive() => State() is "active" or "activating" or "reloading";

    /// <summary>A <c>systemctl</c> call that must succeed.</summary>
    public static void Systemctl(params string[] arguments)
    {
        var result = Tool.Run("systemctl", arguments, Timeout);
        if (!result.Ok)
        {
            throw new InvalidOperationException($"systemctl {string.Join(' ', arguments)} failed ({result.ExitCode}): {result.Reason}");
        }
    }

    /// <summary>A text the binary carries: the unit's and config.yaml's templates.</summary>
    public static string Resource(string name)
    {
        using var stream = typeof(Systemd).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is not in the binary.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
