import { call, client, type Schemas } from './client'

/** The v1 resources (docs/webui-migration-plan.md §3.7), one typed function per call. */

export type S = Schemas
export type SensorType = S['SensorType']
export type SensorState = S['SensorState']
export type HostState = S['HostState']
export type RuleType = S['NotificationRuleType']
export type UdpOtaState = S['UdpOtaState']
export type UdpCommandState = S['UdpCommandState']
export type UdpLogKind = S['UdpLogKind']
export type KeyFamily = S['KeyFamily']

export interface ListQuery {
  page?: number
  pageSize?: number
  sort?: string
  search?: string
}

type Id = string

export const api = {
  dashboard: () => call(client.GET('/api/v1/dashboard')),
  search: (q: string, limit = 8) => call(client.GET('/api/v1/search', { params: { query: { q, limit } } })),

  system: {
    uplink: () => call(client.GET('/api/v1/system/uplink')),
    tasks: () => call(client.GET('/api/v1/system/tasks')),
    statusCodes: () => call(client.GET('/api/v1/system/status-codes')),
    sensorTypes: () => call(client.GET('/api/v1/system/sensor-types')),
    ruleTypes: () => call(client.GET('/api/v1/system/notification-rule-types')),
  },

  hosts: {
    list: (query: ListQuery & { state?: HostState }) => call(client.GET('/api/v1/hosts', { params: { query } })),
    get: (id: Id) => call(client.GET('/api/v1/hosts/{id}', { params: { path: { id } } })),
    create: (body: S['HostRequest']) => call(client.POST('/api/v1/hosts', { body })),
    update: (id: Id, body: S['HostRequest']) =>
      call(client.PUT('/api/v1/hosts/{id}', { params: { path: { id } }, body })),
    remove: (id: Id) => call(client.DELETE('/api/v1/hosts/{id}', { params: { path: { id } } })),
  },

  sensors: {
    list: (query: ListQuery & { hostId?: Id; type?: SensorType; state?: SensorState }) =>
      call(client.GET('/api/v1/sensors', { params: { query } })),
    get: (id: Id) => call(client.GET('/api/v1/sensors/{id}', { params: { path: { id } } })),
    create: (body: S['SensorRequest']) => call(client.POST('/api/v1/sensors', { body })),
    update: (id: Id, body: S['SensorRequest']) =>
      call(client.PUT('/api/v1/sensors/{id}', { params: { path: { id } }, body })),
    remove: (id: Id) => call(client.DELETE('/api/v1/sensors/{id}', { params: { path: { id } } })),
    rotate: (id: Id, body: S['RotateTokensRequest']) =>
      call(client.POST('/api/v1/sensors/{id}/tokens/rotate', { params: { path: { id } }, body })),
    lookup: (token: string) => call(client.POST('/api/v1/sensors/lookup', { body: { token } })),
    events: (id: Id, query: ListQuery & { status?: string; from?: string; to?: string }) =>
      call(client.GET('/api/v1/sensors/{id}/events', { params: { path: { id }, query } })),
    rules: (id: Id) => call(client.GET('/api/v1/sensors/{id}/notification-rules', { params: { path: { id } } })),
    link: (id: Id, ruleId: Id) =>
      call(client.PUT('/api/v1/sensors/{id}/notification-rules/{ruleId}', { params: { path: { id, ruleId } } })),
    unlink: (id: Id, ruleId: Id) =>
      call(client.DELETE('/api/v1/sensors/{id}/notification-rules/{ruleId}', { params: { path: { id, ruleId } } })),
  },

  events: {
    list: (query: ListQuery & { hostId?: Id; sensorId?: Id; status?: string; from?: string; to?: string }) =>
      call(client.GET('/api/v1/events', { params: { query } })),
    get: (id: Id) => call(client.GET('/api/v1/events/{id}', { params: { path: { id } } })),
    stateChanges: (query: { from?: string; to?: string; hostId?: Id; sensorId?: Id; limit?: number }) =>
      call(client.GET('/api/v1/events/state-changes', { params: { query } })),
  },

  rules: {
    list: (query: ListQuery) => call(client.GET('/api/v1/notification-rules', { params: { query } })),
    get: (id: Id) => call(client.GET('/api/v1/notification-rules/{id}', { params: { path: { id } } })),
    create: (body: S['NotificationRuleRequest']) => call(client.POST('/api/v1/notification-rules', { body })),
    update: (id: Id, body: S['NotificationRuleRequest']) =>
      call(client.PUT('/api/v1/notification-rules/{id}', { params: { path: { id } }, body })),
    remove: (id: Id) => call(client.DELETE('/api/v1/notification-rules/{id}', { params: { path: { id } } })),
    link: (id: Id, sensorId: Id) =>
      call(client.PUT('/api/v1/notification-rules/{id}/sensors/{sensorId}', { params: { path: { id, sensorId } } })),
    unlink: (id: Id, sensorId: Id) =>
      call(client.DELETE('/api/v1/notification-rules/{id}/sensors/{sensorId}', { params: { path: { id, sensorId } } })),
  },

  dns: {
    list: (query: ListQuery) => call(client.GET('/api/v1/dns-zones', { params: { query } })),
    get: (id: Id) => call(client.GET('/api/v1/dns-zones/{id}', { params: { path: { id } } })),
    create: (body: S['DnsZoneRequest']) => call(client.POST('/api/v1/dns-zones', { body })),
    update: (id: Id, body: S['DnsZoneRequest']) =>
      call(client.PUT('/api/v1/dns-zones/{id}', { params: { path: { id } }, body })),
    remove: (id: Id) => call(client.DELETE('/api/v1/dns-zones/{id}', { params: { path: { id } } })),
    forceUpdate: (id: Id) => call(client.POST('/api/v1/dns-zones/{id}/force-update', { params: { path: { id } } })),
    record: (id: Id, recordId: Id, body: S['DnsRecordRequest']) =>
      call(client.PUT('/api/v1/dns-zones/{id}/records/{recordId}', { params: { path: { id, recordId } }, body })),
    zoneLog: (id: Id, query: ListQuery) =>
      call(client.GET('/api/v1/dns-zones/{id}/log', { params: { path: { id }, query } })),
    log: (query: ListQuery) => call(client.GET('/api/v1/dns-zones/log', { params: { query } })),
  },

  mail: {
    rules: (query: ListQuery) => call(client.GET('/api/v1/mail-input-rules', { params: { query } })),
    rule: (id: Id) => call(client.GET('/api/v1/mail-input-rules/{id}', { params: { path: { id } } })),
    createRule: (body: S['MailInputRuleRequest']) => call(client.POST('/api/v1/mail-input-rules', { body })),
    updateRule: (id: Id, body: S['MailInputRuleRequest']) =>
      call(client.PUT('/api/v1/mail-input-rules/{id}', { params: { path: { id } }, body })),
    removeRule: (id: Id) => call(client.DELETE('/api/v1/mail-input-rules/{id}', { params: { path: { id } } })),
    log: (query: ListQuery & { processed?: boolean }) => call(client.GET('/api/v1/mail-log', { params: { query } })),
    logEntry: (id: Id) => call(client.GET('/api/v1/mail-log/{id}', { params: { path: { id } } })),
  },

  keyValues: {
    list: (query: ListQuery) => call(client.GET('/api/v1/key-values', { params: { query } })),
    create: (body: S['KeyValueRequest']) => call(client.POST('/api/v1/key-values', { body })),
    update: (id: Id, body: S['KeyValueRequest']) =>
      call(client.PUT('/api/v1/key-values/{id}', { params: { path: { id } }, body })),
    remove: (id: Id) => call(client.DELETE('/api/v1/key-values/{id}', { params: { path: { id } } })),
  },

  users: {
    list: (query: ListQuery) => call(client.GET('/api/v1/users', { params: { query } })),
    create: (body: S['CreateUserRequest']) => call(client.POST('/api/v1/users', { body })),
    update: (id: Id, body: S['UpdateUserRequest']) =>
      call(client.PUT('/api/v1/users/{id}', { params: { path: { id } }, body })),
    remove: (id: Id) => call(client.DELETE('/api/v1/users/{id}', { params: { path: { id } } })),
    resetPassword: (id: Id) => call(client.POST('/api/v1/users/{id}/reset-password', { params: { path: { id } } })),
    resetTotp: (id: Id) => call(client.POST('/api/v1/users/{id}/reset-totp', { params: { path: { id } } })),
    unlock: (id: Id) => call(client.POST('/api/v1/users/{id}/unlock', { params: { path: { id } } })),
    log: (query: ListQuery & { userId?: Id; type?: string }) =>
      call(client.GET('/api/v1/auth-log', { params: { query } })),
  },

  mikrotik: {
    // --- a router (docs/mikrotik-plan.md §10): CanWrite, accepting a host key Admin -------------
    joinScript: (id: Id) => call(client.GET('/api/v1/sensors/{id}/mikrotik/join-script', { params: { path: { id } } })),
    removalScript: (id: Id) =>
      call(client.GET('/api/v1/sensors/{id}/mikrotik/removal-script', { params: { path: { id } } })),
    poll: (id: Id) => call(client.POST('/api/v1/sensors/{id}/mikrotik/poll', { params: { path: { id } } })),
    regenerateKeys: (id: Id) =>
      call(client.POST('/api/v1/sensors/{id}/mikrotik/regenerate-keys', { params: { path: { id } } })),
    acceptHostKey: (id: Id) =>
      call(client.POST('/api/v1/sensors/{id}/mikrotik/host-key/accept', { params: { path: { id } } })),
    // --- its backups (docs/mikrotik-plan.md §6): the list CanRead, contents CanWrite, secrets Admin ---------
    backups: (id: Id, query: ListQuery & { kind?: S['MikrotikBackupKind'] } = {}) =>
      call(client.GET('/api/v1/sensors/{id}/mikrotik/backups', { params: { path: { id }, query } })),
    backUp: (id: Id) => call(client.POST('/api/v1/sensors/{id}/mikrotik/backups', { params: { path: { id } } })),
    backup: (id: Id, backupId: Id) =>
      call(client.GET('/api/v1/sensors/{id}/mikrotik/backups/{backupId}', { params: { path: { id, backupId } } })),
    diff: (id: Id, backupId: Id, against?: Id) =>
      call(
        client.GET('/api/v1/sensors/{id}/mikrotik/backups/{backupId}/diff', {
          params: { path: { id, backupId }, query: { against } },
        }),
      ),
    /** A plain link: the browser downloads the file with the session cookie. */
    downloadUrl: (id: Id, backupId: Id) =>
      `${typeof location !== 'undefined' ? location.origin : ''}/api/v1/sensors/${encodeURIComponent(id)}/mikrotik/backups/${encodeURIComponent(backupId)}/download`,
    jobs: (id: Id, query: ListQuery = {}) =>
      call(client.GET('/api/v1/sensors/{id}/mikrotik/jobs', { params: { path: { id }, query } })),
    job: (id: Id, jobId: Id) =>
      call(client.GET('/api/v1/sensors/{id}/mikrotik/jobs/{jobId}', { params: { path: { id, jobId } } })),
    changelog: (id: Id, version: string) =>
      call(client.GET('/api/v1/sensors/{id}/mikrotik/changelog', { params: { path: { id }, query: { version } } })),
    // --- update jobs: Admin ------------------------------------------------------------------------------
    startJobs: (body: S['MikrotikJobRequest']) => call(client.POST('/api/v1/mikrotik/jobs', { body })),
    allJobs: (query: ListQuery & { kind?: S['MikrotikJobKind']; state?: S['MikrotikJobState'] } = {}) =>
      call(client.GET('/api/v1/mikrotik/jobs', { params: { query } })),
    anyJob: (jobId: Id) => call(client.GET('/api/v1/mikrotik/jobs/{id}', { params: { path: { id: jobId } } })),
    cancelJob: (jobId: Id) => call(client.DELETE('/api/v1/mikrotik/jobs/{id}', { params: { path: { id: jobId } } })),
    // --- remote commands (docs/mikrotik-plan.md §8): as pnmt CanWrite, as a router's admin Admin ------------
    runCommand: (body: S['MikrotikCommandRequest']) => call(client.POST('/api/v1/mikrotik/commands', { body })),
    commands: (query: ListQuery & { sensorId?: Id; batchId?: Id; state?: S['MikrotikJobState'] } = {}) =>
      call(client.GET('/api/v1/mikrotik/commands', { params: { query } })),
    command: (id: Id) => call(client.GET('/api/v1/mikrotik/commands/{id}', { params: { path: { id } } })),
    commandBatch: (batchId: Id) =>
      call(client.GET('/api/v1/mikrotik/commands/batches/{batchId}', { params: { path: { batchId } } })),
    cancelCommand: (id: Id) => call(client.DELETE('/api/v1/mikrotik/commands/{id}', { params: { path: { id } } })),
    redactCommand: (id: Id) => call(client.POST('/api/v1/mikrotik/commands/{id}/redact', { params: { path: { id } } })),
    removeBackup: (id: Id, backupId: Id) =>
      call(client.DELETE('/api/v1/sensors/{id}/mikrotik/backups/{backupId}', { params: { path: { id, backupId } } })),
    // --- the admin page: Admin ------------------------------------------------------------------
    status: () => call(client.GET('/api/v1/mikrotik/status')),
    sshKeys: () => call(client.GET('/api/v1/mikrotik/ssh-keys')),
    releases: () => call(client.GET('/api/v1/mikrotik/releases')),
    rotateSshKey: () => call(client.POST('/api/v1/mikrotik/ssh-keys/rotate')),
    retireSshKeys: (force: boolean) =>
      call(client.POST('/api/v1/mikrotik/ssh-keys/retire', { params: { query: { force } } })),
    routers: (
      query: ListQuery & {
        mode?: S['MikrotikMode']
        setupState?: S['MikrotikSetupState']
        state?: S['SensorState']
        updateAvailable?: boolean
        scriptOutdated?: boolean
        channel?: string
      } = {},
    ) => call(client.GET('/api/v1/mikrotik/routers', { params: { query } })),
    // --- backups for the installation (docs/mikrotik-backup-plan.md §2.2): reading CanWrite, changing Admin ---
    backupSettings: () => call(client.GET('/api/v1/mikrotik/backup-settings')),
    updateBackupSettings: (body: S['MikrotikBackupSettingsRequest']) =>
      call(client.PUT('/api/v1/mikrotik/backup-settings', { body })),
    applyBackupSettings: (body: S['MikrotikRouterBackupRequest']) =>
      call(client.POST('/api/v1/mikrotik/routers/backup-settings', { body })),
    setBackupKey: (recipient: string) => call(client.POST('/api/v1/mikrotik/backup-keys', { body: { recipient } })),
    retireBackupKey: () => call(client.POST('/api/v1/mikrotik/backup-keys/retire')),
  },

  wireGuard: {
    // --- the tunnel network (docs/mikrotik-plan.md §10, §11.2): Admin ------------------------------
    status: () => call(client.GET('/api/v1/wireguard/status')),
    network: () => call(client.GET('/api/v1/wireguard/network')),
    saveNetwork: (body: S['WireGuardNetworkRequest']) => call(client.PUT('/api/v1/wireguard/network', { body })),
    peers: () => call(client.GET('/api/v1/wireguard/peers')),
    tunnels: () => call(client.GET('/api/v1/wireguard/tunnels')),
    createTunnel: (body: S['WireGuardTunnelRequest']) => call(client.POST('/api/v1/wireguard/tunnels', { body })),
    updateTunnel: (id: Id, body: S['WireGuardTunnelRequest']) =>
      call(client.PUT('/api/v1/wireguard/tunnels/{id}', { params: { path: { id } }, body })),
    deleteTunnel: (id: Id) => call(client.DELETE('/api/v1/wireguard/tunnels/{id}', { params: { path: { id } } })),
    regenerateTunnel: (id: Id) =>
      call(client.POST('/api/v1/wireguard/tunnels/{id}/regenerate-keys', { params: { path: { id } } })),
    tunnelConfig: (id: Id) => call(client.GET('/api/v1/wireguard/tunnels/{id}/config', { params: { path: { id } } })),
    // --- a user's own tunnels: any role ----------------------------------------------------------
    myTunnels: () => call(client.GET('/api/v1/account/tunnels')),
    myTunnelConfig: (id: Id) => call(client.GET('/api/v1/account/tunnels/{id}/config', { params: { path: { id } } })),
  },

  /** The Notification Manager (docs/ai-operations-plan.md §11.3): every role reads, a User assigns. */
  notifications: {
    matrix: (
      query: ListQuery & {
        hostId?: string
        importance?: S['Importance']
        type?: SensorType
        state?: SensorState
        uncovered?: boolean
      },
    ) => call(client.GET('/api/v1/notifications/matrix', { params: { query } })),
    coverage: () => call(client.GET('/api/v1/notifications/coverage')),
    assign: (body: S['NotificationAssignRequest']) => call(client.POST('/api/v1/notifications/assign', { body })),
    setHostImportance: (body: S['ImportanceRequest']) => call(client.PUT('/api/v1/hosts/importance', { body })),
    setSensorImportance: (body: S['ImportanceRequest']) => call(client.PUT('/api/v1/sensors/importance', { body })),
    setScopes: (id: Id, body: S['NotificationScopeRequest'][]) =>
      call(client.PUT('/api/v1/notification-rules/{id}/scopes', { params: { path: { id } }, body })),
    previewScopes: (id: Id, body: S['NotificationScopeRequest'][]) =>
      call(client.POST('/api/v1/notification-rules/{id}/scopes/preview', { params: { path: { id } }, body })),
    exclude: (id: Id, sensorId: Id) =>
      call(client.PUT('/api/v1/notification-rules/{id}/exclusions/{sensorId}', { params: { path: { id, sensorId } } })),
    include: (id: Id, sensorId: Id) =>
      call(
        client.DELETE('/api/v1/notification-rules/{id}/exclusions/{sensorId}', { params: { path: { id, sensorId } } }),
      ),
  },

  /** The uplink page (docs/ai-operations-plan.md §7.3): every role reads, an Admin checks now. */
  uplink: {
    get: () => call(client.GET('/api/v1/uplink')),
    checks: (query: ListQuery & { failuresOnly?: boolean; endpoint?: number }) =>
      call(client.GET('/api/v1/uplink/checks', { params: { query } })),
    outages: (days = 7) => call(client.GET('/api/v1/uplink/outages', { params: { query: { days } } })),
    check: () => call(client.POST('/api/v1/uplink/check')),
  },

  /** Monitoring → Decisions (docs/ai-operations-plan.md §5.3): the User role. */
  decisions: {
    list: (query: ListQuery & { state?: S['DecisionState']; kind?: string }) =>
      call(client.GET('/api/v1/decisions', { params: { query } })),
    count: () => call(client.GET('/api/v1/decisions/count')),
    get: (id: Id) => call(client.GET('/api/v1/decisions/{id}', { params: { path: { id } } })),
    decide: (id: Id, body: S['DecideRequest']) =>
      call(client.POST('/api/v1/decisions/{id}/decide', { params: { path: { id } }, body })),
  },

  /** Admin → AI (docs/ai-operations-plan.md §3.6, §15): Admin only. */
  ai: {
    status: () => call(client.GET('/api/v1/ai/status')),
    requests: (
      query: ListQuery & {
        purpose?: string
        role?: S['AiRole']
        model?: string
        outcome?: S['AiOutcome']
        workId?: Id
      },
    ) => call(client.GET('/api/v1/ai/requests', { params: { query } })),
    request: (id: number) => call(client.GET('/api/v1/ai/requests/{id}', { params: { path: { id } } })),
    work: (query: ListQuery & { state?: S['AiWorkState']; purpose?: string }) =>
      call(client.GET('/api/v1/ai/work', { params: { query } })),
    retry: (id: Id) => call(client.POST('/api/v1/ai/work/{id}/retry', { params: { path: { id } } })),
    cancel: (id: Id) => call(client.POST('/api/v1/ai/work/{id}/cancel', { params: { path: { id } } })),
    backfill: (body: S['MailBackfillRequest']) => call(client.POST('/api/v1/ai/mail/backfill', { body })),
    // --- the situation analysis (docs/ai-operations-plan.md §9): CanRead; a run on demand Admin ---------------
    analysis: (query: ListQuery & { state?: S['AnalysisRunState'] }) =>
      call(client.GET('/api/v1/ai/analysis', { params: { query } })),
    analysisRun: (id: Id) => call(client.GET('/api/v1/ai/analysis/{id}', { params: { path: { id } } })),
    runAnalysis: () => call(client.POST('/api/v1/ai/analysis/run')),
  },

  /** Windows servers (docs/windows-sensor-plan.md §9.2): the fleet CanRead, requests CanWrite, tokens and the agent log Admin. */
  windows: {
    servers: (
      query: ListQuery & {
        state?: SensorState
        unsupported?: boolean
        outdated?: boolean
        connected?: boolean
        remoteAccess?: boolean
        auditGaps?: boolean
        diagnosticsFailing?: boolean
        hostId?: Id
      },
    ) => call(client.GET('/api/v1/windows/servers', { params: { query } })),
    server: (id: Id) => call(client.GET('/api/v1/windows/servers/{id}', { params: { path: { id } } })),
    section: (id: Id, section: string) =>
      call(client.GET('/api/v1/windows/servers/{id}/sections/{section}', { params: { path: { id, section } } })),
    logins: (
      id: Id,
      query: ListQuery & {
        outcome?: S['LogonOutcome']
        admin?: boolean
        kind?: S['LogonKind']
        user?: string
        source?: string
        from?: string
        to?: string
      },
    ) => call(client.GET('/api/v1/windows/servers/{id}/logins', { params: { path: { id }, query } })),
    changes: (id: Id, query: ListQuery & { section?: string; notable?: boolean }) =>
      call(client.GET('/api/v1/windows/servers/{id}/changes', { params: { path: { id }, query } })),
    serverLog: (id: Id, query: ListQuery & { kind?: S['WindowsAgentLogKind'] }) =>
      call(client.GET('/api/v1/windows/servers/{id}/log', { params: { path: { id }, query } })),
    collect: (id: Id, sections: string[] = []) =>
      call(client.POST('/api/v1/windows/servers/{id}/collect', { params: { path: { id } }, body: { sections } })),
    sendLog: (id: Id) => call(client.POST('/api/v1/windows/servers/{id}/send-log', { params: { path: { id } } })),
    settings: (id: Id, body: S['WindowsServerSettings']) =>
      call(client.PUT('/api/v1/windows/servers/{id}/settings', { params: { path: { id } }, body })),
    revoke: (id: Id, reason: string | null) =>
      call(client.POST('/api/v1/windows/servers/{id}/revoke', { params: { path: { id } }, body: { reason } })),
    rejoinToken: (id: Id, validHours: number | null = null) =>
      call(
        client.POST('/api/v1/windows/servers/{id}/rejoin-token', { params: { path: { id } }, body: { validHours } }),
      ),
    tokens: (query: ListQuery & { state?: S['WindowsJoinTokenState'] }) =>
      call(client.GET('/api/v1/windows/join-tokens', { params: { query } })),
    createToken: (body: S['WindowsJoinTokenRequest']) => call(client.POST('/api/v1/windows/join-tokens', { body })),
    revokeToken: (id: Id) => call(client.DELETE('/api/v1/windows/join-tokens/{id}', { params: { path: { id } } })),
    agentLog: (query: ListQuery & { kind?: S['WindowsAgentLogKind']; sensorId?: Id }) =>
      call(client.GET('/api/v1/windows/agent-log', { params: { query } })),
    agent: () => call(client.GET('/api/v1/windows/agent')),
    // --- PowerShell scripts (docs/windows-sensor-plan.md §7): Admin ---------------------------------------------
    scripts: (query: ListQuery) => call(client.GET('/api/v1/windows/scripts', { params: { query } })),
    script: (id: Id) => call(client.GET('/api/v1/windows/scripts/{id}', { params: { path: { id } } })),
    createScript: (body: S['WindowsScriptRequest']) => call(client.POST('/api/v1/windows/scripts', { body })),
    updateScript: (id: Id, body: S['WindowsScriptRequest']) =>
      call(client.PUT('/api/v1/windows/scripts/{id}', { params: { path: { id } }, body })),
    parseScript: (id: Id) => call(client.POST('/api/v1/windows/scripts/{id}/parse', { params: { path: { id } } })),
    deleteScript: (id: Id) => call(client.DELETE('/api/v1/windows/scripts/{id}', { params: { path: { id } } })),
    runScript: (id: Id, body: S['WindowsScriptRunRequest']) =>
      call(client.POST('/api/v1/windows/scripts/{id}/run', { params: { path: { id } }, body })),
    wmi: (sensorId: Id) => call(client.GET('/api/v1/windows/servers/{id}/wmi', { params: { path: { id: sensorId } } })),
    saveWmi: (sensorId: Id, body: S['WmiConfig']) =>
      call(client.PUT('/api/v1/windows/servers/{id}/wmi', { params: { path: { id: sensorId } }, body })),
    localScripts: (sensorId: Id) =>
      call(client.GET('/api/v1/windows/servers/{id}/local-scripts', { params: { path: { id: sensorId } } })),
    runLocalScript: (sensorId: Id, body: S['WindowsLocalScriptRunRequest']) =>
      call(client.POST('/api/v1/windows/servers/{id}/local-scripts/run', { params: { path: { id: sensorId } }, body })),
    runAs: () => call(client.GET('/api/v1/windows/run-as')),
    createRunAs: (body: S['WindowsRunAsRequest']) => call(client.POST('/api/v1/windows/run-as', { body })),
    updateRunAs: (id: Id, body: S['WindowsRunAsRequest']) =>
      call(client.PUT('/api/v1/windows/run-as/{id}', { params: { path: { id } }, body })),
    deleteRunAs: (id: Id) => call(client.DELETE('/api/v1/windows/run-as/{id}', { params: { path: { id } } })),
    scriptRuns: (query: ListQuery & { scriptId?: Id; sensorId?: Id; batchId?: Id; state?: S['ScriptRunState'] }) =>
      call(client.GET('/api/v1/windows/script-runs', { params: { query } })),
    scriptRun: (id: Id) => call(client.GET('/api/v1/windows/script-runs/{id}', { params: { path: { id } } })),
    redactRun: (id: Id) => call(client.POST('/api/v1/windows/script-runs/{id}/redact', { params: { path: { id } } })),
  },

  /** Diagnostics (docs/windows-sensor-plan.md §5): reading CanRead, changing Admin, Run now CanWrite. */
  diagnostics: {
    sets: (query: ListQuery) => call(client.GET('/api/v1/diagnostics/sets', { params: { query } })),
    set: (id: Id) => call(client.GET('/api/v1/diagnostics/sets/{id}', { params: { path: { id } } })),
    createSet: (body: S['DiagnosticSetRequest']) => call(client.POST('/api/v1/diagnostics/sets', { body })),
    updateSet: (id: Id, body: S['DiagnosticSetRequest']) =>
      call(client.PUT('/api/v1/diagnostics/sets/{id}', { params: { path: { id } }, body })),
    deleteSet: (id: Id) => call(client.DELETE('/api/v1/diagnostics/sets/{id}', { params: { path: { id } } })),
    assignments: (query: ListQuery & { targetSensorId?: Id; setId?: Id }) =>
      call(client.GET('/api/v1/diagnostics/assignments', { params: { query } })),
    assign: (body: S['DiagnosticAssignmentRequest']) => call(client.POST('/api/v1/diagnostics/assignments', { body })),
    updateAssignment: (id: Id, body: S['DiagnosticAssignmentUpdate']) =>
      call(client.PUT('/api/v1/diagnostics/assignments/{id}', { params: { path: { id } }, body })),
    deleteAssignment: (id: Id) =>
      call(client.DELETE('/api/v1/diagnostics/assignments/{id}', { params: { path: { id } } })),
    run: (id: Id) => call(client.POST('/api/v1/diagnostics/assignments/{id}/run', { params: { path: { id } } })),
    runs: (
      query: ListQuery & { assignmentId?: Id; setId?: Id; targetSensorId?: Id; outcome?: S['DiagnosticOutcome'] },
    ) => call(client.GET('/api/v1/diagnostics/runs', { params: { query } })),
    runDetail: (id: Id) => call(client.GET('/api/v1/diagnostics/runs/{id}', { params: { path: { id } } })),
  },

  /** Admin → Debug (docs/ai-operations-plan.md §8): test sends, 10 a minute. */
  debug: {
    status: () => call(client.GET('/api/v1/debug')),
    pushover: (body: S['DebugPushoverRequest']) => call(client.POST('/api/v1/debug/pushover', { body })),
    mail: (body: S['DebugMailRequest']) => call(client.POST('/api/v1/debug/mail', { body })),
    ai: () => call(client.POST('/api/v1/debug/ai')),
  },

  udp: {
    // --- a node (docs/udp-iot-plan.md §5): CanRead, the writes CanWrite --------------------
    setSecrets: (id: Id, body: S['UdpKeyRequest']) =>
      call(client.PUT('/api/v1/sensors/{id}/udp/key', { params: { path: { id } }, body })),
    regenerate: (id: Id, body: S['UdpRegenerateRequest']) =>
      call(client.POST('/api/v1/sensors/{id}/udp/regenerate-key', { params: { path: { id } }, body })),
    resetCounter: (id: Id) => call(client.POST('/api/v1/sensors/{id}/udp/reset-counter', { params: { path: { id } } })),
    resetSession: (id: Id) => call(client.POST('/api/v1/sensors/{id}/udp/reset-session', { params: { path: { id } } })),
    lastPackets: (id: Id) => call(client.GET('/api/v1/sensors/{id}/udp/last-packets', { params: { path: { id } } })),
    rejections: (id: Id) => call(client.GET('/api/v1/sensors/{id}/udp/rejections', { params: { path: { id } } })),
    commands: (id: Id, query: ListQuery & { state?: UdpCommandState }) =>
      call(client.GET('/api/v1/sensors/{id}/udp/commands', { params: { path: { id }, query } })),
    commandLog: (id: Id, commandId: Id) =>
      call(client.GET('/api/v1/sensors/{id}/udp/commands/{commandId}/log', { params: { path: { id, commandId } } })),
    queueCommand: (id: Id, body: S['UdpCommandRequest']) =>
      call(client.POST('/api/v1/sensors/{id}/udp/commands', { params: { path: { id } }, body })),
    cancelCommand: (id: Id, commandId: Id) =>
      call(client.DELETE('/api/v1/sensors/{id}/udp/commands/{commandId}', { params: { path: { id, commandId } } })),
    nodeJobs: (id: Id) => call(client.GET('/api/v1/sensors/{id}/udp/ota-jobs', { params: { path: { id } } })),

    // --- the admin page: Admin only ------------------------------------------------------
    status: () => call(client.GET('/api/v1/udp/status')),
    nodes: (
      query: ListQuery & { state?: SensorState; keyFamily?: KeyFamily; enabled?: boolean; firmwareVersion?: string },
    ) => call(client.GET('/api/v1/udp/nodes', { params: { query } })),
    log: (query: ListQuery & { sensorId?: Id; deviceId?: number; kind?: UdpLogKind; from?: string; to?: string }) =>
      call(client.GET('/api/v1/udp/command-log', { params: { query } })),
    unknownSenders: () => call(client.GET('/api/v1/udp/unknown-senders')),
    firmware: () => call(client.GET('/api/v1/udp/firmware')),
    /** Multipart (file, name, version, notes); the CSRF middleware adds its header as for every call. */
    uploadFirmware: (form: { file: File; name: string; version: string; notes?: string | null }) => {
      const data = new FormData()
      data.append('file', form.file, form.file.name)
      data.append('name', form.name)
      data.append('version', form.version)
      if (form.notes) {
        data.append('notes', form.notes)
      }
      return call(client.POST('/api/v1/udp/firmware', { body: {}, bodySerializer: () => data }))
    },
    updateFirmware: (id: Id, body: S['UdpFirmwareRequest']) =>
      call(client.PUT('/api/v1/udp/firmware/{id}', { params: { path: { id } }, body })),
    removeFirmware: (id: Id) => call(client.DELETE('/api/v1/udp/firmware/{id}', { params: { path: { id } } })),
    /** The .bin, for a download link (a GET with the session cookie). */
    firmwareUrl: (id: Id) =>
      `${typeof location !== 'undefined' ? location.origin : ''}/api/v1/udp/firmware/${encodeURIComponent(id)}/content`,
    jobs: (query: ListQuery & { state?: UdpOtaState; firmwareId?: Id; sensorId?: Id; from?: string; to?: string }) =>
      call(client.GET('/api/v1/udp/ota-jobs', { params: { query } })),
    startUpdates: (body: S['UdpOtaJobRequest']) => call(client.POST('/api/v1/udp/ota-jobs', { body })),
    cancelUpdate: (id: Id) => call(client.DELETE('/api/v1/udp/ota-jobs/{id}', { params: { path: { id } } })),

    // --- the Protocol subpage: every role ------------------------------------------------
    examples: (sensorId?: Id) => call(client.GET('/api/v1/udp/protocol/examples', { params: { query: { sensorId } } })),
    build: (query: {
      sensorId?: Id
      type: S['PacketType']
      suite: S['SecuritySuite']
      fcnt: number
      ack: boolean
      /** Base64. */
      payload?: string
    }) => call(client.GET('/api/v1/udp/protocol/build', { params: { query } })),
  },
}

