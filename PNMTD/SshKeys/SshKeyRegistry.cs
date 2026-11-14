using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

// The folder is PNMTD/SshKeys/ (docs/linux-sensor-plan.md §9.4); a namespace PNMTD.SshKeys would hide PNMTD.Lib.Agent.SshKeys in
// every PNMTD.* namespace, hence PNMTD.SshKeyManager.
namespace PNMTD.SshKeyManager;

/// <summary>A registered key as messages and lists name it: its name, its owner (a user, or the owner's text), its kind and state.</summary>
public sealed record RegisteredKey(
    Guid Id,
    string Fingerprint,
    string Name,
    Guid? UserId,
    string? Username,
    string? UserDisplayName,
    bool? UserActive,
    string? OwnerText,
    SshKeyKind Kind,
    SshKeyState State)
{
    /// <summary>The user's name, or the owner's text ("storage team", "former user jdoe").</summary>
    public string? Owner => Username ?? OwnerText;

    public bool Active => State == SshKeyState.Active;

    /// <summary>For <see cref="AgentAccessService.KeyText"/> and <see cref="AgentAccessService.KeyName"/>.</summary>
    public SshKeyName Naming => new(Name, Owner, State == SshKeyState.Retired);
}

/// <summary>
/// The key manager's register (docs/linux-sensor-plan.md §6.4), as the rest of PNMTD reads it: keys by fingerprint, named "Philipp,
/// laptop (philipp)"; how a key file and an unregistered key are worded in change lines, events and the removals' history.
/// Registering, editing, retiring and deleting are the API's (<see cref="Services.Domain.SshKeyService"/>).
/// </summary>
public static class SshKeyRegistry
{
    /// <summary>A deleted user's keys keep their names; their owner becomes this text with the user's name (§6.4).</summary>
    public const string FormerUserPrefix = "former user ";

    public static string FormerUser(string username) => FormerUserPrefix + username;

