import type { S } from '@/api/resources'
import type { Option } from './cookieAdmin'

/**
 * Admin → Cookie → Access (docs/cookie-access-plan.md §8) without state: the section in the draft's shape, what is sent,
 * the tab's warnings, the routers of a location, the people a contact can be, and a known access entry's request.
 */

export type AccessValues = Required<S['AccessSettings']>
export type VerdictKey = keyof S['AccessVerdictRules']
export type KindKey = keyof S['AccessKinds']

/** The defaults of §6.1: unusual waits for working hours by e-mail; suspicious and alarming by Pushover too. */
const verdictDefaults: Record<VerdictKey, Required<S['AccessVerdictRule']>> = {
  unusual: { notify: true, email: true, pushover: false, hours: 'Wait', ask: true },
  suspicious: { notify: true, email: true, pushover: true, hours: 'Allowed', ask: true },
  alarming: { notify: true, email: true, pushover: true, hours: 'Everyone', ask: true },
}

/** What §4.6 watches by default: everything but explicit credentials, batch, unlock, cached, SFTP and RDP connections. */
const kindDefaults: Required<S['AccessKinds']> = {
  console: true,
  rdp: true,
  ssh: true,
  winRm: true,
  adminNetwork: true,
  elevation: true,
  explicit: false,
  batch: false,
  unlock: false,
  cached: false,
  sftp: false,
  rdpConnection: false,
  routerWinbox: true,
  routerWeb: true,
  routerApi: true,
  routerSsh: true,
  routerTelnet: true,
  routerFtp: true,
  routerConsole: true,
  pnmtWeb: true,
  pnmtMcp: true,
}

/** Every part present, the defaults of §8 for any the server's answer lacks, so the form can bind to each. */
export function normalizeAccess(v: Partial<S['AccessSettings']>): AccessValues {
  const notices = v.notices ?? {}
  const rule = (key: VerdictKey) => ({ ...verdictDefaults[key], ...(notices[key] ?? {}) })
  return {
    on: v.on ?? false,
    servers: v.servers ?? true,
    routers: v.routers ?? true,
    pnmt: v.pnmt ?? true,
    serverLocations: v.serverLocations ?? [],
    serverHosts: v.serverHosts ?? [],
    excludedHosts: v.excludedHosts ?? [],
    watchNewRouters: v.watchNewRouters ?? true,
    kinds: { ...kindDefaults, ...(v.kinds ?? {}) },
    excluded: (v.excluded ?? []).map((e) => ({
      account: e.account,
      sensorId: e.sensorId ?? null,
      comment: e.comment ?? '',
    })),
    links: { key: true, serverAccounts: true, username: true, ...(v.links ?? {}) },
    notices: { unusual: rule('unusual'), suspicious: rule('suspicious'), alarming: rule('alarming') },
    wasThisYou: {
      on: true,
      pnmt: true,
      servers: true,
      routers: true,
      validHours: 24,
      perPersonDay: 5,
      ...(v.wasThisYou ?? {}),
    },
    contacts: v.contacts ?? [],
    trusted: v.trusted ?? [],
    historyDays: v.historyDays ?? 90,
    learningDays: v.learningDays ?? 14,
    codeNotice: v.codeNotice ?? true,
    limits: { perContactDay: 20, perContactHour: 5, ...(v.limits ?? {}) },
  }
}

/** What is sent: the texts trimmed, the computed `any` of the ways left out. */
export function prepareAccess(v: AccessValues): AccessValues {
  return {
    ...v,
    excluded: v.excluded.map((e) => ({
      account: e.account.trim(),
      sensorId: e.sensorId ?? null,
      comment: e.comment.trim(),
    })),
    links: { key: v.links.key, serverAccounts: v.links.serverAccounts, username: v.links.username },
    trusted: v.trusted.map((t) => ({ name: t.name.trim(), cidr: t.cidr.trim() })),
  }
}

