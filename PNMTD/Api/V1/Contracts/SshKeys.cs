using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// The SSH key manager (docs/linux-sensor-plan.md §6.3–§6.5, §10, §11.4): /api/v1/ssh-keys/… (Admin, except mine), the removals'
// history under /api/v1/ssh-key-removals, and a server's keys under /api/v1/agents/servers/{id}/ssh-keys (CanWrite).

/// <summary>
/// A key's owner: a PNMT user (<see cref="UserId"/>, its user and display name, <see cref="Deactivated"/>), or a text
/// (<see cref="Text"/>: a service, a contractor, "former user jdoe" once the user was deleted, then <see cref="FormerUser"/>).
/// <see cref="Name"/> is what lists show: the user's name, or the text.
/// </summary>
public sealed record SshKeyOwner(Guid? UserId, string? Username, string? DisplayName, string? Text, string Name, bool Deactivated, bool FormerUser);

/// <summary>The last login with a key (§6.3): when, on which server, into which account, from where.</summary>
public sealed record SshKeyLastUse(DateTime At, Ref Server, string Account, string? From);

/// <summary>
/// A registered key in the list (§11.4): its public key's type, bits, fingerprint and comment, the name, owner, kind and note, the
/// state, where it is authorized (<see cref="Servers"/>, <see cref="Accounts"/>: server and account pairs), its last use anywhere,
/// and <see cref="Notes"/>: <c>weak</c>, <c>unused180</c> (not used for 180 days, as far as PNMT knows), <c>ownerDeactivated</c>,
/// <c>retiredStillAuthorized</c>. <see cref="Weakness"/> says why it is weak.
/// </summary>
public sealed record SshKeyItem(
    Guid Id,
    string Name,
    string Fingerprint,
    string Type,
    int? Bits,
    string? Comment,
    SshKeyOwner Owner,
    SshKeyKind Kind,
    string? Note,
    SshKeyState State,
    DateTime? RetiredAt,
    string? RetiredByName,
    int Servers,
    int Accounts,
    SshKeyLastUse? LastUse,
    IReadOnlyList<string> Notes,
    string? Weakness,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime UpdatedAt,
    string? UpdatedByName);

/// <summary>
/// <c>GET /api/v1/ssh-keys</c>. <see cref="PageQuery.Search"/>: a name, fingerprint (with or without <c>SHA256:</c>), comment, owner,
/// or a pasted public key. <see cref="Owner"/>: a user's id, or a text (an owner's text or a user name, case-insensitive substring).
/// <see cref="Filter"/>: <c>weak</c>, <c>unused180</c>, <c>ownerDeactivated</c>, <c>retiredStillAuthorized</c>. Sort: name (default),
/// owner, kind, state, lastUsed, servers, created.
/// </summary>
public sealed class SshKeyQuery : PageQuery
{
    public string? Owner { get; set; }

    public SshKeyKind? Kind { get; set; }

    public SshKeyState? State { get; set; }

    public string? Filter { get; set; }
}

/// <summary>
/// Where a key is authorized (§6.3): the server and its platform, the account, the file and line, the options and notes the agent
/// found, Proxmox's cluster-wide file, first seen and last use there, and whether PNMT may remove it there now
/// (<see cref="CanRemove"/>, else <see cref="CannotRemove"/>: the server's rights, the file's, a protected key, no keys.remove.v1).
/// </summary>
public sealed record SshKeyAuthorizationItem(
    Guid Id,
    Ref Server,
    string Hostname,
    bool Connected,
    string Account,
    string Path,
    int Line,
    string Fingerprint,
    string Type,
    int? Bits,
    string? Comment,
    IReadOnlyList<string> Options,
    IReadOnlyList<string> Notes,
    bool ClusterWide,
    bool Protected,
    string? ProtectedBecause,
    DateTime FirstSeenAt,
    DateTime? LastUsedAt,
    string? LastUsedFrom,
    bool CanRemove,
    string? CannotRemove);

