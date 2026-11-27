import type { LocationQuery } from 'vue-router'
import {
  kindLabels,
  money,
  vendorLabel,
  type LicenseKind,
  type LicenseRow,
  type LicenseType,
  type MoneyAmount,
} from '@/api/licenses'
import { formatDay } from '@/format'

/**
 * The Licences, Services and Hardware tabs' list (docs/licences-plan.md §8.3): its state in the URL, the search, the filters, the
 * sort, the columns and the totals. Items are hundreds, not hundred-thousands, so all of it happens in the browser (decision 19).
 */

// --- sorting ----------------------------------------------------------------------------------------------------------------

/** A column's sort, kept in the URL as ?sort=expires or ?sort=-perYear. */
export interface Sort<F extends string> {
  field: F
  /** 1 ascending, -1 descending. */
  order: 1 | -1
}

/** "expires" or "-perYear" from the URL; null for none or a field the list doesn't know. */
export function parseSort<F extends string>(value: unknown, fields: readonly F[]): Sort<F> | null {
  if (typeof value !== 'string' || !value) return null
  const descending = value.startsWith('-')
  const field = (descending ? value.slice(1) : value) as F
  return fields.includes(field) ? { field, order: descending ? -1 : 1 } : null
}

export function formatSort(sort: Sort<string> | null): string | undefined {
  return sort ? `${sort.order === -1 ? '-' : ''}${sort.field}` : undefined
}

/** As people read names: case-insensitive, with numbers as numbers (Backup 2 before Backup 10). */
export const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' })

export type SortValue = string | number | null

/** In the column's order; null (nothing set) last in both directions; 0 for a tie. */
export function compareValues(a: SortValue, b: SortValue, order: 1 | -1): number {
  if (a === null || b === null) return a === b ? 0 : a === null ? 1 : -1
  if (typeof a === 'number' && typeof b === 'number') return (a - b) * order
  return collator.compare(String(a), String(b)) * order
}

/** A yyyy-MM-dd day as a number that sorts (20261231); null for none. */
export const dayValue = (day: string | null | undefined): number | null =>
  day ? Number(day.slice(0, 10).replaceAll('-', '')) : null

export const sortFields = [
  'number',
  'name',
  'product',
  'vendor',
  'hosts',
  'kind',
  'category',
  'costCenter',
  'location',
  'owner',
  'term',
  'renewal',
  'started',
  'expires',
  'eol',
  'seats',
  'price',
  'perMonth',
  'perYear',
  'reviewed',
  'updated',
] as const
export type SortField = (typeof sortFields)[number]
export type ListSort = Sort<SortField>

/** The price of one billing period, or the one-time price: the amount, times the seats when it is per seat (§6.1). */
export function unitPrice(row: Pick<LicenseRow, 'priceAmount' | 'priceBasis' | 'seats'>): number | null {
  if (row.priceAmount === null) return null
  return row.priceBasis === 'PerSeat' ? row.priceAmount * (row.seats ?? 0) : row.priceAmount
}

export const renewalLabels: Record<LicenseRow['renewal'], string> = {
  None: 'none',
  Manual: 'manual',
  Automatic: 'automatic',
}

/** Manufacturer · product edition: "Microsoft · Windows Server 2016 Standard". */
export function productText(row: Pick<LicenseRow, 'manufacturer' | 'productName' | 'edition'>): string {
  const product = [row.productName, row.edition].filter(Boolean).join(' ')
  return [row.manufacturer, product].filter(Boolean).join(' · ')
}

function valueOf(row: LicenseRow, field: SortField): SortValue {
  switch (field) {
    case 'number':
      return row.number
    case 'name':
      return row.name
    case 'product':
      return productText(row) || null
    case 'vendor':
      return row.vendor
    case 'hosts':
      return row.hosts
    case 'kind':
      return kindLabels[row.kind]
    case 'category':
      return row.categoryName
    case 'costCenter':
      return row.costCenter
    case 'location':
      return row.locationName
    case 'owner':
      return row.ownerName
    case 'term':
      return dayValue(row.cancelBy ?? row.expiresOn)
    case 'renewal':
      return renewalLabels[row.renewal]
    case 'started':
      return dayValue(row.startsOn)
    case 'expires':
      return dayValue(row.expiresOn)
    case 'eol':
      return dayValue(row.endOfLifeOn)
    case 'seats':
      return row.seats
    case 'price':
      return unitPrice(row)
    case 'perMonth':
      return row.priceAmount === null ? null : row.perMonth
    case 'perYear':
      return row.priceAmount === null ? null : row.perYear
    case 'reviewed':
      return dayValue(row.reviewedOn)
    case 'updated':
      return Date.parse(row.updatedAt)
  }
}

