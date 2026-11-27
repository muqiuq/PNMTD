import { computed } from 'vue'
import { useRoute, useRouter, type LocationQuery, type LocationQueryRaw } from 'vue-router'
import type { ChargesQuery, CostsQuery, LicenseChargeKind, LicenseType, MoneyFilter } from '@/api/licenses'

/**
 * The money views' state in the URL (docs/licences-plan.md §8.2, §8.5): the filters both tabs share (?currency, ?type, ?category,
 * ?costCenter, ?location, ?owner), the costs' period (?from, ?to as yyyy-MM, ?years=1), grouping (?group) and what they show
 * (?costs=spent|forecast; ?show=charges is the Charges view), and the charges' own filters (?from, ?to as days, ?item, ?kind,
 * ?source, ?estimated). What the URL holds that isn't valid is ignored, so a link never breaks a view.
 */

export const filterNames = ['currency', 'type', 'category', 'costCenter', 'location', 'owner'] as const
export type FilterName = (typeof filterNames)[number]

const types: readonly LicenseType[] = ['License', 'Service', 'Hardware']
const kinds: readonly LicenseChargeKind[] = ['Purchase', 'Period', 'Extra', 'Credit']
const sources = ['Automatic', 'Person'] as const
export const costGroups = [
  'category',
  'type',
  'provider',
  'manufacturer',
  'costCenter',
  'location',
  'host',
  'owner',
  'item',
] as const
export type CostGroup = (typeof costGroups)[number]

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** The first value of a query parameter, trimmed; undefined for none or an empty one. */
function one(value: LocationQuery[string] | undefined): string | undefined {
  const first = Array.isArray(value) ? value[0] : value
  const text = typeof first === 'string' ? first.trim() : ''
  return text || undefined
}

const idOf = (value: LocationQuery[string] | undefined) => {
  const text = one(value)
  return text && uuid.test(text) ? text.toLowerCase() : undefined
}

/** The filters from the URL; a currency is three letters, ids are ids. */
export function readFilter(query: LocationQuery): MoneyFilter {
  const currency = one(query.currency)
  const type = one(query.type)
  const filter: MoneyFilter = {
    currency: currency && /^[A-Za-z]{3}$/.test(currency) ? currency.toUpperCase() : undefined,
    type: types.find((t) => t === type),
    category: idOf(query.category),
    costCenter: one(query.costCenter)?.slice(0, 100),
    location: idOf(query.location),
    owner: idOf(query.owner),
  }
  return compact(filter)
}

/** The URL's query with the filters replaced: the others (the tab's own) kept, the empty ones left out. */
export function writeFilter(query: LocationQuery | LocationQueryRaw, filter: MoneyFilter): LocationQueryRaw {
  const next: LocationQueryRaw = { ...query }
  for (const name of filterNames) {
    const value = filter[name]
    if (value) next[name] = value
    else delete next[name]
  }
  return next
}

/** The filters set, for a link to another view with the same slice. */
export function filterQuery(filter: MoneyFilter): Record<string, string> {
  const out: Record<string, string> = {}
  for (const name of filterNames) {
    const value = filter[name]
    if (value) out[name] = value
  }
  return out
}

/** How many filters are set, but the currency (one is always shown). */
export const activeFilters = (filter: MoneyFilter) => filterNames.filter((n) => n !== 'currency' && !!filter[n]).length

function compact<T extends object>(value: T): T {
  return Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== '')) as T
}

// --- the costs' period ----------------------------------------------------------------------------------------------------

/** yyyy-MM, or a day's month; undefined for anything else. */
export function monthOf(text: string | undefined): string | undefined {
  const m = text ? /^(\d{4})-(\d{2})(?:-\d{2})?$/.exec(text) : null
  if (!m) return undefined
  const month = Number(m[2])
  return month >= 1 && month <= 12 ? `${m[1]}-${m[2]}` : undefined
}

/** A month n months on (or back): addMonths('2026-11', 2) is '2027-01'. */
export function addMonths(month: string, n: number): string {
  const [y, m] = month.split('-').map(Number)
  const index = y! * 12 + (m! - 1) + n
  return `${Math.floor(index / 12)}-${String((index % 12) + 1).padStart(2, '0')}`
}

/** How many months from one to the other, both counted: monthsBetween('2026-01', '2026-12') is 12. */
export function monthsBetween(from: string, to: string): number {
  const [fy, fm] = from.split('-').map(Number)
  const [ty, tm] = to.split('-').map(Number)
  return (ty! - fy!) * 12 + (tm! - fm!) + 1
}

export type PeriodPreset = 'thisYear' | 'lastYear' | 'last12' | 'next12' | 'range' | 'years'

export interface CostsPeriod {
  preset: PeriodPreset
  /** The window's months (in years mode: this year's, unused). */
  from: string
  to: string
  years: boolean
}

/** The server takes at most 60 months (LicenseQueries.CostsAsync). */
export const maxMonths = 60

/** A preset's window, from today's month. */
export function presetWindow(preset: Exclude<PeriodPreset, 'range'>, today: string): { from: string; to: string } {
  const month = today.slice(0, 7)
  const year = Number(today.slice(0, 4))
  switch (preset) {
    case 'lastYear':
      return { from: `${year - 1}-01`, to: `${year - 1}-12` }
    case 'last12':
      return { from: addMonths(month, -11), to: month }
    case 'next12':
      return { from: addMonths(month, 1), to: addMonths(month, 12) }
    default:
      return { from: `${year}-01`, to: `${year}-12` }
  }
}

