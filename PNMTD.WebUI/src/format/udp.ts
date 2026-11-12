import type { Schemas } from '@/api/client'
import { formatDuration, formatRelative, toDate } from '@/format'

/**
 * PNMT-UDP v1 for the UI (docs/udp-iot-plan.md §3): the suites' names and sizes as the server's
 * codec has them, labels for the API's enums, and the few sizes the UI estimates before it asks
 * the server (a command's bytes on the wire, an update's volume). Parsing and building packets is
 * the server's job: every datagram comes with its fields.
 */

export type SecuritySuite = Schemas['SecuritySuite']
export type KeyFamily = Schemas['KeyFamily']
export type PacketType = Schemas['PacketType']
export type PacketField = Schemas['PacketField']
export type UdpLogKind = Schemas['UdpLogKind']
export type UdpCommandState = Schemas['UdpCommandState']
export type UdpOtaState = Schemas['UdpOtaState']
export type UdpAckMode = Schemas['UdpAckMode']
export type UdpSessionMode = Schemas['UdpSessionMode']
export type UdpRejectReason = Schemas['UdpRejectReason']

export interface SuiteInfo {
  suite: SecuritySuite
  code: number
  /** As in the specification: "HMAC-SHA256/32". */
  name: string
  /** For compact lists: "H32". */
  short: string
  tag: number
  family: KeyFamily | null
  keyed: boolean
  encrypted: boolean
  legacy: boolean
}

const suite = (
  s: SecuritySuite,
  code: number,
  name: string,
  short: string,
  tag: number,
  family: KeyFamily | null,
  extra: Partial<SuiteInfo> = {},
): SuiteInfo => ({
  suite: s,
  code,
  name,
  short,
  tag,
  family,
  keyed: family !== null,
  encrypted: false,
  legacy: false,
  ...extra,
})

/** The twelve suites (§3.5), in code order; the same names as SecuritySuites.Name() on the server. */
export const SUITES: SuiteInfo[] = [
  suite('None', 0, 'NONE', 'NONE', 0, null),
  suite('Token32', 1, 'TOKEN32', 'TOK', 4, null),
  suite('HmacSha256_32', 2, 'HMAC-SHA256/32', 'H32', 4, 'HmacSha256'),
  suite('HmacSha256_64', 3, 'HMAC-SHA256/64', 'H64', 8, 'HmacSha256'),
  suite('AesCmac32', 4, 'AES-CMAC/32', 'C32', 4, 'Aes'),
  suite('AesCmac64', 5, 'AES-CMAC/64', 'C64', 8, 'Aes'),
  suite('AesCcm32', 6, 'AES-CCM/32', 'E32', 4, 'Aes', { encrypted: true }),
  suite('AesCcm64', 7, 'AES-CCM/64', 'E64', 8, 'Aes', { encrypted: true }),
  suite('HmacMd5_32', 8, 'HMAC-MD5/32', 'M32', 4, 'HmacMd5', { legacy: true }),
  suite('HmacMd5_64', 9, 'HMAC-MD5/64', 'M64', 8, 'HmacMd5', { legacy: true }),
  suite('HmacSha1_32', 10, 'HMAC-SHA1/32', 'S32', 4, 'HmacSha1', { legacy: true }),
  suite('HmacSha1_64', 11, 'HMAC-SHA1/64', 'S64', 8, 'HmacSha1', { legacy: true }),
]

const bySuite = new Map(SUITES.map((s) => [s.suite, s]))

export function suiteInfo(s: SecuritySuite | null | undefined): SuiteInfo | undefined {
  return s ? bySuite.get(s) : undefined
}

/** "HMAC-SHA256/32"; "–" for none. */
export function suiteName(s: SecuritySuite | null | undefined): string {
  return suiteInfo(s)?.name ?? (s ? String(s) : '–')
}

/** NONE and TOKEN32: anyone who sees a packet can forge one. */
export const isWeak = (s: SecuritySuite | null | undefined) => s === 'None' || s === 'Token32'

/** "weak", "encrypted", "legacy" for a suite's mark in lists. */
export function suiteMarks(s: SecuritySuite): string[] {
  const info = suiteInfo(s)
  return [isWeak(s) ? 'weak' : null, info?.encrypted ? 'encrypted' : null, info?.legacy ? 'legacy' : null].filter(
    (m): m is string => m !== null,
  )
}

