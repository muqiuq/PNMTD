using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Models;
using PNMTD.Linux;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.SshKeyManager;

/// <summary>
/// Where each key is authorized, and when it was used (docs/linux-sensor-plan.md §6.3): <c>ssh_key_authorizations</c> rebuilt from
/// every <c>ssh-keys</c> section with a new hash, keyed by server, account, file and fingerprint so the first-seen and last-use times
/// survive; the last use from SSH logins with the key; and the keys added and removed, named by their registration:
/// <list type="bullet">
/// <item>a key that isn't registered as active appears: <b>465</b> (the server's <see cref="AgentEntity.KeyAddedEvents"/>), "root:
///   unregistered key ssh-ed25519 SHA256:Kq1f… 'jdoe@laptop' added to /root/.ssh/authorized_keys", "root: retired key Jan, old laptop
///   (jdoe) added again to …";</item>
/// <item>a registered, active key added, any key removed (outside PNMT; PNMT's own removals are worded by
///   <see cref="SshKeyRemovals"/>), a key's options changed: notable lines, so <b>263</b>.</item>
/// </list>
/// A server's first section records its keys as found, with no event for each.
/// </summary>
public static class SshKeyAuthorizations
{
    /// <summary>465s for one section; more keys are summed up in one more.</summary>
    public const int MaxEvents = 5;

    /// <summary>A removal of PNMT's that was sent or applied this recently explains a key gone from its file.</summary>
    public static readonly TimeSpan OwnRemovalWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// The rows a section gives: one per account, file and fingerprint (a key twice in a file counts once, with its first line), with
    /// what the dialogs need from the file (cluster-wide, removable) and the key (protected).
    /// </summary>
    public static List<SshKeyAuthorizationEntity> RowsFrom(Guid sensorId, SshKeysSection section, DateTime now)
    {
        var rows = new Dictionary<(string, string, string), SshKeyAuthorizationEntity>();
        foreach (var file in section.Files)
        {
            var account = Cuts.Cut(file.Account, 256);
            var path = Cuts.Cut(file.Path, 1024);
            foreach (var key in file.Keys)
            {
                var fingerprint = Cuts.Cut(key.Fingerprint, 100);
                if (rows.ContainsKey((account, path, fingerprint)))
                {
                    continue;
                }
                rows[(account, path, fingerprint)] = new SshKeyAuthorizationEntity
                {
                    Id = Guid.NewGuid(), SensorId = sensorId, Account = account, Path = path, Line = key.Line, Fingerprint = fingerprint,
                    Type = Cuts.Cut(key.Type, 100), Bits = key.Bits, Comment = Cuts.CutOrNull(key.Comment, 500), PublicKey = key.Key.Length <= SshKeys.MaxLineChars ? key.Key : "",
                    Options = key.Options.Count == 0 ? null : JsonSerializer.Serialize(key.Options.Take(64).Select(o => Cuts.Cut(o, 4096)), AgentJson.Options),
                    Notes = key.Notes.Take(20).Select(n => Cuts.Cut(n, 300)).ToList(), ClusterWide = file.ClusterWide, Removable = file.Removable,
                    NotRemovable = Cuts.CutOrNull(file.NotRemovable, 500), Protected = key.Protected, ProtectedBecause = Cuts.CutOrNull(key.ProtectedBecause, 200),
                    FirstSeenAt = now,
                };
            }
        }
        return rows.Values.ToList();
    }

