using System.Globalization;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

internal sealed record PasswdEntry(string Name, int Uid, int Gid, string Home, string Shell);

internal sealed record GroupEntry(string Name, int Gid, IReadOnlyList<string> Members);

/// <summary>/etc/shadow's second field and its expiry (days since 1970), never more: the hash itself is never kept.</summary>
internal sealed record ShadowEntry(string Name, bool PasswordSet, bool Locked);

/// <summary>
/// Local accounts, administrators and sudo's rules (docs/linux-sensor-plan.md §5.2): <c>/etc/passwd</c>, <c>/etc/group</c>,
/// <c>/etc/shadow</c> (whether a password is set and not locked, never the hash), and the sudoers rules that give <c>ALL</c>, from
/// <c>/etc/sudoers</c> and its includes, by user and group name only. The parsing is in pure static methods.
/// </summary>
internal static partial class Accounts
{
    /// <summary>Groups whose members are administrators by Debian's and Ubuntu's defaults (sudo), and elsewhere (admin, wheel).</summary>
    public static readonly IReadOnlyList<string> AdminGroups = ["sudo", "admin", "wheel"];

    private static readonly string[] NoLogin = ["nologin", "false", "sync", "halt", "shutdown"];

    [GeneratedRegex(@"^\s*(?:#|@)include(dir)?\s+(.+?)\s*$")]
    private static partial Regex Include();

    [GeneratedRegex(@"^[A-Z_]+:")]
    private static partial Regex Tag();

    public static List<PasswdEntry> ParsePasswd(string text) => text.Split('\n')
        .Select(l => l.Split(':'))
        .Where(f => f.Length >= 7 && f[0].Length > 0 && !f[0].StartsWith('#') && !f[0].StartsWith('+') && !f[0].StartsWith('-'))
        .Select(f => new PasswdEntry(f[0], Int(f[2]), Int(f[3]), f[5], f[6].Trim()))
        .ToList();