/// <summary>A recent login with a key (§11.4's drawer): server, account, from where, when, and how often (an aggregate).</summary>
public sealed record SshKeyLoginItem(Guid Id, Ref Server, string User, LogonKind Kind, DateTime At, DateTime? LastAt, int Count, string? SourceAddress);

/// <summary>A line of a key's history: the audit (registered, edited, retired, …), the servers' changes (added, removed), and its removals.</summary>
public sealed record SshKeyHistoryItem(DateTime At, string Source, string Text, string? By, Ref? Server);

/// <summary>
/// A key's drawer (§11.4): the key, its public key as a .pub file has it (<see cref="PublicKey"/>), the comments seen on servers, every
/// authorization, the recent logins with it (30 days, at most 50), its history, and its removals.
/// </summary>
public sealed record SshKeyDetail(
    SshKeyItem Key,
    string PublicKey,
    IReadOnlyList<string> CommentsSeen,
    IReadOnlyList<SshKeyAuthorizationItem> Authorizations,
    IReadOnlyList<SshKeyLoginItem> RecentLogins,
    IReadOnlyList<SshKeyHistoryItem> History,
    IReadOnlyList<SshKeyRemovalItem> Removals);

/// <summary>
/// <c>POST /api/v1/ssh-keys/parse</c>: one or more public key lines as pasted, for <i>Add keys…</i>'s "computed as you type". Nothing is
/// stored or logged.
/// </summary>
public sealed record SshKeyParseRequest(string? Text)
{
    /// <summary>Without the text: MVC's trace log prints an action's arguments, and a pasted private key must be logged nowhere (§6.6).</summary>
    public override string ToString() => $"{nameof(SshKeyParseRequest)} {{ Text = ({Text?.Length ?? 0} characters) }}";
}

/// <summary>
/// A pasted line (§11.4): a key with its <see cref="Index"/> among the keys (for <see cref="SshKeyRegisterRequest.Names"/>), type,
/// bits, fingerprint, comment, weakness, <see cref="SuggestedName"/> (the comment), whether it is registered already
/// (<see cref="Registered"/>: refused) or twice in the text (<see cref="Duplicate"/>: refused), and on how many servers it is
/// authorized; or why the line is not a key (<see cref="Problem"/>: <c>TooLong</c>, <c>PrivateKey</c>, <c>Unreadable</c>, with
/// <see cref="ProblemText"/>).
/// </summary>
public sealed record SshKeyParseLine(
    int Line,
    int? Index,
    string? Fingerprint,
    string? Type,
    int? Bits,
    string? Comment,
    string? Weakness,
    string? SuggestedName,
    Ref? Registered,
    bool Duplicate,
    int Servers,
    SshKeyLineProblem Problem,
    string? ProblemText);

/// <summary>
/// The parsed text: each line that isn't blank or a comment, the keys that would be registered (<see cref="Keys"/>), and
/// <see cref="PrivateKey"/>: the text holds a private key, so nothing of it is registered (the UI says to throw that key away).
/// </summary>
public sealed record SshKeyParseResult(IReadOnlyList<SshKeyParseLine> Lines, int Keys, int Refused, bool PrivateKey);