/**
 * The rows in the column's order (§8.3): nothing set last both ways (*doesn't expire* comes last), ties by name, then number;
 * without a sort, by number, the newest first.
 */
export function sortRows(rows: readonly LicenseRow[], sort: ListSort | null): LicenseRow[] {
  if (!sort) return [...rows].sort((a, b) => b.number - a.number)
  return [...rows].sort(
    (x, y) =>
      compareValues(valueOf(x, sort.field), valueOf(y, sort.field), sort.order) ||
      collator.compare(x.name, y.name) ||
      x.number - y.number,
  )
}

// --- the state in the URL -----------------------------------------------------------------------------------------------------

/** *Needs attention* (445, 447, 449), *failing* (446, 448), *OK* (§4). */
export type StateFilter = 'attention' | 'failing' | 'ok'
export const stateFilters: readonly StateFilter[] = ['attention', 'failing', 'ok']
export const stateLabels: Record<StateFilter, string> = { attention: 'Needs attention', failing: 'Failing', ok: 'OK' }
export const expiresWindows = [30, 90, 365] as const

/**
 * What the list shows, as the URL keeps it. The names are the links' (the overview and the host's page link to them): ?q, ?sort,
 * ?state=attention,failing, ?retired=1, ?kind=Subscription, ?category, ?manufacturer, ?vendor, ?template, ?costCenter, ?location,
 * ?owner (an id or mine), ?host, ?noHost=1, ?nobody=1, ?expires=30|90|365, ?reviewDue=1, ?noPrice=1, ?unusedSeats=1, ?pastEol=1.
 */
export interface ListState {
  q: string
  sort: ListSort | null
  state: StateFilter[]
  retired: boolean
  kind: LicenseKind[]
  category: string | null
  manufacturer: string | null
  vendor: string | null
  template: string | null
  costCenter: string | null
  location: string | null
  owner: string | null
  host: string | null
  noHost: boolean
  nobody: boolean
  expires: number | null
  reviewDue: boolean
  noPrice: boolean
  unusedSeats: boolean
  pastEol: boolean
}

export const listKeys = [
  'q',
  'sort',
  'state',
  'retired',
  'kind',
  'category',
  'manufacturer',
  'vendor',
  'template',
  'costCenter',
  'location',
  'owner',
  'host',
  'noHost',
  'nobody',
  'expires',
  'reviewDue',
  'noPrice',
  'unusedSeats',
  'pastEol',
] as const satisfies readonly (keyof ListState)[]

const allKinds = Object.keys(kindLabels) as LicenseKind[]

function one(value: LocationQuery[string] | undefined): string {
  const v = Array.isArray(value) ? value[0] : value
  return typeof v === 'string' ? v.trim() : ''
}

const list = (value: LocationQuery[string] | undefined) =>
  one(value)
    .split(',')
    .map((v) => v.trim())
    .filter(Boolean)

const flag = (value: LocationQuery[string] | undefined) => one(value) === '1' || one(value) === 'true'

export function emptyList(): ListState {
  return listFromQuery({})
}

/** A route's query, or what listToQuery wrote. */
export type Query = Record<string, LocationQuery[string] | undefined>

export function listFromQuery(query: Query): ListState {
  const expires = Number(one(query.expires))
  return {
    q: one(query.q),
    sort: parseSort(one(query.sort), sortFields),
    state: [...new Set(list(query.state))].filter((s): s is StateFilter => stateFilters.includes(s as StateFilter)),
    retired: flag(query.retired),
    kind: [...new Set(list(query.kind))].filter((k): k is LicenseKind => allKinds.includes(k as LicenseKind)),
    category: one(query.category) || null,
    manufacturer: one(query.manufacturer) || null,
    vendor: one(query.vendor) || null,
    template: one(query.template) || null,
    costCenter: one(query.costCenter) || null,
    location: one(query.location) || null,
    owner: one(query.owner) || null,
    host: one(query.host) || null,
    noHost: flag(query.noHost),
    nobody: flag(query.nobody),
    expires: (expiresWindows as readonly number[]).includes(expires) ? expires : null,
    reviewDue: flag(query.reviewDue),
    noPrice: flag(query.noPrice),
    unusedSeats: flag(query.unusedSeats),
    pastEol: flag(query.pastEol),
  }
}