export const FAMILIES: { value: KeyFamily; label: string; legacy: boolean; hint: string }[] = [
  { value: 'HmacSha256', label: 'HMAC-SHA256', legacy: false, hint: 'Signed packets; the default.' },
  { value: 'Aes', label: 'AES', legacy: false, hint: 'AES-CMAC signatures and AES-CCM encryption.' },
  { value: 'HmacSha1', label: 'HMAC-SHA1', legacy: true, hint: 'For devices that only have SHA-1.' },
  { value: 'HmacMd5', label: 'HMAC-MD5', legacy: true, hint: 'For devices that only have MD5.' },
]

export function familyLabel(f: KeyFamily | null | undefined): string {
  return FAMILIES.find((x) => x.value === f)?.label ?? String(f ?? '–')
}

/** The family's own suites. */
export function familySuites(f: KeyFamily): SecuritySuite[] {
  return SUITES.filter((s) => s.family === f).map((s) => s.suite)
}

/** What a node of the family may allow: NONE, TOKEN32 and the family's suites (§3.5). */
export function suiteChoices(f: KeyFamily): SecuritySuite[] {
  return ['None', 'Token32', ...familySuites(f)]
}

/** A new node's suites: the family's two signatures (/32 and /64), not encryption. */
export function defaultSuites(f: KeyFamily): SecuritySuite[] {
  return SUITES.filter((s) => s.family === f && !s.encrypted).map((s) => s.suite)
}

/** The family's 64-bit MAC suite, the only one a JOIN may use (§3.8). */
export function joinSuite(f: KeyFamily): SecuritySuite {
  return ({ HmacSha256: 'HmacSha256_64', Aes: 'AesCmac64', HmacSha1: 'HmacSha1_64', HmacMd5: 'HmacMd5_64' } as const)[f]
}

/** The device id's length on the wire: the server answers with the length the node uses, at least this. */
export function idLength(deviceId: number): number {
  return deviceId < 0x100 ? 1 : deviceId < 0x10000 ? 2 : deviceId < 0x1000000 ? 3 : 4
}

/** The id as the header carries it: "07", "01 2C". */
export function idBytes(deviceId: number): string {
  const length = idLength(deviceId)
  const bytes: string[] = []
  for (let i = length - 1; i >= 0; i--) {
    bytes.push(
      Math.floor(deviceId / 256 ** i)
        .toString(16)
        .slice(-2)
        .toUpperCase()
        .padStart(2, '0'),
    )
  }
  return bytes.join(' ')
}

export const MAX_DATAGRAM = 508
export const MAX_COMMAND = 480

/** The header: bytes 0–1, the device id, the 2-byte counter. */
export const headerLength = (deviceId: number) => 2 + idLength(deviceId) + 2

/** A COMMAND on the wire (§3.4): header, CMD_ID, the command, the tag. */
export function commandWireSize(deviceId: number, payloadLength: number, s: SecuritySuite): number {
  return headerLength(deviceId) + 1 + payloadLength + (suiteInfo(s)?.tag ?? 0)
}

/** IPv4 and UDP headers, for the volume of an update (§3.13). */
export const IPV4_UDP = 28

export interface OtaEstimate {
  roundTrips: number
  bytesOnTheWire: number
  secondsAt100Ms: number
  secondsAt2s: number
}

/**
 * The cost of an update (§3.13): one GET (UPDATE_ID, OP, OFFSET) and one OTA_CHUNK (UPDATE_ID,
 * OFFSET, data) per chunk, over IPv4, with an 8-byte tag. 1 MiB in 448-byte chunks: 2,341 round
 * trips, about 1.21 MiB.
 */
export function otaEstimate(size: number, chunk: number, deviceId = 1, tag = 8): OtaEstimate {
  const roundTrips = chunk > 0 ? Math.ceil(size / chunk) : 0
  const header = headerLength(deviceId)
  const up = IPV4_UDP + header + 1 + 1 + 4 + tag
  const downOverhead = IPV4_UDP + header + 1 + 4 + tag
  const bytesOnTheWire = roundTrips * (up + downOverhead) + size
  return { roundTrips, bytesOnTheWire, secondsAt100Ms: roundTrips * 0.1, secondsAt2s: roundTrips * 2 }
}