/// <summary>
/// <c>POST /api/v1/ssh-keys</c> (§6.4): one or more public keys (<see cref="PublicKeys"/>, as in <c>id_ed25519.pub</c>; options before a
/// key are ignored), each with its name: <see cref="Names"/>[i] for the i-th key (the parse's <see cref="SshKeyParseLine.Index"/>),
/// else <see cref="Name"/> as a template (<c>{comment}</c>: the key's comment or short fingerprint, <c>{n}</c>: its number; a plain
/// name for several keys gets " (1)", " (2)"). An owner: a user (<see cref="UserId"/>) or a text (<see cref="Owner"/>), one of them;
/// a kind; a note. Refused as a whole, with each line's reason: a private key (never stored or logged), a line that isn't a key, a
/// key registered before or twice in the text.
/// </summary>
public sealed record SshKeyRegisterRequest(
    string? PublicKeys,
    string? Name,
    IReadOnlyList<string?>? Names,
    Guid? UserId,
    string? Owner,
    SshKeyKind Kind,
    string? Note)
{
    /// <summary>Without the pasted keys: MVC's trace log prints an action's arguments, and a pasted private key must be logged nowhere (§6.6).</summary>
    public override string ToString() =>
        $"{nameof(SshKeyRegisterRequest)} {{ PublicKeys = ({PublicKeys?.Length ?? 0} characters), Name = {Name}, Names = {Names?.Count}, UserId = {UserId}, Owner = {Owner}, Kind = {Kind} }}";
}

/// <summary><c>PUT /api/v1/ssh-keys/{id}</c>: the name, the owner (a user or a text), the kind and the note. The key itself never changes.</summary>
public sealed record SshKeyUpdateRequest(string? Name, Guid? UserId, string? Owner, SshKeyKind Kind, string? Note);

/// <summary>
/// A key found on servers and not registered (§6.4's work list), by fingerprint: its type, bits, the public key as a .pub file has
/// it (for <i>Register…</i>), the comments seen, where it is authorized (with each place's removability), first seen and last use, the
/// agents' notes and its weakness.
/// </summary>
public sealed record SshKeyUnregisteredItem(
    string Fingerprint,
    string Type,
    int? Bits,
    string? PublicKey,
    IReadOnlyList<string> Comments,
    int Servers,
    int Accounts,
    IReadOnlyList<SshKeyAuthorizationItem> Where,
    DateTime FirstSeenAt,
    SshKeyLastUse? LastUse,
    IReadOnlyList<string> Notes,
    string? Weakness);

/// <summary>
/// <c>GET /api/v1/ssh-keys/unregistered</c>: <see cref="PageQuery.Search"/> on fingerprint, comment, server and account;
/// <see cref="SensorId"/>: one server's; <see cref="Filter"/>: <c>weak</c>, <c>unused180</c>. Sort: firstSeen (default, newest first
/// with -firstSeen), lastUsed, servers, type.
/// </summary>
public sealed class SshKeyUnregisteredQuery : PageQuery
{
    public Guid? SensorId { get; set; }

    public string? Filter { get; set; }
}

/// <summary>A user as the key manager shows it.</summary>
public sealed record SshKeyUserRef(Guid Id, string Username, string DisplayName, bool IsActive);

/// <summary>A key with every place it is authorized: a user's keys, for <i>Admin → Users</i> and offboarding.</summary>
public sealed record SshKeyWithAuthorizations(SshKeyItem Key, IReadOnlyList<SshKeyAuthorizationItem> Authorizations);

/// <summary><c>GET /api/v1/ssh-keys/users/{userId}</c>: the user, their keys and where each is authorized, and how many removals offboarding would ask for.</summary>
public sealed record SshKeyUserKeys(SshKeyUserRef User, IReadOnlyList<SshKeyWithAuthorizations> Keys, int Authorizations);

/// <summary>Where one of my keys is authorized: the server's name and the account, and its last use there.</summary>
public sealed record SshKeyMinePlace(string Server, string Account, DateTime? LastUsedAt);

/// <summary><c>GET /api/v1/ssh-keys/mine</c> (§6.4, every role): one of the caller's keys, read-only.</summary>
public sealed record SshKeyMineItem(Guid Id, string Name, string Fingerprint, string Type, int? Bits, string? Comment, SshKeyKind Kind, SshKeyState State,
    IReadOnlyList<SshKeyMinePlace> Where, DateTime? LastUsedAt);

/// <summary>A deactivated user whose keys are still authorized somewhere: the strip's <i>Remove all…</i> (offboarding).</summary>
public sealed record SshKeyDeactivatedUser(SshKeyUserRef User, int Keys, int Authorizations);