/** Query keys: a change invalidates its resource's prefix (and the dashboard). */
export const keys = {
  dashboard: ['dashboard'] as const,
  hosts: ['hosts'] as const,
  host: (id: Id) => ['host', id] as const,
  sensors: ['sensors'] as const,
  sensor: (id: Id) => ['sensor', id] as const,
  events: ['events'] as const,
  rules: ['rules'] as const,
  rule: (id: Id) => ['rule', id] as const,
  dns: ['dns'] as const,
  mail: ['mail'] as const,
  keyValues: ['key-values'] as const,
  users: ['users'] as const,
  system: ['system'] as const,
  /** Everything UDP: the admin page's lists, a node's packets, commands and updates, the protocol examples. */
  udp: ['udp'] as const,
  udpNode: (id: Id) => ['udp', 'node', id] as const,
  /** MikroTik routers: the admin page's status and fleet (docs/mikrotik-plan.md §11.2). */
  mikrotik: ['mikrotik'] as const,
  /** Windows servers and their agents (docs/windows-sensor-plan.md §10). */
  windows: ['windows'] as const,
  windowsServer: (id: Id) => ['windows', 'server', id] as const,
  /** Diagnostics: sets, assignments and runs (docs/windows-sensor-plan.md §10.3). */
  diagnostics: ['diagnostics'] as const,
  /** The uplink page and its checks (docs/ai-operations-plan.md §7.3). */
  uplink: ['uplink'] as const,
  /** The Notification Manager's matrix and coverage (docs/ai-operations-plan.md §11.3). */
  notifications: ['notifications'] as const,
  /** The AI's status, requests and queue (docs/ai-operations-plan.md §15). */
  ai: ['ai'] as const,
  /** Decisions and their count (docs/ai-operations-plan.md §5.3). */
  decisions: ['decisions'] as const,
}

