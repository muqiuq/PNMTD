using System.Security.Cryptography;
using System.Text;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Linux.Collectors;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Keys;

/// <summary>An account as the key files need it: passwd's entry and the groups it is in (for reading with its rights).</summary>
internal sealed record KeyAccount(string Name, uint Uid, uint Gid, string Home, IReadOnlyList<uint> Groups)
{
    public static List<KeyAccount> Read()
    {
        var users = Accounts.ParsePasswd(File.ReadAllText("/etc/passwd"));
        var groups = Accounts.ParseGroup(Tool.ReadFile("/etc/group") ?? "");
        return users.Where(u => u.Uid >= 0 && u.Gid >= 0).Select(u => new KeyAccount(u.Name, (uint)u.Uid, (uint)u.Gid, u.Home,
            groups.Where(g => g.Gid >= 0 && (g.Gid == u.Gid || g.Members.Contains(u.Name))).Select(g => (uint)g.Gid).Append((uint)u.Gid).Distinct().ToList())).ToList();
    }

    public bool IsRoot => Uid == 0;

    /// <summary>Whether a path is in this account's home (not root's, and not a home of "/").</summary>
    public bool InHome(string path) => Home.Length > 1 && (path == Home || path.StartsWith(Home.TrimEnd('/') + "/", StringComparison.Ordinal));
}

/// <summary>
/// A key file as the agent found it (docs/linux-sensor-plan.md §6.1, §6.6): the path sshd reads, the target of a root symlink that was followed,
/// the facts of what was read, and why it may not be read or rewritten. <see cref="Content"/> is the file as read (null when it wasn't).
/// </summary>
internal sealed record InspectedFile(
    KeyAccount Account,
    string Path,
    string Effective,
    string? Target,
    FileFacts? Link,
    FileFacts? Facts,
    bool UserSymlink,
    bool Followed,
    List<string> Problems,
    string? FolderProblem,
    byte[]? Content,
    string? ReadError,
    bool ReadAsUser)
{
    public bool ClusterWide => Effective.StartsWith("/etc/pve/", StringComparison.Ordinal);

    public string? Sha256 => Content is null ? null : Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();
}

/// <summary>
/// Reading key files safely (§6.1, §6.6): <b>a symlink is followed only when it and every link after it, and its target, are root's</b> and no
/// folder on the way is writable by others (Proxmox VE's <c>/root/.ssh/authorized_keys</c> → <c>/etc/pve/priv/authorized_keys</c>); a user's
/// symlink is reported, never followed, never read. <b>A file in a user's home is read with that user's rights</b> (the thread's fsuid,
/// fsgid and groups, <see cref="Native.AsUser"/>), opened without following a symlink and without blocking on a FIFO, and the descriptor's
/// facts are what is reported: whatever a user swaps in, the agent reads only what that user could read anyway. The folders up to the home
/// are checked as sshd's StrictModes checks them.
/// </summary>
internal static class KeyFiles
{
    public static readonly string[] DefaultPatterns = [".ssh/authorized_keys", ".ssh/authorized_keys2"];

