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
  /** `fresh`: *Refresh* waits for a new build; the polls take the server's last one at once. */
  dashboard: (fresh = false) =>
    call(client.GET('/api/v1/dashboard', { params: { query: fresh ? { fresh: true } : {} } })),
  search: (q: string, limit = 8) => call(client.GET('/api/v1/search', { params: { query: { q, limit } } })),

  system: {
    uplink: () => call(client.GET('/api/v1/system/uplink')),
    tasks: () => call(client.GET('/api/v1/system/tasks')),
    statusCodes: () => call(client.GET('/api/v1/system/status-codes')),
    sensorTypes: () => call(client.GET('/api/v1/system/sensor-types')),
    ruleTypes: () => call(client.GET('/api/v1/system/notification-rule-types')),
    /** *System health → Outbound* (docs/redmine-queue-outbound-plan.md §7, §8): the two logs, a record in full, what runs now. */
    outbound: {
      notifications: (query: ListQuery & OutboundNotificationFilters) =>
        call(client.GET('/api/v1/system/outbound/notifications', { params: { query } })),
      notification: (id: number) =>
        call(client.GET('/api/v1/system/outbound/notifications/{id}', { params: { path: { id } } })),
      redmine: (query: ListQuery & RedmineCallFilters) =>
        call(client.GET('/api/v1/system/outbound/redmine', { params: { query } })),
      redmineCall: (id: number) =>
        call(client.GET('/api/v1/system/outbound/redmine/{id}', { params: { path: { id } } })),
      live: () => call(client.GET('/api/v1/system/outbound/live')),
    },
  },

  hosts: {
    list: (query: ListQuery & { state?: HostState; locationId?: Id; noLocation?: boolean }) =>
      call(client.GET('/api/v1/hosts', { params: { query } })),
    get: (id: Id) => call(client.GET('/api/v1/hosts/{id}', { params: { path: { id } } })),
    /** Cookie's notes on the host the reader may see (docs/cookie-object-notes-plan.md §5). */
    cookieNotes: (id: Id) => call(client.GET('/api/v1/hosts/{id}/cookie-notes', { params: { path: { id } } })),
    create: (body: S['HostRequest']) => call(client.POST('/api/v1/hosts', { body })),
    update: (id: Id, body: S['HostRequest']) =>
      call(client.PUT('/api/v1/hosts/{id}', { params: { path: { id } }, body })),
    remove: (id: Id) => call(client.DELETE('/api/v1/hosts/{id}', { params: { path: { id } } })),
    /** Many hosts' location at once (docs/locations-knowledge-plan.md §3.2); null: none. */
    setLocation: (body: S['HostLocationRequest']) => call(client.PUT('/api/v1/hosts/location', { body })),
  },

  /** Locations (docs/locations-knowledge-plan.md §3, §9): every role reads, Users write, Admins delete. */
  locations: {
    list: (query: { search?: string; sort?: string } = {}) =>
      call(client.GET('/api/v1/locations', { params: { query } })),
    get: (id: Id, own = false) =>
      call(client.GET('/api/v1/locations/{id}', { params: { path: { id }, query: own ? { own } : {} } })),
    create: (body: S['LocationRequest']) => call(client.POST('/api/v1/locations', { body })),
    update: (id: Id, body: S['LocationRequest']) =>
      call(client.PUT('/api/v1/locations/{id}', { params: { path: { id } }, body })),
    move: (id: Id, parentId: Id | null) =>
      call(client.POST('/api/v1/locations/{id}/move', { params: { path: { id } }, body: { parentId } })),
    remove: (id: Id) => call(client.DELETE('/api/v1/locations/{id}', { params: { path: { id } } })),
    setValue: (id: Id, fieldId: Id, body: S['LocationValueRequest']) =>
      call(client.PUT('/api/v1/locations/{id}/values/{fieldId}', { params: { path: { id, fieldId } }, body })),
    history: (id: Id, query: ListQuery = {}) =>
      call(client.GET('/api/v1/locations/{id}/history', { params: { path: { id }, query } })),
    suggestions: () => call(client.GET('/api/v1/locations/suggestions')),
    fromText: (body: S['LocationFromTextRequest']) => call(client.POST('/api/v1/locations/from-text', { body })),
  },

  /** The location fields (docs/locations-knowledge-plan.md §4, §10.5): every role reads, Admins define. */
  locationFields: {
    list: () => call(client.GET('/api/v1/location-fields')),
    create: (body: S['LocationFieldRequest']) => call(client.POST('/api/v1/location-fields', { body })),
    update: (id: Id, body: S['LocationFieldRequest']) =>
      call(client.PUT('/api/v1/location-fields/{id}', { params: { path: { id } }, body })),
    order: (ids: Id[]) => call(client.PUT('/api/v1/location-fields/order', { body: { ids } })),
    archive: (id: Id) => call(client.POST('/api/v1/location-fields/{id}/archive', { params: { path: { id } } })),
    restore: (id: Id) => call(client.POST('/api/v1/location-fields/{id}/restore', { params: { path: { id } } })),
    remove: (id: Id) => call(client.DELETE('/api/v1/location-fields/{id}', { params: { path: { id } } })),
  },

  sensors: {
    list: (query: ListQuery & { hostId?: Id; type?: SensorType; state?: SensorState }) =>
      call(client.GET('/api/v1/sensors', { params: { query } })),
    get: (id: Id) => call(client.GET('/api/v1/sensors/{id}', { params: { path: { id } } })),
    cookieNotes: (id: Id) => call(client.GET('/api/v1/sensors/{id}/cookie-notes', { params: { path: { id } } })),
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
    /** The users and groups a rule can be sent to (docs/notification-recipients-plan.md §6.3): Monitoring's Edit. */
    people: () => call(client.GET('/api/v1/notification-rules/people')),
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
    list: (query: ListQuery & { groupId?: Id }) => call(client.GET('/api/v1/users', { params: { query } })),
    create: (body: S['CreateUserRequest']) => call(client.POST('/api/v1/users', { body })),
    update: (id: Id, body: S['UpdateUserRequest']) =>
      call(client.PUT('/api/v1/users/{id}', { params: { path: { id } }, body })),
    remove: (id: Id) => call(client.DELETE('/api/v1/users/{id}', { params: { path: { id } } })),
    resetPassword: (id: Id) => call(client.POST('/api/v1/users/{id}/reset-password', { params: { path: { id } } })),
    resetTotp: (id: Id) => call(client.POST('/api/v1/users/{id}/reset-totp', { params: { path: { id } } })),
    unlock: (id: Id) => call(client.POST('/api/v1/users/{id}/unlock', { params: { path: { id } } })),
    access: (id: Id) => call(client.GET('/api/v1/users/{id}/access', { params: { path: { id } } })),
    /** The person's contact details and their place in the availability plan (docs/cookie-plan.md §8.1). Admin. */
    profile: (id: Id) => call(client.GET('/api/v1/users/{id}/profile', { params: { path: { id } } })),
    saveProfile: (id: Id, body: S['UserProfileRequest']) =>
      call(client.PUT('/api/v1/users/{id}/profile', { params: { path: { id } }, body })),
    /** The person's switches for the lounge (docs/lounge-plan.md §9.4): Cookie and Seraph may talk about them, e-mail them. Admin. */
    lounge: (id: Id) => call(client.GET('/api/v1/users/{id}/lounge', { params: { path: { id } } })),
    saveLounge: (id: Id, body: S['LoungeSwitches']) =>
      call(client.PUT('/api/v1/users/{id}/lounge', { params: { path: { id } }, body })),
  },

  /** One's own contact details (docs/cookie-plan.md §8.1): the e-mail address and the Pushover key, and a test. */
  account: {
    contact: () => call(client.GET('/api/v1/account/contact')),
    saveContact: (body: S['AccountContactRequest']) => call(client.PUT('/api/v1/account/contact', { body })),
    /** A message to one's own saved address or key; 409 when there is none yet, or no provider. */
    testContact: (channel: 'email' | 'pushover') =>
      call(client.POST('/api/v1/account/contact/test', { body: { channel } })),
    /** One's own switches for the lounge (docs/lounge-plan.md §9.4): Cookie and Seraph may talk about one, and e-mail one. */
    lounge: () => call(client.GET('/api/v1/account/lounge')),
    saveLounge: (body: S['LoungeSwitches']) => call(client.PUT('/api/v1/account/lounge', { body })),
    /** One's own notification choices (docs/knowledge-base-extensions-plan.md §10.2): the kinds one's rights allow, by e-mail. */
    notifications: () => call(client.GET('/api/v1/account/notifications')),
    saveNotifications: (body: S['UserNotificationSettingsRequest']) =>
      call(client.PUT('/api/v1/account/notifications', { body })),
  },

  /**
   * The availability plan (docs/cookie-plan.md §8.2, §9.4): the table for Availability's View and everyone in the plan
   * (an empty plan for anyone else), one's own column from today on for everyone in it, the rest for its Edit; the people
   * and the settings for its Manage.
   */
  availability: {
    /** At most 400 days; without from and to, a week back and two months ahead. */
    plan: (query: { from?: string; to?: string } = {}) =>
      call(client.GET('/api/v1/availability/plan', { params: { query } })),
    saveCells: (cells: S['AvailabilityCellChange'][]) =>
      call(client.PUT('/api/v1/availability/entries', { body: { cells } })),
    /** Whole rows: the responsible, the fallback and the comment. */
    saveDays: (days: S['AvailabilityDayChange'][]) => call(client.PUT('/api/v1/availability/days', { body: { days } })),
    /** Fill, or Fill again with its options (docs/adjustments1.md): the days, the people, what may be replaced. */
    fill: (body: S['AvailabilityFillRequest']) => call(client.POST('/api/v1/availability/fill', { body })),
    fillMine: (body: S['AvailabilityFillRequest']) => call(client.POST('/api/v1/availability/fill/mine', { body })),
    people: () => call(client.GET('/api/v1/availability/people')),
    savePerson: (id: Id, body: S['AvailabilityPersonRequest']) =>
      call(client.PUT('/api/v1/availability/people/{id}', { params: { path: { id } }, body })),
    settings: () => call(client.GET('/api/v1/availability/settings')),
    saveSettings: (body: S['AvailabilitySettingsRequest']) =>
      call(client.PUT('/api/v1/availability/settings', { body })),
  },

  // --- groups and rights (docs/audit-redmine-zabbix-plan.md §5): admins; the catalog and one's own access everyone's ----
  groups: {
    list: () => call(client.GET('/api/v1/groups')),
    get: (id: Id) => call(client.GET('/api/v1/groups/{id}', { params: { path: { id } } })),
    create: (body: S['GroupRequest']) => call(client.POST('/api/v1/groups', { body })),
    update: (id: Id, body: S['GroupRequest']) =>
      call(client.PUT('/api/v1/groups/{id}', { params: { path: { id } }, body })),
    remove: (id: Id) => call(client.DELETE('/api/v1/groups/{id}', { params: { path: { id } } })),
    members: (id: Id, userIds: Id[]) =>
      call(client.PUT('/api/v1/groups/{id}/members', { params: { path: { id } }, body: { userIds } })),
  },
  // --- Redmine (docs/audit-redmine-zabbix-plan.md §6): tickets for Redmine's View, links for its Link ------------------------
  redmine: {
    status: () => call(client.GET('/api/v1/redmine/status')),
    lookups: () => call(client.GET('/api/v1/redmine/lookups')),
    overview: (project?: number) => call(client.GET('/api/v1/redmine/overview', { params: { query: { project } } })),
    issues: (query: ListQuery & RedmineFilters) => call(client.GET('/api/v1/redmine/issues', { params: { query } })),
    issue: (id: number) => call(client.GET('/api/v1/redmine/issues/{id}', { params: { path: { id } } })),
    /**
     * The refresh button (docs/redmine-queue-outbound-plan.md §5.3): the view's new data from Redmine, waited for up to
     * Redmine__WaitSeconds; `refreshing` still true (202) when it took longer.
     */
    refresh: (query: RedmineRefresh) => call(client.POST('/api/v1/redmine/refresh', { params: { query } })),
    related: (query: RedmineTarget & { deep?: boolean }) =>
      call(client.GET('/api/v1/redmine/related', { params: { query } })),
    problems: () => call(client.GET('/api/v1/redmine/problems')),
    link: (body: S['RedmineLinkRequest']) => call(client.POST('/api/v1/redmine/links', { body })),
    note: (id: Id, note: string | null) =>
      call(client.PUT('/api/v1/redmine/links/{id}', { params: { path: { id } }, body: { note } })),
    unlink: (id: Id) => call(client.DELETE('/api/v1/redmine/links/{id}', { params: { path: { id } } })),
    test: () => call(client.POST('/api/v1/redmine/test')),
  },
  // --- Zabbix (docs/audit-redmine-zabbix-plan.md §7, §9) --------------------------------------------
  zabbix: {
    status: () => call(client.GET('/api/v1/zabbix/status')),
    summary: (category?: string, suppressed?: boolean) =>
      call(client.GET('/api/v1/zabbix/summary', { params: { query: { category, suppressed } } })),
    problems: (query: ListQuery & ZabbixProblemFilters) =>
      call(client.GET('/api/v1/zabbix/problems', { params: { query } })),
    problem: (eventId: string) =>
      call(client.GET('/api/v1/zabbix/problems/{eventId}', { params: { path: { eventId } } })),
    hosts: (query: ListQuery & ZabbixHostFilters) => call(client.GET('/api/v1/zabbix/hosts', { params: { query } })),
    host: (hostId: number) => call(client.GET('/api/v1/zabbix/hosts/{hostId}', { params: { path: { hostId } } })),
    items: (hostId: number, search?: string) =>
      call(client.GET('/api/v1/zabbix/hosts/{hostId}/items', { params: { path: { hostId }, query: { search } } })),
    suggestions: () => call(client.GET('/api/v1/zabbix/suggestions')),
    attach: (items: S['ZabbixAttachItem'][]) => call(client.POST('/api/v1/zabbix/attach', { body: { items } })),
    test: () => call(client.POST('/api/v1/zabbix/test')),
  },
  rights: {
    catalog: () => call(client.GET('/api/v1/rights')),
    mine: () => call(client.GET('/api/v1/account/access')),
  },

  // --- the audit log (docs/audit-redmine-zabbix-plan.md §4.2) ------------------------------------
  audit: {
    log: (query: ListQuery & AuditFilters) => call(client.GET('/api/v1/audit-log', { params: { query } })),
    types: () => call(client.GET('/api/v1/audit-log/types')),
    /** An object's history (§4.3): its own entries and what belongs to it, without addresses. */
    subject: (id: Id, query: ListQuery) =>
      call(client.GET('/api/v1/audit-log/subjects/{id}', { params: { path: { id }, query } })),
    /** The CSV export of what the filters show (§4.2): a plain link, the browser downloads it. */
    exportUrl: (filters: AuditFilters) => {
      const params = new URLSearchParams()
      for (const [k, v] of Object.entries(filters)) {
        for (const item of Array.isArray(v) ? v : [v])
          if (item !== undefined && item !== null && item !== '') params.append(k, String(item))
      }
      return `${typeof location !== 'undefined' ? location.origin : ''}/api/v1/audit-log/export${params.size ? `?${params}` : ''}`
    },
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
    // --- its logins (docs/cookie-access-plan.md §4.2, §10.4): Monitoring's Details; the switch Cookie's Manage ------
    /** The router's Access tab: its switch, users, who is logged in now, the gap, and its logins, paged. */
    logins: (
      id: Id,
      query: ListQuery & {
        outcome?: S['LogonOutcome']
        admin?: boolean
        kind?: S['LogonKind']
        user?: string
        source?: string
      },
    ) => call(client.GET('/api/v1/sensors/{id}/mikrotik/logins', { params: { path: { id }, query } })),
    setAccessWatch: (id: Id, watch: boolean) =>
      call(client.PUT('/api/v1/sensors/{id}/mikrotik/access-watch', { params: { path: { id } }, body: { watch } })),
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
    /** The Pushover priorities (docs/cookie-object-notes-plan.md §5): Monitoring's View reads, Edit saves. */
    settings: () => call(client.GET('/api/v1/notifications/settings')),
    updateSettings: (body: S['NotificationSettingsRequest']) =>
      call(client.PUT('/api/v1/notifications/settings', { body })),
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
  },

  /**
   * Cookie (docs/cookie-plan.md §19.4): the status every signed-in user's, the cases Cookie's Use, each filtered by the
   * reader's rights; Admin → Cookie (§9.3) Cookie's Manage.
   */
  /**
   * MCP (docs/mcp-plan.md §4.5, §10): one's own two tokens for the dashboard's card, and Admin → MCP. A token's value comes
   * back once, when it is made; never again.
   */
  mcp: {
    account: () => call(client.GET('/api/v1/account/mcp')),
    turnOn: (kind: S['McpTokenKind']) =>
      call(client.POST('/api/v1/account/mcp/{kind}/on', { params: { path: { kind } } })),
    turnOff: (kind: S['McpTokenKind']) =>
      call(client.POST('/api/v1/account/mcp/{kind}/off', { params: { path: { kind } } })),
    regenerate: (kind: S['McpTokenKind']) =>
      call(client.POST('/api/v1/account/mcp/{kind}/regenerate', { params: { path: { kind } } })),
    /** "Allow deleting for 10 minutes" (§7.5), and Stop now. */
    openDeleteWindow: () => call(client.POST('/api/v1/account/mcp/write/delete-window')),
    closeDeleteWindow: () => call(client.DELETE('/api/v1/account/mcp/write/delete-window')),
    myLog: (query: ListQuery & { tokenId?: Id; kind?: S['McpTokenKind']; outcome?: S['McpOutcome'] }) =>
      call(client.GET('/api/v1/account/mcp/log', { params: { query } })),
    /** The documentation's data, from the catalog itself: every signed-in user. */
    catalog: () => call(client.GET('/api/v1/mcp/catalog')),
    /** Everyone's log: MCP's View. */
    log: (
      query: ListQuery & {
        userId?: Id
        tokenId?: Id
        kind?: S['McpTokenKind']
        tool?: string
        outcome?: S['McpOutcome']
        from?: string
        to?: string
      },
    ) => call(client.GET('/api/v1/mcp/log', { params: { query } })),
    /** An entry with its arguments, result and changes: MCP's View, or one's own. */
    entry: (id: number) => call(client.GET('/api/v1/mcp/log/{id}', { params: { path: { id } } })),
    tokens: () => call(client.GET('/api/v1/mcp/tokens')),
    /** Admin → MCP → Status: the tokens and the calls at a glance (MCP's View). */
    status: () => call(client.GET('/api/v1/mcp/status')),
    /** Every token's value stops working for good (MCP's Manage). */
    revokeAll: (reason: string) => call(client.POST('/api/v1/mcp/tokens/revoke-all', { body: { reason } })),
    /** MCP's Manage: with a reason the token's user reads. */
    turnOffToken: (id: Id, reason: string) =>
      call(client.POST('/api/v1/mcp/tokens/{id}/off', { params: { path: { id } }, body: { reason } })),
  },
  cookie: {
    status: () => call(client.GET('/api/v1/cookie/status')),
    /** The reader's personal summary (§8): written when asked, kept for 15 minutes. */
    summary: () => call(client.GET('/api/v1/cookie/summary')),
    /** The trace by the reader's rights (§9.2), newest first; afterId for what came since. */
    trace: (query: { caseId?: Id; roundId?: number; kind?: string; afterId?: number; limit?: number }) =>
      call(client.GET('/api/v1/cookie/trace', { params: { query } })),
    /** The reader's chat with Cookie (§10.2); afterId for what came since. */
    messages: (afterId?: number) => call(client.GET('/api/v1/cookie/messages', { params: { query: { afterId } } })),
    /** A message to Cookie, or to Seraph (§21.1), who explains its verdicts and can't act. */
    post: (text: string, to: 'cookie' | 'seraph' = 'cookie') =>
      call(client.POST('/api/v1/cookie/messages', { body: { text, to } })),
    /** A message with Cookie's powers (docs/cookie-powers-plan.md §4): admins only, audited; Cookie acts on it. */
    postEmpowered: (text: string) => call(client.POST('/api/v1/cookie/messages/empowered', { body: { text } })),
    cases: (query: ListQuery & { open?: boolean; kind?: S['CookieCaseKind'] }) =>
      call(client.GET('/api/v1/cookie/cases', { params: { query } })),
    case: (id: Id) => call(client.GET('/api/v1/cookie/cases/{id}', { params: { path: { id } } })),
    /** A comment on a case (§4.3), for Cookie: Use; Cookie reads it in its next round. */
    comment: (id: Id, text: string) =>
      call(client.POST('/api/v1/cookie/cases/{id}/comments', { params: { path: { id } }, body: { text } })),
    /** "Don't go on with this", with a comment that says why (Cookie: Instruct). */
    stopCase: (id: Id, text: string) =>
      call(client.POST('/api/v1/cookie/cases/{id}/stop', { params: { path: { id } }, body: { text } })),
    goOn: (id: Id, text: string | null) =>
      call(client.POST('/api/v1/cookie/cases/{id}/go-on', { params: { path: { id } }, body: { text } })),
    /** Ask the big brother (§15.2, Cookie: Instruct): the next round calls it. 409 when it is off or Cookie is stopped. */
    askBigBrother: (id: Id) => call(client.POST('/api/v1/cookie/cases/{id}/big-brother', { params: { path: { id } } })),
    /** Cookie's decision log (§11, §21.1), by the reader's rights, newest first. */
    decisions: (query: ListQuery & { caseId?: Id; kind?: string; outcome?: string }) =>
      call(client.GET('/api/v1/cookie/decisions', { params: { query } })),
    /** Right or wrong with a comment; a null rating takes it back. */
    rate: (id: number, body: S['CookieRatingRequest']) =>
      call(client.PUT('/api/v1/cookie/decisions/{id}/rating', { params: { path: { id } }, body })),
    /** Confirm a draft Cookie filled in from one's message (§10.3, Cookie: Instruct). */
    confirmInstruction: (id: Id) =>
      call(client.POST('/api/v1/cookie/instructions/{id}/confirm', { params: { path: { id } } })),
    /** End a mute, a suppress or an instruction now (Cookie: Instruct). */
    endInstruction: (id: Id) => call(client.POST('/api/v1/cookie/instructions/{id}/end', { params: { path: { id } } })),
    // --- Admin → Cookie: Cookie's Manage ----------------------------------------------------------------------------
    overview: () => call(client.GET('/api/v1/cookie/admin/overview')),
    start: () => call(client.POST('/api/v1/cookie/admin/start')),
    stop: () => call(client.POST('/api/v1/cookie/admin/stop')),
    run: () => call(client.POST('/api/v1/cookie/admin/run')),
    rounds: () => call(client.GET('/api/v1/cookie/admin/rounds')),
    /** A prompt as the model receives it: a prompt's key, or "case:" and a kind of case's key. */
    preview: (key: string) => call(client.GET('/api/v1/cookie/admin/prompts/preview', { params: { query: { key } } })),
    /** The users and groups Admin → Cookie names, for Cookie's Manage (Admin → Users is admins' only). */
    people: () => call(client.GET('/api/v1/cookie/admin/people')),
    settings: () => call(client.GET('/api/v1/cookie/admin/settings')),
    defaults: () => call(client.GET('/api/v1/cookie/admin/defaults')),
    /** Redmine's projects, roles, statuses and trackers for the pickers, and whether Redmine can be written. */
    redmine: () => call(client.GET('/api/v1/cookie/admin/redmine')),
    /** Each section as a whole: a new version; a 400 names the fields. */
    save: {
      identity: (body: S['IdentitySettings']) => call(client.PUT('/api/v1/cookie/admin/settings/identity', { body })),
      prompts: (body: S['PromptSettings']) => call(client.PUT('/api/v1/cookie/admin/settings/prompts', { body })),
      cases: (body: S['CaseSettings']) => call(client.PUT('/api/v1/cookie/admin/settings/cases', { body })),
      redmine: (body: S['CookieRedmineSettings']) =>
        call(client.PUT('/api/v1/cookie/admin/settings/redmine', { body })),
      actions: (body: S['ActionSettings']) => call(client.PUT('/api/v1/cookie/admin/settings/actions', { body })),
      models: (body: S['ModelSettings']) => call(client.PUT('/api/v1/cookie/admin/settings/models', { body })),
      rights: (body: S['CookieRightsSettings']) => call(client.PUT('/api/v1/cookie/admin/settings/rights', { body })),
      lessons: (body: S['LessonSettings']) => call(client.PUT('/api/v1/cookie/admin/settings/lessons', { body })),
      access: (body: S['AccessSettings']) => call(client.PUT('/api/v1/cookie/admin/settings/access', { body })),
      lounge: (body: S['LoungeSettings']) => call(client.PUT('/api/v1/cookie/admin/settings/lounge', { body })),
    },
    /** Mute, suppress and instructions (§10.3): the active ones and drafts, then the last 30 days'. */
    instructions: () => call(client.GET('/api/v1/cookie/admin/instructions')),
    /** A new one, confirmed at once. */
    createInstruction: (body: S['CookieInstructionRequest']) =>
      call(client.POST('/api/v1/cookie/admin/instructions', { body })),
    versions: (section: string) =>
      call(client.GET('/api/v1/cookie/admin/settings/{section}/versions', { params: { path: { section } } })),
    /** A version's values as saved: raw JSON. */
    version: (section: string, version: number) =>
      call(
        client.GET('/api/v1/cookie/admin/settings/{section}/versions/{version}', {
          params: { path: { section, version } },
        }),
      ),
    /** The old version's values, saved as a new version. */
    rollback: (section: string, version: number) =>
      call(
        client.POST('/api/v1/cookie/admin/settings/{section}/rollback/{version}', {
          params: { path: { section, version } },
        }),
      ),
    outbox: (query: ListQuery & { state?: S['CookieOutboxState']; channel?: S['CookieChannel'] }) =>
      call(client.GET('/api/v1/cookie/admin/outbox', { params: { query } })),
    /** What a limit held back, sent now (409 for anything else). */
    send: (id: number) => call(client.POST('/api/v1/cookie/admin/outbox/{id}/send', { params: { path: { id } } })),
    // --- the access watch (docs/cookie-access-plan.md §10.3) ---------------------------------------------------------
    /** An access case's own part: its logins, verdict, answer, notices and links (Cookie: Use and the target's right). */
    accessCase: (id: Id) => call(client.GET('/api/v1/cookie/access/cases/{id}', { params: { path: { id } } })),
    /** Expected (with Remember this) or Not expected (§7): a contact for the target, or Cookie: Instruct. */
    answerAccess: (id: Id, body: S['AccessAnswerRequest']) =>
      call(client.POST('/api/v1/cookie/access/cases/{id}/answer', { params: { path: { id } }, body })),
    /** Ask Cookie for a ticket (§7): Cookie: Instruct and Redmine: Link; 409 when closed or Cookie is stopped. */
    /** A person removes one of Cookie's notes on a host or sensor (docs/cookie-object-notes-plan.md §3.6): Monitoring: Edit. */
    removeObjectNote: (id: number) =>
      call(client.DELETE('/api/v1/cookie/object-notes/{id}', { params: { path: { id } } })),
    askAccessTicket: (id: Id) =>
      call(client.POST('/api/v1/cookie/access/cases/{id}/ticket', { params: { path: { id } } })),
    /** This wasn't expected, on a server's or router's successful login (Monitoring: Details): its case. */
    notExpected: (loginId: Id, comment: string | null) =>
      call(
        client.POST('/api/v1/cookie/access/logins/{id}/not-expected', {
          params: { path: { id: loginId } },
          body: { comment },
        }),
      ),
    // Admin → Cookie → Access (§8): Cookie's Manage.
    accessState: () => call(client.GET('/api/v1/cookie/admin/access/state')),
    accessKnown: () => call(client.GET('/api/v1/cookie/admin/access/known')),
    addAccessKnown: (body: S['AccessKnownRequest']) => call(client.POST('/api/v1/cookie/admin/access/known', { body })),
    endAccessKnown: (id: Id) =>
      call(client.POST('/api/v1/cookie/admin/access/known/{id}/end', { params: { path: { id } } })),
    accessRouters: () => call(client.GET('/api/v1/cookie/admin/access/routers')),
    /** Many routers' switches at once (§4.2): all of a location, all on, all off. */
    switchAccessRouters: (sensorIds: Id[], watch: boolean) =>
      call(client.PUT('/api/v1/cookie/admin/access/routers', { body: { sensorIds, watch } })),
  },

  /**
   * The lounge (docs/lounge-plan.md §14): one conversation for every signed-in user; writing needs Lounge: Use and a started
   * lounge. Admin → Cookie → Lounge is Cookie's Manage.
   */
  lounge: {
    /** The newest 100 lines and the state; afterId for what came since. */
    room: (afterId?: number) => call(client.GET('/api/v1/lounge', { params: { query: { afterId } } })),
    /** A message (the voices answer it) or a comment (they take it up), on a message or on none. 409 closed, 429 over 20 an hour. */
    post: (body: S['LoungePostRequest']) => call(client.POST('/api/v1/lounge/messages', { body })),
    /** The side panel: the keepsakes, and the convictions with both positions and the last move. */
    about: () => call(client.GET('/api/v1/lounge/about')),
    // --- Admin → Cookie → Lounge: Cookie's Manage -----------------------------------------------------------------------
    status: () => call(client.GET('/api/v1/cookie/admin/lounge')),
    /** Start, 409 with the reason without a group, Cookie__Enabled or Seraph's model; Stop, the room stays readable. */
    start: () => call(client.POST('/api/v1/cookie/admin/lounge/start')),
    stop: () => call(client.POST('/api/v1/cookie/admin/lounge/stop')),
    /** A scene now, awake or not (not past the budget). */
    talk: () => call(client.POST('/api/v1/cookie/admin/lounge/talk')),
    /** Asleep until the next awake period. */
    sleep: () => call(client.POST('/api/v1/cookie/admin/lounge/sleep')),
    scenes: () => call(client.GET('/api/v1/cookie/admin/lounge/scenes')),
    scene: (id: number) => call(client.GET('/api/v1/cookie/admin/lounge/scenes/{id}', { params: { path: { id } } })),
    /** Keepsakes first, then the memories by their strength now; one voice, or both without. */
    memories: (voice?: string) =>
      call(client.GET('/api/v1/cookie/admin/lounge/memories', { params: { query: { voice } } })),
    forgetMemory: (id: number) =>
      call(client.DELETE('/api/v1/cookie/admin/lounge/memories/{id}', { params: { path: { id } } })),
    /** Forget everything: one voice, or both with null. */
    forget: (voice: string | null) => call(client.POST('/api/v1/cookie/admin/lounge/forget', { body: { voice } })),
    /** Remove a message or a comment, and the memories made from it. */
    removeMessage: (id: number) =>
      call(client.DELETE('/api/v1/cookie/admin/lounge/messages/{id}', { params: { path: { id } } })),
    /** The convictions' history. */
    opinions: () => call(client.GET('/api/v1/cookie/admin/lounge/opinions')),
    resetPositions: () => call(client.POST('/api/v1/cookie/admin/lounge/positions/reset')),
  },

  /**
   * Was this you? (docs/cookie-access-plan.md §6.6): the access watch's only page without sign-in. Opening it answers
   * nothing; only the page's buttons do. A link used, expired or unknown: open false, or 409 on the answer.
   */
  wasThisYou: {
    view: (token: string) => call(client.GET('/api/v1/access/was-this-you/{token}', { params: { path: { token } } })),
    answer: (token: string, itWasMe: boolean) =>
      call(client.POST('/api/v1/access/was-this-you/{token}', { params: { path: { token } }, body: { itWasMe } })),
  },

  /**
   * Servers with an agent, Windows and Linux (docs/windows-sensor-plan.md §9.2; docs/linux-sensor-plan.md §10): the fleet
   * CanRead, requests CanWrite, tokens, the agent log, the downloads and the scripts Admin.
   */
  agents: {
    servers: (
      query: ListQuery & {
        platform?: S['AgentPlatform']
        state?: SensorState
        unsupported?: boolean
        outdated?: boolean
        connected?: boolean
        remoteAccess?: boolean
        auditGaps?: boolean
        diagnosticsFailing?: boolean
        securityUpdates?: boolean
        keyRemoval?: boolean
        unregisteredKeys?: boolean
        retiredKeys?: boolean
        hostId?: Id
      },
    ) => call(client.GET('/api/v1/agents/servers', { params: { query } })),
    server: (id: Id) => call(client.GET('/api/v1/agents/servers/{id}', { params: { path: { id } } })),
    logins: (
      id: Id,
      query: ListQuery & {
        outcome?: S['LogonOutcome']
        admin?: boolean
        kind?: S['LogonKind']
        keyFingerprint?: string
        keyOwner?: string
        user?: string
        source?: string
        from?: string
        to?: string
      },
    ) => call(client.GET('/api/v1/agents/servers/{id}/logins', { params: { path: { id }, query } })),
    changes: (id: Id, query: ListQuery & { section?: string; notable?: boolean }) =>
      call(client.GET('/api/v1/agents/servers/{id}/changes', { params: { path: { id }, query } })),
    serverLog: (id: Id, query: ListQuery & { kind?: S['AgentLogKind'] }) =>
      call(client.GET('/api/v1/agents/servers/{id}/log', { params: { path: { id }, query } })),
    collect: (id: Id, sections: string[] = []) =>
      call(client.POST('/api/v1/agents/servers/{id}/collect', { params: { path: { id } }, body: { sections } })),
    sendLog: (id: Id) => call(client.POST('/api/v1/agents/servers/{id}/send-log', { params: { path: { id } } })),
    settings: (id: Id, body: S['AgentServerSettings']) =>
      call(client.PUT('/api/v1/agents/servers/{id}/settings', { params: { path: { id } }, body })),
    revoke: (id: Id, reason: string | null) =>
      call(client.POST('/api/v1/agents/servers/{id}/revoke', { params: { path: { id } }, body: { reason } })),
    rejoinToken: (id: Id, validHours: number | null = null) =>
      call(client.POST('/api/v1/agents/servers/{id}/rejoin-token', { params: { path: { id } }, body: { validHours } })),
    tokens: (query: ListQuery & { state?: S['AgentJoinTokenState'] }) =>
      call(client.GET('/api/v1/agents/join-tokens', { params: { query } })),
    createToken: (body: S['AgentJoinTokenRequest']) => call(client.POST('/api/v1/agents/join-tokens', { body })),
    revokeToken: (id: Id) => call(client.DELETE('/api/v1/agents/join-tokens/{id}', { params: { path: { id } } })),
    agentLog: (query: ListQuery & { kind?: S['AgentLogKind']; sensorId?: Id }) =>
      call(client.GET('/api/v1/agents/agent-log', { params: { query } })),
    /** A Linux server's SSH keys with their registrations, last use and removability (docs/linux-sensor-plan.md §6.3). CanWrite. */
    serverSshKeys: (id: Id) => call(client.GET('/api/v1/agents/servers/{id}/ssh-keys', { params: { path: { id } } })),
    /** The accounts a Linux server's config.yaml lets scripts run as (§8.4). Admin. */
    runAsAccounts: (id: Id) =>
      call(client.GET('/api/v1/agents/servers/{id}/run-as-accounts', { params: { path: { id } } })),
    downloads: () => call(client.GET('/api/v1/agents/downloads')),
    /** Fetch again (docs/linux-sensor-plan.md §4.4): the files named, or every one that isn't here and verified. */
    fetchDownloads: (files: string[] | null = null) =>
      call(client.POST('/api/v1/agents/downloads/fetch', { body: { files } })),
    // --- the agents' versions (docs/agent-update-plan.md §3, §4, §9): Servers: Manage, uploads admins only ------
    /** The versions here and in GitHub's list, the version each platform's agents get; refresh reads GitHub's list again. */
    versions: (refresh = false) => call(client.GET('/api/v1/agents/versions', { params: { query: { refresh } } })),
    /** Null for PNMT's own; one newer than PNMT needs confirmNewer (409 newer_than_pnmt otherwise). Starts no update. */
    chooseVersions: (body: S['AgentVersionChooseRequest']) =>
      call(client.PUT('/api/v1/agents/versions/chosen', { body })),
    fetchVersion: (version: string, files: string[] | null = null) =>
      call(client.POST('/api/v1/agents/versions/{version}/fetch', { params: { path: { version } }, body: { files } })),
    /** Multipart (file, name, sha256); admins only. */
    uploadVersionFile: (version: string, form: { file: File; name: string; sha256?: string | null }) => {
      const data = new FormData()
      data.append('file', form.file, form.file.name)
      data.append('name', form.name)
      if (form.sha256) {
        data.append('sha256', form.sha256)
      }
      return call(
        client.POST('/api/v1/agents/versions/{version}/files', {
          params: { path: { version } },
          body: {},
          bodySerializer: () => data,
        }),
      )
    },
    deleteVersion: (version: string) =>
      call(client.DELETE('/api/v1/agents/versions/{version}', { params: { path: { version } } })),
    /** A file of a version, or its .sha256, for a download link (a GET with the session cookie). */
    versionFileUrl: (version: string, file: string) =>
      `${typeof location !== 'undefined' ? location.origin : ''}/api/v1/agents/versions/${encodeURIComponent(version)}/files/${encodeURIComponent(file)}`,
    // --- scripts (docs/windows-sensor-plan.md §7; docs/linux-sensor-plan.md §8): Admin --------------------------
    scripts: (query: ListQuery & { language?: S['ScriptLanguage'] }) =>
      call(client.GET('/api/v1/agents/scripts', { params: { query } })),
    scriptTemplate: (language: S['ScriptLanguage']) =>
      call(client.GET('/api/v1/agents/scripts/template', { params: { query: { language } } })),
    script: (id: Id) => call(client.GET('/api/v1/agents/scripts/{id}', { params: { path: { id } } })),
    createScript: (body: S['AgentScriptRequest']) => call(client.POST('/api/v1/agents/scripts', { body })),
    updateScript: (id: Id, body: S['AgentScriptRequest']) =>
      call(client.PUT('/api/v1/agents/scripts/{id}', { params: { path: { id } }, body })),
    parseScript: (id: Id) => call(client.POST('/api/v1/agents/scripts/{id}/parse', { params: { path: { id } } })),
    deleteScript: (id: Id) => call(client.DELETE('/api/v1/agents/scripts/{id}', { params: { path: { id } } })),
    runScript: (id: Id, body: S['AgentScriptRunRequest']) =>
      call(client.POST('/api/v1/agents/scripts/{id}/run', { params: { path: { id } }, body })),
    scriptRuns: (
      query: ListQuery & {
        scriptId?: Id
        sensorId?: Id
        batchId?: Id
        state?: S['ScriptRunState']
        connectorId?: Id
        viaConnectors?: boolean
      },
    ) => call(client.GET('/api/v1/agents/script-runs', { params: { query } })),
    scriptRun: (id: Id) => call(client.GET('/api/v1/agents/script-runs/{id}', { params: { path: { id } } })),
    redactRun: (id: Id) => call(client.POST('/api/v1/agents/script-runs/{id}/redact', { params: { path: { id } } })),
  },

  /**
   * What stays Windows' own (docs/linux-sensor-plan.md §3.3): a Windows server's typed sections (CanRead; access and
   * event-log CanWrite), its WMI, the scripts registered on it, and the run-as accounts (Admin).
   */
  windows: {
    section: (id: Id, section: string) =>
      call(client.GET('/api/v1/windows/servers/{id}/sections/{section}', { params: { path: { id, section } } })),
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
  },

  /**
   * What is Linux's own (docs/linux-sensor-plan.md §10): a Linux server's typed sections (CanRead; access, journal and ssh-keys
   * CanWrite).
   */
  linux: {
    section: (id: Id, section: string) =>
      call(client.GET('/api/v1/linux/servers/{id}/sections/{section}', { params: { path: { id, section } } })),
    /**
     * OS updates (docs/linux-os-updates-plan.md §9): a server's state, runs, logs and changelogs for Monitoring: View; refreshing
     * the lists, installing updates, marking a failure handled and acknowledging findings for Servers: Run; the server's switches,
     * the advisory sources and texts for Servers: Manage; the dashboard's summary for Monitoring: View.
     */
    osUpdates: {
      get: (id: Id) => call(client.GET('/api/v1/linux/servers/{id}/os-updates', { params: { path: { id } } })),
      runs: (id: Id, query: ListQuery & { kind?: S['OsUpdateKind'] }) =>
        call(client.GET('/api/v1/linux/servers/{id}/os-updates/runs', { params: { path: { id }, query } })),
      run: (id: Id, runId: Id) =>
        call(client.GET('/api/v1/linux/servers/{id}/os-updates/runs/{runId}', { params: { path: { id, runId } } })),
      /** The log from a byte offset, for the live view: append its text, ask again from nextOffset. */
      log: (id: Id, runId: Id, from = 0) =>
        call(
          client.GET('/api/v1/linux/servers/{id}/os-updates/runs/{runId}/log', {
            params: { path: { id, runId }, query: { from } },
          }),
        ),
      /** A plain link: the browser downloads the whole log with the session cookie. */
      logUrl: (id: Id, runId: Id) =>
        `${typeof location !== 'undefined' ? location.origin : ''}/api/v1/linux/servers/${encodeURIComponent(id)}/os-updates/runs/${encodeURIComponent(runId)}/log/download`,
      /** A pending package's changelog part (by package or source name); 404 while the server sent none. */
      changelog: (id: Id, pkg: string) =>
        call(
          client.GET('/api/v1/linux/servers/{id}/os-updates/changelogs/{package}', {
            params: { path: { id, package: pkg } },
          }),
        ),
      /** *Refresh lists*: apt-get update now. */
      update: (id: Id) =>
        call(client.POST('/api/v1/linux/servers/{id}/os-updates/update', { params: { path: { id } } })),
      /** *Install updates…*: a full upgrade carries the removals' hash the person saw. */
      upgrade: (id: Id, body: S['OsUpdateUpgradeRequest']) =>
        call(client.POST('/api/v1/linux/servers/{id}/os-updates/upgrade', { params: { path: { id } }, body })),
      /** *Mark handled*: a failed upgrade's 467 ends. */
      dismiss: (id: Id, runId: Id) =>
        call(
          client.POST('/api/v1/linux/servers/{id}/os-updates/runs/{runId}/dismiss', {
            params: { path: { id, runId } },
          }),
        ),
      settings: (id: Id, body: S['OsUpdateServerSettings']) =>
        call(client.PUT('/api/v1/linux/servers/{id}/os-updates/settings', { params: { path: { id } }, body })),
      acknowledge: (id: Id, body: S['OsFindingAcknowledgeRequest']) =>
        call(
          client.POST('/api/v1/linux/servers/{id}/os-updates/findings/acknowledge', { params: { path: { id } }, body }),
        ),
      unacknowledge: (id: Id, findingId: Id) =>
        call(
          client.DELETE('/api/v1/linux/servers/{id}/os-updates/findings/{findingId}/acknowledgement', {
            params: { path: { id, findingId } },
          }),
        ),
      /** The dashboard's card (§10.3). */
      summary: () => call(client.GET('/api/v1/linux/os-updates/summary')),
      // --- Admin → Servers → Advisories (§10.4): Servers: Manage ------------------------------------------------------
      sources: () => call(client.GET('/api/v1/linux/os-updates/sources')),
      createSource: (body: S['OsAdvisorySourceRequest']) =>
        call(client.POST('/api/v1/linux/os-updates/sources', { body })),
      updateSource: (sourceId: Id, body: S['OsAdvisorySourceRequest']) =>
        call(client.PUT('/api/v1/linux/os-updates/sources/{sourceId}', { params: { path: { sourceId } }, body })),
      deleteSource: (sourceId: Id) =>
        call(client.DELETE('/api/v1/linux/os-updates/sources/{sourceId}', { params: { path: { sourceId } } })),
      /** Fetched on the fetcher's next pass, within a minute (202). */
      fetchSource: (sourceId: Id) =>
        call(client.POST('/api/v1/linux/os-updates/sources/{sourceId}/fetch', { params: { path: { sourceId } } })),
      advisories: (query: ListQuery) => call(client.GET('/api/v1/linux/os-updates/advisories', { params: { query } })),
    },
  },

  /**
   * The SSH key manager (docs/linux-sensor-plan.md §6.4, §6.5, §11.4): the register, the keys found unregistered on servers, and
   * removals. Admin and audited; mine for every role.
   */
  sshKeys: {
    list: (query: ListQuery & { owner?: string; kind?: S['SshKeyKind']; state?: S['SshKeyState']; filter?: string }) =>
      call(client.GET('/api/v1/ssh-keys', { params: { query } })),
    summary: () => call(client.GET('/api/v1/ssh-keys/summary')),
    unregistered: (query: ListQuery & { sensorId?: Id; filter?: string }) =>
      call(client.GET('/api/v1/ssh-keys/unregistered', { params: { query } })),
    /** One or more public keys, line by line: fingerprints, names, and what would be refused. Stores and logs nothing. */
    parse: (text: string) => call(client.POST('/api/v1/ssh-keys/parse', { body: { text } })),
    register: (body: S['SshKeyRegisterRequest']) => call(client.POST('/api/v1/ssh-keys', { body })),
    get: (id: Id) => call(client.GET('/api/v1/ssh-keys/{id}', { params: { path: { id } } })),
    update: (id: Id, body: S['SshKeyUpdateRequest']) =>
      call(client.PUT('/api/v1/ssh-keys/{id}', { params: { path: { id } }, body })),
    remove: (id: Id) => call(client.DELETE('/api/v1/ssh-keys/{id}', { params: { path: { id } } })),
    retire: (id: Id) => call(client.POST('/api/v1/ssh-keys/{id}/retire', { params: { path: { id } } })),
    reactivate: (id: Id) => call(client.POST('/api/v1/ssh-keys/{id}/reactivate', { params: { path: { id } } })),
    /** The removal preview for every place a key is authorized. */
    removalItems: (id: Id) => call(client.POST('/api/v1/ssh-keys/{id}/removal-items', { params: { path: { id } } })),
    user: (userId: Id) => call(client.GET('/api/v1/ssh-keys/users/{userId}', { params: { path: { userId } } })),
    userRemovalItems: (userId: Id) =>
      call(client.POST('/api/v1/ssh-keys/users/{userId}/removal-items', { params: { path: { userId } } })),
    /** Offboarding: retires the user's keys and removes each from every server; confirm is the number of removals. */
    offboard: (userId: Id, confirm: number) =>
      call(
        client.POST('/api/v1/ssh-keys/users/{userId}/offboard', { params: { path: { userId } }, body: { confirm } }),
      ),
    preview: (items: S['SshKeyRemovalTarget'][]) =>
      call(client.POST('/api/v1/ssh-keys/remove/preview', { body: { items } })),
    removeKeys: (body: S['SshKeyRemoveRequest']) => call(client.POST('/api/v1/ssh-keys/remove', { body })),
    mine: () => call(client.GET('/api/v1/ssh-keys/mine')),
    removals: (
      query: ListQuery & {
        sensorId?: Id
        fingerprint?: string
        owner?: string
        state?: S['SshKeyRemovalState']
        reason?: S['SshKeyRemovalReason']
        batchId?: Id
      },
    ) => call(client.GET('/api/v1/ssh-key-removals', { params: { query } })),
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

  /**
   * An SSL certificate sensor's certificate (docs/ssl-certificate-plan.md §6.3): reading Certificates' View, Check now and
   * accepting a change its Edit. Its settings go through /sensors (SensorRequest.certificate).
   */
  certificates: {
    get: (id: Id) => call(client.GET('/api/v1/sensors/{id}/certificate', { params: { path: { id } } })),
    /** Checks now and answers once the check is done (at most 30 s). */
    check: (id: Id) => call(client.POST('/api/v1/sensors/{id}/certificate/check', { params: { path: { id } } })),
    /**
     * §8.4: the SHA-256 the person saw and confirmed: true. 400 confirmation_required without it; 409
     * certificate_changed_again when that is no longer the current certificate, 409 no_change without a change.
     */
    accept: (id: Id, body: S['CertificateAcceptRequest']) =>
      call(client.POST('/api/v1/sensors/{id}/certificate/accept', { params: { path: { id } }, body })),
    /** The certificates seen, newest first. */
    versions: (id: Id, query: ListQuery = {}) =>
      call(client.GET('/api/v1/sensors/{id}/certificate/versions', { params: { path: { id }, query } })),
    version: (id: Id, versionId: Id) =>
      call(
        client.GET('/api/v1/sensors/{id}/certificate/versions/{versionId}', { params: { path: { id, versionId } } }),
      ),
    /** The leaf's PEM, or with chain the chain as the server sent it, as text (Copy PEM, a root to trust). */
    pem: (id: Id, versionId: Id, chain = false) =>
      call(
        client.GET('/api/v1/sensors/{id}/certificate/versions/{versionId}/pem', {
          params: { path: { id, versionId }, query: { chain } },
          parseAs: 'text',
        }),
      ) as Promise<string>,
    /** A plain link: the browser downloads the file with the session cookie. */
    pemUrl: (id: Id, versionId: Id, chain = false) =>
      `${typeof location !== 'undefined' ? location.origin : ''}/api/v1/sensors/${encodeURIComponent(id)}/certificate/versions/${encodeURIComponent(versionId)}/pem?chain=${chain}`,
  },

  /**
   * An Advanced sensor's own endpoints (docs/advanced-sensor-plan.md §8.3): its reports, their paths and charts, trying
   * rules (Monitoring: View; Details too for a sensor whose data names people), and its rules and fields (Edit). A change of
   * the rules answers with the sensor's detail once the evaluator re-judged the newest report (at most 5 s). Its setting
   * goes through /sensors (SensorRequest.advanced).
   */
  advanced: {
    get: (id: Id) => call(client.GET('/api/v1/sensors/{id}/advanced', { params: { path: { id } } })),
    reports: (id: Id, query: ListQuery = {}) =>
      call(client.GET('/api/v1/sensors/{id}/advanced/reports', { params: { path: { id }, query } })),
    latest: (id: Id) => call(client.GET('/api/v1/sensors/{id}/advanced/reports/latest', { params: { path: { id } } })),
    report: (id: Id, reportId: Id) =>
      call(client.GET('/api/v1/sensors/{id}/advanced/reports/{reportId}', { params: { path: { id, reportId } } })),
    deleteReport: (id: Id, reportId: Id) =>
      call(client.DELETE('/api/v1/sensors/{id}/advanced/reports/{reportId}', { params: { path: { id, reportId } } })),
    clearReports: (id: Id) =>
      call(client.DELETE('/api/v1/sensors/{id}/advanced/reports', { params: { path: { id } } })),
    paths: (id: Id) => call(client.GET('/api/v1/sensors/{id}/advanced/paths', { params: { path: { id } } })),
    samples: (id: Id, fieldId: Id, range: '24h' | '7d' | '30d') =>
      call(
        client.GET('/api/v1/sensors/{id}/advanced/samples', { params: { path: { id }, query: { fieldId, range } } }),
      ),
    /** Rules against a report without storing anything (§6.6); 400 for a draft that isn't valid (rules[0].operand). */
    test: (id: Id, body: S['AdvancedTestRequest']) =>
      call(client.POST('/api/v1/sensors/{id}/advanced/test', { params: { path: { id } }, body })),
    rules: {
      create: (id: Id, body: S['AdvancedRuleRequest']) =>
        call(client.POST('/api/v1/sensors/{id}/advanced/rules', { params: { path: { id } }, body })),
      update: (id: Id, ruleId: Id, body: S['AdvancedRuleRequest']) =>
        call(client.PUT('/api/v1/sensors/{id}/advanced/rules/{ruleId}', { params: { path: { id, ruleId } }, body })),
      remove: (id: Id, ruleId: Id) =>
        call(client.DELETE('/api/v1/sensors/{id}/advanced/rules/{ruleId}', { params: { path: { id, ruleId } } })),
      order: (id: Id, ids: Id[]) =>
        call(client.PUT('/api/v1/sensors/{id}/advanced/rules/order', { params: { path: { id } }, body: { ids } })),
      /** Another Advanced sensor's rules and fields, appended or replacing these (§8.3). */
      copy: (id: Id, body: S['AdvancedCopyRequest']) =>
        call(client.POST('/api/v1/sensors/{id}/advanced/rules/copy', { params: { path: { id } }, body })),
    },
    fields: {
      create: (id: Id, body: S['AdvancedFieldRequest']) =>
        call(client.POST('/api/v1/sensors/{id}/advanced/fields', { params: { path: { id } }, body })),
      update: (id: Id, fieldId: Id, body: S['AdvancedFieldRequest']) =>
        call(client.PUT('/api/v1/sensors/{id}/advanced/fields/{fieldId}', { params: { path: { id, fieldId } }, body })),
      remove: (id: Id, fieldId: Id) =>
        call(client.DELETE('/api/v1/sensors/{id}/advanced/fields/{fieldId}', { params: { path: { id, fieldId } } })),
      order: (id: Id, ids: Id[]) =>
        call(client.PUT('/api/v1/sensors/{id}/advanced/fields/order', { params: { path: { id } }, body: { ids } })),
    },
  },

  /**
   * 3rd Party Connectors (docs/connectors-plan.md §11): every connector's calls with their figures, the connectors with their
   * target as text and its selectors, the callers with their keys (each display audited), networks and grants.
   */
  /**
   * The network map (docs/network-map-plan.md §10.1): the graph, a device's panel, connections, suggestions, interfaces
   * (Details), scans (Manage; one router Commands: Run) and the settings.
   */
  network: {
    graph: (query: {
      routers?: 'all' | 'locations' | 'problems'
      routerLocationIds?: Id[]
      hostLocationIds?: Id[]
      onlyConnected?: boolean
      suggestions?: boolean
      physical?: boolean
      media?: S['ConnectionMedium'][]
    }) => call(client.GET('/api/v1/network-map/graph', { params: { query } })),
    connectionTypes: () => call(client.GET('/api/v1/network-map/connection-types')),
    device: (kind: S['NetworkDeviceKind'], id: Id) =>
      call(client.GET('/api/v1/network-map/devices/{kind}/{id}', { params: { path: { kind, id } } })),
    summary: () => call(client.GET('/api/v1/network-map/summary')),
    path: (from: string, to: string, physicalOnly = false) =>
      call(client.GET('/api/v1/network-map/path', { params: { query: { from, to, physicalOnly } } })),
    connections: (
      query: ListQuery & {
        locationId?: Id
        deviceId?: Id
        type?: string
        physical?: boolean
        routersOnly?: boolean
        unconfirmed?: boolean
      },
    ) => call(client.GET('/api/v1/network-map/connections', { params: { query } })),
    connection: (id: Id) => call(client.GET('/api/v1/network-map/connections/{id}', { params: { path: { id } } })),
    createConnection: (body: S['NetworkConnectionRequest']) =>
      call(client.POST('/api/v1/network-map/connections', { body })),
    updateConnection: (id: Id, body: S['NetworkConnectionUpdate']) =>
      call(client.PUT('/api/v1/network-map/connections/{id}', { params: { path: { id } }, body })),
    deleteConnection: (id: Id) =>
      call(client.DELETE('/api/v1/network-map/connections/{id}', { params: { path: { id } } })),
    savePositions: (positions: S['NetworkPositionInput'][]) =>
      call(client.PUT('/api/v1/network-map/positions', { body: { positions } })),
    clearPosition: (sensorId: Id) =>
      call(client.DELETE('/api/v1/network-map/positions/{sensorId}', { params: { path: { sensorId } } })),
    resetPositions: () => call(client.DELETE('/api/v1/network-map/positions')),
    suggestions: (
      query: ListQuery & {
        state?: S['NetworkSuggestionState']
        confidence?: S['Confidence']
        kind?: S['SuggestionKind']
        deviceId?: Id
        locationId?: Id
      },
    ) => call(client.GET('/api/v1/network-map/suggestions', { params: { query } })),
    accept: (id: Id, body: S['NetworkSuggestionAccept'] = {}) =>
      call(client.POST('/api/v1/network-map/suggestions/{id}/accept', { params: { path: { id } }, body })),
    acceptMany: (ids: Id[]) => call(client.POST('/api/v1/network-map/suggestions/accept', { body: { ids } })),
    dismiss: (id: Id) =>
      call(client.POST('/api/v1/network-map/suggestions/{id}/dismiss', { params: { path: { id } } })),
    dismissMany: (ids: Id[]) => call(client.POST('/api/v1/network-map/suggestions/dismiss', { body: { ids } })),
    restore: (id: Id) =>
      call(client.POST('/api/v1/network-map/suggestions/{id}/restore', { params: { path: { id } } })),
    interfaces: (
      query: ListQuery & {
        unassigned?: boolean
        routerId?: Id
        locationId?: Id
        source?: string
        vlan?: number
        seenInLastScan?: boolean
        showIgnored?: boolean
        hostId?: Id
      },
    ) => call(client.GET('/api/v1/network-map/interfaces', { params: { query } })),
    assign: (mac: string, body: S['NetworkInterfaceAssignment']) =>
      call(client.PUT('/api/v1/network-map/interfaces/{mac}/assignment', { params: { path: { mac } }, body })),
    clearAssignment: (mac: string) =>
      call(client.DELETE('/api/v1/network-map/interfaces/{mac}/assignment', { params: { path: { mac } } })),
    ignore: (mac: string, ignore: boolean) =>
      ignore
        ? call(client.POST('/api/v1/network-map/interfaces/{mac}/ignore', { params: { path: { mac } } }))
        : call(client.POST('/api/v1/network-map/interfaces/{mac}/unignore', { params: { path: { mac } } })),
    scanStatus: () => call(client.GET('/api/v1/network-map/scans/status')),
    scans: (query: ListQuery) => call(client.GET('/api/v1/network-map/scans', { params: { query } })),
    scan: (id: Id) => call(client.GET('/api/v1/network-map/scans/{id}', { params: { path: { id } } })),
    startScan: (body: S['NetworkScanRequest']) => call(client.POST('/api/v1/network-map/scans', { body })),
    cancelScan: (id: Id) => call(client.POST('/api/v1/network-map/scans/{id}/cancel', { params: { path: { id } } })),
    settings: () => call(client.GET('/api/v1/network-map/settings')),
    saveSettings: (body: S['NetworkMapSettingsRequest']) => call(client.PUT('/api/v1/network-map/settings', { body })),
  },

  connectors: {
    calls: (query: ListQuery & ConnectorCallFilters) =>
      call(client.GET('/api/v1/connector-calls', { params: { query } })),
    figures: () => call(client.GET('/api/v1/connector-calls/figures')),
    call: (id: Id) => call(client.GET('/api/v1/connector-calls/{id}', { params: { path: { id } } })),
    redactCall: (id: Id) => call(client.POST('/api/v1/connector-calls/{id}/redact', { params: { path: { id } } })),
    cancelCall: (id: Id) => call(client.POST('/api/v1/connector-calls/{id}/cancel', { params: { path: { id } } })),
    list: (query: ListQuery & { type?: S['ConnectorType']; enabled?: boolean }) =>
      call(client.GET('/api/v1/connectors', { params: { query } })),
    get: (id: Id) => call(client.GET('/api/v1/connectors/{id}', { params: { path: { id } } })),
    compare: (id: Id) => call(client.GET('/api/v1/connectors/{id}/compare', { params: { path: { id } } })),
    create: (body: S['ConnectorRequest']) => call(client.POST('/api/v1/connectors', { body })),
    update: (id: Id, body: S['ConnectorRequest']) =>
      call(client.PUT('/api/v1/connectors/{id}', { params: { path: { id } }, body })),
    remove: (id: Id) => call(client.DELETE('/api/v1/connectors/{id}', { params: { path: { id } } })),
    check: (id: Id, body: S['ConnectorCheckRequest']) =>
      call(client.POST('/api/v1/connectors/{id}/check', { params: { path: { id } }, body })),
    parse: (body: S['ConnectorParseRequest']) => call(client.POST('/api/v1/connectors/parse', { body })),
    testPattern: (body: S['ConnectorPatternRequest']) =>
      call(client.POST('/api/v1/connectors/patterns/test', { body })),
    servers: (type: S['ConnectorType']) =>
      call(client.GET('/api/v1/connectors/lookups/servers', { params: { query: { type } } })),
    library: (type: S['ConnectorType']) =>
      call(client.GET('/api/v1/connectors/lookups/library', { params: { query: { type } } })),
    libraryVersion: (scriptId: Id, number: number) =>
      call(
        client.GET('/api/v1/connectors/lookups/library/{scriptId}/versions/{number}', {
          params: { path: { scriptId, number } },
        }),
      ),
    registered: (server: Id) =>
      call(client.GET('/api/v1/connectors/lookups/registered', { params: { query: { server } } })),
    accounts: (type: S['ConnectorType'], server?: Id) =>
      call(client.GET('/api/v1/connectors/lookups/accounts', { params: { query: { type, server } } })),
    callers: (query: ListQuery) => call(client.GET('/api/v1/connector-callers', { params: { query } })),
    caller: (id: Id) => call(client.GET('/api/v1/connector-callers/{id}', { params: { path: { id } } })),
    createCaller: (body: S['ConnectorCallerRequest']) => call(client.POST('/api/v1/connector-callers', { body })),
    updateCaller: (id: Id, body: S['ConnectorCallerRequest']) =>
      call(client.PUT('/api/v1/connector-callers/{id}', { params: { path: { id } }, body })),
    removeCaller: (id: Id) => call(client.DELETE('/api/v1/connector-callers/{id}', { params: { path: { id } } })),
    /** Audited on the server: every display of a key is an entry. */
    key: (id: Id) => call(client.GET('/api/v1/connector-callers/{id}/key', { params: { path: { id } } })),
    rotateKey: (id: Id) => call(client.POST('/api/v1/connector-callers/{id}/key/rotate', { params: { path: { id } } })),
    docs: () => call(client.GET('/api/v1/connectors/docs')),
  },
  /** The private CAs (docs/ssl-certificate-plan.md §4.5, §8.5): reading Certificates' View, changing its Manage. */
  certificateAuthorities: {
    list: () => call(client.GET('/api/v1/certificate-authorities')),
    /** What §4.5's rules make of a PEM, or why it is refused; stores nothing. */
    preview: (body: S['CertificateAuthorityRequest']) =>
      call(client.POST('/api/v1/certificate-authorities/preview', { body })),
    create: (body: S['CertificateAuthorityRequest']) => call(client.POST('/api/v1/certificate-authorities', { body })),
    /** 409 authority_in_use, naming the sensors that trust it. */
    remove: (id: Id) => call(client.DELETE('/api/v1/certificate-authorities/{id}', { params: { path: { id } } })),
    pemUrl: (id: Id) =>
      `${typeof location !== 'undefined' ? location.origin : ''}/api/v1/certificate-authorities/${encodeURIComponent(id)}/pem`,
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
/** Redmine's search filters (docs/audit-redmine-zabbix-plan.md §6.3). */
export interface RedmineFilters {
  project?: number
  status?: 'open' | 'closed' | 'all'
  tracker?: number
  priority?: number
  assignee?: string
  overdue?: boolean
  linked?: boolean
  /** Open tickets nobody but Cookie changed for Redmine__StaleDays. */
  stale?: boolean
}

/**
 * What a Redmine page's refresh button refreshes (docs/redmine-queue-outbound-plan.md §5.3): the overview of a project (or
 * all), a ticket, or the search with exactly the query of the page shown.
 */
export type RedmineRefresh =
  | { view: 'overview'; project?: number }
  | { view: 'issue'; id: number }
  | ({ view: 'search' } & ListQuery & RedmineFilters)

/** The notification log's filters (docs/redmine-queue-outbound-plan.md §7.2), kept in the URL. */
export interface OutboundNotificationFilters {
  channel?: S['OutboundChannel']
  outcome?: S['OutboundOutcome']
}

/** The Redmine log's filters (§7.2), kept in the URL; `job` for one job's calls. */
export interface RedmineCallFilters {
  kind?: S['RedmineCallKind']
  lane?: S['RedmineLane']
  outcome?: S['OutboundOutcome']
  job?: string
}

/** Zabbix's problems' filters (docs/audit-redmine-zabbix-plan.md §7.5), all in the page's URL. */
export interface ZabbixProblemFilters {
  minSeverity?: S['ZabbixSeverity']
  severity?: S['ZabbixSeverity']
  group?: string
  tag?: string
  host?: string
  hostId?: number
  acknowledged?: boolean
  suppressed?: boolean
  olderThanMinutes?: number
}

/** The Zabbix hosts' filters (§7.6). */
export interface ZabbixHostFilters {
  group?: string
  status?: 'monitored' | 'disabled' | 'maintenance'
  availability?: S['ZabbixAvailability']
  attached?: boolean
}

/** What a ticket can be linked to (§6.5): exactly one of them. */
export interface RedmineTarget {
  pageId?: Id
  locationId?: Id
  hostId?: Id
  sensorId?: Id
}

/** The audit log's filters (docs/audit-redmine-zabbix-plan.md §4.2), all in the page's URL. */
export interface AuditFilters {
  search?: string
  userId?: Id
  type?: string
  categories?: S['AuditCategory'][]
  outcome?: S['AuditOutcome']
  actor?: S['AuditActor']
  subject?: Id
  from?: string
  to?: string
  correlationId?: string
}

/** The filters of *Calls* (docs/connectors-plan.md §13.2), kept in the URL. */
export interface ConnectorCallFilters {
  connectorId?: Id
  callerId?: Id
  active?: boolean
  state?: S['ConnectorCallState']
  refusedBy?: string
  from?: string
  reference?: string
  since?: string
  until?: string
}

export const keys = {
  dashboard: ['dashboard'] as const,
  /** The lounge (docs/lounge-plan.md §15): the room, its side panel, and Admin → Cookie → Lounge. */
  lounge: ['lounge'] as const,
  hosts: ['hosts'] as const,
  host: (id: Id) => ['host', id] as const,
  /** Cookie's notes on a host or a sensor (docs/cookie-object-notes-plan.md §6). */
  cookieNotes: (type: 'host' | 'sensor', id: Id) => ['cookie-notes', type, id] as const,
  /** Locations: the tree, a location's page and history (docs/locations-knowledge-plan.md §3). */
  locations: ['locations'] as const,
  location: (id: Id) => ['locations', id] as const,
  /** The fields' definitions, which every location page renders with. */
  locationFields: ['location-fields'] as const,
  sensors: ['sensors'] as const,
  sensor: (id: Id) => ['sensor', id] as const,
  /** An SSL certificate sensor's certificate and the certificates seen: under the sensor's key, refreshed with it. */
  certificate: (id: Id) => ['sensor', id, 'certificate'] as const,
  /** The private CAs (docs/ssl-certificate-plan.md §8.5). */
  certificateAuthorities: ['certificate-authorities'] as const,
  /** An Advanced sensor's reports, paths and charts (docs/advanced-sensor-plan.md §10): under the sensor's key, refreshed with it. */
  advanced: (id: Id) => ['sensor', id, 'advanced'] as const,
  events: ['events'] as const,
  rules: ['rules'] as const,
  rule: (id: Id) => ['rule', id] as const,
  dns: ['dns'] as const,
  mail: ['mail'] as const,
  keyValues: ['key-values'] as const,
  users: ['users'] as const,
  groups: ['groups'] as const,
  rights: ['rights'] as const,
  redmine: ['redmine'] as const,
  zabbix: ['zabbix'] as const,
  audit: ['audit'] as const,
  system: ['system'] as const,
  /** *System health → Outbound*: the two logs, a record, what runs now (docs/redmine-queue-outbound-plan.md §7). */
  outbound: ['system', 'outbound'] as const,
  /** Everything UDP: the admin page's lists, a node's packets, commands and updates, the protocol examples. */
  udp: ['udp'] as const,
  udpNode: (id: Id) => ['udp', 'node', id] as const,
  /** MikroTik routers: the admin page's status and fleet (docs/mikrotik-plan.md §11.2). */
  mikrotik: ['mikrotik'] as const,
  /** Servers with an agent, of every platform (docs/windows-sensor-plan.md §10; docs/linux-sensor-plan.md §11). */
  agents: ['agents'] as const,
  agentServer: (id: Id) => ['agents', 'server', id] as const,
  /** A Linux server's OS updates (docs/linux-os-updates-plan.md §10.1): under the server's key, so its refreshes include them. */
  osUpdatesServer: (id: Id) => ['agents', 'server', id, 'os-updates'] as const,
  /** OS updates across the fleet: the dashboard's card, the advisory sources and texts (§10.3, §10.4). */
  osUpdates: ['os-updates'] as const,
  /** The SSH key manager (docs/linux-sensor-plan.md §11.4): the register, unregistered keys, removals, a user's keys. */
  sshKeys: ['ssh-keys'] as const,
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
  /** Cookie: the status, the cases, and Admin → Cookie's sections, defaults, versions and outbox (docs/cookie-plan.md §19.4). */
  cookie: ['cookie'] as const,
  /** One's own account's contact details (docs/cookie-plan.md §8.1). */
  account: ['account'] as const,
  /** The availability plan: the days, the people and the settings (docs/cookie-plan.md §8.2, §9.4). */
  availability: ['availability'] as const,
  /** MCP: one's tokens, the catalog, the log, everyone's tokens (docs/mcp-plan.md §10). */
  mcp: ['mcp'] as const,
  /** 3rd Party Connectors: the calls, the connectors, the callers (docs/connectors-plan.md §13). */
  connectors: ['connectors'] as const,
  connector: (id: Id) => ['connectors', 'connector', id] as const,
  connectorCaller: (id: Id) => ['connectors', 'caller', id] as const,
  /** The network map: the graph, panels, connections, suggestions, interfaces and scans (docs/network-map-plan.md §8). */
  network: ['network'] as const,
}

/** A connector's PUT from its detail, with some fields changed (v1's PUT takes every writable field; arguments only when given). */
export function connectorRequest(
  c: S['ConnectorDetail'],
  patch: Partial<S['ConnectorRequest']> = {},
): S['ConnectorRequest'] {
  return {
    name: c.name,
    slug: c.slug,
    purpose: c.purpose,
    enabled: c.enabled,
    target: {
      server: c.target.server,
      scriptName: c.target.scriptName,
      scriptText: c.target.scriptText,
      takenFrom: c.target.takenFrom,
      function: c.target.function,
      runAsKind: c.target.runAsKind,
      account: c.target.account,
      shell: c.target.shell,
      timeoutSeconds: c.target.timeoutSeconds,
    },
    arguments: null,
    mode: c.mode,
    waitSeconds: c.waitSeconds,
    returnOutput: c.returnOutput,
    maxCallsPerHour: c.maxCallsPerHour,
    maxQueued: c.maxQueued,
    maxQueueSeconds: c.maxQueueSeconds,
    reviewEnabled: c.review.enabled,
    reviewPurpose: c.review.purpose,
    reviewThreshold: c.review.threshold,
    reviewChecks: c.review.checks,
    reviewFallback: c.review.fallback,
    callerIds: null,
    ...patch,
  }
}

/** The request of a resource as it is, with some fields changed: v1's PUT takes every writable field. */
export function hostRequest(h: S['HostDetail'], patch: Partial<S['HostRequest']> = {}): S['HostRequest'] {
  return {
    name: h.name,
    location: h.location,
    locationId: h.assignedLocation?.id ?? null,
    notes: h.notes,
    enabled: h.enabled,
    importance: h.importance,
    ...patch,
  }
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
    // A Zabbix sensor's rules as they are (docs/audit-redmine-zabbix-plan.md §7.7).
    zabbix: (patch.type ?? s.type) === 'ZABBIX' && s.zabbix ? zabbixRequest(s.zabbix) : undefined,
    // An SSL certificate sensor's settings as they are (docs/ssl-certificate-plan.md §8.1), for the same reason.
    certificate:
      (patch.type ?? s.type) === 'SSL_CERTIFICATE' && s.certificate ? certificateRequest(s.certificate) : undefined,
    // An Advanced sensor's setting as it is (docs/advanced-sensor-plan.md §8.2); its rules and fields have endpoints of their own.
    advanced: (patch.type ?? s.type) === 'ADVANCED' && s.advanced ? { namesPeople: s.advanced.namesPeople } : undefined,
    ...patch,
  }
}

/** An SSL certificate sensor's settings for SensorRequest.certificate, from its summary, with some changed. */
export function certificateRequest(
  c: S['CertificateSensorSummary'],
  patch: Partial<S['CertificateSensorRequest']> = {},
): S['CertificateSensorRequest'] {
  return {
    trust: c.trust,
    authorityIds: c.authorities.map((a) => a.id),
    checkName: c.checkName,
    checkExpiry: c.checkExpiry,
    incompleteChainFails: c.incompleteChainFails,
    serverName: c.serverName,
    connectTo: c.connectTo,
    warnDays: c.warnDays,
    alertOnCertificateChange: c.alertOnCertificateChange,
    alertOnKeyChange: c.alertOnKeyChange,
    alertOnIssuerChange: c.alertOnIssuerChange,
    issuerMatch: c.issuerMatch,
    ...patch,
  }
}

/** A Zabbix sensor's rules for SensorRequest.zabbix, as they are, with some changed. */
export function zabbixRequest(
  z: S['ZabbixSensorDetail'],
  patch: Partial<S['ZabbixSensorRequest']> = {},
): S['ZabbixSensorRequest'] {
  return {
    zabbixHostId: z.zabbixHostId,
    minSeverity: z.minSeverity,
    countAcknowledged: z.countAcknowledged,
    countSuppressed: z.countSuppressed,
    unavailableFails: z.unavailableFails,
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

/** The whole rule as a request (docs/notification-recipients-plan.md §6.1): the recipient only for an address, the ids from the refs. */
export function ruleRequest(
  r: S['NotificationRuleDetail'],
  patch: Partial<S['NotificationRuleRequest']> = {},
): S['NotificationRuleRequest'] {
  const kind = r.recipientKind ?? 'Address'
  return {
    name: r.name,
    recipient: kind === 'Address' ? r.recipient : null,
    type: r.type,
    enabled: r.enabled,
    recipientKind: kind,
    userId: r.user?.id ?? null,
    groupId: r.group?.id ?? null,
    byEmail: r.byEmail,
    byPushover: r.byPushover,
    hours: r.hours ?? 'Always',
    ...patch,
  }
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