/// <summary>
/// <c>GET /api/v1/ssh-keys/summary</c>, the page's strip (§11.4): registered keys (active, retired), unregistered keys found on servers
/// (by fingerprint), retired keys still authorized, deactivated users' keys still authorized (and those users), weak keys (registered
/// or found), registered keys unused for 180 days, and removals pending.
/// </summary>
public sealed record SshKeySummary(
    int Registered,
    int Active,
    int Retired,
    int Unregistered,
    int RetiredStillAuthorized,
    int DeactivatedUsersKeysAuthorized,
    IReadOnlyList<SshKeyDeactivatedUser> DeactivatedUsers,
    int Weak,
    int Unused180,
    int PendingRemovals);

// --- a server's keys (§6.3: GET /api/v1/agents/servers/{id}/ssh-keys) ---------------------------------------------

/// <summary>A key's registration, where it has one.</summary>
public sealed record SshKeyRegistration(Guid Id, string Name, SshKeyOwner Owner, SshKeyKind Kind, SshKeyState State);

/// <summary>
/// A key on a server's tab: what the agent found (line, type, bits, fingerprint, comment, options, notes, protected), the public key as a
/// .pub file has it (for <i>Register…</i>), its registration or null, first seen and last use, and whether PNMT may remove it.
/// </summary>
public sealed record SshKeyServerKey(
    int Line,
    string Type,
    int? Bits,
    string Fingerprint,
    string? Comment,
    IReadOnlyList<string> Options,
    IReadOnlyList<string> Notes,
    bool Protected,
    string? ProtectedBecause,
    string PublicKey,
    SshKeyRegistration? Registration,
    DateTime? FirstSeenAt,
    DateTime? LastUsedAt,
    string? LastUsedFrom,
    bool CanRemove,
    string? CannotRemove,
    string? Weakness);

/// <summary>A key file on a server's tab (§6.1): account, path (and a followed symlink's target), owner, mode, cluster-wide, problems, lines that aren't keys, removability, keys.</summary>
public sealed record SshKeyServerFile(
    string Account,
    string Path,
    string? Target,
    bool Exists,
    string? Owner,
    string? Group,
    string? Mode,
    DateTime? ModifiedAt,
    bool ClusterWide,
    IReadOnlyList<string> Problems,
    IReadOnlyList<int> InvalidLines,
    bool Removable,
    string? NotRemovable,
    IReadOnlyList<SshKeyServerKey> Keys);

/// <summary>
/// A server's <i>SSH keys</i> tab (§6.3): sshd's key settings and CA keys, the files and their keys with their registration, last use and
/// removability, the section's notes, and whether PNMT may remove keys on this server at all (<see cref="RemovalAllowed"/>, else
/// <see cref="RemovalRefused"/> with the line of config.yaml that would allow it). Null section parts until the agent reported it.
/// </summary>
public sealed record SshKeyServerView(
    Guid SensorId,
    DateTime? CollectedAt,
    SshdKeySettings? Sshd,
    IReadOnlyList<CaKey> CaKeys,
    IReadOnlyList<SshKeyServerFile> Files,
    int TotalKeys,
    bool Truncated,
    IReadOnlyList<string> Notes,
    bool Connected,
    bool RemovalAllowed,
    string? RemovalRefused,
    IReadOnlyList<string> RemovalUsers,
    int Unregistered,
    int RetiredAuthorized,
    int Weak);

// --- removals (§6.5) -------------------------------------------------------------------------------------------

/// <summary>One key in one file on one server: what a removal names. <see cref="Path"/> is the one the agent reported.</summary>
public sealed record SshKeyRemovalTarget(Guid SensorId, string Account, string Path, string Fingerprint);

/// <summary><c>POST /api/v1/ssh-keys/remove/preview</c>: the items the dialog would remove.</summary>
public sealed record SshKeyRemovalPreviewRequest(IReadOnlyList<SshKeyRemovalTarget>? Items);

