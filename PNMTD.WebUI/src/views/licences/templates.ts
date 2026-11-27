import {
  kindLabels,
  money,
  typeLabels,
  type LicenseBilling,
  type LicenseKind,
  type LicenseNames,
  type LicensePriceBasis,
  type LicenseRenewal,
  type LicenseTemplate,
  type LicenseTemplateRequest,
  type LicenseType,
} from '@/api/licenses'
import { collator, compareValues, dayValue, type Sort, type SortValue } from './licenceList'

/**
 * The Templates tab's helpers (docs/licences-plan.md §8.6): the templates' sort, search and words; a template's draft and its
 * request; the categories' order; the names' renames and merges.
 */

// --- the templates' list ---------------------------------------------------------------------------------------------------------

export const templateSortFields = [
  'name',
  'type',
  'category',
  'manufacturer',
  'product',
  'edition',
  'vendor',
  'kind',
  'term',
  'price',
  'eol',
  'items',
] as const
export type TemplateSortField = (typeof templateSortFields)[number]
export type TemplateSort = Sort<TemplateSortField>

function valueOf(t: LicenseTemplate, field: TemplateSortField): SortValue {
  switch (field) {
    case 'name':
      return t.name
    case 'type':
      return typeLabels[t.type]
    case 'category':
      return t.categoryName
    case 'manufacturer':
      return t.manufacturer
    case 'product':
      return t.productName
    case 'edition':
      return t.edition
    case 'vendor':
      return t.vendor
    case 'kind':
      return t.kind ? kindLabels[t.kind] : null
    case 'term':
      return t.termMonths
    case 'price':
      return t.priceAmount
    case 'eol':
      return dayValue(t.endOfLifeOn)
    case 'items':
      return t.items
  }
}

/** In the column's order, nothing set last both ways, ties by name; by name without a sort (the server's order). */
export function sortTemplates(list: readonly LicenseTemplate[], sort: TemplateSort | null): LicenseTemplate[] {
  if (!sort) return [...list]
  return [...list].sort(
    (x, y) =>
      compareValues(valueOf(x, sort.field), valueOf(y, sort.field), sort.order) || collator.compare(x.name, y.name),
  )
}

/** Every word in the name, type, category, manufacturer, product, edition or vendor. */
export function matchesTemplate(t: LicenseTemplate, q: string): boolean {
  const words = q.trim().toLowerCase().split(/\s+/).filter(Boolean)
  const texts = [t.name, typeLabels[t.type], t.categoryName, t.manufacturer, t.productName, t.edition, t.vendor]
    .filter((x): x is string => !!x)
    .map((x) => x.toLowerCase())
  return words.every((w) => texts.some((x) => x.includes(w)))
}

const months = (n: number) => (n === 1 ? '1 month' : `${n} months`)

const renewalWords: Record<LicenseRenewal, string> = {
  None: 'not renewed',
  Manual: 'renewed by hand',
  Automatic: 'renews automatically',
}

/** The default term: "12 months · renews automatically", "–" without one. */
export function termText(t: Pick<LicenseTemplate, 'termMonths' | 'renewal'>): string {
  const parts = [t.termMonths ? months(t.termMonths) : null, t.renewal ? renewalWords[t.renewal] : null].filter(Boolean)
  return parts.length ? parts.join(' · ') : '–'
}

/** How often: "a month", "a year", "every 3 months". */
export const periodText = (n: number) => (n === 1 ? 'a month' : n === 12 ? 'a year' : `every ${months(n)}`)

/** The default billing and price: "EUR 330.00 a month", "~EUR 1,100.00 a month", "EUR 12.50 per seat a year", "EUR 890.00 one-time". */
export function priceText(
  t: Pick<LicenseTemplate, 'priceAmount' | 'currency' | 'priceBasis' | 'billing' | 'billingMonths'>,
): string {
  const how =
    t.billing === 'OneTime'
      ? 'one-time'
      : t.billing && t.billingMonths
        ? periodText(t.billingMonths)
        : t.billing === 'Variable'
          ? 'variable'
          : ''
  if (t.priceAmount === null) return how ? how : '–'
  const amount = money(t.priceAmount, t.currency ?? '').trim()
  return [`${t.billing === 'Variable' ? '~' : ''}${amount}`, t.priceBasis === 'PerSeat' ? 'per seat' : '', how]
    .filter(Boolean)
    .join(' ')
}

// --- a template's draft -----------------------------------------------------------------------------------------------------------

/** The dialog's fields: text as text ('' for none), the defaults as null for *no default*. */
export interface TemplateDraft {
  name: string
  type: LicenseType
  categoryId: string | null
  manufacturer: string | null
  productName: string
  edition: string
  vendor: string | null
  productUrl: string
  kind: LicenseKind | null
  seats: number | null
  seatUnit: string | null
  seatsFromHosts: boolean | null
  priceAmount: number | null
  currency: string
  priceBasis: LicensePriceBasis | null
  billing: LicenseBilling | null
  billingMonths: number | null
  termMonths: number | null
  renewal: LicenseRenewal | null
  warnDays: number | null
  noticeDays: number | null
  warnBeforeRenewal: boolean | null
  reviewMonths: number | null
  supportContact: string
  supportUrl: string
  sla: string
  endOfLifeOn: string | null
  endOfLifeNote: string
  note: string
}

