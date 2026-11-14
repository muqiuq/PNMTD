namespace PNMTD.Audit;

/// <summary>
/// The audit log's event types (docs/webui-migration-plan.md §4.3; docs/audit-redmine-zabbix-plan.md §3.9). Their category,
/// label, tone and target type are in <see cref="AuditCatalog"/>, which every type used in code must be part of.
/// </summary>
public static class AuditEvents
{
    public const string LoginSucceeded = "LoginSucceeded";
    public const string LoginFailed = "LoginFailed";
    public const string LockedOut = "LockedOut";
    public const string TotpFailed = "TotpFailed";
    public const string Logout = "Logout";
    public const string TotpEnabled = "TotpEnabled";
    public const string TotpDisabled = "TotpDisabled";
    public const string TotpReset = "TotpReset";
    public const string PasswordChanged = "PasswordChanged";
    public const string PasswordReset = "PasswordReset";
    public const string SessionsRevoked = "SessionsRevoked";
    public const string UserCreated = "UserCreated";
    public const string UserUpdated = "UserUpdated";
    public const string UserUnlocked = "UserUnlocked";
    public const string RoleChanged = "RoleChanged";
    public const string UserDeleted = "UserDeleted";

    // UDP nodes (docs/udp-iot-plan.md §4.3, §4.11): the security-relevant changes, never the key itself.
    public const string UdpKeyChanged = "UdpKeyChanged";
    public const string UdpCounterReset = "UdpCounterReset";
    public const string UdpSessionReset = "UdpSessionReset";
    public const string UdpFirmwareUploaded = "UdpFirmwareUploaded";
    public const string UdpFirmwareDeleted = "UdpFirmwareDeleted";
    public const string UdpUpdateStarted = "UdpUpdateStarted";
    public const string UdpUpdateCancelled = "UdpUpdateCancelled";

    // MikroTik routers (docs/mikrotik-plan.md §9.6): who saw a join script, and the security-relevant changes.
    public const string MikrotikJoinScriptViewed = "MikrotikJoinScriptViewed";
    public const string MikrotikHostKeyAccepted = "MikrotikHostKeyAccepted";
    public const string MikrotikSecretsChanged = "MikrotikSecretsChanged";
    public const string MikrotikManagementAccessChanged = "MikrotikManagementAccessChanged";
    public const string MikrotikDiagnosticsChanged = "MikrotikDiagnosticsChanged";
    public const string MikrotikBackupDownloaded = "MikrotikBackupDownloaded";
    public const string MikrotikBackupDeleted = "MikrotikBackupDeleted";
    public const string MikrotikUpdateStarted = "MikrotikUpdateStarted";
    public const string MikrotikUpdateCancelled = "MikrotikUpdateCancelled";
    public const string MikrotikCommandStarted = "MikrotikCommandStarted";
    public const string MikrotikCommandCancelled = "MikrotikCommandCancelled";
    public const string MikrotikCommandRedacted = "MikrotikCommandRedacted";
    public const string MikrotikSshKeyRotated = "MikrotikSshKeyRotated";
    public const string MikrotikSshKeyRetired = "MikrotikSshKeyRetired";

    // The backups' one-way encryption (docs/mikrotik-backup-plan.md §3.3, §3.5).
    public const string MikrotikBackupKeySet = "MikrotikBackupKeySet";
    public const string MikrotikBackupKeyRetired = "MikrotikBackupKeyRetired";
    public const string MikrotikBackupEncryptionChanged = "MikrotikBackupEncryptionChanged";

    // The WireGuard network (docs/mikrotik-plan.md §9.6).
    public const string WireGuardNetworkChanged = "WireGuardNetworkChanged";
    public const string WireGuardKeysRegenerated = "WireGuardKeysRegenerated";
    public const string WireGuardTunnelCreated = "WireGuardTunnelCreated";
    public const string WireGuardTunnelChanged = "WireGuardTunnelChanged";
    public const string WireGuardTunnelDeleted = "WireGuardTunnelDeleted";
    public const string WireGuardTunnelDownloaded = "WireGuardTunnelDownloaded";

    // The debug page's test sends (docs/ai-operations-plan.md §8).
    public const string DebugNotificationSent = "DebugNotificationSent";
    public const string DebugAiTested = "DebugAiTested";

    // The AI's work (docs/ai-operations-plan.md §14).
    public const string AiWorkRetried = "AiWorkRetried";
    public const string AiWorkCancelled = "AiWorkCancelled";
    public const string AiBackfillStarted = "AiBackfillStarted";
    public const string AiAnalysisStarted = "AiAnalysisStarted";