/** Whether any way of §5.3 links an account to a person. */
export const linksAny = (links: S['AccessAccountLinks']) => links.key || links.serverAccounts || links.username

export interface AccessWarning {
  key: string
  severity: 'warn' | 'info'
  text: string
}

/**
 * The tab's warnings (§8): from the server's state (Cookie's group's rights, PublicBaseUrl, the night, the notice's
 * level, PNMT's sign-ins, the routers, the contacts) and from the draft (no contact, no way to link accounts).
 */
export function accessWarnings(state: S['AccessAdminState'] | undefined, v: AccessValues | null): AccessWarning[] {
  const list: AccessWarning[] = []
  const add = (key: string, text: string, severity: AccessWarning['severity'] = 'warn') =>
    list.push({ key, severity, text })
  if (v && !v.contacts.length) add('no-contact', 'No contact is set: the watch reaches nobody.')
  if (state) {
    if (!state.cookieReadsLogins)
      add(
        'cookie-logins',
        "Cookie's group lacks Monitoring: Details, so Cookie can't read servers' and routers' logins. Only code's notice for strong signals works for them.",
      )
    if (!state.cookieReadsSignIns)
      add(
        'cookie-sign-ins',
        "Cookie's group lacks Audit log: View, so Cookie can't judge PNMT's sign-ins. Only code's notice for strong signals works for them.",
      )
    if (!state.publicBaseUrl && (v?.wasThisYou.on ?? true))
      add('public-base-url', "PublicBaseUrl isn't set, so Was this you? can't link to PNMT and nobody is asked.")
    if (!state.nightContact)
      add(
        'night',
        'Nobody is reached outside working hours for a suspicious login: let a contact be reached then, or set suspicious to every contact.',
      )
    if (!state.noticeAuto)
      add(
        'notice-level',
        "The access notice isn't at Auto (Actions): Cookie's notices wait in the decisions inbox. Code's notice for strong signals still goes out at once.",
      )
    if (state.pnmtOneAddress)
      add(
        'one-address',
        "Every sign-in to PNMT in the last 30 days came from one address: a proxy that doesn't forward it? Then only the device, the hours and the failures count.",
      )
    if (state.routersNotWatched > 0)
      add(
        'routers',
        state.routersNotWatched === 1
          ? "1 router isn't watched: its logins are stored and shown, nothing more."
          : `${state.routersNotWatched} routers aren't watched: their logins are stored and shown, nothing more.`,
        'info',
      )
    if (state.routersWithGap.length)
      add(
        'gap',
        `The log of ${state.routersWithGap.join(', ')} may be missing lines: a busy router can push a login out between two polls.`,
      )
    state.contacts.forEach((c, i) => add(`contact-${i}`, `${c.name} ${c.problem}`))
  }
  if (v) {
    if (!linksAny(v.links)) {
      if (v.wasThisYou.on && (v.wasThisYou.servers || v.wasThisYou.routers))
        add(
          'ask-links',
          'Was this you? is on for servers or routers, but no way links an account to a person, so nobody there is asked.',
        )
      add(
        'links',
        "No way links an account to a person: the signals that need one (a former person, not working, someone else's key) are off.",
        'info',
      )
    }
  }
  return list
}

// --- the routers (§4.2) -------------------------------------------------------------------------------------------

/** The routers' locations, by name; "Without a location" (the value "none") last when some have none. */
export function routerLocations(routers: readonly S['AccessRouterItem'][]): Option[] {
  const named = new Map<string, string>()
  let none = false
  for (const r of routers) {
    if (r.locationId) named.set(r.locationId, r.locationName ?? 'A location')
    else none = true
  }
  const options = [...named.entries()]
    .map(([value, label]) => ({ label, value }))
    .sort((a, b) => a.label.localeCompare(b.label))
  return none ? [...options, { label: 'Without a location', value: 'none' }] : options
}