    public static async Task<IReadOnlyDictionary<string, RegisteredKey>> ByFingerprintAsync(PnmtdDbContext db, IEnumerable<string> fingerprints, CancellationToken ct)
    {
        var list = fingerprints.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            return new Dictionary<string, RegisteredKey>(StringComparer.Ordinal);
        }
        var keys = await Query(db, k => list.Contains(k.Fingerprint)).ToListAsync(ct);
        return keys.ToDictionary(k => k.Fingerprint, StringComparer.Ordinal);
    }

    /// <summary>
    /// The registered keys that <paramref name="filter"/> selects, with their owners. The filter goes before the projection: SQL can't
    /// look into a record built by its constructor.
    /// </summary>
    public static IQueryable<RegisteredKey> Query(PnmtdDbContext db, System.Linq.Expressions.Expression<Func<SshKeyEntity, bool>>? filter = null) =>
        from k in filter is null ? db.SshKeys.AsNoTracking() : db.SshKeys.AsNoTracking().Where(filter)
        join u in db.Users.AsNoTracking() on k.UserId equals u.Id into users
        from u in users.DefaultIfEmpty()
        select new RegisteredKey(k.Id, k.Fingerprint, k.Name, k.UserId, u == null ? null : u.Username, u == null ? null : u.DisplayName,
            u == null ? null : (bool?)u.IsActive, k.Owner, k.Kind, k.State);

    /// <summary>The fingerprints of the keys of an owner: a user's id, or a text (an owner's text, a user's name or display name; a substring pattern).</summary>
    public static IQueryable<string> OwnedBy(PnmtdDbContext db, Guid? userId, string? pattern) =>
        userId is { } id ? db.SshKeys.AsNoTracking().Where(k => k.UserId == id).Select(k => k.Fingerprint)
            : from k in db.SshKeys.AsNoTracking()
              join u in db.Users.AsNoTracking() on k.UserId equals u.Id into users
              from u in users.DefaultIfEmpty()
              where EF.Functions.ILike(k.Owner ?? "", pattern ?? "") || (u != null && (EF.Functions.ILike(u.Username, pattern ?? "") || EF.Functions.ILike(u.DisplayName, pattern ?? "")))
              select k.Fingerprint;

    /// <summary>A key file in a sentence: "deploy's authorized_keys", or with its path when it isn't the usual one: "root's /etc/ssh/keys/root".</summary>
    public static string File(string account, string path) =>
        $"{account}'s {(path.EndsWith("/.ssh/authorized_keys", StringComparison.Ordinal) || path.EndsWith("/.ssh/authorized_keys2", StringComparison.Ordinal) ? System.IO.Path.GetFileName(path) : path)}";

    /// <summary>
    /// A key in change lines and the removals' history: "Philipp, laptop (philipp)" when registered, "unregistered key ssh-ed25519
    /// SHA256:Kq1f9hJ0…rS2A 'jdoe@laptop'" when not.
    /// </summary>
    public static string Describe(string fingerprint, string? type, string? comment, RegisteredKey? key) =>
        key is not null ? AgentAccessService.KeyName(fingerprint, key.Naming)
            : $"unregistered key {(type is null ? "" : type + " ")}{SshKeys.Short(fingerprint)}{Quoted(comment)}";

    public static string Quoted(string? comment) => string.IsNullOrEmpty(comment) ? "" : $" '{comment}'";

    /// <summary>The key as a .pub file has it: type, Base64 and comment.</summary>
    public static string OpenSsh(string type, string key, string? comment) => comment is null ? $"{type} {key}" : $"{type} {key} {comment}";

    /// <summary>
    /// Why PNMT may not remove keys on this server at all (§6.5 step 4, as far as PNMT can tell from the hello), with the line of
    /// config.yaml that would allow it; null when it may.
    /// </summary>
    public static string? ServerCannotRemove(AgentRightsSummary? rights, IReadOnlyCollection<string> capabilities, bool revoked)
    {
        if (revoked)
        {
            return "The server's agent is revoked.";
        }
        if (rights is null)
        {
            return "The agent announced no rights: PNMT treats the server as granting nothing.";
        }
        if (ServerRights.IsRefused(rights))
        {
            return $"/etc/pnmt-agent/config.yaml is refused{(rights.Problem is { } problem ? $" ({problem})" : "")}: it grants PNMT nothing.";
        }
        if (!rights.KeyRemoval)
        {
            return "config.yaml doesn't allow PNMT to remove keys: \"ssh_keys: remove: true\" in /etc/pnmt-agent/config.yaml would.";
        }
        if (!capabilities.Contains(AgentCapabilities.KeysRemove))
        {
            return "The agent doesn't announce keys.remove.v1: update it (pnmt-agent update).";
        }
        return null;
    }

    /// <summary>
    /// Why PNMT may not remove a key from a file on a server, or null (§6.5 step 4, in its order): the server's rights, the account
    /// in ssh_keys.users, a protected key, and the file as the agent judged it (its own rights, a user's symlink, …).
    /// </summary>
    public static string? CannotRemove(AgentRightsSummary? rights, IReadOnlyCollection<string> capabilities, bool revoked, string account, bool fileRemovable,
        string? fileNotRemovable, bool isProtected, string? protectedBecause)
    {
        if (ServerCannotRemove(rights, capabilities, revoked) is { } why)
        {
            return why;
        }
        if (rights!.KeyRemovalUsers.Count > 0 && !rights.KeyRemovalUsers.Contains(account, StringComparer.Ordinal))
        {
            return $"config.yaml allows removing keys only for {string.Join(", ", rights.KeyRemovalUsers)}: {account} in ssh_keys.users would allow it here.";
        }
        if (isProtected)
        {
            return $"Protected on this server ({protectedBecause ?? "ssh_keys.protect"}): it is never removed.";
        }
        if (!fileRemovable)
        {
            return fileNotRemovable ?? "The agent doesn't allow removing keys from this file.";
        }
        return null;
    }
}
