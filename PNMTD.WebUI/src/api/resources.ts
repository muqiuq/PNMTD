import { call, client, type Schemas } from './client'

/** The v1 resources (docs/webui-migration-plan.md §3.7), one typed function per call. */

export type S = Schemas
export type SensorType = S['SensorType']
export type SensorState = S['SensorState']
export type HostState = S['HostState']
export type RuleType = S['NotificationRuleType']

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
}

/** The request of a resource as it is, with some fields changed: v1's PUT takes every writable field. */
export function hostRequest(h: S['HostDetail'], patch: Partial<S['HostRequest']> = {}): S['HostRequest'] {
  return { name: h.name, location: h.location, notes: h.notes, enabled: h.enabled, ...patch }
}

export function sensorRequest(s: S['SensorDetail'], patch: Partial<S['SensorRequest']> = {}): S['SensorRequest'] {
  return {
    hostId: s.host.id,
    type: s.type,
    name: s.name,
    enabled: s.enabled,
    ignore: s.ignore,
    interval: s.interval,
    gracePeriod: s.gracePeriod,
    parameters: s.parameters,
    textId: s.textId,
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
