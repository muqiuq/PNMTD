using System.Text.Json;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Keys;

/// <summary>What <c>pnmt-agent internal rewrite-keys</c> answers on stdout.</summary>
internal sealed record RewriteAnswer(int Removed, string? Sha256, string? Error);

/// <summary>
/// Removing a key (docs/linux-sensor-plan.md §6.5, §6.6), for the signed <c>remove-ssh-key</c>. <b>The checks of §6.5 step 4 in order</b>, the
/// first that fails refuses with its reason:
/// <list type="number">
/// <item><c>ssh_keys.remove</c> is true;</item>
/// <item>the account is in <c>ssh_keys.users</c>, or that list is empty;</item>
/// <item>the fingerprint is not protected (<c>ssh_keys.protect</c>; on Proxmox VE a cluster node's own key);</item>
/// <item>the path is one the agent itself found for that account in its last scan (the path sshd reads, or a followed root symlink's target);</item>
/// <item>looked at again now: a regular file, not a user's symlink, owned by the account or root, in folders not writable by others;</item>
/// <item>under /etc only in <c>writable_paths</c>.</item>
/// </list>
/// Then the file as read (with the owner's rights) is backed up (<see cref="KeyBackups"/>), and rewritten without every line of the key: <b>with
/// its owner's rights</b> for a user's file (the agent started as that user with <c>setpriv</c>: <c>internal rewrite-keys</c>), by the agent
/// itself for root's (Proxmox VE's cluster file included), through the folder's descriptor either way (<see cref="KeyRewrite"/>), and only if
/// the file is still what was backed up. Logged in the log file and the journal.
/// </summary>
internal sealed class KeyRemoval(SshKeysCollector collector, IEnumerable<IAgentRightsSource> rightsSources, ILogger<KeyRemoval> logger)
{
    private readonly IAgentRightsSource? rights = rightsSources.FirstOrDefault();
    private readonly Lock gate = new();

    public SshKeyRemovalResult Remove(RemoveSshKeyRequest request, string requestId)
    {
        lock (gate)
        {
            SshKeyRemovalResult Answer(SshKeyRemovalOutcome outcome, string? message, int lines = 0, string? backup = null, string? hash = null) =>
                new(requestId, request.RemovalId, outcome, lines, backup, hash, message);
            var current = rights?.Current ?? AgentRights.Defaults;
            var keys = current.Keys;
            var fingerprint = SshKeys.NormalizeFingerprint(request.Fingerprint);

            // 1 and 2: the rights.
            if (!keys.Remove)
            {
                return Refused(Answer, request, "Key removal is off on this server (config.yaml: ssh_keys.remove: false).");
            }
            if (keys.Users.Count > 0 && !keys.Users.Contains(request.Account, StringComparer.Ordinal))
            {
                return Refused(Answer, request, $"config.yaml's ssh_keys.users doesn't name {request.Account} ({string.Join(", ", keys.Users)}).");
            }
            if (fingerprint is null)
            {
                return Refused(Answer, request, $"\"{request.Fingerprint}\" is not a key's fingerprint (SHA256:…).");
            }
            // 3: protected keys.
            if (keys.Protect.Contains(fingerprint, StringComparer.Ordinal))
            {
                return Refused(Answer, request, $"The key {fingerprint} is protected on this server (config.yaml's ssh_keys.protect).");
            }
            var scan = collector.Last ?? collector.Scan(current, []).Scan;
            if (LinuxOs.IsProxmox() && ProxmoxNode(fingerprint, scan) is { } node)
            {
                return Refused(Answer, request, $"The key {fingerprint} is a Proxmox VE cluster node's own key ({node}): the cluster's SSH needs it.");
            }
            // 4: a file the agent found itself for that account, in its last scan (made now when there is none yet).
            var found = scan.Files.FirstOrDefault(f => f.Account.Name == request.Account && (f.Path == request.Path || f.Target == request.Path));
            if (found is null)
            {
                var known = scan.Files.Where(f => f.Account.Name == request.Account).Select(f => f.Path).ToList();
                return Refused(Answer, request, $"The agent found no file {request.Path} for {request.Account}: PNMT can only name the files sshd reads for it"
                    + (known.Count == 0 ? " (none for this account)." : $" ({string.Join(", ", known)})."));
            }

            // 5 and 6: the file as it is now.
            var accounts = KeyAccount.Read();
            string Owner(uint uid) => accounts.FirstOrDefault(a => a.Uid == uid)?.Name ?? uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var account = accounts.FirstOrDefault(a => a.Name == request.Account);
            if (account is null)
            {
                return Refused(Answer, request, $"There is no account {request.Account} on this server.");
            }
            var file = KeyFiles.Inspect(account, found.Path, Owner);
            if (file is null)
            {
                return Answer(SshKeyRemovalOutcome.NotFound, $"{found.Path} doesn't exist any more.");
            }
            if (KeyFiles.NotRemovable(keys, file, Owner) is { } reason)
            {
                return Refused(Answer, request, char.ToUpperInvariant(reason[0]) + reason[1..] + ".");
            }
            if (file.Content is not { } content)
            {
                return Failed(Answer, request, $"{file.Effective} could not be read: {file.ReadError}.");
            }
            var (_, lines) = KeyRewrite.Without(content, fingerprint);
            if (lines == 0)
            {
                return Answer(SshKeyRemovalOutcome.NotFound, $"The key {fingerprint} is not in {file.Effective} (any more).", hash: file.Sha256);
            }

            string? backup = null;
            try
            {
                backup = KeyBackups.Save(account.Name, file.Effective, content, file.Facts!, fingerprint, request.RemovalId, lines);
                var (removed, hash) = file.Facts!.Uid == 0
                    ? KeyRewrite.RemoveKey(file.Effective, fingerprint, KeyRewrite.Sha(content), keepOwner: true)
                    : AsOwner(file, fingerprint, KeyRewrite.Sha(content));
                if (removed == 0)
                {
                    KeyBackups.Delete(backup);
                    return Answer(SshKeyRemovalOutcome.NotFound, $"The key {fingerprint} is not in {file.Effective} (any more).", hash: hash);
                }
                var where = file.Path == file.Effective ? file.Path : $"{file.Path} ({file.Effective})";
                var text = $"PNMT removed the key {fingerprint} from {where}: {removed} line{(removed == 1 ? "" : "s")}, backup {backup}"
                    + $" (pnmt-agent keys restore {backup}).";
                logger.LogWarning("{Message}", text);
                Journal.Write(text, warning: true);
                return Answer(SshKeyRemovalOutcome.Removed, file.ClusterWide ? "Removed from Proxmox VE's cluster-wide file: from every node of the cluster." : null,
                    removed, backup, hash);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or JsonException
                                          or Core.Collectors.NotAvailableException)
            {
                if (backup is not null)
                {
                    KeyBackups.Delete(backup);
                }
                var hint = e.Message.Contains("Read-only file system", StringComparison.Ordinal) && file.Effective.StartsWith("/etc/", StringComparison.Ordinal)
                    ? " The unit's ReadWritePaths= don't have it yet: pnmt-agent update writes them from ssh_keys.writable_paths."
                    : "";
                return Failed(Answer, request, $"{file.Effective}: {e.Message}{hint}");
            }
        }
    }

