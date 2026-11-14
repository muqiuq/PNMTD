using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace PNMTD.Models.Db;

// SSH keys (docs/linux-sensor-plan.md §6.3–§6.5, §9.3): the key manager's register, where each key is authorized on which
// server, and the removals PNMT asked for. Joined by the fingerprint's value, never by a reference, so a key found today and
// registered tomorrow is named everywhere, past logins included. Migrations AddSshKeys (L5) and AddSshKeyRemovals (L6).

[JsonConverter(typeof(JsonStringEnumConverter<SshKeyKind>))]
public enum SshKeyKind { Person, Service }

[JsonConverter(typeof(JsonStringEnumConverter<SshKeyState>))]
public enum SshKeyState { Active, Retired }

/// <summary>
/// A key in the key manager (§6.4): the public key as it was pasted or found, a name (required), an owner (a PNMT user, or a text
/// for keys of nobody in PNMT; "former user jdoe" once the user is deleted), a kind, a note, and active or retired. Registering
/// names and assigns a key; it never deploys it anywhere (§2.2).
/// </summary>
[Table("ssh_keys")]
public class SshKeyEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary><c>SHA256:…</c>, as <c>ssh-keygen -l</c> prints it. Unique: a key is registered once.</summary>
    [MaxLength(100)]
    public string Fingerprint { get; set; } = "";

    [MaxLength(100)]
    public string Type { get; set; } = "";

    public int? Bits { get; set; }

    /// <summary>The key's Base64, as in a .pub file.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>The comment it came with.</summary>
    [MaxLength(500)]
    public string? Comment { get; set; }

    /// <summary>"Philipp, laptop", "Backup server bkp-01".</summary>
    [MaxLength(200)]
    public string Name { get; set; } = "";

    /// <summary>A PNMT user; set to null when the user is deleted (and <see cref="Owner"/> becomes "former user …").</summary>
    public Guid? UserId { get; set; }

    /// <summary>For keys of nobody in PNMT: a service, a contractor, a vendor's support.</summary>
    [MaxLength(200)]
    public string? Owner { get; set; }

    public SshKeyKind Kind { get; set; }

    [MaxLength(2000)]
    public string? Note { get; set; }

    public SshKeyState State { get; set; }

    public DateTime? RetiredAt { get; set; }

    [MaxLength(200)]
    public string? RetiredByName { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    public string? CreatedByName { get; set; }

    public DateTime UpdatedAt { get; set; }

    [MaxLength(200)]
    public string? UpdatedByName { get; set; }
}

/// <summary>
/// A key found in a server's key file (§6.3): rebuilt from each <c>ssh-keys</c> section, keyed by server, account, file and
/// fingerprint, so the first-seen and last-use times survive. Whether PNMT may remove it there comes from the section
/// (<see cref="Removable"/>, <see cref="Protected"/>), for the remove dialog.
/// </summary>
[Table("ssh_key_authorizations")]
public class SshKeyAuthorizationEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    [MaxLength(256)]
    public string Account { get; set; } = "";

    [MaxLength(1024)]
    public string Path { get; set; } = "";

    public int Line { get; set; }

    [MaxLength(100)]
    public string Fingerprint { get; set; } = "";

    [MaxLength(100)]
    public string Type { get; set; } = "";

    public int? Bits { get; set; }

    [MaxLength(500)]
    public string? Comment { get; set; }

    /// <summary>The key's Base64, for <i>Register…</i> from the server's tab and the <i>Unregistered</i> list.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>The options (<c>from=</c>, <c>command=</c>, …) as a JSON list.</summary>
    [Column(TypeName = "jsonb")]
    public string? Options { get; set; }

    /// <summary>The agent's notes (§6.1): weak, an expiry passed, root without from=, twice in the file, a CA, a cluster node's.</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>Proxmox VE's cluster-wide file (/etc/pve): a removal takes the key from every node.</summary>
    public bool ClusterWide { get; set; }

    /// <summary>The file's: whether this server's config.yaml lets PNMT remove keys here, with the reason when not.</summary>
    public bool Removable { get; set; }

    [MaxLength(500)]
    public string? NotRemovable { get; set; }

    /// <summary>config.yaml's ssh_keys.protect, or a Proxmox cluster node's own key: never removed.</summary>
    public bool Protected { get; set; }

    [MaxLength(200)]
    public string? ProtectedBecause { get; set; }

    public DateTime FirstSeenAt { get; set; }

    /// <summary>The last SSH login with this key into this account on this server (§5.6).</summary>
    public DateTime? LastUsedAt { get; set; }

    [MaxLength(100)]
    public string? LastUsedFrom { get; set; }
}

/// <summary>Why a removal was asked for (§6.5): from a server's tab, a key's "from every server", a retirement, an offboarding, the Unregistered list.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SshKeyRemovalReason>))]
public enum SshKeyRemovalReason { Server, Key, Retirement, Offboarding, Unregistered }

/// <summary>
/// A removal's state (§6.5): <see cref="Pending"/> until its server connects (at most Agents__KeyRemovalPendingDays, then
/// <see cref="Expired"/>), <see cref="Sent"/> to the agent, then what the agent answered: <see cref="Removed"/>,
/// <see cref="NotFound"/> (gone already), <see cref="Refused"/> (its config.yaml, or PNMT's own check), <see cref="Failed"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SshKeyRemovalState>))]
public enum SshKeyRemovalState { Pending, Sent, Removed, NotFound, Refused, Failed, Expired }

/// <summary>
/// A key's removal from one server's file, asked for by an Admin (§6.5): permanent, it outlives the server (kept
/// Agents__KeepKeyRemovalsDays, a year). The key's name and the server's are kept as they were.
/// </summary>
[Table("ssh_key_removals")]
public class SshKeyRemovalEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The removals asked for together (one dialog, one offboarding).</summary>
    public Guid BatchId { get; set; }

    public Guid? SensorId { get; set; }

    [MaxLength(500)]
    public string ServerName { get; set; } = "";

    [MaxLength(256)]
    public string Account { get; set; } = "";

    [MaxLength(1024)]
    public string Path { get; set; } = "";

    [MaxLength(100)]
    public string Fingerprint { get; set; } = "";

    /// <summary>The key as named when asked: "Jan, old laptop (jdoe)", or "unregistered key SHA256:Kq1f…".</summary>
    [MaxLength(500)]
    public string? KeyName { get; set; }

    public DateTime RequestedAt { get; set; }

    public Guid? RequestedBy { get; set; }

    [MaxLength(200)]
    public string? RequestedByName { get; set; }

    public SshKeyRemovalReason Reason { get; set; }

    public SshKeyRemovalState State { get; set; }

    /// <summary>When it went to the agent (the last time, for one sent again at a reconnect).</summary>
    public DateTime? SentAt { get; set; }

    public int? LinesRemoved { get; set; }

    /// <summary>The backup's name on the server, for <c>pnmt-agent keys restore</c>.</summary>
    [MaxLength(500)]
    public string? Backup { get; set; }

    [MaxLength(100)]
    public string? NewFileHash { get; set; }

    [MaxLength(2000)]
    public string? Message { get; set; }

    /// <summary>When the agent answered: removed, not found, refused or failed; or when it expired.</summary>
    public DateTime? AppliedAt { get; set; }
}