/** The state's keys for the URL; undefined removes a key, so the defaults leave nothing behind. */
export function listToQuery(state: ListState): Record<(typeof listKeys)[number], string | undefined> {
  const text = (v: string | null) => (v ? v : undefined)
  const on = (v: boolean) => (v ? '1' : undefined)
  return {
    q: text(state.q.trim()),
    sort: formatSort(state.sort),
    state: state.state.length ? state.state.join(',') : undefined,
    retired: on(state.retired),
    kind: state.kind.length ? state.kind.join(',') : undefined,
    category: text(state.category),
    manufacturer: text(state.manufacturer),
    vendor: text(state.vendor),
    template: text(state.template),
    costCenter: text(state.costCenter),
    location: text(state.location),
    owner: text(state.owner),
    host: text(state.host),
    noHost: on(state.noHost),
    nobody: on(state.nobody),
    expires: state.expires ? String(state.expires) : undefined,
    reviewDue: on(state.reviewDue),
    noPrice: on(state.noPrice),
    unusedSeats: on(state.unusedSeats),
    pastEol: on(state.pastEol),
  }
}

/** The filters behind *More filters*: all but the search, the sort, the state and the retired. */
export const moreKeys = [
  'kind',
  'category',
  'manufacturer',
  'vendor',
  'template',
  'costCenter',
  'location',
  'owner',
  'host',
  'noHost',
  'nobody',
  'expires',
  'reviewDue',
  'noPrice',
  'unusedSeats',
  'pastEol',
] as const satisfies readonly (keyof ListState)[]
export type MoreKey = (typeof moreKeys)[number]

const isSet = (value: ListState[keyof ListState]) =>
  Array.isArray(value) ? value.length > 0 : value !== null && value !== false && value !== ''

export const moreSet = (state: ListState): MoreKey[] => moreKeys.filter((k) => isSet(state[k]))

/** Whether anything narrows the rows: the search or a filter (the sort and the retired don't). */
export const filtering = (state: ListState) => !!state.q.trim() || state.state.length > 0 || moreSet(state).length > 0

/** The state with one of *More filters* cleared. */
export function without(state: ListState, key: MoreKey): ListState {
  const empty = emptyList()
  return { ...state, [key]: empty[key] }
}

/** The state with every filter and the search cleared; the sort and the retired stay. */
export function cleared(state: ListState): ListState {
  return { ...emptyList(), sort: state.sort, retired: state.retired }
}

// --- the search and the filters -------------------------------------------------------------------------------------------------

const prefixedNumber = /^[a-z]+-(\d+)$/i
const bareNumber = /^\d+$/

/**
 * The search (§8.3): every word must be found in the number (LIC-12, SVC-12 and 12 alike), the name, manufacturer, product,
 * edition, vendor, licensed to, reference, customer number, cost centre, a key's last five characters (or a whole key ending in
 * them), or the names of its hosts. Never in the note (decision 18).
 */
export function matchesSearch(row: LicenseRow, q: string): boolean {
  const words = q.trim().toLowerCase().split(/\s+/).filter(Boolean)
  if (!words.length) return true
  const texts = [
    row.name,
    row.manufacturer,
    row.productName,
    row.edition,
    row.vendor,
    row.licensedTo,
    row.reference,
    row.customerNumber,
    row.costCenter,
    row.licenseKeyHint,
    row.productKeyHint,
    ...row.hostNames,
  ]
    .filter((t): t is string => !!t)
    .map((t) => t.toLowerCase())
  const hints = [row.licenseKeyHint, row.productKeyHint].filter((h): h is string => !!h).map((h) => h.toUpperCase())
  return words.every((word) => {
    const number = prefixedNumber.exec(word) ?? (bareNumber.test(word) ? [word, word] : null)
    if (number && Number(number[1]) === row.number) return true
    if (!number && row.display.toLowerCase().includes(word)) return true
    if (texts.some((t) => t.includes(word))) return true
    const letters = word.replace(/[^a-z0-9]/g, '').toUpperCase()
    return letters.length > 5 && hints.includes(letters.slice(-5))
  })
}

/** Days from one yyyy-MM-dd day to another: 0 the same day, negative before. */
export function daysBetween(from: string, to: string): number {
  const at = (day: string) => {
    const [y, m, d] = day.slice(0, 10).split('-').map(Number)
    return Date.UTC(y, m - 1, d)
  }
  return Math.round((at(to) - at(from)) / 86_400_000)
}

