import type { S } from '@/api/resources'
import { formatAbsolute } from '@/format'

/**
 * An SSL certificate sensor in the web UI (docs/ssl-certificate-plan.md §8.1): the address as the server reads it (§4.3),
 * the defaults per trust mode (§5.4), the days left, the labels, the interval (§5.4, R18), and the comparison of two
 * certificates (§8.4).
 */

export type Trust = S['CertificateTrust']
export type IssuerMatch = S['CertificateIssuerMatch']

const DAY = 86_400_000

// --- the address (§4.3) ---------------------------------------------------------------------------------------------

export interface Endpoint {
  host: string
  port: number
  /** As stored: host:port, [v6]:port. */
  normalized: string
}

/** The schemes whose default port an address may take from a URL, as TlsEndpoint knows them. */
const SCHEME_PORTS: Record<string, number> = {
  https: 443,
  wss: 443,
  ldaps: 636,
  imaps: 993,
  pop3s: 995,
  smtps: 465,
  ftps: 990,
}

const DEFAULT_PORT = 443

/** An IPv4 address in its four parts (not the shorthands "10.1" or "123"). */
export function isIpv4(text: string): boolean {
  const parts = text.split('.')
  return parts.length === 4 && parts.every((p) => /^\d{1,3}$/.test(p) && Number(p) <= 255)
}

/** An IPv6 address, compressed as the server stores it; null when it isn't one. */
export function ipv6(text: string): string | null {
  if (!text.includes(':') || !/^[0-9a-fA-F:.]+$/.test(text)) return null
  try {
    const host = new URL(`http://[${text}]/`).hostname
    return host.slice(1, -1)
  } catch {
    return null
  }
}

export const isIp = (text: string | null | undefined) => !!text && (isIpv4(text) || ipv6(text) !== null)

/** A DNS name (international ones too) or an IP address, at most 253 characters, as TlsEndpoint.IsHost. */
export function isHost(host: string): boolean {
  if (!host || host.length > 253 || /\s/.test(host)) return false
  if (isIp(host)) return true
  const labels = host.replace(/\.$/, '').toLowerCase().split('.')
  return (
    labels.every((l) => l.length >= 1 && l.length <= 63 && /^[\p{L}\p{N}_-]+$/u.test(l) && !/^-|-$/.test(l)) &&
    !/^\d+$/.test(labels[labels.length - 1]!)
  )
}

function port(text: string): number | null {
  return /^\d{1,5}$/.test(text) && Number(text) >= 1 && Number(text) <= 65535 ? Number(text) : null
}

/**
 * host, host:port, an https:// URL (the scheme's port, the path dropped), [v6]:port or a bare IPv6 address (443): the form's
 * hint and the name it suggests. The server validates again and stores what it makes of it.
 */
