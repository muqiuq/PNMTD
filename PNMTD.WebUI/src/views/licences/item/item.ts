import {
  daysLeftText,
  daysUntil,
  kindsOf,
  type LicenseCharge,
  type LicenseDetail,
  type LicenseKind,
  type LicenseRequest,
  type LicenseTemplate,
  type LicenseType,
  type MoneyAmount,
} from '@/api/licenses'
import { formatDay } from '@/format'

/**
 * An item's page and form in the web UI (docs/licences-plan.md §8.7): the kinds' defaults (§3.3), the headline and the term's bar,
 * who hears about it (§5.2), the charges the form offers to record since the start (§6.2), and the days as yyyy-MM-dd.
 */

const DAY = 86_400_000

// --- days ---------------------------------------------------------------------------------------------------------------------

function parts(day: string): [number, number, number] {
  const [y, m, d] = day.slice(0, 10).split('-').map(Number)
  return [y, m, d]
}

/** A yyyy-MM-dd day as a count of days, for comparing and measuring. */
export function dayNumber(day: string): number {
  const [y, m, d] = parts(day)
  return Date.UTC(y, m - 1, d) / DAY
}

function iso(date: Date): string {
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}-${String(date.getUTCDate()).padStart(2, '0')}`
}

/** Today in the browser's day, as a count of days. */
export function todayNumber(now: number): number {
  const d = new Date(now)
  return Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()) / DAY
}

export const addDays = (day: string, days: number) => iso(new Date((dayNumber(day) + days) * DAY))

/** Months later (or earlier), at most the month's last day, as .NET's AddMonths: 31 Jan + 1 month is 28 Feb. */
export function addMonths(day: string, months: number): string {
  const [y, m, d] = parts(day)
  const index = y * 12 + (m - 1) + months
  const year = Math.floor(index / 12)
  const month = index - year * 12
  const last = new Date(Date.UTC(year, month + 1, 0)).getUTCDate()
  return iso(new Date(Date.UTC(year, month, Math.min(d, last))))
}

// --- the kinds (§3.3) -----------------------------------------------------------------------------------------------------------

/**
 * What a kind brings to the form (§3.3), as changes; a field it leaves out stays as it is. A purchase's and a lease's expiry is the
 * warranty's or the lease's end, so they keep it; a trial needs one, so it keeps it too.
 */
export function kindDefaults(type: LicenseType, kind: LicenseKind): Partial<LicenseRequest> {
  const once = { billing: 'OneTime', billingMonths: null } as const
  const noRenewal = { renewal: 'None', noticeDays: null, warnBeforeRenewal: false } as const
  const noExpiry = { expiresOn: null, termMonths: null, ...noRenewal } as const
  switch (kind) {
    case 'Perpetual':
    case 'OneOff':
      return { ...once, ...noExpiry }
    case 'Subscription':
      return type === 'Service'
        ? {
            billing: 'Recurring',
            billingMonths: 1,
            termMonths: 1,
            renewal: 'Automatic',
            noticeDays: null,
            warnBeforeRenewal: false,
          }
        : {
            billing: 'Recurring',
            billingMonths: 12,
            termMonths: 12,
            renewal: 'Manual',
            noticeDays: null,
            warnBeforeRenewal: false,
          }
    case 'Contract':
      return {
        billing: 'Recurring',
        billingMonths: 1,
        termMonths: 12,
        renewal: 'Automatic',
        noticeDays: 90,
        warnBeforeRenewal: true,
      }
    case 'Support':
      return {
        billing: 'Recurring',
        billingMonths: 12,
        termMonths: 12,
        renewal: 'Manual',
        noticeDays: null,
        warnBeforeRenewal: false,
      }
    case 'Usage':
      return { billing: 'Variable', billingMonths: 1, ...noExpiry }
    case 'Purchase':
      return { ...once, termMonths: null, ...noRenewal }
    case 'Lease':
      return { billing: 'Recurring', billingMonths: 1, termMonths: null, ...noRenewal }
    case 'Trial':
      return { priceAmount: null, ...once, termMonths: null, ...noRenewal }
    case 'Free':
      return { priceAmount: null, ...once, ...noExpiry }
    default:
      return {}
  }
}

/** A renewal, or a trial, needs the expiry (§3.3's validation). */
export const needsExpiry = (r: Pick<LicenseRequest, 'renewal' | 'kind'>) =>
  r.renewal === 'Manual' || r.renewal === 'Automatic' || r.kind === 'Trial'

/** The expiry the defaults fill in for a renewing item: the end of its first term from the start (or today). */
function defaultExpiry(r: LicenseRequest, today: string): string | null {
  const renews = r.renewal === 'Manual' || r.renewal === 'Automatic'
  return renews && r.termMonths ? addDays(addMonths(r.startsOn ?? today, r.termMonths), -1) : null
}

/** An expiry the defaults filled (or none) follows the new defaults; one entered by hand stays. */
function refill(before: LicenseRequest, after: LicenseRequest, today: string): LicenseRequest {
  const filled = !before.expiresOn || before.expiresOn === defaultExpiry(before, today)
  return filled ? { ...after, expiresOn: defaultExpiry(after, today) } : after
}

/** The draft with another kind and its defaults (§8.7: the form fills them in when the kind changes). */
export function withKind(draft: LicenseRequest, kind: LicenseKind, today: string): LicenseRequest {
  return refill(draft, { ...draft, kind, ...kindDefaults(draft.type ?? 'License', kind) }, today)
}

/** A licence counted in devices, cores or instances counts its seats on its hosts (§3.3); anything else doesn't. */
export const seatsFromHostsDefault = (type: LicenseType, unit: string | null | undefined) =>
  type === 'License' && ['devices', 'cores', 'instances'].includes((unit ?? 'devices').trim().toLowerCase())

const firstKind: Record<LicenseType, LicenseKind> = {
  License: 'Perpetual',
  Service: 'Subscription',
  Hardware: 'Purchase',
}

/**
 * A new item's form (§8.7): the type's first kind with its defaults, started today, reviewed every 12 months
 * (Licenses__ReviewMonths' default), the warning days and the currency left to the server's settings.
 */
export function blankRequest(type: LicenseType, today: string): LicenseRequest {
  const base: LicenseRequest = {
    type,
    name: null,
    templateId: null,
    categoryId: null,
    manufacturer: null,
    productName: null,
    edition: null,
    vendor: null,
    kind: 'Other',
    licensedTo: null,
    reference: null,
    customerNumber: null,
    portalUrl: null,
    supportContact: null,
    supportUrl: null,
    sla: null,
    costCenter: null,
    locationId: null,
    ownerId: null,
    licenseKey: null,
    productKey: null,
    seats: null,
    seatUnit: 'devices',
    seatsFromHosts: seatsFromHostsDefault(type, 'devices'),
    priceAmount: null,
    currency: null,
    priceBasis: 'Total',
    billing: 'OneTime',
    billingMonths: null,
    recordCharges: true,
    startsOn: today,
    expiresOn: null,
    termMonths: null,
    renewal: 'None',
    warnDays: null,
    noticeDays: null,
    warnBeforeRenewal: false,
    endOfLifeFromTemplate: false,
    endOfLifeOn: null,
    endOfLifeNote: null,
    eolWarnDays: null,
    reviewedOn: null,
    reviewMonths: 12,
    note: null,
    recordPastCharges: false,
    acknowledgeCredentials: false,
  }
  return withKind(base, firstKind[type], today)
}

/** The default name from a template (§3.3): its product and edition, *Microsoft 365 Business Standard*. */
export const templateName = (t: Pick<LicenseTemplate, 'productName' | 'edition'>) =>
  [t.productName, t.edition].filter((p) => p && p.trim()).join(' ')

/**
 * A draft filled from a template (§3.2): its type, category, product and end of life; its kind with the kind's defaults, then each of
 * its own defaults that it sets. The name is the template's product and edition.
 */
export function withTemplate(draft: LicenseRequest, t: LicenseTemplate, today: string): LicenseRequest {
  const type = t.type
  const kind =
    t.kind && kindsOf[type].includes(t.kind)
      ? t.kind
      : kindsOf[type].includes(draft.kind ?? 'Other')
        ? draft.kind!
        : 'Other'
  const before: LicenseRequest = { ...draft, type }
  let next: LicenseRequest = {
    ...before,
    ...kindDefaults(type, kind),
    kind,
    templateId: t.id,
    categoryId: t.categoryId,
    name: templateName(t),
    manufacturer: t.manufacturer,
    productName: t.productName,
    edition: t.edition,
    vendor: t.vendor,
    endOfLifeFromTemplate: true,
  }
  const own = <K extends keyof LicenseRequest>(key: K, value: LicenseRequest[K] | null | undefined) => {
    if (value !== null && value !== undefined) next = { ...next, [key]: value }
  }
  own('seats', t.seats)
  own('seatUnit', t.seatUnit)
  own('priceAmount', t.priceAmount)
  own('currency', t.currency)
  own('priceBasis', t.priceBasis)
  own('billing', t.billing)
  own('billingMonths', t.billingMonths)
  own('termMonths', t.termMonths)
  own('renewal', t.renewal)
  own('warnDays', t.warnDays)
  own('noticeDays', t.noticeDays)
  own('warnBeforeRenewal', t.warnBeforeRenewal)
  own('reviewMonths', t.reviewMonths)
  own('supportContact', t.supportContact)
  own('supportUrl', t.supportUrl)
  own('sla', t.sla)
  next.seatsFromHosts = t.seatsFromHosts ?? seatsFromHostsDefault(type, next.seatUnit)
  return refill(before, next, today)
}

// --- the headline and the term's bar (§8.7) --------------------------------------------------------------------------------------

type Day = (value: string | Date) => string

const inDays = (days: number) => (days === 0 ? 'today' : days === 1 ? 'tomorrow' : `in ${days} days`)
const ago = (days: number) => (days === -1 ? 'yesterday' : `${-days} days ago`)

export type HeadlineItem = Pick<
  LicenseDetail,
  'status' | 'statusChangedAt' | 'expiresOn' | 'kind' | 'renewal' | 'warnDays' | 'cancelBy'
>

/**
 * The line over the item's page (§8.7): *Valid · 35 days left*, *Expires in 5 days*, *Expired 2 days ago*, *Doesn't expire*,
 * *Renews automatically in 95 days · cancel by 2 Apr 2027*, *Warranty until 30 Apr 2028*; *Retired on …* (§4). The days are counted
 * live; the messages keep absolute days.
 */
export function headline(item: HeadlineItem, now: number, day: Day = formatDay): string {
  if (item.status === 'Retired')
    return item.statusChangedAt ? `Retired on ${day(new Date(item.statusChangedAt))}` : 'Retired'
  if (!item.expiresOn) return "Doesn't expire"
  const days = daysUntil(item.expiresOn, now)
  if (item.status === 'Cancelled')
    return days < 0 ? `Ended ${ago(days)} (cancelled)` : `Ends ${inDays(days)} (cancelled)`
  if (item.renewal === 'Automatic' && days >= 0) {
    const deadline = item.cancelBy
      ? daysUntil(item.cancelBy, now) >= 0
        ? ` · cancel by ${day(item.cancelBy)}`
        : ` · the deadline to cancel was ${day(item.cancelBy)}`
      : ''
    return `Renews automatically ${inDays(days)}${deadline}`
  }
  const warning = days <= item.warnDays
  switch (item.kind) {
    case 'Purchase':
      if (days < 0) return `Warranty ended ${ago(days)}`
      return warning ? `Warranty ends ${inDays(days)}` : `Warranty until ${day(item.expiresOn)}`
    case 'Lease':
      if (days < 0) return `Lease ended ${ago(days)}`
      return warning ? `Lease ends ${inDays(days)}` : `Lease ends on ${day(item.expiresOn)}`
    default:
      if (days < 0) return `Expired ${ago(days)}`
      if (warning) return `${item.kind === 'Trial' ? 'Trial ends' : 'Expires'} ${inDays(days)}`
      return `Valid · ${daysLeftText(item.expiresOn, now)}`
  }
}

export interface TermBar {
  start: string
  end: string
  /** Positions in percent of the term, 0 at its first day, 100 after its last. */
  today: number
  warnFrom: string | null
  warn: number | null
  cancelBy: number | null
  endOfLife: number | null
  expired: boolean
}

export type TermItem = Pick<
  LicenseDetail,
  | 'startsOn'
  | 'expiresOn'
  | 'termMonths'
  | 'warnDays'
  | 'renewal'
  | 'status'
  | 'cancelBy'
  | 'warnBeforeRenewal'
  | 'endOfLifeOn'
>

/**
 * The term as a bar (§8.7): from the current term's first day (the expiry + 1 day − the term, §6.1; never before the start) to the
 * expiry, with today and the warning's start marked, the cancellation deadline of an automatic renewal, and the end of life when it
 * falls within. Null without an expiry or a start.
 */
export function termBar(item: TermItem, now: number): TermBar | null {
  if (!item.expiresOn) return null
  const anchor = item.termMonths ? addMonths(addDays(item.expiresOn, 1), -item.termMonths) : null
  const start = anchor && item.startsOn ? (anchor > item.startsOn ? anchor : item.startsOn) : (anchor ?? item.startsOn)
  if (!start || start > item.expiresOn) return null
  const first = dayNumber(start)
  const span = dayNumber(item.expiresOn) + 1 - first
  const at = (n: number) => Math.min(100, Math.max(0, ((n - first) / span) * 100))
  const within = (day: string | null | undefined) =>
    day && day >= start && day <= item.expiresOn! ? at(dayNumber(day)) : null
  const automatic = item.renewal === 'Automatic' && item.status === 'Active'
  let warnFrom: string | null = null
  if (automatic) {
    if (item.warnBeforeRenewal && item.cancelBy) warnFrom = addDays(item.cancelBy, -item.warnDays)
  } else if (item.warnDays > 0) {
    warnFrom = addDays(item.expiresOn, -item.warnDays)
  }
  const today = todayNumber(now)
  return {
    start,
    end: item.expiresOn,
    today: at(today),
    warnFrom,
    warn: warnFrom ? at(dayNumber(warnFrom)) : null,
    cancelBy: automatic ? within(item.cancelBy) : null,
    endOfLife: within(item.endOfLifeOn),
    expired: today > dayNumber(item.expiresOn),
  }
}

// --- notifying (§5.2, §5.5) -------------------------------------------------------------------------------------------------------

export type Notifying = LicenseDetail['notifying']

/**
 * Who hears about it (§8.7): the owner by e-mail (or that the owner has no e-mail address), the hosts' sensors through their rules,
 * the people who chose items nobody owns; *nobody* otherwise. Never whether the owner turned these e-mails off.
 */
export function notifyingLines(n: Notifying): string[] {
  const lines: string[] = []
  if (n.hasOwner) lines.push(n.ownerHasEmail ? 'the owner, by e-mail' : 'the owner has no e-mail address in PNMT')
  if (n.hosts === 1) lines.push("the host's sensor, through its rules")
  else if (n.hosts > 1) lines.push(`the ${n.hosts} hosts' sensors, through their rules`)
  if (!n.hasOwner && n.unownedChoosers > 0)
    lines.push(
      n.unownedChoosers === 1
        ? '1 person who chose items nobody owns'
        : `${n.unownedChoosers} people who chose items nobody owns`,
    )
  const told = (n.hasOwner && n.ownerHasEmail) || n.hosts > 0 || (!n.hasOwner && n.unownedChoosers > 0)
  if (!told) lines.push('nobody')
  return lines
}