/** A yyyy-MM-dd day moved by days. */
export function addDays(day: string, days: number): string {
  const [y, m, d] = day.slice(0, 10).split('-').map(Number)
  return new Date(Date.UTC(y, m - 1, d + days)).toISOString().slice(0, 10)
}

export function stateOf(code: number | null | undefined): StateFilter | null {
  if (code === null || code === undefined) return null
  if (code < 400) return 'ok'
  return code === 446 || code === 448 ? 'failing' : 'attention'
}

const same = (a: string | null, b: string) => a !== null && a.toLowerCase() === b.toLowerCase()

export interface FilterContext {
  /** yyyy-MM-dd, the browser's day. */
  today: string
  /** For ?owner=mine. */
  userId?: string | null
  /** The ids of the items on ?host, once loaded. */
  hostItems?: ReadonlySet<string> | null
}

/** Whether a row passes the filters (not the search). */
export function matchesFilters(row: LicenseRow, s: ListState, ctx: FilterContext): boolean {
  if (s.state.length) {
    const state = stateOf(row.verdictCode)
    if (!state || !s.state.includes(state)) return false
  }
  if (!s.retired && row.status === 'Retired') return false
  if (s.kind.length && !s.kind.includes(row.kind)) return false
  if (s.category && row.categoryId !== s.category) return false
  if (s.manufacturer && !same(row.manufacturer, s.manufacturer)) return false
  if (s.vendor && !same(row.vendor, s.vendor)) return false
  if (s.template && row.templateId !== s.template) return false
  if (s.costCenter && !same(row.costCenter, s.costCenter)) return false
  if (s.location && row.locationId !== s.location) return false
  if (s.owner && row.ownerId !== (s.owner === 'mine' ? (ctx.userId ?? '') : s.owner)) return false
  if (s.host && !ctx.hostItems?.has(row.id)) return false
  if (s.noHost && row.hosts > 0) return false
  if (s.nobody && !row.nobodyNotified) return false
  if (s.expires) {
    if (!row.expiresOn) return false
    const days = daysBetween(ctx.today, row.expiresOn)
    if (days < 0 || days > s.expires) return false
  }
  if (s.reviewDue && !row.reviewDue) return false
  if (s.noPrice && (row.priceAmount !== null || row.kind === 'Free' || row.kind === 'Trial')) return false
  if (s.unusedSeats && !(row.seatsFromHosts && row.seats !== null && row.usedSeats < row.seats)) return false
  if (s.pastEol && !(row.endOfLifeOn && row.endOfLifeOn < ctx.today)) return false
  return true
}

/** The rows the list shows: searched, filtered and sorted. */
export function shownRows(rows: readonly LicenseRow[], s: ListState, ctx: FilterContext): LicenseRow[] {
  return sortRows(
    rows.filter((r) => matchesSearch(r, s.q) && matchesFilters(r, s, ctx)),
    s.sort,
  )
}

// --- the totals ------------------------------------------------------------------------------------------------------------------

const cents = (amount: number) => Math.round(amount * 100) / 100

/** Amounts per currency, the largest first; none of 0. Never converted (§6.6). */
export function perCurrency(entries: Iterable<{ currency: string; amount: number }>): MoneyAmount[] {
  const sums = new Map<string, number>()
  for (const e of entries) sums.set(e.currency, (sums.get(e.currency) ?? 0) + e.amount)
  return [...sums]
    .map(([currency, amount]) => ({ currency, amount: cents(amount) }))
    .filter((m) => m.amount !== 0)
    .sort((a, b) => b.amount - a.amount || a.currency.localeCompare(b.currency))
}

export interface ListTotals {
  count: number
  perYear: MoneyAmount[]
  /** The one-time purchases that start this year. */
  oneTime: MoneyAmount[]
}

/** Under the table, for the rows shown (§8.3): the count, the running cost per year, and this year's one-time purchases. */
export function listTotals(rows: readonly LicenseRow[], today: string): ListTotals {
  const year = today.slice(0, 4)
  return {
    count: rows.length,
    perYear: perCurrency(rows.map((r) => ({ currency: r.currency, amount: r.perYear }))),
    oneTime: perCurrency(
      rows
        .filter((r) => r.billing === 'OneTime' && r.startsOn?.slice(0, 4) === year)
        .map((r) => ({ currency: r.currency, amount: unitPrice(r) ?? 0 })),
    ),
  }
}