/** The ids of a location's routers ("none": those without one) whose switch isn't already as asked. */
export const routersToSwitch = (routers: readonly S['AccessRouterItem'][], location: string | null, watch: boolean) =>
  routers
    .filter(
      (r) =>
        (location === null || (location === 'none' ? !r.locationId : r.locationId === location)) && r.watch !== watch,
    )
    .map((r) => r.sensorId)

// --- the contacts (§6.2) ------------------------------------------------------------------------------------------

/** A contact as one value: "user:<id>" or "group:<id>". */
export const contactKey = (c: Pick<S['AccessContact'], 'userId' | 'groupId'>) =>
  c.userId ? `user:${c.userId}` : `group:${c.groupId ?? ''}`

/** A new contact for every kind of target, reached in working hours only (the defaults of §6.2). */
export function contactOf(key: string): S['AccessContact'] | null {
  const [kind, id] = key.split(':')
  if (!id || (kind !== 'user' && kind !== 'group')) return null
  return {
    userId: kind === 'user' ? id : null,
    groupId: kind === 'group' ? id : null,
    servers: true,
    routers: true,
    pnmt: true,
    outsideHours: false,
  }
}

// --- known access (§4.4, §7) --------------------------------------------------------------------------------------

/** A target a known access entry can be for: PNMT (the value "pnmt"), or a server's or router's sensor. */
export interface TargetOption extends Option {
  kind: S['AccessTargetKind']
}

/** The add dialog as filled in. */
export interface KnownForm {
  scope: 'target' | 'location' | 'all'
  /** For a target: "pnmt" or a sensor's id. */
  target: string | null
  locationId: string | null
  /** For a location or every target: every kind (null), or only servers, routers or PNMT. */
  kind: S['AccessTargetKind'] | null
  account: string
  source: string
  groups: string[]
  until: Date | null
  comment: string
}

export const knownForm = (): KnownForm => ({
  scope: 'target',
  target: null,
  locationId: null,
  kind: null,
  account: '',
  source: '',
  groups: [],
  until: null,
  comment: '',
})

/** The server's checks (AccessService.AddKnownAsync) a form can fail before it is sent, by field. */
export function knownProblems(f: KnownForm, now: number): Record<string, string> {
  const problems: Record<string, string> = {}
  if (f.scope === 'target' && !f.target) problems.sensorId = 'Choose the target.'
  if (f.scope === 'location' && !f.locationId) problems.locationId = 'Choose the location.'
  const comment = f.comment.trim()
  if (!comment) problems.comment = 'Say why.'
  else if (comment.length > 500) problems.comment = 'At most 500 characters.'
  if (f.account.length > 520) problems.account = 'At most 520 characters.'
  if (f.until && f.until.getTime() <= now) problems.until = 'In the future, or empty for no end.'
  return problems
}

/** The request: a target's kind from its option, empty texts as any (null), no groups as any way. */
export function knownRequest(f: KnownForm, targets: readonly TargetOption[]): S['AccessKnownRequest'] {
  const pnmt = f.target === 'pnmt'
  const target =
    f.scope === 'target' ? (pnmt ? 'Pnmt' : (targets.find((t) => t.value === f.target)?.kind ?? null)) : f.kind
  return {
    scope: f.scope,
    target,
    sensorId: f.scope === 'target' && !pnmt ? f.target : null,
    locationId: f.scope === 'location' ? f.locationId : null,
    account: f.account.trim() || null,
    source: f.source.trim() || null,
    groups: f.groups.length ? [...f.groups] : null,
    until: f.until ? f.until.toISOString() : null,
    comment: f.comment.trim(),
  }
}

export type KnownState = 'active' | 'ended' | 'expired'

/** An entry ended by a person, or past its end; else it counts as history. */
export function knownState(k: Pick<S['AccessKnownItem'], 'endedAt' | 'until'>, now: number): KnownState {
  if (k.endedAt) return 'ended'
  if (k.until && new Date(k.until).getTime() <= now) return 'expired'
  return 'active'
}