    public static List<GroupEntry> ParseGroup(string text) => text.Split('\n')
        .Select(l => l.Split(':'))
        .Where(f => f.Length >= 4 && f[0].Length > 0 && !f[0].StartsWith('#'))
        .Select(f => new GroupEntry(f[0], Int(f[2]), f[3].Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        .ToList();

    /// <summary>
    /// /etc/shadow: a password is set when the field holds a hash (not empty, <c>*</c> or <c>!</c> alone); locked when it starts with
    /// <c>!</c> (<c>passwd -l</c>) or the account expired.
    /// </summary>
    public static List<ShadowEntry> ParseShadow(string text, DateOnly today)
    {
        var days = today.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;
        return text.Split('\n')
            .Select(l => l.Split(':'))
            .Where(f => f.Length >= 2 && f[0].Length > 0)
            .Select(f =>
            {
                var hash = f[1].TrimStart('!');
                var expired = f.Length > 7 && int.TryParse(f[7], NumberStyles.None, CultureInfo.InvariantCulture, out var expire) && expire <= days;
                return new ShadowEntry(f[0], hash.Length > 0 && hash != "*", f[1].StartsWith('!') || expired);
            })
            .ToList();
    }

    public static bool IsLoginShell(string shell) => shell.Length > 0 && !NoLogin.Contains(Path.GetFileName(shell));

    /// <summary>
    /// The rules of a sudoers file that give <c>ALL</c> (<c>%sudo ALL=(ALL:ALL) ALL</c>, <c>deploy ALL=(ALL) NOPASSWD: ALL</c>), one per
    /// user or group named, and the files and folders it includes. Defaults, aliases and comments are skipped; continued lines joined.
    /// </summary>
    public static (List<SudoRule> Rules, List<(string Path, bool Folder)> Includes) ParseSudoers(string text, string file)
    {
        var rules = new List<SudoRule>();
        var includes = new List<(string, bool)>();
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var number = i + 1;
            var line = lines[i];
            while (line.EndsWith('\\') && i + 1 < lines.Length)
            {
                line = line[..^1] + " " + lines[++i];
            }
            if (Include().Match(line) is { Success: true } include)
            {
                var target = include.Groups[2].Value.Trim('"');
                includes.Add((target.StartsWith('/') ? target : Path.Combine(Path.GetDirectoryName(file) ?? "/etc", target), include.Groups[1].Success));
                continue;
            }
            var content = StripComment(line).Trim();
            if (content.Length == 0 || content.StartsWith("Defaults", StringComparison.Ordinal)
                || content.Split((char[])[' ', '\t'])[0] is "User_Alias" or "Runas_Alias" or "Host_Alias" or "Cmnd_Alias" or "Cmd_Alias")
            {
                continue;
            }
            var equals = content.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            var head = Regex.Replace(content[..equals], @"\s*,\s*", ",").Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (head.Length < 2)
            {
                continue;
            }
            var spec = content[(equals + 1)..].Trim();
            if (spec.StartsWith('('))
            {
                var close = spec.IndexOf(')');
                spec = close > 0 ? spec[(close + 1)..].Trim() : spec;
            }
            var noPassword = false;
            while (Tag().Match(spec) is { Success: true } tag)
            {
                noPassword |= tag.Value == "NOPASSWD:";
                spec = spec[tag.Length..].Trim();
            }
            var commands = spec.Split(':')[0].Split(',', StringSplitOptions.TrimEntries);
            if (!commands.Contains("ALL", StringComparer.Ordinal))
            {
                continue;
            }
            rules.AddRange(head[0].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(who => new SudoRule(who, file, number, noPassword)));
        }
        return (rules, includes);
    }

    /// <summary>A sudoers comment starts with # (but <c>#123</c> is a uid, and <c>#include</c> was handled).</summary>
    private static string StripComment(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '#' && (i + 1 >= line.Length || !char.IsDigit(line[i + 1])))
            {
                return line[..i];
            }
        }
        return line;
    }

    /// <summary>Every rule that gives ALL, following the includes (sudo ignores files in an includedir with a dot or ending in ~).</summary>
    public static List<SudoRule> ReadSudoRules()
    {
        var rules = new List<SudoRule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([Path.Combine("/etc", "sudoers")]);
        while (queue.Count > 0 && seen.Count < 200)
        {
            var file = queue.Dequeue();
            if (!seen.Add(file) || Tool.ReadFile(file) is not { } text)
            {
                continue;
            }
            var (found, includes) = ParseSudoers(text, file);
            rules.AddRange(found);
            foreach (var (path, folder) in includes)
            {
                if (!folder)
                {
                    queue.Enqueue(path);
                }
                else if (Directory.Exists(path))
                {
                    foreach (var entry in Directory.GetFiles(path).Order(StringComparer.Ordinal)
                                 .Where(f => !Path.GetFileName(f).Contains('.') && !f.EndsWith('~')))
                    {
                        queue.Enqueue(entry);
                    }
                }
            }
        }
        return rules;
    }

    /// <summary>
    /// The administrators (§5.2): uid 0; members of sudo, admin and wheel (by the group's list or as their primary group); and whoever a
    /// sudoers rule gives ALL, directly or through a group.
    /// </summary>
    public static List<LinuxAdmin> Administrators(IReadOnlyList<PasswdEntry> users, IReadOnlyList<GroupEntry> groups, IReadOnlyList<SudoRule> rules)
    {
        var admins = new List<LinuxAdmin>();
        void Add(string name, string via)
        {
            if (!admins.Any(a => a.Name == name && a.Via == via))
            {
                admins.Add(new LinuxAdmin(name, via));
            }
        }
        foreach (var user in users.Where(u => u.Uid == 0))
        {
            Add(user.Name, "uid 0");
        }
        IEnumerable<string> Members(GroupEntry group) => group.Members.Concat(users.Where(u => u.Gid == group.Gid).Select(u => u.Name)).Distinct();
        foreach (var group in groups.Where(g => AdminGroups.Contains(g.Name)))
        {
            foreach (var member in Members(group))
            {
                Add(member, $"group {group.Name}");
            }
        }
        foreach (var rule in rules)
        {
            if (rule.Who.StartsWith('%'))
            {
                var name = rule.Who[1..];
                if (!AdminGroups.Contains(name) && groups.FirstOrDefault(g => g.Name == name) is { } group)
                {
                    foreach (var member in Members(group))
                    {
                        Add(member, $"sudoers ({rule.Who})");
                    }
                }
            }
            else if (users.Any(u => u.Name == rule.Who))
            {
                Add(rule.Who, "sudoers");
            }
        }
        return admins.OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Via, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The accounts PNMT lists (§5.2): those with a login shell, and uid 0; with their groups, and from /etc/shadow whether a password is
    /// set and not locked. System accounts: below UID_MIN (login.defs, 1000) or nobody.
    /// </summary>
    public static List<LinuxAccount> List(IReadOnlyList<PasswdEntry> users, IReadOnlyList<GroupEntry> groups, IReadOnlyList<ShadowEntry>? shadow, int uidMin)
    {
        return users.Where(u => IsLoginShell(u.Shell) || u.Uid == 0)
            .Select(u =>
            {
                var memberOf = groups.Where(g => g.Gid == u.Gid || g.Members.Contains(u.Name)).Select(g => g.Name).Distinct().Order(StringComparer.Ordinal).ToList();
                var s = shadow?.FirstOrDefault(e => e.Name == u.Name);
                return new LinuxAccount(u.Name, u.Uid, u.Gid, u.Home, u.Shell, IsLoginShell(u.Shell), memberOf, s?.PasswordSet, s?.Locked,
                    u.Uid != 0 && (u.Uid < uidMin || u.Uid >= 60000));
            })
            .OrderBy(a => a.Uid)
            .ToList();
    }

    /// <summary>login.defs' UID_MIN, 1000 when it says nothing.</summary>
    public static int UidMin(string? loginDefs) => loginDefs?.Split('\n').Select(l => l.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        .FirstOrDefault(f => f.Length >= 2 && f[0] == "UID_MIN") is { } line && int.TryParse(line[1], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
        ? min
        : 1000;

    private static int Int(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
}