const TYPE_NAMES: Record<PacketType, string> = {
  Heartbeat: 'HEARTBEAT',
  Data: 'DATA',
  Event: 'EVENT',
  CmdResult: 'CMD_RESULT',
  Boot: 'BOOT',
  Join: 'JOIN',
  Ota: 'OTA',
  Ack: 'ACK',
  Command: 'COMMAND',
  JoinAccept: 'JOIN_ACCEPT',
  OtaOffer: 'OTA_OFFER',
  OtaChunk: 'OTA_CHUNK',
}

/** As in the specification: "CMD_RESULT". */
export const typeName = (t: PacketType) => TYPE_NAMES[t] ?? t

export const UPLINK_TYPES: PacketType[] = ['Heartbeat', 'Data', 'Event', 'CmdResult', 'Boot', 'Join', 'Ota']
export const DOWNLINK_TYPES: PacketType[] = ['Ack', 'Command', 'JoinAccept', 'OtaOffer', 'OtaChunk']

export const ACK_MODES: { value: UdpAckMode; label: string }[] = [
  { value: 'OnRequest', label: 'On request' },
  { value: 'Always', label: 'Always' },
  { value: 'Never', label: 'Never' },
]

export const SESSION_MODES: { value: UdpSessionMode; label: string }[] = [
  { value: 'None', label: 'None (persistent counter)' },
  { value: 'JoinRequired', label: 'JOIN required' },
]

export const ackLabel = (m: UdpAckMode) => ({ OnRequest: 'on request', Always: 'always', Never: 'never' })[m] ?? m

/** "BadTag" → "Bad tag". */
export function words(name: string): string {
  const spaced = name.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase()
  return spaced.charAt(0).toUpperCase() + spaced.slice(1)
}

const KIND_LABELS: Partial<Record<UdpLogKind, string>> = {
  ResultReceived: 'Result received',
  UnmatchedResult: 'Unmatched result',
  OtaQueued: 'Update queued',
  OtaOffered: 'Update offered',
  OtaStarted: 'Download started',
  OtaDeclined: 'Update declined',
  OtaVerified: 'Image verified',
  OtaFailed: 'Update failed',
  OtaApplied: 'Update applied',
  OtaCancelled: 'Update cancelled',
  OtaExpired: 'Update expired',
}

export const kindLabel = (k: UdpLogKind) => KIND_LABELS[k] ?? words(k)

export const LOG_KINDS: UdpLogKind[] = [
  'Queued',
  'Sent',
  'Resent',
  'ResultReceived',
  'UnmatchedResult',
  'Cancelled',
  'Expired',
  'Failed',
  'OtaQueued',
  'OtaOffered',
  'OtaStarted',
  'OtaDeclined',
  'OtaVerified',
  'OtaFailed',
  'OtaApplied',
  'OtaCancelled',
  'OtaExpired',
]

export function kindIcon(k: UdpLogKind): string {
  switch (k) {
    case 'Queued':
    case 'OtaQueued':
      return 'pi pi-inbox'
    case 'Sent':
    case 'OtaOffered':
      return 'pi pi-arrow-down'
    case 'Resent':
      return 'pi pi-replay'
    case 'ResultReceived':
    case 'UnmatchedResult':
    case 'OtaVerified':
    case 'OtaStarted':
      return 'pi pi-arrow-up'
    case 'OtaApplied':
      return 'pi pi-check-circle'
    case 'Cancelled':
    case 'OtaCancelled':
      return 'pi pi-ban'
    case 'Expired':
    case 'OtaExpired':
      return 'pi pi-clock'
    default:
      return 'pi pi-times-circle'
  }
}

export type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast'

export function kindSeverity(k: UdpLogKind): Severity {
  if (/Failed|Declined|Unmatched/.test(k)) return 'danger'
  if (/Cancelled|Expired/.test(k)) return 'secondary'
  if (/Result|Applied|Verified/.test(k)) return 'success'
  return 'info'
}

export function commandSeverity(state: UdpCommandState, resultStatus?: number | null): Severity {
  switch (state) {
    case 'Completed':
      return resultStatus ? 'danger' : 'success'
    case 'Failed':
      return 'danger'
    case 'Queued':
    case 'Sent':
      return 'info'
    default:
      return 'secondary'
  }
}

export const COMMAND_STATES: UdpCommandState[] = ['Queued', 'Sent', 'Completed', 'Failed', 'Expired', 'Cancelled']
export const isOpenCommand = (s: UdpCommandState) => s === 'Queued' || s === 'Sent'

