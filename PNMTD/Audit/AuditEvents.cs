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

    /// <summary>The day's 2FA code (docs/mcp-plan.md §3.3).</summary>
    public const string TotpDailyConfirmed = "TotpDailyConfirmed";
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

    // Groups and rights (docs/audit-redmine-zabbix-plan.md §5.6). RoleChanged stays for the entries from before 1.4.
    public const string UserAdminChanged = "UserAdminChanged";
    public const string UserGroupsChanged = "UserGroupsChanged";
    public const string GroupCreated = "GroupCreated";
    public const string GroupUpdated = "GroupUpdated";
    public const string GroupRightsChanged = "GroupRightsChanged";
    public const string GroupMembersChanged = "GroupMembersChanged";
    public const string GroupDeleted = "GroupDeleted";

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
    // A difference between a router and its join script acknowledged as intended, and the acknowledgement removed (docs/mikrotik-join-options-plan.md §3).
    public const string MikrotikDifferenceAcknowledged = "MikrotikDifferenceAcknowledged";
    public const string MikrotikDifferenceAcknowledgementEnded = "MikrotikDifferenceAcknowledgementEnded";

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

    // OS updates on Linux servers (docs/linux-os-updates-plan.md §6.5, §8.2): a run asked for, a failure marked handled, the server's
    // switches, high-risk findings acknowledged (and how each acknowledgement ended), and the advisory sources.
    public const string OsUpdateRequested = "OsUpdateRequested";
    public const string OsUpdateFailureDismissed = "OsUpdateFailureDismissed";
    public const string OsUpdateSettingsChanged = "OsUpdateSettingsChanged";
    public const string OsUpdateFindingAcknowledged = "OsUpdateFindingAcknowledged";
    public const string OsUpdateFindingAcknowledgementEnded = "OsUpdateFindingAcknowledgementEnded";
    public const string OsAdvisorySourceCreated = "OsAdvisorySourceCreated";
    public const string OsAdvisorySourceChanged = "OsAdvisorySourceChanged";
    public const string OsAdvisorySourceDeleted = "OsAdvisorySourceDeleted";

    /// <summary>Fetch again of the agents' files (docs/linux-sensor-plan.md §4.4).</summary>
    public const string AgentDownloadsFetched = "AgentDownloadsFetched";

    // The agents' versions (docs/agent-update-plan.md §9).
    public const string AgentVersionChosen = "AgentVersionChosen";
    public const string AgentVersionFetched = "AgentVersionFetched";
    public const string AgentVersionUploaded = "AgentVersionUploaded";
    public const string AgentVersionDeleted = "AgentVersionDeleted";

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

    // Redmine (docs/audit-redmine-zabbix-plan.md §6.5): links, and the connection's test.
    public const string RedmineLinkAdded = "RedmineLinkAdded";
    public const string RedmineLinkChanged = "RedmineLinkChanged";
    public const string RedmineLinkRemoved = "RedmineLinkRemoved";
    public const string RedmineTested = "RedmineTested";

    /// <summary>A PNMT user linked to a Redmine user, or unlinked (docs/cookie-redmine-tab-plan.md §4.1).</summary>
    public const string RedmineUserLinkChanged = "RedmineUserLinkChanged";

    // Cookie (docs/cookie-plan.md §9.3, §19): its configuration, start and stop, and what it sends.
    public const string CookieSettingsChanged = "CookieSettingsChanged";
    public const string CookieSettingsRolledBack = "CookieSettingsRolledBack";
    public const string CookieStarted = "CookieStarted";
    public const string CookieStopped = "CookieStopped";
    public const string CookieRunRequested = "CookieRunRequested";
    public const string CookieTicketCreated = "CookieTicketCreated";
    public const string CookieNoteAdded = "CookieNoteAdded";

    /// <summary>A person removed one of Cookie's notes on a host or sensor (docs/cookie-object-notes-plan.md §3.6).</summary>
    public const string CookieNoteRemoved = "CookieNoteRemoved";
    public const string CookieMessageSent = "CookieMessageSent";
    public const string CookieHeldBackSent = "CookieHeldBackSent";
    public const string CookieCaseCommented = "CookieCaseCommented";
    public const string CookieCaseStopped = "CookieCaseStopped";
    public const string CookieCaseGoOn = "CookieCaseGoOn";
    public const string CookieInstructionCreated = "CookieInstructionCreated";
    public const string CookieInstructionConfirmed = "CookieInstructionConfirmed";
    public const string CookieInstructionEnded = "CookieInstructionEnded";
    public const string CookieDecisionRated = "CookieDecisionRated";
    public const string CookieBigBrotherAsked = "CookieBigBrotherAsked";
    public const string CookiePowersUsed = "CookiePowersUsed";

    // The lounge (docs/lounge-plan.md §13): its Start and Stop, Talk now and Put to sleep, moderation, and one's own switches.
    public const string LoungeStarted = "LoungeStarted";
    public const string LoungeStopped = "LoungeStopped";
    public const string LoungeTalkNow = "LoungeTalkNow";
    public const string LoungeSlept = "LoungeSlept";
    public const string LoungeMessageDeleted = "LoungeMessageDeleted";
    public const string LoungeMemoryDeleted = "LoungeMemoryDeleted";
    public const string LoungeForgotten = "LoungeForgotten";
    public const string LoungePositionsReset = "LoungePositionsReset";
    public const string LoungeSwitchesChanged = "LoungeSwitchesChanged";

    // One's own notification choices (docs/knowledge-base-extensions-plan.md §10.2).
    public const string NotificationPreferencesChanged = "NotificationPreferencesChanged";

    // The access watch (docs/cookie-access-plan.md §10.1).
    public const string AccessAnswered = "AccessAnswered";
    public const string AccessPersonAnswered = "AccessPersonAnswered";
    public const string AccessTicketAsked = "AccessTicketAsked";
    public const string AccessKnownAdded = "AccessKnownAdded";
    public const string AccessKnownEnded = "AccessKnownEnded";
    public const string AccessWatchChanged = "AccessWatchChanged";

    // The person and the availability plan (docs/cookie-plan.md §8.1, §8.2).
    public const string ContactChanged = "ContactChanged";
    public const string ContactTested = "ContactTested";
    public const string AvailabilityChanged = "AvailabilityChanged";
    public const string AvailabilityFilled = "AvailabilityFilled";
    public const string AvailabilityPeopleChanged = "AvailabilityPeopleChanged";
    public const string AvailabilitySettingsChanged = "AvailabilitySettingsChanged";

    // Zabbix (docs/audit-redmine-zabbix-plan.md §7.8): the connection's test, and attaching hosts in bulk.
    public const string ZabbixTested = "ZabbixTested";
    public const string ZabbixHostsAttached = "ZabbixHostsAttached";

    // SSL certificates (docs/ssl-certificate-plan.md §6.3): accepting a changed certificate, the private CAs, Check now.
    public const string CertificateAccepted = "CertificateAccepted";
    public const string CertificateAuthorityAdded = "CertificateAuthorityAdded";
    public const string CertificateAuthorityRemoved = "CertificateAuthorityRemoved";
    public const string CertificateCheckRequested = "CertificateCheckRequested";

    // Advanced sensors (docs/advanced-sensor-plan.md §8.3): their rules and fields (each change on the sensor's History), and
    // deleting reports that carried what they shouldn't.
    public const string AdvancedRulesChanged = "AdvancedRulesChanged";
    public const string AdvancedFieldsChanged = "AdvancedFieldsChanged";
    public const string AdvancedReportDeleted = "AdvancedReportDeleted";
    public const string AdvancedReportsCleared = "AdvancedReportsCleared";

    // Every other operation (docs/audit-redmine-zabbix-plan.md §3.9, Appendix A).
    public const string HostsImportanceChanged = "HostsImportanceChanged";
    public const string HostsLocationChanged = "HostsLocationChanged";
    public const string SensorCreated = "SensorCreated";
    public const string SensorUpdated = "SensorUpdated";
    public const string SensorDeleted = "SensorDeleted";
    public const string SensorTokensRotated = "SensorTokensRotated";
    public const string SensorsImportanceChanged = "SensorsImportanceChanged";
    public const string NotificationRuleCreated = "NotificationRuleCreated";
    public const string NotificationRuleUpdated = "NotificationRuleUpdated";
    public const string NotificationRuleDeleted = "NotificationRuleDeleted";
    public const string NotificationRuleSensorLinked = "NotificationRuleSensorLinked";
    public const string NotificationRuleSensorUnlinked = "NotificationRuleSensorUnlinked";
    public const string NotificationRuleScopesChanged = "NotificationRuleScopesChanged";
    public const string NotificationRuleExclusionAdded = "NotificationRuleExclusionAdded";
    public const string NotificationRuleExclusionRemoved = "NotificationRuleExclusionRemoved";
    public const string NotificationsAssigned = "NotificationsAssigned";

    /// <summary>The Pushover priorities (docs/cookie-object-notes-plan.md §5): the detail names each value that changed.</summary>
    public const string NotificationSettingsChanged = "NotificationSettingsChanged";
    public const string DnsZoneCreated = "DnsZoneCreated";
    public const string DnsZoneUpdated = "DnsZoneUpdated";
    public const string DnsZoneDeleted = "DnsZoneDeleted";
    public const string DnsZoneUpdateForced = "DnsZoneUpdateForced";
    public const string DnsRecordUpdated = "DnsRecordUpdated";
    public const string MailInputRuleCreated = "MailInputRuleCreated";
    public const string MailInputRuleUpdated = "MailInputRuleUpdated";
    public const string MailInputRuleDeleted = "MailInputRuleDeleted";
    public const string TotpSetupStarted = "TotpSetupStarted";
    public const string KeyValueCreated = "KeyValueCreated";
    public const string KeyValueUpdated = "KeyValueUpdated";
    public const string KeyValueDeleted = "KeyValueDeleted";
    public const string UplinkCheckRun = "UplinkCheckRun";
    public const string AuditLogExported = "AuditLogExported";
    public const string UdpCommandQueued = "UdpCommandQueued";
    public const string UdpCommandCancelled = "UdpCommandCancelled";
    public const string UdpFirmwareChanged = "UdpFirmwareChanged";
    public const string MikrotikPollRequested = "MikrotikPollRequested";
    public const string MikrotikBackupRequested = "MikrotikBackupRequested";
    public const string MikrotikBackupSettingsChanged = "MikrotikBackupSettingsChanged";
    public const string AgentCollectRequested = "AgentCollectRequested";
    public const string AgentLogRequested = "AgentLogRequested";
    public const string AgentScriptRunRedacted = "AgentScriptRunRedacted";
    public const string WindowsRunAsChanged = "WindowsRunAsChanged";
    public const string DiagnosticsRun = "DiagnosticsRun";
    public const string DecisionDecided = "DecisionDecided";
    public const string LocationCreated = "LocationCreated";
    public const string LocationUpdated = "LocationUpdated";
    public const string LocationMoved = "LocationMoved";
    public const string LocationValueChanged = "LocationValueChanged";
    public const string LocationsCreatedFromText = "LocationsCreatedFromText";
    public const string LocationFieldCreated = "LocationFieldCreated";
    public const string LocationFieldChanged = "LocationFieldChanged";
    public const string LocationFieldArchived = "LocationFieldArchived";
    public const string LocationFieldRestored = "LocationFieldRestored";
    public const string LocationFieldsReordered = "LocationFieldsReordered";
    public const string KbPageCreated = "KbPageCreated";
    public const string KbPageSaved = "KbPageSaved";
    public const string KbPageArchived = "KbPageArchived";
    public const string KbPageRestored = "KbPageRestored";
    public const string KbRevisionRestored = "KbRevisionRestored";
    public const string KbAiTagsRequested = "KbAiTagsRequested";
    public const string KbTagRenamed = "KbTagRenamed";
    public const string FileUploaded = "FileUploaded";

    // The MCP server (docs/mcp-plan.md §9.4): the tokens and the window for deleting. A tool's own writes keep their events.
    public const string McpTokenOn = "McpTokenOn";
    public const string McpTokenOff = "McpTokenOff";
    public const string McpTokenRegenerated = "McpTokenRegenerated";
    public const string McpTokenExpired = "McpTokenExpired";
    public const string McpTokenBlocked = "McpTokenBlocked";
    public const string McpTokenTurnedOffByAdmin = "McpTokenTurnedOffByAdmin";
    public const string McpTokensRevokedByAdmin = "McpTokensRevokedByAdmin";
    public const string McpDeleteAllowed = "McpDeleteAllowed";
    public const string McpDeleteEnded = "McpDeleteEnded";

    // 3rd Party Connectors (docs/connectors-plan.md §12.2). A call whose run starts is AgentScriptRun, with the connector as actor.
    public const string ConnectorChanged = "ConnectorChanged";
    public const string ConnectorCallerChanged = "ConnectorCallerChanged";
    public const string ConnectorCallerKeyViewed = "ConnectorCallerKeyViewed";
    public const string ConnectorCallerKeyRotated = "ConnectorCallerKeyRotated";

    /// <summary>A valid key from outside its caller's networks, a refusal by the review, an address blocked for failed keys.</summary>
    public const string ConnectorRefused = "ConnectorRefused";
    public const string ConnectorCallRedacted = "ConnectorCallRedacted";
    public const string ConnectorCallCancelled = "ConnectorCallCancelled";

    // The network map (docs/network-map-plan.md §10.1). Scheduled scans are not audited.
    public const string NetworkConnectionCreated = "NetworkConnectionCreated";
    public const string NetworkConnectionUpdated = "NetworkConnectionUpdated";
    public const string NetworkConnectionDeleted = "NetworkConnectionDeleted";
    public const string NetworkSuggestionAccepted = "NetworkSuggestionAccepted";
    public const string NetworkSuggestionDismissed = "NetworkSuggestionDismissed";
    public const string NetworkSuggestionRestored = "NetworkSuggestionRestored";
    public const string NetworkInterfaceAssigned = "NetworkInterfaceAssigned";
    public const string NetworkInterfaceCleared = "NetworkInterfaceCleared";
    public const string NetworkInterfaceIgnored = "NetworkInterfaceIgnored";
    public const string NetworkInterfaceUnignored = "NetworkInterfaceUnignored";
    public const string NetworkScanStarted = "NetworkScanStarted";
    public const string NetworkScanCancelled = "NetworkScanCancelled";
    public const string NetworkMapSettingsChanged = "NetworkMapSettingsChanged";
    public const string NetworkPositionsSaved = "NetworkPositionsSaved";
    public const string NetworkPositionCleared = "NetworkPositionCleared";
    public const string NetworkPositionsReset = "NetworkPositionsReset";
    public const string NetworkSkipRuleCreated = "NetworkSkipRuleCreated";
    public const string NetworkSkipRuleUpdated = "NetworkSkipRuleUpdated";
    public const string NetworkSkipRuleDeleted = "NetworkSkipRuleDeleted";
    // Responsibilities (docs/responsibilities-plan.md §12.2).
    public const string ResponsibilityCreated = "ResponsibilityCreated";
    public const string ResponsibilityChanged = "ResponsibilityChanged";
    public const string ResponsibilityDeleted = "ResponsibilityDeleted";
    public const string ResponsibilityLinked = "ResponsibilityLinked";
    public const string ResponsibilityUnlinked = "ResponsibilityUnlinked";
    public const string GlobalResponsibleChanged = "GlobalResponsibleChanged";
    public const string ResponsibilityRedmineSaved = "ResponsibilityRedmineSaved";
    public const string ResponsibilityRedmineDeleted = "ResponsibilityRedmineDeleted";
    // The overview (docs/responsibilities-extensions-plan.md §8.2): saved places, and back to automatic.
    public const string ResponsibilityMapSaved = "ResponsibilityMapSaved";
    public const string ResponsibilityMapReset = "ResponsibilityMapReset";
    public const string ResponsibilityNoteCreated = "ResponsibilityNoteCreated";
    public const string ResponsibilityNoteChanged = "ResponsibilityNoteChanged";
    public const string ResponsibilityNoteDeleted = "ResponsibilityNoteDeleted";
    // Boards (docs/boards-plan.md §13.2). Views aren't audited; a refusal by network is, at most once per board and address an hour.
    public const string BoardCreated = "BoardCreated";
    public const string BoardChanged = "BoardChanged";
    public const string BoardDeleted = "BoardDeleted";
    public const string BoardUrlShown = "BoardUrlShown";
    public const string BoardUrlRenewed = "BoardUrlRenewed";
    public const string BoardRefused = "BoardRefused";
    // Board messages (docs/board-messages-plan.md §12.2): by a person or by Cookie.
    public const string BoardMessageSent = "BoardMessageSent";
    public const string BoardMessageWithdrawn = "BoardMessageWithdrawn";

    // Licences & services (docs/licences-plan.md §10.2): the items, their hosts, links, renewals, charges and documents; the templates
    // and categories; PNMT's automatic renewals; what turning the feature off and on did to the item sensors.
    public const string LicenseCreated = "LicenseCreated";
    public const string LicenseChanged = "LicenseChanged";
    public const string LicenseDeleted = "LicenseDeleted";
    public const string LicenseRenewed = "LicenseRenewed";
    public const string LicenseRenewedAutomatically = "LicenseRenewedAutomatically";
    public const string LicenseReviewed = "LicenseReviewed";
    public const string LicenseCancelled = "LicenseCancelled";
    public const string LicenseReactivated = "LicenseReactivated";
    public const string LicenseRetired = "LicenseRetired";
    public const string LicenseKeysShown = "LicenseKeysShown";
    public const string LicenseAssigned = "LicenseAssigned";
    public const string LicenseAssignmentChanged = "LicenseAssignmentChanged";
    public const string LicenseUnassigned = "LicenseUnassigned";
    public const string LicenseSensorLinked = "LicenseSensorLinked";
    public const string LicenseSensorUnlinked = "LicenseSensorUnlinked";
    public const string LicenseRenewalChanged = "LicenseRenewalChanged";
    public const string LicenseRenewalDeleted = "LicenseRenewalDeleted";
    public const string LicenseChargeRecorded = "LicenseChargeRecorded";
    public const string LicenseChargeChanged = "LicenseChargeChanged";
    public const string LicenseChargeDeleted = "LicenseChargeDeleted";
    public const string LicenseDocumentAdded = "LicenseDocumentAdded";
    public const string LicenseDocumentChanged = "LicenseDocumentChanged";
    public const string LicenseDocumentDownloaded = "LicenseDocumentDownloaded";
    public const string LicenseDocumentRemoved = "LicenseDocumentRemoved";
    public const string LicenseNamesRenamed = "LicenseNamesRenamed";
    public const string LicenseTemplateCreated = "LicenseTemplateCreated";
    public const string LicenseTemplateChanged = "LicenseTemplateChanged";
    public const string LicenseTemplateDeleted = "LicenseTemplateDeleted";
    public const string LicenseCategoryCreated = "LicenseCategoryCreated";
    public const string LicenseCategoryChanged = "LicenseCategoryChanged";
    public const string LicenseCategoryDeleted = "LicenseCategoryDeleted";
    public const string LicenseSensorsRemoved = "LicenseSensorsRemoved";
    public const string LicenseSensorsMade = "LicenseSensorsMade";
}