/** The request of a resource as it is, with some fields changed: v1's PUT takes every writable field. */
export function hostRequest(h: S['HostDetail'], patch: Partial<S['HostRequest']> = {}): S['HostRequest'] {
  return { name: h.name, location: h.location, notes: h.notes, enabled: h.enabled, importance: h.importance, ...patch }
}

export function sensorRequest(s: S['SensorDetail'], patch: Partial<S['SensorRequest']> = {}): S['SensorRequest'] {
  return {
    hostId: s.host.id,
    type: s.type,
    name: s.name,
    importance: s.importance ?? null,
    enabled: s.enabled,
    ignore: s.ignore,
    interval: s.interval,
    gracePeriod: s.gracePeriod,
    parameters: s.parameters,
    textId: s.textId,
    notes: s.notes ?? null,
    // A UDP node's settings as they are (never its key or token: omitted means unchanged); without
    // them a save would reset the node to the defaults.
    udp: (patch.type ?? s.type) === 'UDP_NODE' && s.udp ? udpNodeRequest(s.udp) : undefined,
    // A router's settings as they are (docs/mikrotik-plan.md §10), for the same reason.
    mikrotik: (patch.type ?? s.type) === 'MIKROTIK' && s.mikrotik ? mikrotikRequest(s.mikrotik) : undefined,
    ...patch,
  }
}