export function parseAddress(text: string): Endpoint | { error: string } {
  let value = text.trim()
  if (!value) return { error: 'An address: host, host:port or an https:// URL.' }
  let p = DEFAULT_PORT
  const scheme = value.indexOf('://')
  if (scheme >= 0) {
    const name = value.slice(0, scheme).toLowerCase()
    const known = SCHEME_PORTS[name]
    if (known === undefined) {
      return { error: `A TLS address: ${name}:// is not one (https://, ldaps://, imaps://, smtps://, …).` }
    }
    p = known
    value = value.slice(scheme + 3)
    const end = value.search(/[/?#]/)
    if (end >= 0) value = value.slice(0, end)
    const at = value.lastIndexOf('@')
    if (at >= 0) value = value.slice(at + 1)
  }
  let host: string
  if (value.startsWith('[')) {
    const close = value.indexOf(']')
    if (close < 0) return { error: 'An IPv6 address in brackets: [2001:db8::1]:443.' }
    host = value.slice(1, close)
    const rest = value.slice(close + 1)
    if (rest) {
      const given = rest.startsWith(':') ? port(rest.slice(1)) : null
      if (given === null) return { error: 'A port from 1 to 65535 after the address: [2001:db8::1]:443.' }
      p = given
    }
    if (ipv6(host) === null) return { error: 'Not an IPv6 address.' }
  } else if ((value.match(/:/g) ?? []).length > 1) {
    host = value
    if (ipv6(host) === null) {
      return { error: 'Not an address: an IPv6 address with a port goes in brackets, [2001:db8::1]:443.' }
    }
  } else {
    const colon = value.indexOf(':')
    host = colon >= 0 ? value.slice(0, colon) : value
    if (colon >= 0) {
      const given = port(value.slice(colon + 1))
      if (given === null) return { error: 'A port from 1 to 65535: host:443.' }
      p = given
    }
  }
  host = host.replace(/\.+$/, '')
  if (!isHost(host)) return { error: 'A host name or an IP address.' }
  const v6 = ipv6(host)
  host = v6 ?? (isIpv4(host) ? host : host.toLowerCase())
  return { host, port: p, normalized: v6 ? `[${v6}]:${p}` : `${host}:${p}` }
}

export const ADDRESS_HINT = 'host, host:port or an https:// URL, for example shop.example.com or mail.example.com:993.'

// --- the trust and the defaults (§5.1, §5.4) -------------------------------------------------------------------------

export interface TrustDefaults {
  checkName: boolean
  checkExpiry: boolean
  incompleteChainFails: boolean
  alertOnCertificateChange: boolean
  alertOnKeyChange: boolean
  alertOnIssuerChange: boolean
}

/** §5.4's table: the form sets them when a mode is chosen, the API for the fields a request leaves out. */
export function trustDefaults(trust: Trust): TrustDefaults {
  return trust === 'Pinned'
    ? {
        checkName: false,
        checkExpiry: true,
        incompleteChainFails: false,
        alertOnCertificateChange: true,
        alertOnKeyChange: false,
        alertOnIssuerChange: false,
      }
    : {
        checkName: true,
        checkExpiry: true,
        incompleteChainFails: false,
        alertOnCertificateChange: false,
        alertOnKeyChange: false,
        alertOnIssuerChange: true,
      }
}

export const TRUST_OPTIONS: { value: Trust; label: string; hint: string }[] = [
  {
    value: 'Public',
    label: 'Public CAs',
    hint: "Valid when a public CA of PNMT's trust store vouches for it, as for a browser.",
  },
  {
    value: 'Private',
    label: 'A private CA',
    hint: "Valid when one of these private CAs signed it; public CAs don't count. Admin → Certificates keeps them.",
  },
  {
    value: 'Pinned',
    label: 'Pinned (self-signed or unknown CA)',
    hint: "No CA: the certificate the first check reads is pinned, and a change fails the sensor until it is accepted. Compare its fingerprint once with the device's own display.",
  },
]

export const trustHint = (trust: Trust) => TRUST_OPTIONS.find((o) => o.value === trust)?.hint ?? ''

/** A new sensor's settings as *Add sensor* edits them (§8.2). */
export interface CertificateDraft extends TrustDefaults {
  trust: Trust
  authorityIds: string[]
  serverName: string
  connectTo: string
  /** Null: the installation's default (Certificates__WarnDays, 14). */
  warnDays: number | null
  issuerMatch: IssuerMatch
}

export function newCertificateDraft(trust: Trust = 'Public'): CertificateDraft {
  return {
    trust,
    authorityIds: [],
    ...trustDefaults(trust),
    serverName: '',
    connectTo: '',
    warnDays: null,
    issuerMatch: 'Organization',
  }
}

/** Another mode, with its defaults (§5.4); the CAs, the endpoint and the warning stay. */
export const withTrust = (d: CertificateDraft, trust: Trust): CertificateDraft => ({
  ...d,
  trust,
  ...trustDefaults(trust),
})

export function certificateRequestFromDraft(d: CertificateDraft): S['CertificateSensorRequest'] {
  const pinned = d.trust === 'Pinned'
  return {
    trust: d.trust,
    authorityIds: d.trust === 'Private' ? [...d.authorityIds] : [],
    checkName: d.checkName,
    checkExpiry: d.checkExpiry,
    incompleteChainFails: pinned ? false : d.incompleteChainFails,
    serverName: d.serverName.trim() || null,
    connectTo: d.connectTo.trim() || null,
    warnDays: d.warnDays,
    alertOnCertificateChange: d.alertOnCertificateChange,
    alertOnKeyChange: d.alertOnKeyChange,
    alertOnIssuerChange: pinned ? false : d.alertOnIssuerChange,
    issuerMatch: d.issuerMatch,
  }
}

export const PINNED_NEEDS_A_RULE =
  'A pinned certificate is trusted by its pin alone: fail on a new certificate or on a new key.'

/** What the form says before sending (the server says the same, under the same keys). */
export function draftErrors(d: CertificateDraft): Record<string, string | undefined> {
  return {
    'certificate.authorityIds':
      d.trust === 'Private' && !d.authorityIds.length ? 'The private mode needs at least one private CA.' : undefined,
    'certificate.alertOnCertificateChange':
      d.trust === 'Pinned' && !d.alertOnCertificateChange && !d.alertOnKeyChange ? PINNED_NEEDS_A_RULE : undefined,
    'certificate.warnDays':
      d.warnDays !== null && (!Number.isInteger(d.warnDays) || d.warnDays < 1 || d.warnDays > 365)
        ? 'From 1 to 365 days.'
        : undefined,
  }
}

/** The fields the server validates, for its errors (field names as in its problem details). */
export const CERTIFICATE_FIELDS = [
  'certificate.trust',
  'certificate.authorityIds',
  'certificate.warnDays',
  'certificate.serverName',
  'certificate.connectTo',
  'certificate.alertOnCertificateChange',
  'certificate.issuerMatch',
] as const

/** The status strip's words: "public CAs", the private CAs' names, "pinned". */
export function trustLabel(c: Pick<S['CertificateSensorSummary'], 'trust' | 'authorities'>): string {
  if (c.trust === 'Pinned') return 'pinned'
  if (c.trust === 'Private') return c.authorities.map((a) => a.name).join(', ') || 'a private CA'
  return 'public CAs'
}

export const ISSUER_MATCH_OPTIONS: { value: IssuerMatch; label: string; hint: string }[] = [
  {
    value: 'Organization',
    label: 'Organisation',
    hint: "The issuing CA's organisation (O=): Let's Encrypt R10 → R11 is no new issuer, Let's Encrypt → ZeroSSL is.",
  },
  {
    value: 'Exact',
    label: 'Exact name',
    hint: "The issuing CA's full name: every other intermediate is a new issuer.",
  },
]

/** "Accepted by anna", "Pinned at the first check", "Accepted automatically (renewal)". */
export function acceptedText(by: string | null | undefined, pinned: boolean): string {
  const verb = pinned ? 'Pinned' : 'Accepted'
  if (!by) return verb
  if (by === 'first check') return `${verb} at the first check`
  if (by.startsWith('automatically')) return `${verb} ${by}`
  return `${verb} by ${by}`
}

// --- the days left (§3: absolute dates in messages, the UI counts live) ---------------------------------------------

/** Whole days until the chain's expiry, as the server counts them (negative once expired); null without a certificate. */
export function daysLeft(expiresAt: string | null | undefined, now: number): number | null {
  if (!expiresAt) return null
  const at = Date.parse(expiresAt)
  return Number.isNaN(at) ? null : Math.floor((at - now) / DAY)
}

export const days = (n: number) => `${n} ${Math.abs(n) === 1 ? 'day' : 'days'}`

/** "76 days left", "today", "expired 2 days ago". */
export function daysLeftText(expiresAt: string | null | undefined, now: number): string {
  const at = expiresAt ? Date.parse(expiresAt) : NaN
  if (Number.isNaN(at)) return '–'
  if (at <= now) {
    const ago = Math.floor((now - at) / DAY)
    return ago === 0 ? 'expired today' : `expired ${days(ago)} ago`
  }
  const left = Math.floor((at - now) / DAY)
  return left === 0 ? 'expires today' : `${days(left)} left`
}

export type Tone = 'ok' | 'warn' | 'fail' | 'none'

/** The tab's headline (§8.3): "Valid · 76 days left", "Expires in 5 days", "Expired 2 days ago". */
export function validityHeadline(
  expiresAt: string | null | undefined,
  warnDays: number,
  now: number,
): { text: string; tone: Tone } {
  const at = expiresAt ? Date.parse(expiresAt) : NaN
  if (Number.isNaN(at)) return { text: 'No certificate read yet', tone: 'none' }
  if (at <= now) {
    const ago = Math.floor((now - at) / DAY)
    return { text: ago === 0 ? 'Expired today' : `Expired ${days(ago)} ago`, tone: 'fail' }
  }
  const left = Math.floor((at - now) / DAY)
  if (left <= warnDays) return { text: left === 0 ? 'Expires today' : `Expires in ${days(left)}`, tone: 'warn' }
  return { text: `Valid · ${days(left)} left`, tone: 'ok' }
}

/** §5.4's warnings about the warning's length against the certificate's lifetime. */
export function warningLengthNote(warnDays: number, lifetimeDays: number | null | undefined): string | null {
  if (!lifetimeDays || lifetimeDays <= 0) return null
  if (warnDays >= lifetimeDays) {
    return `The warning (${days(warnDays)}) is as long as the certificate's lifetime (${days(lifetimeDays)}) or longer: the sensor is always about to expire.`
  }
  if (warnDays * 3 > lifetimeDays) {
    return `The warning (${days(warnDays)}) is more than a third of the certificate's lifetime (${days(lifetimeDays)}): healthy renewals may look like problems.`
  }
  return null
}

// --- labels -------------------------------------------------------------------------------------------------------

const VALIDATION_LEVELS: Record<string, string> = {
  DV: 'Domain validated',
  OV: 'Organisation validated',
  IV: 'Individual validated',
  EV: 'Extended validation',
}

/** "Domain validated (DV)"; another level as it is. */
export function validationLevelLabel(level: string | null | undefined): string | null {
  if (!level) return null
  const name = VALIDATION_LEVELS[level]
  return name ? `${name} (${level})` : level
}

const KEY_USAGES: Record<string, string> = {
  DigitalSignature: 'Digital signature',
  NonRepudiation: 'Non-repudiation',
  KeyEncipherment: 'Key encipherment',
  DataEncipherment: 'Data encipherment',
  KeyAgreement: 'Key agreement',
  KeyCertSign: 'Certificate signing',
  CrlSign: 'CRL signing',
  EncipherOnly: 'Encipher only',
  DecipherOnly: 'Decipher only',
}

/** A key usage in words: the server's label as it is, .NET's flag names ("KeyCertSign") in words. */
export const keyUsageLabel = (usage: string) => KEY_USAGES[usage] ?? usage

const POLICIES: Record<string, string> = {
  '2.23.140.1.1': 'Extended validation (CA/B Forum)',
  '2.23.140.1.2.1': 'Domain validated (CA/B Forum)',
  '2.23.140.1.2.2': 'Organisation validated (CA/B Forum)',
  '2.23.140.1.2.3': 'Individual validated (CA/B Forum)',
  '2.5.29.32.0': 'Any policy',
}

/** A policy OID with its name where it is a known one. */
export const policyLabel = (oid: string) => (POLICIES[oid] ? `${POLICIES[oid]} · ${oid}` : oid)

/** Where a certificate of the chain came from (§4.4). */
export function sourceLabel(source: S['CertificateSource'], name: string | null | undefined): string {
  switch (source) {
    case 'Server':
      return 'sent by the server'
    case 'PublicRoot':
      return 'public root'
    case 'PrivateCa':
      return name ? `private CA: ${name}` : 'private CA'
    default:
      return "from PNMT's trust store"
  }
}

/** SHA-256 or SHA-1 in hex as openssl shows it: AB:CD:…, upper case. */
export function fingerprint(hex: string | null | undefined): string {
  const clean = (hex ?? '').replace(/[^0-9a-fA-F]/g, '').toUpperCase()
  return clean.match(/.{1,2}/g)?.join(':') ?? ''
}

/** The first 8 bytes, for lists: AB:CD:EF:01:23:45:67:89…. */
export function shortFingerprint(hex: string | null | undefined): string {
  const full = fingerprint(hex)
  return full.length > 23 ? `${full.slice(0, 23)}…` : full
}

/**
 * §8.4's command that shows the SHA-256 the server presents, for this endpoint: the address connected to, and the server
 * name sent (none for an IP address without one).
 */
export function opensslCommand(
  c: Pick<S['CertificateSensorSummary'], 'host' | 'port' | 'serverName' | 'connectTo'>,
): string {
  const target = c.connectTo || c.host
  const connect = ipv6(target) ? `[${ipv6(target)}]:${c.port}` : `${target}:${c.port}`
  const name = c.serverName || (isIp(c.host) ? null : c.host)
  return (
    `openssl s_client -connect ${connect}${name ? ` -servername ${name}` : ''} </dev/null 2>/dev/null \\\n` +
    '  | openssl x509 -noout -fingerprint -sha256'
  )
}

// --- the interval (§5.4, R18: 1 hour to 30 days, daily by default) ---------------------------------------------------

export const MIN_INTERVAL = 3_600
export const MAX_INTERVAL = 2_592_000
export const DEFAULT_INTERVAL = 86_400

export const INTERVAL_PRESETS: { label: string; seconds: number }[] = [
  { label: 'Hourly', seconds: 3_600 },
  { label: 'Every 6 hours', seconds: 21_600 },
  { label: 'Every 12 hours', seconds: 43_200 },
  { label: 'Daily', seconds: 86_400 },
  { label: 'Weekly', seconds: 604_800 },
  { label: 'Monthly (30 days)', seconds: 2_592_000 },
]

/** "Daily", "Every 3 days", "Every 12 hours"; 0 is daily, as the server takes it. */
export function intervalLabel(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined) return '–'
  const s = seconds <= 0 ? DEFAULT_INTERVAL : seconds
  const preset = INTERVAL_PRESETS.find((p) => p.seconds === s)
  if (preset) return preset.label
  if (s % 86_400 === 0) return `Every ${s / 86_400} days`
  if (s % 3_600 === 0) return `Every ${s / 3_600} hours`
  return `Every ${Math.round(s / 60)} minutes`
}

export function intervalError(seconds: number | null | undefined): string | undefined {
  return seconds === null ||
    seconds === undefined ||
    !Number.isInteger(seconds) ||
    seconds < MIN_INTERVAL ||
    seconds > MAX_INTERVAL
    ? 'From 1 hour to 30 days.'
    : undefined
}

// --- the comparison (§8.4) ---------------------------------------------------------------------------------------------

export type FieldChange = S['CertificateFieldChange']

/** Two versions field by field, as the server's comparison of the accepted and the current one. */
export function compareFacts(a: S['CertificateFacts'], b: S['CertificateFacts']): FieldChange[] {
  const fields: FieldChange[] = []
  const add = (field: string, x: string | null, y: string | null) =>
    fields.push({ field, accepted: x, current: y, changed: x !== y })
  const keyLabel = (k: S['CertificateKey']) =>
    k.label ?? (k.curve ? `${k.algorithm} ${k.curve}` : k.size ? `${k.algorithm} ${k.size}` : k.algorithm)
  add('SHA-256', a.sha256, b.sha256)
  add('Key', keyLabel(a.key), keyLabel(b.key))
  add('Key (SPKI SHA-256)', a.spkiSha256, b.spkiSha256)
  add('Issuer', a.issuer.dn, b.issuer.dn)
  add('Subject', a.subject.dn, b.subject.dn)
  add('Names', a.names.join(', '), b.names.join(', '))
  const lower = (list: string[]) => new Set(list.map((n) => n.toLowerCase()))
  const added = b.names.filter((n) => !lower(a.names).has(n.toLowerCase()))
  const removed = a.names.filter((n) => !lower(b.names).has(n.toLowerCase()))
  if (added.length) fields.push({ field: 'Names added', accepted: null, current: added.join(', '), changed: true })
  if (removed.length)
    fields.push({ field: 'Names removed', accepted: removed.join(', '), current: null, changed: true })
  add('Serial', a.serial, b.serial)
  add('Valid from', formatAbsolute(a.notBefore), formatAbsolute(b.notBefore))
  add('Valid until', formatAbsolute(a.notAfter), formatAbsolute(b.notAfter))
  return fields
}

/** The kind of change a field shows, for its tag in the comparison: the certificate, its key, its issuer. */
export function fieldKind(field: string): 'certificate' | 'key' | 'issuer' | null {
  if (field === 'SHA-256') return 'certificate'
  if (field.startsWith('Key')) return 'key'
  if (field === 'Issuer') return 'issuer'
  return null
}

// --- PEM (§8.5) ---------------------------------------------------------------------------------------------------------

/** The certificates of a PEM text, each a whole BEGIN/END block. */
export function pemBlocks(text: string): string[] {
  return text.match(/-----BEGIN CERTIFICATE-----[\s\S]*?-----END CERTIFICATE-----/g) ?? []
}

/** DER bytes as a PEM certificate, 64 characters a line. */
export function derToPem(bytes: Uint8Array): string {
  let binary = ''
  for (const b of bytes) binary += String.fromCharCode(b)
  const lines = btoa(binary).match(/.{1,64}/g) ?? []
  return `-----BEGIN CERTIFICATE-----\n${lines.join('\n')}\n-----END CERTIFICATE-----\n`
}

/** A file's certificates as PEM: a PEM text as it is, a binary DER certificate (.cer, .crt) wrapped. */
export function pemOfFile(bytes: Uint8Array): string {
  const text = new TextDecoder().decode(bytes)
  if (text.includes('-----BEGIN')) return text
  // An ASN.1 SEQUENCE with a long length: a DER certificate.
  return bytes[0] === 0x30 && bytes.length > 64 ? derToPem(bytes) : text
}

/** SHA-256 of a PEM certificate's DER, upper-case hex; null where WebCrypto isn't there (a page without HTTPS). */
export async function pemSha256(pem: string): Promise<string | null> {
  if (typeof crypto === 'undefined' || !crypto.subtle) return null
  const body = pem.replace(/-----(BEGIN|END) CERTIFICATE-----/g, '').replace(/\s+/g, '')
  const der = Uint8Array.from(atob(body), (c) => c.charCodeAt(0))
  const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', der))
  return Array.from(hash, (b) => b.toString(16).padStart(2, '0'))
    .join('')
    .toUpperCase()
}

/** The certificate of a chain PEM with this SHA-256; without WebCrypto the last one (the root a server sends last). */
export async function pemOf(chainPem: string, sha256: string): Promise<string | null> {
  const blocks = pemBlocks(chainPem)
  const wanted = sha256.replace(/:/g, '').toUpperCase()
  for (const block of blocks) {
    const sha = await pemSha256(block)
    if (sha === null) return `${blocks[blocks.length - 1]}\n`
    if (sha === wanted) return `${block}\n`
  }
  return null
}