    /// <summary>The options of a row, as the section had them.</summary>
    public static IReadOnlyList<string> Options(SshKeyAuthorizationEntity row)
    {
        if (string.IsNullOrEmpty(row.Options))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<string>>(row.Options, AgentJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Rebuilds a server's rows from its section: new keys get their first-seen time now and their last use from the logins PNMT
    /// has; keys still there keep both; keys gone are deleted. Saved by the caller.
    /// </summary>
    public static async Task SyncAsync(PnmtdDbContext db, Guid sensorId, SshKeysSection section, DateTime now, CancellationToken ct)
    {
        var existing = (await db.SshKeyAuthorizations.Where(a => a.SensorId == sensorId).ToListAsync(ct))
            .GroupBy(a => (a.Account, a.Path, a.Fingerprint)).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<(string, string, string)>();
        var added = new List<SshKeyAuthorizationEntity>();
        foreach (var row in RowsFrom(sensorId, section, now))
        {
            var key = (row.Account, row.Path, row.Fingerprint);
            seen.Add(key);
            if (existing.TryGetValue(key, out var current))
            {
                current.Line = row.Line;
                current.Type = row.Type;
                current.Bits = row.Bits;
                current.Comment = row.Comment;
                current.PublicKey = row.PublicKey;
                current.Options = row.Options;
                current.Notes = row.Notes;
                current.ClusterWide = row.ClusterWide;
                current.Removable = row.Removable;
                current.NotRemovable = row.NotRemovable;
                current.Protected = row.Protected;
                current.ProtectedBecause = row.ProtectedBecause;
                continue;
            }
            db.SshKeyAuthorizations.Add(row);
            added.Add(row);
        }
        db.SshKeyAuthorizations.RemoveRange(existing.Where(e => !seen.Contains(e.Key)).Select(e => e.Value));
        if (added.Count == 0)
        {
            return;
        }
        // A key found now may have logged in before PNMT saw its file: its last use from the logins (kept 180 days).
        var fingerprints = added.Select(r => r.Fingerprint).Distinct().ToList();
        var logins = await db.AgentLogins.AsNoTracking()
            .Where(l => l.SensorId == sensorId && l.Outcome == LogonOutcome.Success && l.KeyFingerprint != null && fingerprints.Contains(l.KeyFingerprint))
            .Select(l => new { l.User, l.KeyFingerprint, At = l.LastAt ?? l.At, l.SourceAddress })
            .ToListAsync(ct);
        foreach (var row in added)
        {
            var last = logins.Where(l => l.User == row.Account && l.KeyFingerprint == row.Fingerprint).MaxBy(l => l.At);
            if (last is not null)
            {
                row.LastUsedAt = last.At;
                row.LastUsedFrom = last.SourceAddress;
            }
        }
    }

    /// <summary>
    /// A stored ssh-keys section (from <see cref="AgentChanges.StoredAsync"/>): the rows rebuilt, and the readable changes against the
    /// section it replaces, with 465 for keys that aren't registered as active. <paramref name="quiet"/>: rows only (a section that
    /// was stored before the key manager existed). The first section of a server says how many keys it found, and nothing per key.
    /// </summary>
    public static async Task<List<AgentChange>> StoredAsync(PnmtdDbContext db, AgentEntity agent, SshKeysSection after, SshKeysSection? before, bool quiet,
        DateTime now, CancellationToken ct)
    {
        await SyncAsync(db, agent.SensorId, after, now, ct);
        if (quiet)
        {
            return [];
        }
        var fingerprints = after.Files.SelectMany(f => f.Keys).Concat(before?.Files.SelectMany(f => f.Keys) ?? []).Select(k => k.Fingerprint);
        var names = await SshKeyRegistry.ByFingerprintAsync(db, fingerprints, ct);
        if (before is null)
        {
            var keys = after.Files.SelectMany(f => f.Keys.DistinctBy(k => k.Fingerprint)).ToList();
            var files = after.Files.Count(f => f.Keys.Count > 0);
            var notActive = keys.Select(k => k.Fingerprint).Distinct().Count(f => !names.TryGetValue(f, out var key) || !key.Active);
            return
            [
                new($"{keys.Count} {(keys.Count == 1 ? "key" : "keys")} found in {files} {(files == 1 ? "file" : "files")}"
                    + (notActive > 0 ? $" ({notActive} not registered as active)" : ""), false),
            ];
        }

        var (added, removed, options) = LinuxDiffers.SshKeyDeltas(before, after);
        var changes = new List<AgentChange>();
        var alerts = new List<string>();
        foreach (var place in added)
        {
            var key = names.GetValueOrDefault(place.Key.Fingerprint);
            if (key is { Active: true })
            {
                changes.Add(new($"{SshKeyRegistry.Describe(place.Key.Fingerprint, place.Key.Type, place.Key.Comment, key)} added to {SshKeyRegistry.File(place.Account, place.Path)}",
                    true, Key: place.Key.Fingerprint));
                continue;
            }
            // §6.3: 465 instead of 263, so nobody vouches for a key by adding it.
            var message = key is null
                ? $"{place.Account}: unregistered key {place.Key.Type} {SshKeys.Short(place.Key.Fingerprint)}{SshKeyRegistry.Quoted(place.Key.Comment)} added to {place.Path}"
                : $"{place.Account}: retired key {AgentAccessService.KeyName(place.Key.Fingerprint, key.Naming)} added again to {place.Path}";
            changes.Add(new(message, false, Key: place.Key.Fingerprint));
            alerts.Add(message);
        }
        var gone = removed.Select(r => r.Key.Fingerprint).Distinct().ToList();
        var since = now - OwnRemovalWindow;
        var own = gone.Count == 0 ? [] : await db.SshKeyRemovals.AsNoTracking()
            .Where(r => r.SensorId == agent.SensorId && gone.Contains(r.Fingerprint)
                && ((r.State == SshKeyRemovalState.Sent && r.SentAt > since) || (r.State == SshKeyRemovalState.Removed && r.AppliedAt > since)))
            .Select(r => new { r.Account, r.Path, r.Fingerprint }).ToListAsync(ct);
        foreach (var place in removed)
        {
            if (own.Any(r => r.Account == place.Account && r.Path == place.Path && r.Fingerprint == place.Key.Fingerprint))
            {
                // PNMT's own: the removal's result says "… by PNMT (philipp)", once.
                continue;
            }
            var key = names.GetValueOrDefault(place.Key.Fingerprint);
            changes.Add(new($"{SshKeyRegistry.Describe(place.Key.Fingerprint, place.Key.Type, place.Key.Comment, key)} removed from "
                + $"{SshKeyRegistry.File(place.Account, place.Path)}, outside PNMT", true, Key: place.Key.Fingerprint));
        }
        foreach (var (was, now2) in options)
        {
            var key = names.GetValueOrDefault(now2.Key.Fingerprint);
            changes.Add(new($"{SshKeyRegistry.Describe(now2.Key.Fingerprint, now2.Key.Type, now2.Key.Comment, key)} in {SshKeyRegistry.File(now2.Account, now2.Path)}: "
                + $"options {OptionsText(was.Key.Options)} → {OptionsText(now2.Key.Options)}", true, was.Key.Options, now2.Key.Options, now2.Key.Fingerprint));
        }
        if (alerts.Count > 0 && agent.KeyAddedEvents && agent.Sensor.Enabled && agent.Sensor.Parent.Enabled)
        {
            var source = AgentPlatformInfo.Linux.EventSource;
            foreach (var alert in alerts.Take(MaxEvents))
            {
                await SensorEventWriter.WriteAsync(db, agent.SensorId, PNMTStatusCodes.LINUX_KEY_ADDED, Cuts.Cut(alert, 1000), source, ct);
            }
            if (alerts.Count > MaxEvents)
            {
                await SensorEventWriter.WriteAsync(db, agent.SensorId, PNMTStatusCodes.LINUX_KEY_ADDED,
                    $"{agent.Hostname}: {alerts.Count - MaxEvents} more keys that aren't registered as active were added (see the SSH keys tab)", source, ct);
            }
        }
        return changes;
    }

    private static string OptionsText(IReadOnlyList<string> options) => options.Count == 0 ? "none" : Cuts.Cut(string.Join(",", options), 300);

    /// <summary>
    /// A login with a key (§5.6) sets its last use on that server, for that account (SSH sessions and file transfers alike): the
    /// newest of the batch, never older than what is there.
    /// </summary>
    public static async Task UsedAsync(PnmtdDbContext db, Guid sensorId, IEnumerable<AgentLoginEntity> logins, CancellationToken ct)
    {
        var uses = logins.Where(l => l.Outcome == LogonOutcome.Success && l.KeyFingerprint is not null && l.Kind is LogonKind.Ssh or LogonKind.Sftp)
            .GroupBy(l => (l.User, l.KeyFingerprint!)).Select(g => g.MaxBy(l => l.LastAt ?? l.At)!).ToList();
        foreach (var use in uses)
        {
            DateTime? at = use.LastAt ?? use.At;
            var from = use.SourceAddress;
            await db.SshKeyAuthorizations
                .Where(a => a.SensorId == sensorId && a.Account == use.User && a.Fingerprint == use.KeyFingerprint && (a.LastUsedAt == null || a.LastUsedAt < at))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.LastUsedAt, at).SetProperty(a => a.LastUsedFrom, from), ct);
        }
    }
}
