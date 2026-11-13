using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Enums;

namespace PNMTD.Models.Db;

/// <summary>
/// The settings and latest state of a MIKROTIK sensor, 1:1, deleted with it (docs/mikrotik-plan.md §9.2).
/// Holds no secret: PNMTD logs in with the installation's key (<see cref="MikrotikSshKeyEntity"/>).
/// </summary>
[Table("mikrotik_routers")]
public class MikrotikRouterEntity
{
    [Key]
    public Guid SensorId { get; set; }

    public virtual SensorEntity Sensor { get; set; } = null!;

    public MikrotikMode Mode { get; set; }

    /// <summary>Direct SSH (§3.6): a host name or address PNMTD can reach.</summary>
    public string? Address { get; set; }

    /// <summary>22 by default; the first login corrects it when the router's SSH service uses another.</summary>
    public int SshPort { get; set; } = 22;

    /// <summary>Direct SSH: PNMTD's address as the router sees it, for the <c>pnmt</c> user's <c>address</c>. Null: unrestricted (the UI warns).</summary>
    public string? PnmtSourceAddress { get; set; }

    /// <summary>Custom tunnels may reach the management ports (§3.5). Admin.</summary>
    public bool ManagementAccess { get; set; }

    /// <summary><c>sensitive</c> in the group, <c>show-sensitive</c> in exports (§6.6). Admin.</summary>
    public bool IncludeSecrets { get; set; }

    /// <summary><c>test</c> in the group: ping and traceroute as <c>pnmt</c> (§8.4). Admin.</summary>
    public bool Diagnostics { get; set; }

    public int BackupIntervalHours { get; set; } = 24;

    public MikrotikSetupState SetupState { get; set; }

    /// <summary>When the settings that the join script carries last changed: "script outdated" (§3.3).</summary>
    public DateTime ScriptChangedAt { get; set; }

    // --- the host key (§4.4) -------------------------------------------------------------------------

    public string? HostKeyType { get; set; }

    /// <summary>SHA-256, unpadded Base64 as <c>ssh-keygen -l</c> writes it (without "SHA256:").</summary>
    public string? HostKeyFingerprint { get; set; }

    public DateTime? HostKeyPinnedAt { get; set; }

    /// <summary>A different key the router presented, refused until an Admin accepts it.</summary>
    public string? PendingHostKeyType { get; set; }

    public string? PendingHostKeyFingerprint { get; set; }

    public DateTime? PendingHostKeySeenAt { get; set; }

    // --- the latest reading (§5.1) ---------------------------------------------------------------------

    public string? Identity { get; set; }

    public string? Model { get; set; }

    public string? BoardName { get; set; }

    public string? SerialNumber { get; set; }

    public string? Architecture { get; set; }

    public string? Version { get; set; }

    public string? Channel { get; set; }

    public string? FirmwareCurrent { get; set; }

    public string? FirmwareUpgrade { get; set; }

    public DateTime? BootedAt { get; set; }

    /// <summary>The latest <see cref="Lib.Mikrotik.RouterReading"/> as JSON: resources, the group, the tunnel's settings.</summary>
    [Column(TypeName = "jsonb")]
    public string? Reading { get; set; }

    public DateTime? LastPollAt { get; set; }

    public DateTime? LastSuccessAt { get; set; }

    /// <summary>Since when polls fail; 440 once this is older than the GracePeriod.</summary>
    public DateTime? FailingSince { get; set; }

    public string? LastError { get; set; }

    /// <summary>The code the last failure maps to: 440 (unreachable) or 441 (access denied).</summary>
    public int? LastErrorCode { get; set; }

    /// <summary>244 is written once per version (§5.3).</summary>
    public string? NotifiedUpdateVersion { get; set; }

    public DateTime Created { get; set; }
}

/// <summary>
/// The installation's MikroTik settings, one row (§9.2): its tag (the comment "pnmt:&lt;tag&gt;" on
/// everything a join script creates) and the latest versions per channel.
/// </summary>
[Table("mikrotik_settings")]
public class MikrotikSettingsEntity
{
    public const int SingletonId = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = SingletonId;

    /// <summary>Four hex digits, random per installation.</summary>
    public string Tag { get; set; } = "";

    /// <summary>The newest version of each channel: <c>{"stable":{"version":"7.25.1","releasedAt":"…"}, …}</c>.</summary>
    [Column(TypeName = "jsonb")]
    public string? LatestVersions { get; set; }

    public DateTime? VersionsCheckedAt { get; set; }

    public string? VersionCheckError { get; set; }
}

/// <summary>
/// PNMTD's SSH key pairs for the <c>pnmt</c> user (§4.2): one in use, and while a rotation runs, the
/// one before it. The private key is under Data Protection.
/// </summary>
[Table("mikrotik_ssh_keys")]
public class MikrotikSshKeyEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The OpenSSH line, "ssh-ed25519 AAAA… pnmt-&lt;id&gt;@&lt;host&gt;".</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>The comment, which RouterOS shows as the key's owner: how a poll tells PNMTD's keys apart.</summary>
    public string Comment { get; set; } = "";

    public string PrivateKeyProtected { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    /// <summary>Set when a newer key replaced it; it is still offered until no router needs it.</summary>
    public DateTime? RetiredAt { get; set; }
}