    /// <summary>An AuthorizedKeysFile pattern for an account: <c>%h</c>, <c>%u</c>, <c>%U</c>, <c>%%</c>; a relative one is in the home.</summary>
    public static string? Expand(string pattern, KeyAccount account)
    {
        var text = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '%' || i + 1 >= pattern.Length)
            {
                text.Append(pattern[i]);
                continue;
            }
            switch (pattern[++i])
            {
                case '%':
                    text.Append('%');
                    break;
                case 'h':
                    text.Append(account.Home);
                    break;
                case 'u':
                    text.Append(account.Name);
                    break;
                case 'U':
                    text.Append(account.Uid);
                    break;
                default:
                    // A token sshd doesn't allow here: the pattern isn't one.
                    return null;
            }
        }
        var path = text.ToString();
        if (!path.StartsWith('/'))
        {
            if (account.Home.Length == 0 || !account.Home.StartsWith('/'))
            {
                return null;
            }
            path = System.IO.Path.Combine(account.Home, path);
        }
        return System.IO.Path.GetFullPath(path);
    }

    /// <summary>The file at a path for an account, read when it may be; null when there is nothing at the path.</summary>
    public static InspectedFile? Inspect(KeyAccount account, string path, Func<uint, string> owner, bool read = true)
    {
        var link = Native.StatFull(path, followLinks: false);
        if (link is null)
        {
            return null;
        }
        var problems = new List<string>();
        if (UsersFolderLink(path, owner) is { } folderLink)
        {
            // A folder on the way is a user's symlink (~/.ssh → /root/.ssh): the same as a user's symlink to the file (§6.6).
            problems.Add(folderLink);
            return new InspectedFile(account, path, path, null, link, null, true, false, problems, null, null, null, false);
        }
        var effective = path;
        string? target = null;
        FileFacts? facts = link;
        var userSymlink = false;
        var followed = false;
        if (link.Symlink)
        {
            var (resolved, refusal, isUsers) = FollowRootSymlink(path, owner);
            if (resolved is null)
            {
                problems.Add(refusal!);
                return new InspectedFile(account, path, path, null, link, null, isUsers, false, problems, null, null, null, false);
            }
            effective = target = resolved;
            followed = true;
            facts = Native.StatFull(resolved, followLinks: false);
            if (facts is null)
            {
                problems.Add($"a symlink to {resolved}, which doesn't exist");
                return new InspectedFile(account, path, effective, target, link, null, false, true, problems, null, null, null, false);
            }
        }
        if (!facts!.Regular)
        {
            problems.Add(facts.Directory ? "a folder, not a file" : "not a regular file: not read");
            return new InspectedFile(account, path, effective, target, link, facts, userSymlink, followed, problems, null, null, null, false);
        }
        if (facts.Uid != account.Uid && facts.Uid != 0)
        {
            problems.Add($"owned by {owner(facts.Uid)}, not by {account.Name} or root: sshd's StrictModes refuses it");
        }
        if (facts.WritableByOthers)
        {
            problems.Add("writable by group or others: sshd's StrictModes refuses it, and without StrictModes anyone with that access can add keys");
        }
        var folderProblem = FolderProblem(effective, account, owner);
        if (folderProblem is not null)
        {
            problems.Add(folderProblem);
        }
        if (facts.Size > SshKeys.MaxFileBytes)
        {
            problems.Add($"too large: {facts.Size:N0} bytes, the limit is 1 MiB: not read");
            return new InspectedFile(account, path, effective, target, link, facts, false, followed, problems, folderProblem, null, null, false);
        }
        if (!read)
        {
            return new InspectedFile(account, path, effective, target, link, facts, false, followed, problems, folderProblem, null, null, false);
        }
        // In a user's home everything is read with the user's rights, a root symlink's target too: nothing there reads more than the user could.
        var asUser = !account.IsRoot && (account.InHome(path) || account.InHome(effective) || facts.Uid == account.Uid);
        var (content, readFacts, error) = asUser
            ? ReadAs(account, effective)
            : Read(effective);
        if (error is not null)
        {
            problems.Add(asUser ? $"could not be read with {account.Name}'s rights: {error}" : $"could not be read: {error}");
            return new InspectedFile(account, path, effective, target, link, facts, false, followed, problems, folderProblem, null, error, asUser);
        }
        return new InspectedFile(account, path, effective, target, link, readFacts ?? facts, false, followed, problems, folderProblem, content, null, asUser);
    }

    /// <summary>
    /// Follows a symlink only when every link of the chain and the target are root's and no folder holding them is writable by others: the
    /// target, or why not (and whether a user's link is the reason).
    /// </summary>
    public static (string? Target, string? Refusal, bool UsersLink) FollowRootSymlink(string path, Func<uint, string> owner)
    {
        var current = path;
        for (var hop = 0; hop < 8; hop++)
        {
            var facts = Native.StatFull(current, followLinks: false);
            if (facts is null)
            {
                return (null, $"a symlink to {current}, which doesn't exist", false);
            }
            if (!facts.Symlink)
            {
                if (facts.Uid != 0)
                {
                    return (null, $"a symlink to {current}, which is {owner(facts.Uid)}'s, not root's: not followed and not read (§6.6)", false);
                }
                if (WritableFolder(current) is { } folder)
                {
                    return (null, $"a symlink to {current}, in a folder writable by others ({folder}): not followed", false);
                }
                return (current, null, false);
            }
            if (facts.Uid != 0)
            {
                return (null, $"a symlink owned by {owner(facts.Uid)}: never followed, not read (§6.6)", true);
            }
            if (WritableFolder(current) is { } linkFolder)
            {
                return (null, $"a symlink in a folder writable by others ({linkFolder}): not followed", false);
            }
            string? next;
            try
            {
                next = new FileInfo(current).LinkTarget;
            }
            catch (IOException e)
            {
                return (null, $"a symlink that can't be read: {e.Message}", false);
            }
            if (next is null)
            {
                return (null, "a symlink without a target", false);
            }
            current = System.IO.Path.GetFullPath(next.StartsWith('/') ? next : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(current)!, next));
        }
        return (null, "a chain of more than 8 symlinks: not followed", false);
    }

    /// <summary>A folder of the path that is a symlink not owned by root (<c>~/.ssh</c> → elsewhere), or null.</summary>
    public static string? UsersFolderLink(string path, Func<uint, string> owner)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        for (var i = 0; i < parts.Length - 1; i++)
        {
            current += "/" + parts[i];
            if (Native.StatFull(current, followLinks: false) is { Symlink: true } facts && facts.Uid != 0)
            {
                return $"{current} is a symlink owned by {owner(facts.Uid)}: never followed, not read (§6.6)";
            }
        }
        return null;
    }

    /// <summary>The first folder above a path that is writable by group or others (without the sticky bit), or null.</summary>
    private static string? WritableFolder(string path)
    {
        for (var folder = System.IO.Path.GetDirectoryName(path); !string.IsNullOrEmpty(folder); folder = System.IO.Path.GetDirectoryName(folder))
        {
            var facts = Native.StatFull(folder, followLinks: true);
            if (facts is { WritableByOthers: true } && (facts.Bits & 0x200) == 0)
            {
                return folder;
            }
            if (folder == "/")
            {
                break;
            }
        }
        return null;
    }

    /// <summary>
    /// sshd's StrictModes on the folders (auth_secure_path): from the file's folder up to the home (or /), each must be root's or the account's
    /// and not writable by group or others.
    /// </summary>
    public static string? FolderProblem(string path, KeyAccount account, Func<uint, string> owner)
    {
        var stopAt = account.InHome(path) ? account.Home.TrimEnd('/') : "/";
        for (var folder = System.IO.Path.GetDirectoryName(path); !string.IsNullOrEmpty(folder); folder = System.IO.Path.GetDirectoryName(folder))
        {
            var facts = Native.StatFull(folder, followLinks: true);
            if (facts is null)
            {
                return $"in a folder that can't be read: {folder}";
            }
            if (facts.Uid != 0 && facts.Uid != account.Uid)
            {
                return $"in a folder owned by {owner(facts.Uid)}: {folder}";
            }
            if (facts.WritableByOthers)
            {
                return $"in a folder writable by group or others: {folder}";
            }
            if (folder == stopAt || folder == "/")
            {
                break;
            }
        }
        return null;
    }

    /// <summary>Reads a file as root: no symlink followed, no FIFO waited for; the descriptor's facts.</summary>
    public static (byte[]? Content, FileFacts? Facts, string? Error) Read(string path)
    {
        var fd = Native.Open(path, Native.ReadOnly | Native.NoFollow | Native.NonBlocking | Native.NoControllingTerminal);
        if (fd < 0)
        {
            var errno = Native.LastError;
            return (null, null, errno == Native.ELOOP ? "it became a symlink: not followed" : Native.Describe(errno));
        }
        try
        {
            var facts = Native.StatFd(fd);
            if (facts is null || !facts.Regular)
            {
                return (null, facts, "not a regular file");
            }
            var content = Native.ReadAll(fd, SshKeys.MaxFileBytes + 1);
            if (content is null)
            {
                return (null, facts, Native.Describe(Native.LastError));
            }
            return content.Length > SshKeys.MaxFileBytes ? (null, facts, "larger than 1 MiB") : (content, facts, null);
        }
        finally
        {
            Native.Close(fd);
        }
    }

    /// <summary>Reads a file with the account's rights (§6.1): the kernel checks the open as it would for that user.</summary>
    public static (byte[]? Content, FileFacts? Facts, string? Error) ReadAs(KeyAccount account, string path)
    {
        try
        {
            return Native.AsUser(account.Uid, account.Gid, account.Groups, () => Read(path));
        }
        catch (IOException e)
        {
            return (null, null, e.Message);
        }
    }

    /// <summary>
    /// Why PNMT may not remove keys from this file (§6.5 step 4, items 1, 2, 5 and 6), or null: removal off, the account not allowed, a user's
    /// symlink or not a regular file, another owner, a folder writable by others, under /etc and not in <c>writable_paths</c>.
    /// </summary>
    public static string? NotRemovable(KeyRights rights, InspectedFile file, Func<uint, string> owner)
    {
        if (!rights.Remove)
        {
            return "key removal is off on this server (config.yaml: ssh_keys.remove: false)";
        }
        if (rights.Users.Count > 0 && !rights.Users.Contains(file.Account.Name, StringComparer.Ordinal))
        {
            return $"config.yaml's ssh_keys.users doesn't name {file.Account.Name}";
        }
        if (file.UserSymlink)
        {
            return "a symlink owned by a user: never rewritten (§6.6)";
        }
        if (file.Facts is null || !file.Facts.Regular)
        {
            return file.Link?.Symlink == true && !file.Followed ? "a symlink that isn't followed: never rewritten" : "not a regular file";
        }
        if (file.Facts.Uid != file.Account.Uid && file.Facts.Uid != 0)
        {
            return $"owned by {owner(file.Facts.Uid)}, not by {file.Account.Name} or root";
        }
        if (file.FolderProblem is not null)
        {
            return file.FolderProblem;
        }
        if (file.Facts.WritableByOthers)
        {
            return "writable by group or others";
        }
        if (file.Effective.StartsWith("/etc/", StringComparison.Ordinal)
            && !rights.WritablePaths.Any(w => file.Effective == w || file.Effective.StartsWith(w.TrimEnd('/') + "/", StringComparison.Ordinal)))
        {
            return $"{file.Effective} is under /etc, which the agent's sandbox keeps read-only: add its folder to ssh_keys.writable_paths in config.yaml, then pnmt-agent update";
        }
        if (file.Facts.Size > SshKeys.MaxFileBytes)
        {
            return "larger than 1 MiB";
        }
        return null;
    }
}