/** A router's settings for SensorRequest.mikrotik, as they are, with some changed. */
export function mikrotikRequest(
  m: S['MikrotikRouterDetail'],
  patch: Partial<S['MikrotikRouterRequest']> = {},
): S['MikrotikRouterRequest'] {
  return {
    mode: m.mode,
    address: m.address,
    sshPort: m.sshPort,
    sourceAddress: m.sourceAddress,
    backupIntervalHours: m.backupIntervalHours,
    encryptBackups: m.encryptBackups,
    managementAccess: m.managementAccess,
    includeSecrets: m.includeSecrets,
    diagnostics: m.diagnostics,
    ...patch,
  }
}

/** A node's settings for SensorRequest.udp, without key and token (those stay as they are). */
export function udpNodeRequest(u: S['UdpNodeDetail'], patch: Partial<S['UdpNodeRequest']> = {}): S['UdpNodeRequest'] {
  return {
    deviceId: u.deviceId,
    keyFamily: u.keyFamily,
    allowedSuites: [...u.allowedSuites],
    sessionMode: u.sessionMode,
    ackMode: u.ackMode,
    replayProtection: u.replayProtection,
    responseSuite: u.responseSuite,
    ...patch,
  }
}

export function ruleRequest(r: S['NotificationRuleDetail'], patch: Partial<S['NotificationRuleRequest']> = {}) {
  return { name: r.name, recipient: r.recipient, type: r.type, enabled: r.enabled, ...patch }
}

export function mailRuleRequest(
  r: S['MailInputRule'],
  patch: Partial<S['MailInputRuleRequest']> = {},
): S['MailInputRuleRequest'] {
  return {
    name: r.name,
    enabled: r.enabled,
    fromTest: r.fromTest,
    subjectTest: r.subjectTest,
    bodyTest: r.bodyTest,
    okTest: r.okTest,
    okCode: r.okCode,
    failTest: r.failTest,
    failCode: r.failCode,
    defaultCode: r.defaultCode,
    extractMessageRegex: r.extractMessageRegex,
    sensorId: r.sensor?.id ?? null,
    ...patch,
  }
}