/**
 * The period from the URL: ?years=1 for the last five years as columns; ?from and ?to (yyyy-MM, or a day's month) for a window,
 * which reads as the preset it matches; neither for this year. A window that ends before it starts, or is longer than the
 * server takes, is this year.
 */
export function readPeriod(query: LocationQuery, today: string): CostsPeriod {
  const thisYear = presetWindow('thisYear', today)
  if (one(query.years) === '1' || one(query.years) === 'true') return { preset: 'years', ...thisYear, years: true }
  const from = monthOf(one(query.from))
  const to = monthOf(one(query.to)) ?? (from ? `${from.slice(0, 4)}-12` : undefined)
  if (!from || !to || to < from || monthsBetween(from, to) > maxMonths)
    return { preset: 'thisYear', ...thisYear, years: false }
  const presets = ['thisYear', 'lastYear', 'last12', 'next12'] as const
  const preset = presets.find((p) => {
    const w = presetWindow(p, today)
    return w.from === from && w.to === to
  })
  return { preset: preset ?? 'range', from, to, years: false }
}

/** The period's part of the URL: nothing for this year. */
export function periodQuery(period: Pick<CostsPeriod, 'preset' | 'from' | 'to'>): {
  from?: string
  to?: string
  years?: string
} {
  if (period.preset === 'years') return { years: '1' }
  if (period.preset === 'thisYear') return {}
  return { from: period.from, to: period.to }
}

// --- the costs' view -------------------------------------------------------------------------------------------------------

export type CostsShow = 'spent' | 'forecast' | 'both'

export interface CostsView {
  group: CostGroup
  show: CostsShow
  /** The group opened on arrival (a click on the overview's bars). */
  open?: string
  /** ?show=charges: the Charges view. */
  charges: boolean
}

export function readCostsView(query: LocationQuery): CostsView {
  const group = one(query.group)
  const costs = one(query.costs)
  return {
    group: costGroups.find((g) => g === group) ?? 'category',
    show: costs === 'spent' || costs === 'forecast' ? costs : 'both',
    open: one(query.open),
    charges: one(query.show) === 'charges',
  }
}

/** What the costs call asks: the period, the grouping, what to show and the filters. */
export function costsQuery(
  period: CostsPeriod,
  view: Pick<CostsView, 'group' | 'show'>,
  filter: MoneyFilter,
): CostsQuery {
  return compact({
    ...filter,
    from: period.years ? undefined : period.from,
    to: period.years ? undefined : period.to,
    years: period.years || undefined,
    group: view.group === 'category' ? undefined : view.group,
    show: view.show === 'both' ? undefined : view.show,
  })
}

// --- the charges' filters --------------------------------------------------------------------------------------------------

/** A day (yyyy-MM-dd); a month as its first day, or with `end` its last. */
export function dayOf(text: string | undefined, end = false): string | undefined {
  if (!text) return undefined
  const day = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text)
  if (day) {
    const date = new Date(Date.UTC(Number(day[1]), Number(day[2]) - 1, Number(day[3])))
    return date.getUTCDate() === Number(day[3]) && date.getUTCMonth() === Number(day[2]) - 1 ? text : undefined
  }
  const month = monthOf(text)
  if (!month) return undefined
  if (!end) return `${month}-01`
  const [y, m] = month.split('-').map(Number)
  const last = new Date(Date.UTC(y!, m!, 0)).getUTCDate()
  return `${month}-${String(last).padStart(2, '0')}`
}

/**
 * The Charges view's filters: the period as days (a month from the costs' window reads as its first and last day), type and
 * category from the shared filters, ?item, ?kind, ?source and ?estimated=true|false.
 */
export function readCharges(query: LocationQuery): ChargesQuery {
  const kind = one(query.kind)
  const source = one(query.source)
  const estimated = one(query.estimated)
  const filter = readFilter(query)
  return compact({
    from: dayOf(one(query.from)),
    to: dayOf(one(query.to), true),
    type: filter.type,
    category: filter.category,
    license: idOf(query.item),
    kind: kinds.find((k) => k === kind),
    source: sources.find((s) => s === source),
    estimated:
      estimated === 'true' || estimated === '1' ? true : estimated === 'false' || estimated === '0' ? false : undefined,
  })
}

/** The URL's query with the charges' filters replaced. */
export function writeCharges(query: LocationQuery | LocationQueryRaw, charges: ChargesQuery): LocationQueryRaw {
  const next: LocationQueryRaw = { ...query }
  const set = (name: string, value: string | undefined) => {
    if (value) next[name] = value
    else delete next[name]
  }
  set('from', charges.from)
  set('to', charges.to)
  set('type', charges.type)
  set('category', charges.category)
  set('item', charges.license)
  set('kind', charges.kind)
  set('source', charges.source)
  set('estimated', charges.estimated === undefined ? undefined : String(charges.estimated))
  return next
}

// --- in a component ------------------------------------------------------------------------------------------------------

/** The shared filters as a model on the URL: reading follows the route, writing replaces the query (no history entry). */
export function useUrlFilter() {
  const route = useRoute()
  const router = useRouter()
  return computed<MoneyFilter>({
    get: () => readFilter(route.query),
    set: (filter) => void router.replace({ query: writeFilter(route.query, filter) }),
  })
}