export const moneyList = (amounts: readonly MoneyAmount[]) =>
  amounts.map((m) => money(m.amount, m.currency)).join(' · ')

const countWords: Record<LicenseType, [string, string]> = {
  License: ['licence', 'licences'],
  Service: ['service', 'services'],
  Hardware: ['device', 'devices'],
}

/** "1 licence", "42 licences", "3 devices". */
export const countText = (count: number, type: LicenseType) => `${count} ${countWords[type][count === 1 ? 0 : 1]}`

/** "42 licences · per year: EUR 18,420.00 · CHF 2,300.00 · one-time this year: EUR 890.00". */
export function totalsText(totals: ListTotals, type: LicenseType): string {
  const parts = [countText(totals.count, type)]
  if (totals.perYear.length) parts.push(`per year: ${moneyList(totals.perYear)}`)
  if (totals.oneTime.length) parts.push(`one-time this year: ${moneyList(totals.oneTime)}`)
  return parts.join(' · ')
}

// --- the columns -------------------------------------------------------------------------------------------------------------------

/** Every column but the number and the name, which are always there. */
export type ColumnKey = Exclude<SortField, 'number' | 'name'>

/** The columns in the order a type shows them: its own first (§8.3's mock-ups), then the others. */
const columnOrder: Record<LicenseType, ColumnKey[]> = {
  License: [
    'product',
    'vendor',
    'hosts',
    'expires',
    'eol',
    'perYear',
    'reviewed',
    'kind',
    'category',
    'costCenter',
    'location',
    'owner',
    'term',
    'renewal',
    'started',
    'seats',
    'price',
    'perMonth',
    'updated',
  ],
  Service: [
    'vendor',
    'category',
    'location',
    'term',
    'perMonth',
    'owner',
    'product',
    'hosts',
    'kind',
    'costCenter',
    'renewal',
    'started',
    'expires',
    'eol',
    'seats',
    'price',
    'perYear',
    'reviewed',
    'updated',
  ],
  Hardware: [
    'product',
    'hosts',
    'kind',
    'started',
    'expires',
    'eol',
    'price',
    'vendor',
    'category',
    'costCenter',
    'location',
    'owner',
    'term',
    'renewal',
    'seats',
    'perMonth',
    'perYear',
    'reviewed',
    'updated',
  ],
}

const defaults: Record<LicenseType, ColumnKey[]> = {
  License: ['product', 'vendor', 'hosts', 'expires', 'eol', 'perYear', 'reviewed'],
  Service: ['vendor', 'category', 'location', 'term', 'perMonth', 'owner'],
  Hardware: ['product', 'hosts', 'kind', 'started', 'expires', 'eol', 'price'],
}

export function columnLabel(key: ColumnKey, type: LicenseType): string {
  switch (key) {
    case 'product':
      return type === 'Hardware' ? 'Model' : 'Product'
    case 'vendor':
      return vendorLabel(type)
    case 'hosts':
      return type === 'Hardware' ? 'Host' : 'Hosts'
    case 'kind':
      return 'Kind'
    case 'category':
      return 'Category'
    case 'costCenter':
      return 'Cost centre'
    case 'location':
      return 'Location'
    case 'owner':
      return 'Owner'
    case 'term':
      return 'Term'
    case 'renewal':
      return 'Renewal'
    case 'started':
      return 'Started'
    case 'expires':
      return type === 'Hardware' ? 'Warranty / lease ends' : 'Expires'
    case 'eol':
      return 'EOL'
    case 'seats':
      return 'Seats'
    case 'price':
      return 'Price'
    case 'perMonth':
      return 'Per month'
    case 'perYear':
      return 'Per year'
    case 'reviewed':
      return 'Reviewed'
    case 'updated':
      return 'Updated'
  }
}

export const columnsOf = (type: LicenseType) => columnOrder[type].map((key) => ({ key, label: columnLabel(key, type) }))
export const defaultColumns = (type: LicenseType): ColumnKey[] => [...defaults[type]]

const columnsStorage = (type: LicenseType) => `pnmt.licences.columns.${type}`

/** The columns a person chose for a type, in the browser (§8.3); the type's defaults until then. */
export function loadColumns(type: LicenseType, storage: Pick<Storage, 'getItem'> = localStorage): ColumnKey[] {
  try {
    const stored = JSON.parse(storage.getItem(columnsStorage(type)) ?? 'null') as unknown
    if (Array.isArray(stored)) return columnOrder[type].filter((k) => stored.includes(k))
  } catch {
    // Not JSON: the defaults.
  }
  return defaultColumns(type)
}