/** A template's fields copied (never the query's object itself), or an empty one of a type. */
export function templateDraft(t: LicenseTemplate | null, type: LicenseType = 'License'): TemplateDraft {
  return {
    name: t?.name ?? '',
    type: t?.type ?? type,
    categoryId: t?.categoryId ?? null,
    manufacturer: t?.manufacturer ?? null,
    productName: t?.productName ?? '',
    edition: t?.edition ?? '',
    vendor: t?.vendor ?? null,
    productUrl: t?.productUrl ?? '',
    kind: t?.kind ?? null,
    seats: t?.seats ?? null,
    seatUnit: t?.seatUnit ?? null,
    seatsFromHosts: t?.seatsFromHosts ?? null,
    priceAmount: t?.priceAmount ?? null,
    currency: t?.currency ?? '',
    priceBasis: t?.priceBasis ?? null,
    billing: t?.billing ?? null,
    billingMonths: t?.billingMonths ?? null,
    termMonths: t?.termMonths ?? null,
    renewal: t?.renewal ?? null,
    warnDays: t?.warnDays ?? null,
    noticeDays: t?.noticeDays ?? null,
    warnBeforeRenewal: t?.warnBeforeRenewal ?? null,
    reviewMonths: t?.reviewMonths ?? null,
    supportContact: t?.supportContact ?? '',
    supportUrl: t?.supportUrl ?? '',
    sla: t?.sla ?? '',
    endOfLifeOn: t?.endOfLifeOn ?? null,
    endOfLifeNote: t?.endOfLifeNote ?? '',
    note: t?.note ?? '',
  }
}

const trimmed = (text: string | null | undefined) => text?.trim() || null

/** The request: text trimmed, empty as null; the currency in capitals; a one-time price has no billing period. */
export function templateRequest(d: TemplateDraft): LicenseTemplateRequest {
  return {
    name: trimmed(d.name),
    type: d.type,
    categoryId: d.categoryId,
    manufacturer: trimmed(d.manufacturer),
    productName: trimmed(d.productName),
    edition: trimmed(d.edition),
    vendor: trimmed(d.vendor),
    productUrl: trimmed(d.productUrl),
    kind: d.kind,
    seats: d.seats,
    seatUnit: trimmed(d.seatUnit),
    seatsFromHosts: d.seatsFromHosts,
    priceAmount: d.priceAmount,
    currency: trimmed(d.currency)?.toUpperCase() ?? null,
    priceBasis: d.priceBasis,
    billing: d.billing,
    billingMonths: d.billing === 'OneTime' ? null : d.billingMonths,
    termMonths: d.termMonths,
    renewal: d.renewal,
    warnDays: d.warnDays,
    noticeDays: d.noticeDays,
    warnBeforeRenewal: d.warnBeforeRenewal,
    reviewMonths: d.reviewMonths,
    supportContact: trimmed(d.supportContact),
    supportUrl: trimmed(d.supportUrl),
    sla: trimmed(d.sla),
    endOfLifeOn: d.endOfLifeOn,
    endOfLifeNote: trimmed(d.endOfLifeNote),
    note: trimmed(d.note),
  }
}

/** Whether the end of life (its day or its note) differs from the template's: its followers are evaluated again (§8.6). */
export function endOfLifeChanged(t: LicenseTemplate | null, d: TemplateDraft): boolean {
  if (!t) return false
  return (t.endOfLifeOn ?? null) !== (d.endOfLifeOn ?? null) || (t.endOfLifeNote ?? null) !== trimmed(d.endOfLifeNote)
}

// --- the categories and the names -------------------------------------------------------------------------------------------------

/** The ids with one moved up (-1) or down (+1); the same order at either end. */
export function moved(ids: readonly string[], id: string, by: -1 | 1): string[] {
  const list = [...ids]
  const at = list.indexOf(id)
  const to = at + by
  if (at < 0 || to < 0 || to >= list.length) return list
  ;[list[at], list[to]] = [list[to], list[at]]
  return list
}

export type NameField = keyof LicenseNames

/** The rename's field for each list (§8.6). */
export const renameFields: Record<NameField, 'manufacturer' | 'vendor' | 'costCenter' | 'seatUnit'> = {
  manufacturers: 'manufacturer',
  vendors: 'vendor',
  costCenters: 'costCenter',
  seatUnits: 'seatUnit',
}

export const nameLabels: Record<NameField, { plural: string; one: string }> = {
  manufacturers: { plural: 'Manufacturers', one: 'manufacturer' },
  vendors: { plural: 'Vendors and providers', one: 'vendor or provider' },
  costCenters: { plural: 'Cost centres', one: 'cost centre' },
  seatUnits: { plural: 'Seat units', one: 'seat unit' },
}

/** The name in use a rename merges into: the same name exactly, not the one renamed. */
export function mergeTarget<T extends { name: string }>(names: readonly T[], from: string, to: string): T | null {
  const target = to.trim()
  if (!target || target === from) return null
  return names.find((n) => n.name === target) ?? null
}

/** "14 items and 2 templates", "1 item", "nothing". */
export function usesText(items: number, templates: number): string {
  const parts = [
    items ? `${items} item${items === 1 ? '' : 's'}` : null,
    templates ? `${templates} template${templates === 1 ? '' : 's'}` : null,
  ].filter(Boolean)
  return parts.length ? parts.join(' and ') : 'nothing'
}