    // Servers with an agent and diagnostics (docs/windows-sensor-plan.md §9.2): Agent* for every platform since L1 of
    // docs/linux-sensor-plan.md (§3.3; they were Windows* before), Windows* for what is Windows' own (WMI).
    public const string AgentJoinTokenCreated = "AgentJoinTokenCreated";
    public const string AgentJoinTokenRevoked = "AgentJoinTokenRevoked";
    public const string AgentJoined = "AgentJoined";
    public const string AgentJoinFailed = "AgentJoinFailed";
    public const string AgentRevoked = "AgentRevoked";
    public const string AgentSettingsChanged = "AgentSettingsChanged";
    public const string WindowsSettingsChanged = "WindowsSettingsChanged";
    public const string DiagnosticsSetChanged = "DiagnosticsSetChanged";
    public const string DiagnosticsAssignmentChanged = "DiagnosticsAssignmentChanged";
    public const string AgentScriptChanged = "AgentScriptChanged";
    public const string AgentScriptRun = "AgentScriptRun";

    /// <summary>Fetch again of the agents' files (docs/linux-sensor-plan.md §4.4).</summary>
    public const string AgentDownloadsFetched = "AgentDownloadsFetched";

    // The SSH key manager (docs/linux-sensor-plan.md §6.4, §6.5): registering, editing, retiring and deleting a key, and its
    // removals from servers (asked for, and what the agent did).
    public const string SshKeyRegistered = "SshKeyRegistered";
    public const string SshKeyChanged = "SshKeyChanged";
    public const string SshKeyRetired = "SshKeyRetired";
    public const string SshKeyDeleted = "SshKeyDeleted";
    public const string SshKeyRemoved = "SshKeyRemoved";

    // Uploaded files (docs/locations-knowledge-plan.md §5.5, §8.7): an Admin's delete, at once.
    public const string FileDeleted = "FileDeleted";

    // Locations (docs/locations-knowledge-plan.md §8.7): what goes for good.
    public const string LocationDeleted = "LocationDeleted";
    public const string LocationFieldDeleted = "LocationFieldDeleted";

    // The knowledge base (docs/locations-knowledge-plan.md §8.7): a page deleted, its sensitive mark, a redaction (never the
    // text), a tag deleted.
    public const string KbPageDeleted = "KbPageDeleted";
    public const string KbPageSensitivityChanged = "KbPageSensitivityChanged";
    public const string KbRevisionRedacted = "KbRevisionRedacted";
    public const string KbTagDeleted = "KbTagDeleted";

    // Hosts (docs/audit-redmine-zabbix-plan.md Appendix A).
    public const string HostCreated = "HostCreated";
    public const string HostUpdated = "HostUpdated";
    public const string HostDeleted = "HostDeleted";
}

/// <summary>What an entry is about: its category on the page and in the filter (docs/audit-redmine-zabbix-plan.md §3.2).</summary>
public enum AuditCategory
{
    Auth, Users, Hosts, Sensors, Notifications, Locations, Knowledge, Files, Udp, Mikrotik, WireGuard, Agents, SshKeys,
    Diagnostics, Dns, Mail, Ai, Decisions, System, Redmine, Zabbix,
}

/// <summary>The colour of an event's tag.</summary>
public enum AuditTone { Neutral, Success, Warn, Danger }

/// <summary>The kinds of object an entry can be about (<c>TargetType</c>).</summary>
public static class AuditTargets
{
    public const string User = "User";
    public const string Group = "Group";
    public const string Host = "Host";
    public const string Sensor = "Sensor";
    public const string Location = "Location";
    public const string LocationField = "LocationField";
    public const string KbPage = "KbPage";
    public const string KbTag = "KbTag";
    public const string File = "File";
    public const string NotificationRule = "NotificationRule";
    public const string DnsZone = "DnsZone";
    public const string MailInputRule = "MailInputRule";
    public const string KeyValue = "KeyValue";
    public const string Decision = "Decision";
    public const string Tunnel = "Tunnel";
    public const string SshKey = "SshKey";
    public const string Firmware = "Firmware";
    public const string OtaJob = "OtaJob";
    public const string JoinToken = "JoinToken";
    public const string Script = "Script";
    public const string ScriptRun = "ScriptRun";
    public const string RunAs = "RunAs";
    public const string DiagnosticsSet = "DiagnosticsSet";
    public const string DiagnosticsAssignment = "DiagnosticsAssignment";
    public const string MikrotikJob = "MikrotikJob";
    public const string MikrotikCommand = "MikrotikCommand";
    public const string UdpCommand = "UdpCommand";
    public const string AiWork = "AiWork";
    public const string RedmineTicket = "RedmineTicket";
    public const string ZabbixHost = "ZabbixHost";
}