export const OTA_STATES: UdpOtaState[] = [
  'Queued',
  'Offered',
  'Downloading',
  'Verified',
  'Applied',
  'Declined',
  'Failed',
  'Cancelled',
  'Expired',
]
export const isOpenUpdate = (s: UdpOtaState) =>
  s === 'Queued' || s === 'Offered' || s === 'Downloading' || s === 'Verified'

export function otaSeverity(s: UdpOtaState): Severity {
  switch (s) {
    case 'Applied':
      return 'success'
    case 'Declined':
      return 'warn'
    case 'Failed':
      return 'danger'
    case 'Cancelled':
    case 'Expired':
      return 'secondary'
    default:
      return 'info'
  }
}

/** CMD_RESULT's STATUS in words (§3.4), as the server's Payloads.CommandStatus. */
export function resultStatusText(status: number | null | undefined): string {
  if (status === null || status === undefined) return '–'
  const known = ['OK', 'unknown command', 'invalid argument', 'failed', 'busy'][status]
  if (known) return known
  return status >= 128 ? `device-specific ${status}` : `reserved ${status}`
}

const day = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short', year: 'numeric' })
const clock = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' })

/** "generated on 12 Nov 2026" or "entered manually on 12 Nov 2026 by alice". */
export function secretSource(source: Schemas['UdpSecretSource'], at: string | null, by: string | null): string {
  const date = toDate(at)
  const when = date ? ` on ${day.format(date)}` : ''
  return source === 'Manual' ? `entered manually${when}${by ? ` by ${by}` : ''}` : `generated${when}`
}

/** The next expected packet: the last one plus the interval. */
export function nextExpected(lastSeenAt: string | null | undefined, intervalSeconds: number): Date | null {
  const last = toDate(lastSeenAt)
  return last && intervalSeconds > 0 ? new Date(last.getTime() + intervalSeconds * 1000) : null
}

/**
 * When a queued command goes out (§6.4): with the node's next packet. "Delivered with the node's
 * next packet. The last arrived 3 min ago, every 10 min → around 14:32".
 */
export function deliveryEstimate(lastSeenAt: string | null | undefined, intervalSeconds: number, now: number): string {
  const last = toDate(lastSeenAt)
  if (!last) {
    return "Delivered with the node's next packet. It has not reported yet."
  }
  const every = intervalSeconds > 0 ? `, every ${formatDuration(intervalSeconds * 1000)}` : ''
  const next = nextExpected(lastSeenAt, intervalSeconds)
  const around = next
    ? next.getTime() >= now
      ? ` → around ${clock.format(next)}`
      : ` → it is overdue (expected ${formatRelative(next, now)})`
    : ''
  return `Delivered with the node's next packet. The last arrived ${formatRelative(last, now)}${every}${around}.`
}

/** "pnmt.example.com:5028" → host and port ("[2001:db8::1]:5028" too). */
export function endpointParts(endpoint: string): { host: string; port: string } {
  const i = endpoint.lastIndexOf(':')
  if (i <= 0) {
    return { host: endpoint, port: '5028' }
  }
  return { host: endpoint.slice(0, i).replace(/^\[|\]$/g, ''), port: endpoint.slice(i + 1) }
}

/** Rate and time left of a download, from its progress so far. */
export function downloadRate(
  job: Pick<Schemas['UdpOtaJobItem'], 'state' | 'startedAt' | 'lastRequestAt' | 'highestOffset' | 'firmwareSize'>,
): { bytesPerSecond: number; secondsLeft: number } | null {
  const start = toDate(job.startedAt)
  const last = toDate(job.lastRequestAt)
  if (job.state !== 'Downloading' || !start || !last || last <= start || job.highestOffset <= 0) {
    return null
  }
  const bytesPerSecond = job.highestOffset / ((last.getTime() - start.getTime()) / 1000)
  return { bytesPerSecond, secondsLeft: Math.max(0, job.firmwareSize - job.highestOffset) / bytesPerSecond }
}

/** A share 0–1 as a whole percentage for progress bars. */
export const percent = (fraction: number | null | undefined) =>
  Math.round(Math.min(1, Math.max(0, fraction ?? 0)) * 100)

/** The expanded rows of a ListTable with one row toggled (a collapsed row must leave the map). */
export function toggleRow(expanded: Record<string, boolean>, id: string | number): Record<string, boolean> {
  const key = String(id)
  const next = { ...expanded }
  if (next[key]) {
    delete next[key]
  } else {
    next[key] = true
  }
  return next
}