/// <summary>What an entry is about: its category on the page and in the filter (docs/audit-redmine-zabbix-plan.md §3.2).</summary>
public enum AuditCategory
{
    Auth, Users, Hosts, Sensors, Notifications, Locations, Knowledge, Files, Udp, Mikrotik, WireGuard, Agents, SshKeys,
    Diagnostics, Dns, Mail, Ai, Decisions, System, Redmine, Zabbix, Cookie, Availability, Mcp, Certificates, Connectors, Network,
    Responsibilities, Boards, Licenses,
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
    public const string CookieCase = "CookieCase";
    public const string McpToken = "McpToken";
    public const string CertificateAuthority = "CertificateAuthority";
    public const string Connector = "Connector";
    public const string ConnectorCaller = "ConnectorCaller";
    public const string ConnectorCall = "ConnectorCall";
    public const string NetworkConnection = "NetworkConnection";
    public const string NetworkSuggestion = "NetworkSuggestion";
    public const string NetworkInterface = "NetworkInterface";
    public const string NetworkScan = "NetworkScan";
    public const string NetworkRouterPosition = "NetworkRouterPosition";
    public const string NetworkSkipRule = "NetworkSkipRule";
    public const string OsAdvisorySource = "OsAdvisorySource";
    public const string Responsibility = "Responsibility";
    public const string ResponsibilityNote = "ResponsibilityNote";
    public const string Board = "Board";
    public const string BoardMessage = "BoardMessage";
    public const string License = "License";
    public const string LicenseTemplate = "LicenseTemplate";
    public const string LicenseCategory = "LicenseCategory";
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
            AuditEvents.UserUpdated, AuditEvents.UserUnlocked, AuditEvents.RoleChanged, AuditEvents.UserDeleted, AuditEvents.UserAdminChanged,
            AuditEvents.UserGroupsChanged);
        Add(AuditCategory.Users, AuditTargets.Group, AuditEvents.GroupCreated, AuditEvents.GroupUpdated, AuditEvents.GroupRightsChanged,
            AuditEvents.GroupMembersChanged, AuditEvents.GroupDeleted);
        Add(AuditCategory.Udp, AuditTargets.Sensor, AuditEvents.UdpKeyChanged, AuditEvents.UdpCounterReset, AuditEvents.UdpSessionReset);
        Add(AuditCategory.Udp, AuditTargets.Firmware, AuditEvents.UdpFirmwareUploaded, AuditEvents.UdpFirmwareDeleted);
        Add(AuditCategory.Udp, AuditTargets.OtaJob, AuditEvents.UdpUpdateStarted, AuditEvents.UdpUpdateCancelled);
        Add(AuditCategory.Mikrotik, AuditTargets.Sensor, AuditEvents.MikrotikJoinScriptViewed, AuditEvents.MikrotikHostKeyAccepted,
            AuditEvents.MikrotikSecretsChanged, AuditEvents.MikrotikManagementAccessChanged, AuditEvents.MikrotikDiagnosticsChanged,
            AuditEvents.MikrotikBackupDownloaded, AuditEvents.MikrotikBackupDeleted, AuditEvents.MikrotikBackupEncryptionChanged,
            AuditEvents.MikrotikDifferenceAcknowledged, AuditEvents.MikrotikDifferenceAcknowledgementEnded);
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
        Add(AuditCategory.Agents, AuditTargets.Sensor, AuditEvents.OsUpdateRequested, AuditEvents.OsUpdateFailureDismissed, AuditEvents.OsUpdateSettingsChanged,
            AuditEvents.OsUpdateFindingAcknowledged, AuditEvents.OsUpdateFindingAcknowledgementEnded);
        Add(AuditCategory.Agents, AuditTargets.OsAdvisorySource, AuditEvents.OsAdvisorySourceCreated, AuditEvents.OsAdvisorySourceChanged,
            AuditEvents.OsAdvisorySourceDeleted);
        Add(AuditCategory.Agents, null, AuditEvents.AgentDownloadsFetched, AuditEvents.AgentVersionChosen, AuditEvents.AgentVersionFetched,
            AuditEvents.AgentVersionUploaded, AuditEvents.AgentVersionDeleted);
        Add(AuditCategory.SshKeys, AuditTargets.SshKey, AuditEvents.SshKeyRegistered, AuditEvents.SshKeyChanged, AuditEvents.SshKeyRetired,
            AuditEvents.SshKeyDeleted, AuditEvents.SshKeyRemoved);
        Add(AuditCategory.Files, AuditTargets.File, AuditEvents.FileDeleted);
        Add(AuditCategory.Locations, AuditTargets.Location, AuditEvents.LocationDeleted);
        Add(AuditCategory.Locations, AuditTargets.LocationField, AuditEvents.LocationFieldDeleted);
        Add(AuditCategory.Knowledge, AuditTargets.KbPage, AuditEvents.KbPageDeleted, AuditEvents.KbPageSensitivityChanged,
            AuditEvents.KbRevisionRedacted);
        Add(AuditCategory.Knowledge, AuditTargets.KbTag, AuditEvents.KbTagDeleted);
        Add(AuditCategory.Hosts, AuditTargets.Host, AuditEvents.HostCreated, AuditEvents.HostUpdated, AuditEvents.HostDeleted);
        Add(AuditCategory.Redmine, AuditTargets.RedmineTicket, AuditEvents.RedmineLinkAdded, AuditEvents.RedmineLinkChanged, AuditEvents.RedmineLinkRemoved);
        Add(AuditCategory.Redmine, AuditTargets.User, AuditEvents.RedmineUserLinkChanged);
        Add(AuditCategory.Redmine, null, AuditEvents.RedmineTested);
        Add(AuditCategory.Zabbix, AuditTargets.ZabbixHost, AuditEvents.ZabbixHostsAttached);
        Add(AuditCategory.Certificates, AuditTargets.Sensor, AuditEvents.CertificateAccepted);
        Add(AuditCategory.Certificates, AuditTargets.CertificateAuthority, AuditEvents.CertificateAuthorityAdded, AuditEvents.CertificateAuthorityRemoved);
        Add(AuditCategory.Zabbix, null, AuditEvents.ZabbixTested);
        Add(AuditCategory.Cookie, null, AuditEvents.CookieSettingsChanged, AuditEvents.CookieSettingsRolledBack, AuditEvents.CookieStarted,
            AuditEvents.CookieStopped, AuditEvents.CookieRunRequested, AuditEvents.CookiePowersUsed);
        Add(AuditCategory.Cookie, AuditTargets.CookieCase, AuditEvents.CookieTicketCreated, AuditEvents.CookieNoteAdded, AuditEvents.CookieNoteRemoved, AuditEvents.CookieMessageSent,
            AuditEvents.CookieHeldBackSent, AuditEvents.CookieCaseCommented, AuditEvents.CookieCaseStopped, AuditEvents.CookieCaseGoOn, AuditEvents.CookieDecisionRated,
            AuditEvents.CookieBigBrotherAsked, AuditEvents.AccessAnswered, AuditEvents.AccessPersonAnswered, AuditEvents.AccessTicketAsked);
        Add(AuditCategory.Cookie, null, AuditEvents.AccessKnownAdded, AuditEvents.AccessKnownEnded);
        Add(AuditCategory.Cookie, AuditTargets.Sensor, AuditEvents.AccessWatchChanged);
        Add(AuditCategory.Cookie, null, AuditEvents.CookieInstructionCreated, AuditEvents.CookieInstructionConfirmed, AuditEvents.CookieInstructionEnded);
        Add(AuditCategory.Cookie, null, AuditEvents.LoungeStarted, AuditEvents.LoungeStopped, AuditEvents.LoungeTalkNow, AuditEvents.LoungeSlept,
            AuditEvents.LoungeMessageDeleted, AuditEvents.LoungeMemoryDeleted, AuditEvents.LoungeForgotten, AuditEvents.LoungePositionsReset);
        Add(AuditCategory.Users, AuditTargets.User, AuditEvents.LoungeSwitchesChanged);
        Add(AuditCategory.Users, AuditTargets.User, AuditEvents.NotificationPreferencesChanged);
        Add(AuditCategory.Users, AuditTargets.User, AuditEvents.ContactChanged, AuditEvents.ContactTested);
        Add(AuditCategory.Availability, null, AuditEvents.AvailabilityChanged, AuditEvents.AvailabilityFilled, AuditEvents.AvailabilityPeopleChanged,
            AuditEvents.AvailabilitySettingsChanged);
        Add(AuditCategory.Hosts, AuditTargets.Host, AuditEvents.HostsImportanceChanged, AuditEvents.HostsLocationChanged);
        Add(AuditCategory.Sensors, AuditTargets.Sensor, AuditEvents.SensorCreated, AuditEvents.SensorUpdated, AuditEvents.SensorDeleted, AuditEvents.SensorTokensRotated, AuditEvents.SensorsImportanceChanged);
        Add(AuditCategory.Notifications, AuditTargets.NotificationRule, AuditEvents.NotificationRuleCreated, AuditEvents.NotificationRuleUpdated, AuditEvents.NotificationRuleDeleted, AuditEvents.NotificationRuleSensorLinked, AuditEvents.NotificationRuleSensorUnlinked, AuditEvents.NotificationRuleScopesChanged, AuditEvents.NotificationRuleExclusionAdded, AuditEvents.NotificationRuleExclusionRemoved);
        Add(AuditCategory.Notifications, null, AuditEvents.NotificationsAssigned, AuditEvents.NotificationSettingsChanged);
        Add(AuditCategory.Dns, AuditTargets.DnsZone, AuditEvents.DnsZoneCreated, AuditEvents.DnsZoneUpdated, AuditEvents.DnsZoneDeleted, AuditEvents.DnsZoneUpdateForced, AuditEvents.DnsRecordUpdated);
        Add(AuditCategory.Mail, AuditTargets.MailInputRule, AuditEvents.MailInputRuleCreated, AuditEvents.MailInputRuleUpdated, AuditEvents.MailInputRuleDeleted);
        Add(AuditCategory.Auth, AuditTargets.User, AuditEvents.TotpSetupStarted, AuditEvents.TotpDailyConfirmed);
        Add(AuditCategory.System, AuditTargets.KeyValue, AuditEvents.KeyValueCreated, AuditEvents.KeyValueUpdated, AuditEvents.KeyValueDeleted);
        Add(AuditCategory.System, null, AuditEvents.UplinkCheckRun, AuditEvents.AuditLogExported);
        Add(AuditCategory.Udp, AuditTargets.UdpCommand, AuditEvents.UdpCommandQueued, AuditEvents.UdpCommandCancelled);
        Add(AuditCategory.Udp, AuditTargets.Firmware, AuditEvents.UdpFirmwareChanged);
        Add(AuditCategory.Mikrotik, AuditTargets.Sensor, AuditEvents.MikrotikPollRequested, AuditEvents.MikrotikBackupRequested);
        Add(AuditCategory.Certificates, AuditTargets.Sensor, AuditEvents.CertificateCheckRequested);
        Add(AuditCategory.Sensors, AuditTargets.Sensor, AuditEvents.AdvancedRulesChanged, AuditEvents.AdvancedFieldsChanged,
            AuditEvents.AdvancedReportDeleted, AuditEvents.AdvancedReportsCleared);
        Add(AuditCategory.Mikrotik, null, AuditEvents.MikrotikBackupSettingsChanged);
        Add(AuditCategory.Agents, AuditTargets.Sensor, AuditEvents.AgentCollectRequested, AuditEvents.AgentLogRequested);
        Add(AuditCategory.Agents, AuditTargets.ScriptRun, AuditEvents.AgentScriptRunRedacted);
        Add(AuditCategory.Agents, AuditTargets.RunAs, AuditEvents.WindowsRunAsChanged);
        Add(AuditCategory.Diagnostics, AuditTargets.DiagnosticsAssignment, AuditEvents.DiagnosticsRun);
        Add(AuditCategory.Decisions, AuditTargets.Decision, AuditEvents.DecisionDecided);
        Add(AuditCategory.Locations, AuditTargets.Location, AuditEvents.LocationCreated, AuditEvents.LocationUpdated, AuditEvents.LocationMoved, AuditEvents.LocationValueChanged);
        Add(AuditCategory.Locations, null, AuditEvents.LocationsCreatedFromText);
        Add(AuditCategory.Locations, AuditTargets.LocationField, AuditEvents.LocationFieldCreated, AuditEvents.LocationFieldChanged, AuditEvents.LocationFieldArchived, AuditEvents.LocationFieldRestored);
        Add(AuditCategory.Locations, null, AuditEvents.LocationFieldsReordered);
        Add(AuditCategory.Knowledge, AuditTargets.KbPage, AuditEvents.KbPageCreated, AuditEvents.KbPageSaved, AuditEvents.KbPageArchived, AuditEvents.KbPageRestored, AuditEvents.KbRevisionRestored, AuditEvents.KbAiTagsRequested);
        Add(AuditCategory.Knowledge, AuditTargets.KbTag, AuditEvents.KbTagRenamed);
        Add(AuditCategory.Files, AuditTargets.File, AuditEvents.FileUploaded);
        Add(AuditCategory.Mcp, AuditTargets.McpToken, AuditEvents.McpTokenOn, AuditEvents.McpTokenOff, AuditEvents.McpTokenRegenerated,
            AuditEvents.McpTokenExpired, AuditEvents.McpTokenBlocked, AuditEvents.McpTokenTurnedOffByAdmin, AuditEvents.McpDeleteAllowed,
            AuditEvents.McpDeleteEnded);
        Add(AuditCategory.Mcp, null, AuditEvents.McpTokensRevokedByAdmin);
        Add(AuditCategory.Connectors, AuditTargets.Connector, AuditEvents.ConnectorChanged);
        Add(AuditCategory.Connectors, AuditTargets.ConnectorCaller, AuditEvents.ConnectorCallerChanged, AuditEvents.ConnectorCallerKeyViewed,
            AuditEvents.ConnectorCallerKeyRotated);
        Add(AuditCategory.Connectors, null, AuditEvents.ConnectorRefused);
        Add(AuditCategory.Connectors, AuditTargets.ConnectorCall, AuditEvents.ConnectorCallRedacted, AuditEvents.ConnectorCallCancelled);
        Add(AuditCategory.Network, AuditTargets.NetworkConnection, AuditEvents.NetworkConnectionCreated, AuditEvents.NetworkConnectionUpdated,
            AuditEvents.NetworkConnectionDeleted);
        Add(AuditCategory.Network, AuditTargets.NetworkSuggestion, AuditEvents.NetworkSuggestionAccepted, AuditEvents.NetworkSuggestionDismissed,
            AuditEvents.NetworkSuggestionRestored);
        Add(AuditCategory.Network, AuditTargets.NetworkInterface, AuditEvents.NetworkInterfaceAssigned, AuditEvents.NetworkInterfaceCleared,
            AuditEvents.NetworkInterfaceIgnored, AuditEvents.NetworkInterfaceUnignored);
        Add(AuditCategory.Network, AuditTargets.NetworkScan, AuditEvents.NetworkScanStarted, AuditEvents.NetworkScanCancelled);
        Add(AuditCategory.Network, null, AuditEvents.NetworkMapSettingsChanged);
        Add(AuditCategory.Network, AuditTargets.NetworkRouterPosition, AuditEvents.NetworkPositionsSaved, AuditEvents.NetworkPositionCleared,
            AuditEvents.NetworkPositionsReset);
        Add(AuditCategory.Network, AuditTargets.NetworkSkipRule, AuditEvents.NetworkSkipRuleCreated, AuditEvents.NetworkSkipRuleUpdated,
            AuditEvents.NetworkSkipRuleDeleted);
        Add(AuditCategory.Responsibilities, AuditTargets.Responsibility, AuditEvents.ResponsibilityCreated, AuditEvents.ResponsibilityChanged,
            AuditEvents.ResponsibilityDeleted, AuditEvents.ResponsibilityLinked, AuditEvents.ResponsibilityUnlinked,
            AuditEvents.ResponsibilityRedmineSaved, AuditEvents.ResponsibilityRedmineDeleted, AuditEvents.ResponsibilityMapSaved,
            AuditEvents.ResponsibilityMapReset);
        Add(AuditCategory.Responsibilities, null, AuditEvents.GlobalResponsibleChanged);
        Add(AuditCategory.Responsibilities, AuditTargets.ResponsibilityNote, AuditEvents.ResponsibilityNoteCreated, AuditEvents.ResponsibilityNoteChanged,
            AuditEvents.ResponsibilityNoteDeleted);
        Add(AuditCategory.Boards, AuditTargets.Board, AuditEvents.BoardCreated, AuditEvents.BoardChanged, AuditEvents.BoardDeleted,
            AuditEvents.BoardUrlShown, AuditEvents.BoardUrlRenewed, AuditEvents.BoardRefused);
        Add(AuditCategory.Boards, AuditTargets.BoardMessage, AuditEvents.BoardMessageSent, AuditEvents.BoardMessageWithdrawn);
        Add(AuditCategory.Licenses, AuditTargets.License, AuditEvents.LicenseCreated, AuditEvents.LicenseChanged, AuditEvents.LicenseDeleted,
            AuditEvents.LicenseRenewed, AuditEvents.LicenseRenewedAutomatically, AuditEvents.LicenseReviewed, AuditEvents.LicenseCancelled,
            AuditEvents.LicenseReactivated, AuditEvents.LicenseRetired, AuditEvents.LicenseKeysShown, AuditEvents.LicenseAssigned,
            AuditEvents.LicenseAssignmentChanged, AuditEvents.LicenseUnassigned, AuditEvents.LicenseSensorLinked, AuditEvents.LicenseSensorUnlinked,
            AuditEvents.LicenseRenewalChanged, AuditEvents.LicenseRenewalDeleted, AuditEvents.LicenseChargeRecorded, AuditEvents.LicenseChargeChanged,
            AuditEvents.LicenseChargeDeleted, AuditEvents.LicenseDocumentAdded, AuditEvents.LicenseDocumentChanged,
            AuditEvents.LicenseDocumentDownloaded, AuditEvents.LicenseDocumentRemoved);
        Add(AuditCategory.Licenses, null, AuditEvents.LicenseNamesRenamed, AuditEvents.LicenseSensorsRemoved, AuditEvents.LicenseSensorsMade);
        Add(AuditCategory.Licenses, AuditTargets.LicenseTemplate, AuditEvents.LicenseTemplateCreated, AuditEvents.LicenseTemplateChanged,
            AuditEvents.LicenseTemplateDeleted);
        Add(AuditCategory.Licenses, AuditTargets.LicenseCategory, AuditEvents.LicenseCategoryCreated, AuditEvents.LicenseCategoryChanged,
            AuditEvents.LicenseCategoryDeleted);
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
        ["Zabbix"] = "Zabbix", ["Windows"] = "Windows", ["Mcp"] = "MCP", ["Os"] = "OS", ["License"] = "Licence",
    };

    /// <summary>"MikrotikJoinScriptViewed" → "MikroTik join script viewed".</summary>
    public static string Label(string type)
    {
        var words = System.Text.RegularExpressions.Regex.Split(type.Replace("WireGuard", "Wireguard"), "(?<=[a-z0-9])(?=[A-Z])");
        return string.Join(' ', words.Select((w, i) => Words.TryGetValue(w, out var special) ? special : i == 0 ? w : w.ToLowerInvariant()));
    }

    private static AuditTone Tone(string type) =>
        System.Text.RegularExpressions.Regex.IsMatch(type, "Failed|LockedOut") ? AuditTone.Danger
        // Trust decisions (docs/ssl-certificate-plan.md §6.3): a changed certificate accepted, a private CA added.
        : type is AuditEvents.CertificateAccepted or AuditEvents.CertificateAuthorityAdded ? AuditTone.Warn
        // An app's reports deleted (docs/advanced-sensor-plan.md §8.3).
        : type is AuditEvents.AdvancedReportsCleared ? AuditTone.Warn
        // docs/connectors-plan.md §12.2: a key shown or replaced, a call refused for a reason that may mean a leaked key.
        : type is AuditEvents.ConnectorCallerKeyViewed or AuditEvents.ConnectorCallerKeyRotated ? AuditTone.Warn
        : type is AuditEvents.ConnectorRefused ? AuditTone.Danger
        // docs/boards-plan.md §13.2: a board's URL shown or replaced, a screen refused by its networks.
        : type is AuditEvents.BoardUrlShown or AuditEvents.BoardUrlRenewed or AuditEvents.BoardRefused ? AuditTone.Warn
        // docs/board-messages-plan.md §12.2: a message over a wall's board is notable.
        : type is AuditEvents.BoardMessageSent ? AuditTone.Warn
        // docs/linux-os-updates-plan.md §6.5: a high-risk finding accepted for a while.
        : type is AuditEvents.OsUpdateFindingAcknowledged ? AuditTone.Warn
        // docs/licences-plan.md §10.2: a key shown, a document downloaded, item sensors made or removed by the switch.
        : type is AuditEvents.LicenseKeysShown or AuditEvents.LicenseDocumentDownloaded or AuditEvents.LicenseSensorsMade ? AuditTone.Warn
        : System.Text.RegularExpressions.Regex.IsMatch(type, "Deleted|Reset|Revoked|Disabled|Cancelled|Retired|Removed|Redacted|Detached") ? AuditTone.Warn
        : System.Text.RegularExpressions.Regex.IsMatch(type, "Succeeded|Enabled|Created|Joined|Registered|Attached|Uploaded") ? AuditTone.Success
        : AuditTone.Neutral;
}
