using System.Text;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Linux.Collectors;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Keys;

/// <summary>What the last scan found: the files with their facts and content, for removals (§6.5 step 4) and the watch (§6.2).</summary>
internal sealed record KeyScan(IReadOnlyList<InspectedFile> Files, IReadOnlyList<string> KeyFolders, IReadOnlyList<string> Homes,
    IReadOnlyDictionary<string, string> ClusterKeys, DateTimeOffset At);

/// <summary>
/// <c>ssh-keys</c> (docs/linux-sensor-plan.md §6.1 → <see cref="SshKeysSection"/>): sshd's own view (<c>sshd -T</c>; with Match blocks per
/// account, <c>-C user=…,host=,addr=</c>, de-duplicated), every account of <c>/etc/passwd</c>, the AuthorizedKeysFile patterns expanded, each
/// file read safely (<see cref="KeyFiles"/>), its keys parsed by <see cref="SshKeys.ParseLine"/> with their notes, protection and whether PNMT
/// may remove them; lines that aren't keys by number only; the CA keys of TrustedUserCAKeys. Caps: 1 MiB and 1,000 keys per file, 20,000 per
/// server. On Proxmox VE the cluster file is <i>cluster-wide</i> and the nodes' own keys are protected. Every 5 minutes, and within seconds of
/// a change (<see cref="KeysFeature"/>'s inotify watch).
/// </summary>
internal sealed class SshKeysCollector(IEnumerable<IAgentRightsSource> rightsSources) : Collector
{
    private readonly IAgentRightsSource? rights = rightsSources.FirstOrDefault();
    private KeyScan? last;

    public override string Name => SectionNames.SshKeys;

    public override TimeSpan Interval => TimeSpan.FromMinutes(5);

    /// <summary>Many accounts with Match blocks: one <c>sshd -T -C</c> each.</summary>
    public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