/** The warning on the page when nobody is told (§5.2); null when someone is. */
export function nobodyLine(n: Notifying): string | null {
  if (!n.nobody) return null
  return n.hasOwner
    ? 'On no host, and its owner has no e-mail address in PNMT, so nobody is told when it expires.'
    : 'On no host and without an owner, so nobody is told when it expires.'
}

// --- money (§6) ---------------------------------------------------------------------------------------------------------------------

export type PastChargesDraft = Pick<
  LicenseRequest,
  | 'billing'
  | 'billingMonths'
  | 'termMonths'
  | 'startsOn'
  | 'expiresOn'
  | 'priceAmount'
  | 'priceBasis'
  | 'seats'
  | 'recordCharges'
>

/**
 * How many billing periods lie between the start and today (§6.2), for the new item's offer *Also record the charges since …*: the
 * billing days stepped from the anchor (§6.1: the current term's first day with an expiry and a term, else the start), on or after the
 * start, before today (today's is recorded anyway) and not after the expiry. None for a one-time price, without a price, or while PNMT
 * doesn't record the charges.
 */
export function pastCharges(r: PastChargesDraft, today: string): number {
  const step = r.billing === 'OneTime' ? null : (r.billingMonths ?? r.termMonths)
  if (!step || step < 1 || !r.startsOn || r.startsOn >= today || r.recordCharges === false) return 0
  if (r.priceAmount === null || r.priceAmount === undefined) return 0
  if (r.priceBasis === 'PerSeat' && !r.seats) return 0
  const anchor = r.expiresOn && r.termMonths ? addMonths(addDays(r.expiresOn, 1), -r.termMonths) : r.startsOn
  const [ay, am] = parts(anchor)
  const [sy, sm] = parts(r.startsOn)
  let k = Math.floor(((sy - ay) * 12 + sm - am) / step) - 1
  let count = 0
  for (let guard = 0; guard < 100_000; guard++, k++) {
    const day = addMonths(anchor, k * step)
    if (day >= today) break
    if (day >= r.startsOn && (!r.expiresOn || day <= r.expiresOn)) count++
  }
  return count
}