export function saveColumns(
  type: LicenseType,
  keys: readonly ColumnKey[],
  storage: Pick<Storage, 'setItem'> = localStorage,
) {
  try {
    storage.setItem(columnsStorage(type), JSON.stringify(keys))
  } catch {
    // Storage full or off: the choice lasts this visit.
  }
}

/** The chosen columns in the type's order. */
export const ordered = (type: LicenseType, keys: readonly ColumnKey[]) =>
  columnOrder[type].filter((k) => keys.includes(k))

// --- the cells ---------------------------------------------------------------------------------------------------------------------

export type Tone = 'warn' | 'danger' | null

/** Expires (§8.3): "31 Dec 2026 · 35 d", "1 Mar 2027 · auto", "14 Nov 2026 · expired", "doesn't expire". */
export function expiresCell(row: LicenseRow, today: string): { day: string | null; extra: string; tone: Tone } {
  if (!row.expiresOn) return { day: null, extra: "doesn't expire", tone: null }
  const day = formatDay(row.expiresOn)
  if (row.renewal === 'Automatic' && row.status === 'Active') return { day, extra: 'auto', tone: null }
  const days = daysBetween(today, row.expiresOn)
  if (days < 0) return { day, extra: 'expired', tone: 'danger' }
  return { day, extra: `${days} d`, tone: row.verdictCode === 445 ? 'warn' : null }
}

/** Hosts (§8.3): "3", "40/50" for seats counted on hosts, "7/5" over-assigned (with ⚠, in the warn tone), "–" on none. */
export function hostsCell(row: LicenseRow): { text: string; over: boolean; title: string } {
  const hosts = `${row.hosts} host${row.hosts === 1 ? '' : 's'}`
  if (row.seatsFromHosts && row.seats !== null) {
    const over = row.usedSeats > row.seats
    return {
      text: `${row.usedSeats}/${row.seats}`,
      over,
      title: `${hosts} · ${row.usedSeats} of ${row.seats} ${row.seatUnit}${over ? `, over by ${row.usedSeats - row.seats}` : ''}`,
    }
  }
  return { text: row.hosts ? String(row.hosts) : '–', over: false, title: row.hostNames.join(', ') || 'On no host' }
}

/** Per year or per month (§8.3): "EUR 3,960.00", "~EUR 1,100.00" for a variable price, "one-time", "free", "–" unknown. */
export function periodPrice(row: LicenseRow, per: 'year' | 'month'): string {
  if (row.priceAmount === null) return row.kind === 'Free' ? 'free' : '–'
  if (row.billing === 'OneTime') return 'one-time'
  const amount = money(per === 'year' ? row.perYear : row.perMonth, row.currency)
  return row.billing === 'Variable' ? `~${amount}` : amount
}

/** Hardware's price (§8.3): one-time, or per month. */
export function priceText(row: LicenseRow): string {
  const unit = unitPrice(row)
  if (unit === null) return row.kind === 'Free' ? 'free' : '–'
  if (row.billing === 'OneTime') return money(unit, row.currency)
  return `${row.billing === 'Variable' ? '~' : ''}${money(row.perMonth, row.currency)} a month`
}

/**
 * A service's term (§8.3), one or two lines: "renews 1 Jul 2027" and "cancel by 2 Apr 2027", "renews monthly", "usage",
 * "ends 31 Dec 2026 (cancelled)", "expires 31 Dec 2026", "one-off", "no end".
 */
export function termLines(row: LicenseRow, today: string): string[] {
  const cancelBy = row.cancelBy && row.cancelBy >= today ? `cancel by ${formatDay(row.cancelBy)}` : null
  if (row.expiresOn) {
    if (row.status === 'Cancelled') return [`ends ${formatDay(row.expiresOn)} (cancelled)`]
    if (row.renewal === 'Automatic') {
      const renews = row.termMonths === 1 ? 'renews monthly' : `renews ${formatDay(addDays(row.expiresOn, 1))}`
      return cancelBy ? [renews, cancelBy] : [renews]
    }
    return [`${row.renewal === 'None' ? 'ends' : 'expires'} ${formatDay(row.expiresOn)}`]
  }
  if (row.kind === 'Usage' || row.billing === 'Variable') return ['usage']
  if (row.kind === 'OneOff') return ['one-off']
  return ['no end']
}