    /// <summary>The last scan (null before the first).</summary>
    public KeyScan? Last => System.Threading.Volatile.Read(ref last);

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var (section, scan) = Scan(rights?.Current ?? AgentRights.Defaults, errors);
        System.Threading.Volatile.Write(ref last, scan);
        return section;
    }

    public (SshKeysSection Section, KeyScan Scan) Scan(AgentRights current, List<string> errors)
    {
        var now = DateTimeOffset.UtcNow;
        var notes = new List<string>();
        var accounts = KeyAccount.Read();
        var groups = Accounts.ParseGroup(Tool.ReadFile("/etc/group") ?? "");
        string Owner(uint uid) => accounts.FirstOrDefault(a => a.Uid == uid)?.Name ?? uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string Group(uint gid) => groups.FirstOrDefault(g => g.Gid == gid)?.Name ?? gid.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Dictionary<string, List<string>>? sshd = null;
        if (Sshd.Installed)
        {
            sshd = Isolated.Part(errors, "sshd -T", () => Sshd.Effective());
        }
        else
        {
            notes.Add("sshd (openssh-server) is not installed: the files of the default pattern are read (Dropbear reads them too).");
        }
        var configFiles = Isolated.Part(errors, "sshd_config", Sshd.ConfigFiles) ?? [];
        var match = Sshd.HasMatchBlocks(configFiles);
        var global = sshd is null ? [.. KeyFiles.DefaultPatterns] : Sshd.Words(sshd, "authorizedkeysfile");
        var matchCandidates = match ? Sshd.MatchKeyFiles(configFiles) : [];
        var settings = new SshdKeySettings(
            global,
            sshd is null ? null : Sshd.Path(sshd, "authorizedkeyscommand"),
            sshd is null ? null : Sshd.Path(sshd, "authorizedkeyscommanduser"),
            sshd is null ? null : Sshd.Path(sshd, "trustedusercakeys"),
            sshd is null ? null : Sshd.Path(sshd, "authorizedprincipalsfile"),
            sshd is null || Sshd.Yes(sshd, "pubkeyauthentication", fallback: true),
            sshd is null ? null : Sshd.One(sshd, "permitrootlogin"),
            sshd is null || Sshd.Yes(sshd, "strictmodes", fallback: true),
            match,
            sshd is not null);
        if (!settings.PubkeyAuthentication)
        {
            notes.Add("PubkeyAuthentication is off: sshd accepts none of these keys now.");
        }
        if (settings.AuthorizedKeysCommand is { } command)
        {
            notes.Add($"Keys also come from AuthorizedKeysCommand {command}{(settings.AuthorizedKeysCommandUser is { } runAs ? $" (run as {runAs})" : "")}: those can't be listed.");
        }
        if (match)
        {
            notes.Add("sshd_config has Match blocks: the patterns were evaluated per account.");
        }

        // The files each account's patterns name (§6.1): with Match blocks, sshd's view for that account, and the Match blocks' own patterns.
        var candidates = new List<(KeyAccount Account, string Path)>();
        var matchFailed = false;
        foreach (var account in accounts.OrderBy(a => a.Uid))
        {
            var patterns = global;
            if (match && sshd is not null)
            {
                var own = Isolated.Part(matchFailed ? [] : errors, $"sshd -T for {account.Name}", () => Sshd.Words(Sshd.Effective($"user={account.Name},host=,addr="), "authorizedkeysfile"));
                if (own is null)
                {
                    matchFailed = true;
                }
                patterns = own ?? global;
            }
            foreach (var path in patterns.Select(p => KeyFiles.Expand(p, account)).Concat(matchCandidates.Select(p => KeyFiles.Expand(p, account))).Where(p => p is not null))
            {
                if (!candidates.Contains((account, path!)))
                {
                    candidates.Add((account, path!));
                }
            }
        }

        var proxmox = LinuxOs.IsProxmox();
        var cluster = proxmox ? Isolated.Part(errors, "Proxmox VE's cluster keys", ClusterKeys) ?? [] : new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<AuthorizedKeysFile>();
        var inspected = new List<InspectedFile>();
        var total = 0;
        var truncated = false;
        foreach (var (account, path) in candidates)
        {
            var file = Isolated.Part(errors, $"{path}", () => KeyFiles.Inspect(account, path, Owner));
            if (file is null)
            {
                continue;
            }
            inspected.Add(file);
            var problems = new List<string>(file.Problems);
            var keys = new List<AuthorizedKey>();
            var invalid = new List<int>();
            if (file.Content is { } content)
            {
                var lines = Encoding.UTF8.GetString(content).Split('\n');
                var parsed = new List<(int Line, SshPublicKey Key)>();
                for (var i = 0; i < lines.Length; i++)
                {
                    var (key, problem) = SshKeys.ParseLine(lines[i].TrimEnd('\r'));
                    if (key is not null)
                    {
                        parsed.Add((i + 1, key));
                    }
                    else if (problem != SshKeyLineProblem.Blank)
                    {
                        invalid.Add(i + 1);
                        if (problem == SshKeyLineProblem.PrivateKey && !problems.Any(p => p.Contains("private key", StringComparison.Ordinal)))
                        {
                            problems.Add($"line {i + 1} holds a private key, which doesn't belong in this file");
                        }
                    }
                }
                if (parsed.Count > SshKeys.MaxKeysPerFile)
                {
                    problems.Add($"{parsed.Count:N0} keys: only the first {SshKeys.MaxKeysPerFile:N0} are listed");
                    truncated = true;
                }
                foreach (var (line, key) in parsed.Take(SshKeys.MaxKeysPerFile))
                {
                    if (total >= SshKeys.MaxKeysPerServer)
                    {
                        truncated = true;
                        break;
                    }
                    total++;
                    var keyNotes = SshKeys.Notes(key, account.Name, now);
                    var twice = parsed.Where(p => p.Key.Fingerprint == key.Fingerprint).Select(p => p.Line).ToList();
                    if (twice.Count > 1)
                    {
                        keyNotes.Add($"twice in this file (lines {string.Join(", ", twice)})");
                    }
                    string? protectedBecause = null;
                    if (current.Keys.Protect.Contains(key.Fingerprint, StringComparer.Ordinal))
                    {
                        protectedBecause = "config.yaml's ssh_keys.protect";
                    }
                    if (ClusterNode(key, cluster) is { } node)
                    {
                        keyNotes.Add($"a Proxmox VE cluster node's key ({node})");
                        protectedBecause ??= $"a Proxmox VE cluster node's own key ({node})";
                    }
                    keys.Add(new AuthorizedKey(line, key.Type, key.Bits, key.Fingerprint, key.Comment, key.Options, key.Key, keyNotes, protectedBecause is not null,
                        protectedBecause));
                }
            }
            var notRemovable = KeyFiles.NotRemovable(current.Keys, file, Owner);
            var facts = file.Facts ?? file.Link;
            files.Add(new AuthorizedKeysFile(account.Name, path, file.Target, file.Facts is not null || file.UserSymlink,
                facts is null ? null : Owner(facts.Uid), facts is null ? null : Group(facts.Gid), facts?.Octal, facts?.Size, facts?.ModifiedAt, file.Sha256,
                file.ClusterWide, problems, keys, invalid, notRemovable is null, notRemovable));
        }
        if (truncated)
        {
            notes.Add($"The caps were reached (1 MiB and {SshKeys.MaxKeysPerFile:N0} keys per file, {SshKeys.MaxKeysPerServer:N0} per server): not every key is listed.");
        }

        var caKeys = settings.TrustedUserCaKeys is { } caFile ? Isolated.Part(errors, "TrustedUserCAKeys", () => CaKeys(caFile)) ?? [] : [];
        if (caKeys.Count > 0)
        {
            notes.Add($"TrustedUserCAKeys: certificates signed by {caKeys.Count} CA key{(caKeys.Count == 1 ? "" : "s")} get in, whatever these files say.");
        }
        var section = new SshKeysSection(settings, files, caKeys, total, truncated, notes);
        var (keyFolders, homes) = WatchFolders(accounts, inspected);
        return (section, new KeyScan(inspected, keyFolders, homes, cluster, now));
    }

    /// <summary>A key of a Proxmox VE cluster node (§6.6): its comment is <c>root@&lt;node&gt;</c> for a node in /etc/pve/nodes, or it is this node's own key.</summary>
    private static string? ClusterNode(SshPublicKey key, IReadOnlyDictionary<string, string> cluster)
    {
        if (cluster.TryGetValue(key.Fingerprint, out var own))
        {
            return own;
        }
        return key.Comment is { } comment && comment.StartsWith("root@", StringComparison.Ordinal) && cluster.ContainsKey("node:" + comment[5..]) ? comment : null;
    }

    /// <summary>
    /// Proxmox VE's nodes (<c>node:&lt;name&gt;</c>, from /etc/pve/nodes) and this node's own root keys (<c>/root/.ssh/id_*.pub</c>, by fingerprint),
    /// which the cluster's SSH needs.
    /// </summary>
    private static Dictionary<string, string> ClusterKeys()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists("/etc/pve/nodes"))
        {
            foreach (var node in Directory.GetDirectories("/etc/pve/nodes"))
            {
                result["node:" + Path.GetFileName(node)] = "root@" + Path.GetFileName(node);
            }
        }
        var hostname = Tool.ReadValue("/proc/sys/kernel/hostname")?.Split('.')[0] ?? "this node";
        foreach (var pub in Directory.Exists("/root/.ssh") ? Directory.GetFiles("/root/.ssh", "id_*.pub") : [])
        {
            if (Tool.ReadFile(pub) is { } text && SshKeys.ParseLine(text.Trim()).Key is { } key)
            {
                result[key.Fingerprint] = $"root@{hostname}";
            }
        }
        return result;
    }

    /// <summary>TrustedUserCAKeys' file (§6.1): every CA key with its fingerprint.</summary>
    private static List<CaKey> CaKeys(string file)
    {
        var (content, _, error) = KeyFiles.Read(file);
        if (content is null)
        {
            throw new IOException($"{file}: {error}");
        }
        return Encoding.UTF8.GetString(content).Split('\n')
            .Select(l => SshKeys.ParseLine(l.TrimEnd('\r')).Key)
            .Where(k => k is not null)
            .Take(100)
            .Select(k => new CaKey(file, k!.Type, k.Bits, k.Fingerprint, k.Comment))
            .ToList();
    }

    /// <summary>
    /// What the watch (§6.2) watches: the folders holding key files (and followed targets), every home's .ssh, the home itself while it has
    /// none, /etc/ssh and its sshd_config.d, and /etc for passwd and group.
    /// </summary>
    private static (List<string> KeyFolders, List<string> Homes) WatchFolders(IEnumerable<KeyAccount> accounts, IEnumerable<InspectedFile> files)
    {
        var folders = new List<string> { "/etc", "/etc/ssh", "/etc/ssh/sshd_config.d" };
        var homes = new List<string>();
        folders.AddRange(files.Select(f => Path.GetDirectoryName(f.Path)!));
        folders.AddRange(files.Where(f => f.Target is not null).Select(f => Path.GetDirectoryName(f.Target!)!));
        foreach (var account in accounts)
        {
            var home = account.Home.TrimEnd('/');
            if (home.Length <= 1 || !home.StartsWith('/') || home is "/proc" or "/sys" or "/dev" or "/run" || home.StartsWith("/proc/", StringComparison.Ordinal)
                || home.StartsWith("/sys/", StringComparison.Ordinal) || home.StartsWith("/dev/", StringComparison.Ordinal))
            {
                continue;
            }
            var ssh = Path.Combine(home, ".ssh");
            if (Native.StatFull(ssh, followLinks: false) is { Directory: true })
            {
                folders.Add(ssh);
            }
            else if (Native.StatFull(home, followLinks: false) is { Directory: true })
            {
                homes.Add(home);
            }
        }
        bool IsFolder(string f) => Native.StatFull(f, followLinks: false) is { Directory: true };
        var keyFolders = folders.Where(IsFolder).Distinct(StringComparer.Ordinal).ToList();
        return (keyFolders, homes.Where(h => !keyFolders.Contains(h)).Distinct(StringComparer.Ordinal).ToList());
    }
}
