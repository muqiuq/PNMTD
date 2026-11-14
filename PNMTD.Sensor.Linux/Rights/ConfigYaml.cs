using System.Globalization;
using System.Text.RegularExpressions;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Linux.Platform;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PNMTD.Sensor.Linux.Rights;

/// <summary>config.yaml as read: the rights, and whether to log at debug level (<c>log_level</c>, applied without a restart).</summary>
internal sealed record LinuxConfig(AgentRights Rights, bool Debug)
{
    public static LinuxConfig Defaults { get; } = new(AgentRights.Defaults, false);

    public static LinuxConfig Refused(string problem) => new(AgentRights.RefusedBecause(problem), false);
}

/// <summary>
/// <c>/etc/pnmt-agent/config.yaml</c> (docs/linux-sensor-plan.md §4.6): what PNMT may do on this server, written by root, never by
/// PNMT. Read with YamlDotNet's representation model (no reflection), by the rule table of §4.6:
/// <list type="bullet">
/// <item>owned by root, not writable by group or others (nor its folder), valid YAML, known keys: applied;</item>
/// <item>missing: the defaults, everything that changes something off;</item>
/// <item>not root's, writable by others, unreadable, or not valid YAML: everything off, and the reason (a problem in the service);</item>
/// <item>an unknown key or a wrong value: that setting off, with a warning, so a typo never silently grants or withholds. A list that
/// restricts a right (<c>users</c>, <c>protect</c>, <c>run_as</c>, <c>allowed_hashes</c>) that can't be read turns that right off,
/// because an empty list would mean "every".</item>
/// </list>
/// </summary>
internal static partial class ConfigYaml
{
    private const int MaxBytes = 64 * 1024;

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}\$?$")]
    private static partial Regex AccountName();

    [GeneratedRegex("^SHA256:[A-Za-z0-9+/]{43}$")]
    private static partial Regex Fingerprint();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    /// <summary>Folders the agent may never be given: its own rights, its unit, and /etc itself.</summary>
    private static readonly string[] NeverWritable = ["/etc/pnmt-agent", "/etc/systemd"];

    public static LinuxConfig Read(string path = AgentInfo.ConfigFile)
    {
        var file = Native.Stat(path);
        if (file is null)
        {
            // A dangling symlink, or a file root can't stat, is no "missing".
            return Native.Stat(path, followLinks: false) is null ? LinuxConfig.Defaults : LinuxConfig.Refused("it can't be read (a broken link?)");
        }
        if (Unsafe(file, Native.Stat(Path.GetDirectoryName(path)!), Path.GetDirectoryName(path)!) is { } problem)
        {
            return LinuxConfig.Refused(problem);
        }
        if (file.Size > MaxBytes)
        {
            return LinuxConfig.Refused($"it is larger than {MaxBytes / 1024} KiB");
        }
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LinuxConfig.Refused($"it can't be read: {e.Message}");
        }
    }

    /// <summary>Why the file is unsafe, or null (§4.6): another owner than root, or writable by its group or others; the same for its folder.</summary>
    public static string? Unsafe(FileFacts file, FileFacts? folder, string folderPath)
    {
        if (file.Directory)
        {
            return "it is a folder, not a file";
        }
        if (file.Uid != 0)
        {
            return $"it is owned by uid {file.Uid}, not root";
        }
        if (Writable(file.Mode) is { } who)
        {
            return $"it is writable by {who} (mode {Octal(file.Mode)}): only root may change it";
        }
        if (folder is not null && folder.Uid != 0)
        {
            return $"{folderPath} is owned by uid {folder.Uid}, not root";
        }
        if (folder is not null && Writable(folder.Mode) is { } folderWho)
        {
            return $"{folderPath} is writable by {folderWho} (mode {Octal(folder.Mode)}), so the file could be replaced";
        }
        return null;
    }

    private static string? Writable(UnixFileMode mode) =>
        (mode.HasFlag(UnixFileMode.GroupWrite), mode.HasFlag(UnixFileMode.OtherWrite)) switch
        {
            (true, true) => "its group and others",
            (true, false) => "its group",
            (false, true) => "others",
            _ => null,
        };

    public static string Octal(UnixFileMode mode) => "0" + Convert.ToString((int)mode & 0xFFF, 8).PadLeft(3, '0');

    /// <summary>The settings from the file's text (pure, for <c>config check</c> and tests).</summary>
    public static LinuxConfig Parse(string text)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException e)
        {
            return LinuxConfig.Refused($"it is not valid YAML: {e.Message.Trim()} (line {e.Start.Line})");
        }
        if (stream.Documents.Count == 0)
        {
            return new LinuxConfig(AgentRights.Defaults with { Source = AgentRights.FromFile }, false);
        }
        if (stream.Documents.Count > 1)
        {
            return LinuxConfig.Refused("it holds more than one YAML document");
        }
        var rootNode = stream.Documents[0].RootNode;
        if (rootNode is YamlScalarNode empty && IsNull(empty))
        {
            return new LinuxConfig(AgentRights.Defaults with { Source = AgentRights.FromFile }, false);
        }
        if (rootNode is not YamlMappingNode root)
        {
            return LinuxConfig.Refused("it is not a mapping of settings (log_level, ssh_keys, scripts, collect)");
        }
        var warnings = new List<string>();
        var debug = false;
        var keys = KeyRights.Off;
        var scripts = ScriptRights.Off;
        var collect = CollectRights.Off;
        foreach (var (keyNode, value) in root.Children)
        {
            var name = (keyNode as YamlScalarNode)?.Value ?? "?";
            switch (name)
            {
                case "log_level":
                    debug = LogLevel(value, warnings);
                    break;
                case "ssh_keys":
                    keys = Keys(value, warnings);
                    break;
                case "scripts":
                    scripts = Scripts(value, warnings);
                    break;
                case "collect":
                    collect = Collect(value, warnings);
                    break;
                default:
                    warnings.Add($"{name} (line {keyNode.Start.Line}): unknown setting, ignored");
                    break;
            }
        }
        return new LinuxConfig(new AgentRights(AgentRights.FromFile, null, keys, scripts, collect, warnings), debug);
    }

    private static bool LogLevel(YamlNode node, List<string> warnings)
    {
        if (node is YamlScalarNode s && !IsNull(s) && s.Value!.Trim().ToLowerInvariant() is "info" or "debug")
        {
            return s.Value.Trim().Equals("debug", StringComparison.OrdinalIgnoreCase);
        }
        if (node is not YamlScalarNode n || !IsNull(n))
        {
            warnings.Add($"log_level (line {node.Start.Line}): info or debug; info applies");
        }
        return false;
    }

    private static KeyRights Keys(YamlNode node, List<string> warnings)
    {
        if (Section("ssh_keys", node, warnings) is not { } settings)
        {
            return KeyRights.Off;
        }
        var remove = false;
        IReadOnlyList<string>? users = [];
        IReadOnlyList<string>? protect = [];
        IReadOnlyList<string> writable = [];
        foreach (var (name, value) in settings)
        {
            switch (name)
            {
                case "remove":
                    remove = Bool("ssh_keys.remove", value, warnings) ?? false;
                    break;
                case "users":
                    users = List("ssh_keys.users", value, warnings, AccountName().IsMatch, "an account name");
                    break;
                case "protect":
                    protect = List("ssh_keys.protect", value, warnings, Fingerprint().IsMatch, "a fingerprint SHA256:… as ssh-keygen -l prints it");
                    break;
                case "writable_paths":
                    writable = WritablePaths(value, warnings);
                    break;
                default:
                    warnings.Add($"ssh_keys.{name} (line {value.Start.Line}): unknown setting, ignored");
                    break;
            }
        }
        if (remove && (users is null || protect is null))
        {
            warnings.Add($"ssh_keys.remove: off, because ssh_keys.{(users is null ? "users" : "protect")} can't be read");
            remove = false;
        }
        return new KeyRights(remove, users ?? [], protect ?? [], writable);
    }

    private static ScriptRights Scripts(YamlNode node, List<string> warnings)
    {
        if (Section("scripts", node, warnings) is not { } settings)
        {
            return ScriptRights.Off;
        }
        var enabled = false;
        IReadOnlyList<string>? runAs = [];
        IReadOnlyList<string>? hashes = [];
        var timeout = ScriptRights.DefaultTimeoutMinutes;
        foreach (var (name, value) in settings)
        {
            switch (name)
            {
                case "enabled":
                    enabled = Bool("scripts.enabled", value, warnings) ?? false;
                    break;
                case "run_as":
                    runAs = List("scripts.run_as", value, warnings, AccountName().IsMatch, "an account name");
                    break;
                case "allowed_hashes":
                    hashes = List("scripts.allowed_hashes", value, warnings, h => Sha256().IsMatch(h.ToLowerInvariant()), "a SHA-256 of 64 hex digits")
                        ?.Select(h => h.ToLowerInvariant()).ToList();
                    break;
                case "max_timeout_minutes":
                    if (value is YamlScalarNode { Style: ScalarStyle.Plain } s && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                        && minutes is >= 1 and <= 1440)
                    {
                        timeout = minutes;
                    }
                    else if (value is not YamlScalarNode n || !IsNull(n))
                    {
                        warnings.Add($"scripts.max_timeout_minutes (line {value.Start.Line}): a number of minutes from 1 to 1440; {timeout} applies");
                    }
                    break;
                default:
                    warnings.Add($"scripts.{name} (line {value.Start.Line}): unknown setting, ignored");
                    break;
            }
        }
        if (enabled && (runAs is null || hashes is null))
        {
            warnings.Add($"scripts.enabled: off, because scripts.{(runAs is null ? "run_as" : "allowed_hashes")} can't be read");
            enabled = false;
        }
        return new ScriptRights(enabled, runAs ?? [], hashes ?? [], timeout);
    }

    private static CollectRights Collect(YamlNode node, List<string> warnings)
    {
        if (Section("collect", node, warnings) is not { } settings)
        {
            return CollectRights.Off;
        }
        var sudo = false;
        var cron = false;
        foreach (var (name, value) in settings)
        {
            switch (name)
            {
                case "sudo_commands":
                    sudo = Bool("collect.sudo_commands", value, warnings) ?? false;
                    break;
                case "cron_commands":
                    cron = Bool("collect.cron_commands", value, warnings) ?? false;
                    break;
                default:
                    warnings.Add($"collect.{name} (line {value.Start.Line}): unknown setting, ignored");
                    break;
            }
        }
        return new CollectRights(sudo, cron);
    }

    /// <summary>A group's settings by name; an empty group is all defaults; anything but a mapping turns the whole group off, with a warning.</summary>
    private static List<(string Name, YamlNode Value)>? Section(string name, YamlNode node, List<string> warnings)
    {
        if (node is YamlScalarNode s && IsNull(s))
        {
            return [];
        }
        if (node is not YamlMappingNode mapping)
        {
            warnings.Add($"{name} (line {node.Start.Line}): not a group of settings, so everything in it is off");
            return null;
        }
        return mapping.Children.Select(p => ((p.Key as YamlScalarNode)?.Value ?? "?", p.Value)).ToList();
    }

    /// <summary>true or false, unquoted (YAML 1.2's core schema; <c>yes</c> and <c>on</c> are refused); null for anything else, with a warning.</summary>
    private static bool? Bool(string name, YamlNode node, List<string> warnings)
    {
        if (node is YamlScalarNode { Style: ScalarStyle.Plain } s && s.Value?.ToLowerInvariant() is "true" or "false")
        {
            return s.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        if (node is YamlScalarNode n && IsNull(n))
        {
            return null;
        }
        warnings.Add($"{name} (line {node.Start.Line}): true or false; off");
        return null;
    }

    /// <summary>A list of strings that each pass <paramref name="valid"/>; null (with a warning) when it isn't one.</summary>
    private static List<string>? List(string name, YamlNode node, List<string> warnings, Func<string, bool> valid, string what)
    {
        if (node is YamlScalarNode n && IsNull(n))
        {
            return [];
        }
        if (node is not YamlSequenceNode sequence)
        {
            warnings.Add($"{name} (line {node.Start.Line}): a list ([a, b] or one \"- a\" per line)");
            return null;
        }
        var items = new List<string>();
        foreach (var item in sequence.Children)
        {
            if (item is not YamlScalarNode { Value: { } text } || !valid(text.Trim()))
            {
                warnings.Add($"{name} (line {item.Start.Line}): \"{(item as YamlScalarNode)?.Value}\" is not {what}");
                return null;
            }
            items.Add(text.Trim());
        }
        return items.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Key files under /etc the agent may rewrite (§4.3): absolute, normalised, under /etc; never /etc itself, the agent's own folder
    /// or systemd's. A path that doesn't qualify is left out with a warning (fewer writable paths only restrict).
    /// </summary>
    private static List<string> WritablePaths(YamlNode node, List<string> warnings)
    {
        var paths = List("ssh_keys.writable_paths", node, warnings, p => p.Length > 0, "a path") ?? [];
        var result = new List<string>();
        foreach (var path in paths)
        {
            var normal = path.StartsWith('/') && !path.Any(char.IsControl) && !path.Contains(' ') ? Path.GetFullPath(path).TrimEnd('/') : null;
            if (normal is null || !normal.StartsWith("/etc/", StringComparison.Ordinal))
            {
                warnings.Add($"ssh_keys.writable_paths: \"{path}\" is left out: only absolute paths under /etc (the one folder read-only for the agent)");
            }
            else if (NeverWritable.Any(n => normal == n || normal.StartsWith(n + "/", StringComparison.Ordinal)))
            {
                warnings.Add($"ssh_keys.writable_paths: \"{path}\" is left out: the agent's own rights and unit are never writable for it");
            }
            else
            {
                result.Add(normal);
            }
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool IsNull(YamlScalarNode node) =>
        node.Style == ScalarStyle.Plain && (string.IsNullOrEmpty(node.Value) || node.Value is "~" or "null" or "Null" or "NULL");

    /// <summary>
    /// join's step 8 (§4.4): the commented template, everything off, only when there is no file (a file deployed beforehand by
    /// Ansible or Puppet, or a link, is never touched). The folder 0755, the file 0600, both root's.
    /// </summary>
    public static bool WriteTemplateIfMissing()
    {
        if (Native.Stat(AgentInfo.ConfigFile, followLinks: false) is not null)
        {
            return false;
        }
        if (!Directory.Exists(AgentInfo.ConfigFolder))
        {
            Directory.CreateDirectory(AgentInfo.ConfigFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Native.ChownToRoot(AgentInfo.ConfigFolder);
        }
        var temporary = AgentInfo.ConfigFile + ".tmp";
        using (var stream = new FileStream(temporary, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(Systemd.Resource("config.yaml"));
        }
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Native.ChownToRoot(temporary);
        try
        {
            File.Move(temporary, AgentInfo.ConfigFile, overwrite: false);
            return true;
        }
        catch (IOException)
        {
            // Written by someone else in the meantime: theirs stays.
            File.Delete(temporary);
            return false;
        }
    }
}