/// <summary>An event type of the catalog: its category, a label for people, the tone of its tag, what it is about.</summary>
public sealed record AuditEventInfo(string Type, AuditCategory Category, string Label, AuditTone Tone, string? TargetType);

/// <summary>
/// Every event type with its category, label, tone and target type (docs/audit-redmine-zabbix-plan.md §3.9), served by
/// <c>GET /api/v1/audit-log/types</c> so that the page's filter can't fall behind the server's events again.
/// </summary>
public static class AuditCatalog
{
    private static readonly List<AuditEventInfo> Entries = [];
    private static readonly Dictionary<string, AuditEventInfo> ByType = new(StringComparer.Ordinal);

    /// <summary>The types that always describe a failure (the log's rows from before 1.4 have no outcome of their own).</summary>
    public static readonly IReadOnlySet<string> Failures = new HashSet<string>(StringComparer.Ordinal)
    {
        AuditEvents.LoginFailed, AuditEvents.TotpFailed, AuditEvents.LockedOut, AuditEvents.AgentJoinFailed,
    };

    static AuditCatalog()
    {
        void Add(AuditCategory category, string? target, params string[] types)
        {
            foreach (var type in types)
            {
                var info = new AuditEventInfo(type, category, Label(type), Tone(type), target);
                Entries.Add(info);
                ByType.Add(type, info);
            }
        }

        Add(AuditCategory.Auth, null, AuditEvents.LoginSucceeded, AuditEvents.LoginFailed, AuditEvents.LockedOut, AuditEvents.TotpFailed,
            AuditEvents.Logout);
        Add(AuditCategory.Auth, AuditTargets.User, AuditEvents.TotpEnabled, AuditEvents.TotpDisabled, AuditEvents.PasswordChanged,
            AuditEvents.SessionsRevoked);
        Add(AuditCategory.Users, AuditTargets.User, AuditEvents.TotpReset, AuditEvents.PasswordReset, AuditEvents.UserCreated,
            AuditEvents.UserUpdated, AuditEvents.UserUnlocked, AuditEvents.RoleChanged, AuditEvents.UserDeleted);
        Add(AuditCategory.Udp, AuditTargets.Sensor, AuditEvents.UdpKeyChanged, AuditEvents.UdpCounterReset, AuditEvents.UdpSessionReset);
        Add(AuditCategory.Udp, AuditTargets.Firmware, AuditEvents.UdpFirmwareUploaded, AuditEvents.UdpFirmwareDeleted);
        Add(AuditCategory.Udp, AuditTargets.OtaJob, AuditEvents.UdpUpdateStarted, AuditEvents.UdpUpdateCancelled);
        Add(AuditCategory.Mikrotik, AuditTargets.Sensor, AuditEvents.MikrotikJoinScriptViewed, AuditEvents.MikrotikHostKeyAccepted,
            AuditEvents.MikrotikSecretsChanged, AuditEvents.MikrotikManagementAccessChanged, AuditEvents.MikrotikDiagnosticsChanged,
            AuditEvents.MikrotikBackupDownloaded, AuditEvents.MikrotikBackupDeleted, AuditEvents.MikrotikBackupEncryptionChanged);
        Add(AuditCategory.Mikrotik, AuditTargets.MikrotikJob, AuditEvents.MikrotikUpdateStarted, AuditEvents.MikrotikUpdateCancelled);
        Add(AuditCategory.Mikrotik, AuditTargets.MikrotikCommand, AuditEvents.MikrotikCommandStarted, AuditEvents.MikrotikCommandCancelled,
            AuditEvents.MikrotikCommandRedacted);
        Add(AuditCategory.Mikrotik, null, AuditEvents.MikrotikSshKeyRotated, AuditEvents.MikrotikSshKeyRetired, AuditEvents.MikrotikBackupKeySet,
            AuditEvents.MikrotikBackupKeyRetired);
        Add(AuditCategory.WireGuard, null, AuditEvents.WireGuardNetworkChanged, AuditEvents.WireGuardKeysRegenerated);
        Add(AuditCategory.WireGuard, AuditTargets.Tunnel, AuditEvents.WireGuardTunnelCreated, AuditEvents.WireGuardTunnelChanged,
            AuditEvents.WireGuardTunnelDeleted, AuditEvents.WireGuardTunnelDownloaded);
        Add(AuditCategory.System, null, AuditEvents.DebugNotificationSent);
        Add(AuditCategory.Ai, null, AuditEvents.DebugAiTested, AuditEvents.AiBackfillStarted, AuditEvents.AiAnalysisStarted);
        Add(AuditCategory.Ai, AuditTargets.AiWork, AuditEvents.AiWorkRetried, AuditEvents.AiWorkCancelled);
        Add(AuditCategory.Agents, AuditTargets.JoinToken, AuditEvents.AgentJoinTokenCreated, AuditEvents.AgentJoinTokenRevoked);
        Add(AuditCategory.Agents, AuditTargets.Sensor, AuditEvents.AgentJoined, AuditEvents.AgentJoinFailed, AuditEvents.AgentRevoked,
            AuditEvents.AgentSettingsChanged, AuditEvents.WindowsSettingsChanged);
        Add(AuditCategory.Diagnostics, AuditTargets.DiagnosticsSet, AuditEvents.DiagnosticsSetChanged);
        Add(AuditCategory.Diagnostics, AuditTargets.DiagnosticsAssignment, AuditEvents.DiagnosticsAssignmentChanged);
        Add(AuditCategory.Agents, AuditTargets.Script, AuditEvents.AgentScriptChanged, AuditEvents.AgentScriptRun);
        Add(AuditCategory.Agents, null, AuditEvents.AgentDownloadsFetched);
        Add(AuditCategory.SshKeys, AuditTargets.SshKey, AuditEvents.SshKeyRegistered, AuditEvents.SshKeyChanged, AuditEvents.SshKeyRetired,
            AuditEvents.SshKeyDeleted, AuditEvents.SshKeyRemoved);
        Add(AuditCategory.Files, AuditTargets.File, AuditEvents.FileDeleted);
        Add(AuditCategory.Locations, AuditTargets.Location, AuditEvents.LocationDeleted);
        Add(AuditCategory.Locations, AuditTargets.LocationField, AuditEvents.LocationFieldDeleted);
        Add(AuditCategory.Knowledge, AuditTargets.KbPage, AuditEvents.KbPageDeleted, AuditEvents.KbPageSensitivityChanged,
            AuditEvents.KbRevisionRedacted);
        Add(AuditCategory.Knowledge, AuditTargets.KbTag, AuditEvents.KbTagDeleted);
        Add(AuditCategory.Hosts, AuditTargets.Host, AuditEvents.HostCreated, AuditEvents.HostUpdated, AuditEvents.HostDeleted);
    }