    /// <summary>The cluster node whose own key this is (§6.6): one of this node's root keys, or a key with a node's <c>root@&lt;node&gt;</c> comment.</summary>
    private static string? ProxmoxNode(string fingerprint, KeyScan scan)
    {
        if (scan.ClusterKeys.TryGetValue(fingerprint, out var own))
        {
            return own;
        }
        foreach (var line in scan.Files.Where(f => f.Content is not null).SelectMany(f => System.Text.Encoding.UTF8.GetString(f.Content!).Split('\n')))
        {
            if (SshKeys.ParseLine(line.TrimEnd('\r')).Key is { } key && key.Fingerprint == fingerprint && key.Comment is { } comment
                && comment.StartsWith("root@", StringComparison.Ordinal) && scan.ClusterKeys.ContainsKey("node:" + comment[5..]))
            {
                return comment;
            }
        }
        return null;
    }

    /// <summary>The rewrite as the file's owner (§6.5 step 6): the agent itself, started with setpriv as that user.</summary>
    private static (int Removed, string? Sha256) AsOwner(InspectedFile file, string fingerprint, string expected)
    {
        var answer = RunAsOwner(file.Account, file.Facts!.Uid, ["internal", "rewrite-keys", "--path", file.Effective, "--fingerprint", fingerprint, "--sha256", expected], null);
        return (answer.Removed, answer.Sha256);
    }

    /// <summary>
    /// <c>setpriv --reuid=&lt;uid&gt; --regid=&lt;gid&gt; --init-groups -- pnmt-agent internal …</c>: the agent's own binary as the account, its answer
    /// a JSON line on stdout; <paramref name="input"/> on its stdin.
    /// </summary>
    public static RewriteAnswer RunAsOwner(KeyAccount account, uint uid, IReadOnlyList<string> arguments, string? input)
    {
        if (!Tool.Exists("setpriv"))
        {
            throw new Core.Collectors.NotAvailableException("setpriv (util-linux) is not installed: a user's file can't be rewritten with the user's rights");
        }
        var result = Tool.Run("setpriv", [$"--reuid={uid}", $"--regid={account.Gid}", "--init-groups", "--", AgentInfo.CurrentExecutable, .. arguments],
            TimeSpan.FromSeconds(60), input);
        var line = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        var answer = line is not null && line.StartsWith('{') ? JsonSerializer.Deserialize<RewriteAnswer>(line, AgentJson.Options) : null;
        if (answer is null)
        {
            throw new IOException($"the rewrite as {account.Name} failed ({result.ExitCode}): {result.Reason}");
        }
        if (answer.Error is { } error)
        {
            throw result.ExitCode == 4 ? new KeyFileChangedException(error) : new IOException(error);
        }
        return answer;
    }

    private SshKeyRemovalResult Refused(Func<SshKeyRemovalOutcome, string?, int, string?, string?, SshKeyRemovalResult> answer, RemoveSshKeyRequest request, string reason)
    {
        logger.LogWarning("Refused to remove the key {Fingerprint} from {Path} ({Account}): {Reason}", request.Fingerprint, request.Path, request.Account, reason);
        Journal.Write($"PNMT asked to remove the key {request.Fingerprint} from {request.Path} ({request.Account}): refused. {reason}", warning: true);
        return answer(SshKeyRemovalOutcome.Refused, reason, 0, null, null);
    }

    private SshKeyRemovalResult Failed(Func<SshKeyRemovalOutcome, string?, int, string?, string?, SshKeyRemovalResult> answer, RemoveSshKeyRequest request, string message)
    {
        logger.LogError("Removing the key {Fingerprint} from {Path} ({Account}) failed: {Message}", request.Fingerprint, request.Path, request.Account, message);
        Journal.Write($"Removing the key {request.Fingerprint} from {request.Path} ({request.Account}) failed: {message}", warning: true);
        return answer(SshKeyRemovalOutcome.Failed, message, 0, null, null);
    }
}