/** What was spent per year (§8.7's Costs: *2026: EUR 3,630.00 · 2025: EUR 3,600.00*), newest first, per currency. */
export function spentByYear(charges: readonly Pick<LicenseCharge, 'on' | 'amount' | 'currency'>[]): {
  year: number
  amounts: MoneyAmount[]
}[] {
  const years = new Map<number, Map<string, number>>()
  for (const c of charges) {
    const year = Number(c.on.slice(0, 4))
    const sums = years.get(year) ?? new Map<string, number>()
    sums.set(c.currency, Math.round(((sums.get(c.currency) ?? 0) + c.amount) * 100) / 100)
    years.set(year, sums)
  }
  return [...years.entries()]
    .sort((a, b) => b[0] - a[0])
    .map(([year, sums]) => ({
      year,
      amounts: [...sums.entries()]
        .sort((a, b) => a[0].localeCompare(b[0]))
        .map(([currency, amount]) => ({ currency, amount })),
    }))
}

/** How often it is paid for: *one-time*, *every month*, *every 12 months*, *variable, every month*. */
export function billingText(billing: LicenseRequest['billing'], months: number | null | undefined): string {
  if (billing === 'OneTime') return 'one-time'
  const every = months === 1 ? 'every month' : months ? `every ${months} months` : 'every … months'
  return billing === 'Variable' ? `variable, ${every}` : every
}

export const monthsText = (months: number | null | undefined) =>
  months ? (months === 1 ? '1 month' : `${months} months`) : null

// --- keys (§3.4) --------------------------------------------------------------------------------------------------------------------

/** A key as the page shows it before *Show*: its last five characters, `•••••-CC9DR`. */
export const maskedKey = (hint: string | null | undefined) => (hint ? `•••••-${hint}` : '•••••')

/** The words of 422 looks_like_credential (§3.3), asked before saving anyway. */
export const credentialWarning =
  'This looks like a key. Keys belong in the licence key and product key fields, which are encrypted and shown only on request.'