/// <summary>
/// An item of the remove dialog (§6.5 step 2): the server (connected or not), the key's name and owner as registered, whether the
/// server allows the removal (<see cref="Allowed"/>, else <see cref="Refused"/>: the reason from its rights), and its warnings: the
/// account would have no key left; root would have no key left with password login off; used in the last 24 hours; a cluster-wide
/// file (with <see cref="ClusterNodes"/>); the server is offline, so the removal waits.
/// </summary>
public sealed record SshKeyRemovalPreviewItem(
    SshKeyRemovalTarget Item,
    Ref? Server,
    string ServerName,
    bool Connected,
    string KeyName,
    Guid? KeyId,
    string? KeyOwner,
    string? Comment,
    bool Allowed,
    string? Refused,
    IReadOnlyList<string> Warnings,
    bool ClusterWide,
    IReadOnlyList<string> ClusterNodes,
    DateTime? LastUsedAt,
    int KeysLeft);

/// <summary>
/// The remove dialog's content: each item, how many are allowed and refused, the warnings for all (sessions that use a key stay
/// open), and <see cref="Confirm"/>: the number the Admin types (every item, the refused ones included).
/// </summary>
public sealed record SshKeyRemovalPreview(IReadOnlyList<SshKeyRemovalPreviewItem> Items, int Allowed, int Refused, IReadOnlyList<string> Warnings, int Confirm);

/// <summary>
/// <c>POST /api/v1/ssh-keys/remove</c> (§6.5): the items, why (<see cref="Reason"/>), and <see cref="Confirm"/>, which must equal the
/// number of items. Each is recorded: sent to a connected agent that announced keys.remove.v1, pending for an offline server, or
/// refused where the server doesn't allow it.
/// </summary>
public sealed record SshKeyRemoveRequest(IReadOnlyList<SshKeyRemovalTarget>? Items, SshKeyRemovalReason Reason, int Confirm);

/// <summary>A removal in the history (§11.4's <i>Removals</i>), with the key's current registration (<see cref="KeyId"/>) when it has one.</summary>
public sealed record SshKeyRemovalItem(
    Guid Id,
    Guid BatchId,
    Ref? Server,
    string ServerName,
    string Account,
    string Path,
    string Fingerprint,
    string? KeyName,
    Guid? KeyId,
    DateTime RequestedAt,
    string? RequestedByName,
    SshKeyRemovalReason Reason,
    SshKeyRemovalState State,
    DateTime? SentAt,
    int? LinesRemoved,
    string? Backup,
    string? Message,
    DateTime? AppliedAt);

/// <summary>What <c>remove</c> did: the batch, each item's removal now (sent, pending, refused, or already answered), and the counts.</summary>
public sealed record SshKeyRemoveResult(Guid BatchId, IReadOnlyList<SshKeyRemovalItem> Items, int Sent, int Pending, int Refused);

/// <summary>
/// <c>GET /api/v1/ssh-key-removals</c>: by server, key (fingerprint, with or without <c>SHA256:</c>), owner (a user's id or a text, as
/// the keys' filter), state, reason and batch.
/// </summary>
public sealed class SshKeyRemovalQuery : PageQuery
{
    public Guid? SensorId { get; set; }

    public string? Fingerprint { get; set; }

    public string? Owner { get; set; }

    public SshKeyRemovalState? State { get; set; }

    public SshKeyRemovalReason? Reason { get; set; }

    public Guid? BatchId { get; set; }
}

/// <summary><c>POST /api/v1/ssh-keys/users/{userId}/offboard</c>: <see cref="Confirm"/> is the number of removals (the preview's).</summary>
public sealed record SshKeyOffboardRequest(int Confirm);

/// <summary>Offboarding (§6.4): the user's keys retired, and their removal from every server and account.</summary>
public sealed record SshKeyOffboardResult(SshKeyUserRef User, int Retired, SshKeyRemoveResult Removal);