    public static IReadOnlyList<AuditEventInfo> All => Entries;

    public static bool Contains(string type) => ByType.ContainsKey(type);

    /// <summary>The catalog's entry; for a type it doesn't know (an old row), one made up from the name.</summary>
    public static AuditEventInfo Get(string type) =>
        ByType.TryGetValue(type, out var info) ? info : new AuditEventInfo(type, AuditCategory.System, Label(type), Tone(type), null);

    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["Totp"] = "2FA", ["Udp"] = "UDP", ["Kb"] = "KB", ["Ai"] = "AI", ["Ssh"] = "SSH", ["Dns"] = "DNS", ["Wmi"] = "WMI",
        ["Ota"] = "OTA", ["Mikrotik"] = "MikroTik", ["Wireguard"] = "WireGuard", ["Csv"] = "CSV", ["Redmine"] = "Redmine",
        ["Zabbix"] = "Zabbix", ["Windows"] = "Windows",
    };

    /// <summary>"MikrotikJoinScriptViewed" → "MikroTik join script viewed".</summary>
    public static string Label(string type)
    {
        var words = System.Text.RegularExpressions.Regex.Split(type.Replace("WireGuard", "Wireguard"), "(?<=[a-z0-9])(?=[A-Z])");
        return string.Join(' ', words.Select((w, i) => Words.TryGetValue(w, out var special) ? special : i == 0 ? w : w.ToLowerInvariant()));
    }

    private static AuditTone Tone(string type) =>
        System.Text.RegularExpressions.Regex.IsMatch(type, "Failed|LockedOut") ? AuditTone.Danger
        : System.Text.RegularExpressions.Regex.IsMatch(type, "Deleted|Reset|Revoked|Disabled|Cancelled|Retired|Removed|Redacted|Detached") ? AuditTone.Warn
        : System.Text.RegularExpressions.Regex.IsMatch(type, "Succeeded|Enabled|Created|Joined|Registered|Attached|Uploaded") ? AuditTone.Success
        : AuditTone.Neutral;
}
