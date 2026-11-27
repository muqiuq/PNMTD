import { call, client, readCookie, type Schemas } from './client'

/**
 * Licences & services' calls (docs/licences-plan.md §10.1): the items, their hosts, links, renewals, charges, documents and keys; the
 * money views; the templates and categories. Every call answers 404 licenses_disabled while Licenses__Enabled is off (§13.2), so the
 * UI asks GET /system/info's `licenses` before showing any of it. Kept beside resources.ts, as responsibilities.ts is.
 */

type S = Schemas
type Id = string

export type LicenseRow = S['LicenseRow']
export type LicenseDetail = S['LicenseDetail']
export type LicenseRequest = S['LicenseRequest']
export type LicenseType = S['LicenseType']
export type LicenseKind = S['LicenseKind']
export type LicenseStatus = S['LicenseStatus']
export type LicenseBilling = S['LicenseBilling']
export type LicenseRenewal = S['LicenseRenewal']
export type LicensePriceBasis = S['LicensePriceBasis']
export type LicenseFinding = S['LicenseFindingItem']
export type LicenseAssignment = S['LicenseAssignmentItem']
export type LicenseSensorLink = S['LicenseSensorLinkItem']
export type LicenseRenewalItem = S['LicenseRenewalItem']
export type LicenseRenewRequest = S['LicenseRenewRequest']
export type LicenseCharge = S['LicenseChargeItem']
export type LicenseChargeRequest = S['LicenseChargeRequest']
export type LicenseChargeKind = S['LicenseChargeKind']
export type LicenseDocument = S['LicenseDocumentItem']
export type LicenseDocumentKind = S['LicenseDocumentKind']
export type LicenseTemplate = S['LicenseTemplateItem']
export type LicenseTemplateRequest = S['LicenseTemplateRequest']
export type LicenseCategory = S['LicenseCategoryItem']
export type LicenseNames = S['LicenseNames']
export type LicensePerson = S['LicensePerson']
export type LicenseTarget = S['LicenseTarget']
export type LicenseLinkedItem = S['LicenseLinkedItem']
export type LicenseOverview = S['LicenseOverview']
export type LicenseCosts = S['LicenseCosts']
export type LicenseCostLine = S['LicenseCostLine']
export type LicenseUpcoming = S['LicenseUpcoming']
export type LicenseUpcomingEntry = S['LicenseUpcomingEntry']
export type LicenseSensorSummary = S['LicenseSensorSummary']
export type LinkedItemSummary = S['LinkedItemSummary']
export type MoneyAmount = S['MoneyAmount']
export type LicenseActivation = S['LicenseActivation']

/** The filters every money view shares (§8.2), kept in the URL. */
export interface MoneyFilter {
  currency?: string
  type?: LicenseType
  category?: Id
  costCenter?: string
  location?: Id
  owner?: Id
}

export interface CostsQuery extends MoneyFilter {
  from?: string
  to?: string
  group?: string
  show?: 'spent' | 'forecast' | 'both'
  years?: boolean
}

export interface ChargesQuery {
  from?: string
  to?: string
  type?: LicenseType
  category?: Id
  license?: Id
  kind?: LicenseChargeKind
  source?: S['LicenseChargeSource']
  estimated?: boolean
  currency?: string
}

const path = (id: Id) => ({ params: { path: { id } } })

/** A query string of the defined values, for the CSV links (the browser downloads them itself). */
export function queryString(query: object): string {
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(query)) {
    if (Array.isArray(value)) value.forEach((v) => params.append(key, String(v)))
    else if (value !== undefined && value !== null && value !== '') params.set(key, String(value))
  }
  const text = params.toString()
  return text ? `?${text}` : ''
}

export const licensesApi = {
  list: (query: { type?: LicenseType; status?: LicenseStatus[] } = {}) =>
    call(client.GET('/api/v1/licenses', { params: { query } })),
  exportUrl: (query: { type?: LicenseType; status?: LicenseStatus[]; ids?: Id[] } = {}) =>
    `/api/v1/licenses/export.csv${queryString(query)}`,
  overview: (query: MoneyFilter = {}) => call(client.GET('/api/v1/licenses/overview', { params: { query } })),
  costs: (query: CostsQuery = {}) => call(client.GET('/api/v1/licenses/costs', { params: { query } })),
  costsExportUrl: (query: CostsQuery = {}) => `/api/v1/licenses/costs/export.csv${queryString(query)}`,
  charges: (query: ChargesQuery = {}) => call(client.GET('/api/v1/licenses/charges', { params: { query } })),
  chargesExportUrl: (query: ChargesQuery = {}) => `/api/v1/licenses/charges/export.csv${queryString(query)}`,
  upcoming: (query: { months?: number; type?: LicenseType; owner?: Id } = {}) =>
    call(client.GET('/api/v1/licenses/upcoming', { params: { query } })),
  names: () => call(client.GET('/api/v1/licenses/names')),
  people: () => call(client.GET('/api/v1/licenses/people')),
  /** A host's items, or a location's (its own and its hosts', below it too): the panels (§8.8). */
  linked: (query: { host?: Id; location?: Id }) => call(client.GET('/api/v1/licenses/linked', { params: { query } })),
  /** Windows activation matched by a key's last five characters (§8.11): an item's hosts that use its key, or a host's matches. */
  activations: (query: { license?: Id; host?: Id }) =>
    call(client.GET('/api/v1/licenses/activations', { params: { query } })),
  /** Hosts to assign or sensors to link, by name, without Monitoring: View (Edit). */
  targets: (kind: 'host' | 'sensor', q: string, license?: Id) =>
    call(client.GET('/api/v1/licenses/targets', { params: { query: { kind, q, license } } })),
  get: (id: Id) => call(client.GET('/api/v1/licenses/{id}', path(id))),
  byNumber: (number: number) => call(client.GET('/api/v1/licenses/number/{number}', { params: { path: { number } } })),
  /** Both keys, shown: audited. 409 key_lost. */
  keys: (id: Id) => call(client.GET('/api/v1/licenses/{id}/keys', path(id))),
  history: (id: Id, page = 1, pageSize = 50) =>
    call(client.GET('/api/v1/licenses/{id}/history', { params: { path: { id }, query: { page, pageSize } } })),
  /** 422 looks_like_credential for a note or support contact that looks like a key, unless acknowledged. */
  create: (body: LicenseRequest) => call(client.POST('/api/v1/licenses', { body })),
  duplicate: (id: Id) => call(client.POST('/api/v1/licenses/{id}/duplicate', path(id))),
  update: (id: Id, body: LicenseRequest) => call(client.PUT('/api/v1/licenses/{id}', { ...path(id), body })),
  renew: (id: Id, body: LicenseRenewRequest) => call(client.POST('/api/v1/licenses/{id}/renew', { ...path(id), body })),
  review: (id: Id, body: { on?: string | null; note?: string | null }) =>
    call(
      client.POST('/api/v1/licenses/{id}/review', {
        ...path(id),
        body: { on: body.on ?? null, note: body.note ?? null, ids: null },
      }),
    ),
  reviewMany: (ids: Id[], body: { on?: string | null; note?: string | null }) =>
    call(client.POST('/api/v1/licenses/review', { body: { on: body.on ?? null, note: body.note ?? null, ids } })),
  cancel: (id: Id, endsOn: string | null) =>
    call(client.POST('/api/v1/licenses/{id}/cancel', { ...path(id), body: { endsOn } })),
  reactivate: (id: Id) => call(client.POST('/api/v1/licenses/{id}/reactivate', path(id))),
  /** 409 confirm_needed while it is on hosts and not confirmed. */
  retire: (id: Id, confirmed: boolean) =>
    call(client.POST('/api/v1/licenses/{id}/retire', { ...path(id), body: { confirmed } })),
  remove: (id: Id, number: string) =>
    call(client.DELETE('/api/v1/licenses/{id}', { params: { path: { id }, query: { number } } })),
  assignments: (id: Id) => call(client.GET('/api/v1/licenses/{id}/assignments', path(id))),
  assign: (id: Id, hostIds: Id[], seats?: number | null, note?: string | null) =>
    call(
      client.POST('/api/v1/licenses/{id}/assignments', {
        ...path(id),
        body: { hostIds, seats: seats ?? null, note: note ?? null },
      }),
    ),
  updateAssignment: (id: Id, hostId: Id, seats: number | null, note: string | null) =>
    call(
      client.PUT('/api/v1/licenses/{id}/assignments/{hostId}', {
        params: { path: { id, hostId } },
        body: { seats, note },
      }),
    ),
  unassign: (id: Id, hostIds: Id[]) =>
    call(client.POST('/api/v1/licenses/{id}/assignments/remove', { ...path(id), body: { hostIds } })),
  sensorLinks: (id: Id) => call(client.GET('/api/v1/licenses/{id}/sensor-links', path(id))),
  link: (id: Id, sensorIds: Id[], note?: string | null) =>
    call(client.POST('/api/v1/licenses/{id}/sensor-links', { ...path(id), body: { sensorIds, note: note ?? null } })),
  unlink: (id: Id, sensorId: Id) =>
    call(client.DELETE('/api/v1/licenses/{id}/sensor-links/{sensorId}', { params: { path: { id, sensorId } } })),
  renewals: (id: Id) => call(client.GET('/api/v1/licenses/{id}/renewals', path(id))),
  updateRenewal: (id: Id, renewalId: Id, reference: string | null, note: string | null) =>
    call(
      client.PUT('/api/v1/licenses/{id}/renewals/{renewalId}', {
        params: { path: { id, renewalId } },
        body: { reference, note },
      }),
    ),
  deleteRenewal: (id: Id, renewalId: Id) =>
    call(client.DELETE('/api/v1/licenses/{id}/renewals/{renewalId}', { params: { path: { id, renewalId } } })),
  itemCharges: (id: Id) => call(client.GET('/api/v1/licenses/{id}/charges', path(id))),
  recordCharge: (id: Id, body: LicenseChargeRequest) =>
    call(client.POST('/api/v1/licenses/{id}/charges', { ...path(id), body })),
  updateCharge: (id: Id, chargeId: Id, body: LicenseChargeRequest) =>
    call(client.PUT('/api/v1/licenses/{id}/charges/{chargeId}', { params: { path: { id, chargeId } }, body })),
  /** A person's charge (Manage); 409 automatic_charge for PNMT's own. */
  deleteCharge: (id: Id, chargeId: Id) =>
    call(client.DELETE('/api/v1/licenses/{id}/charges/{chargeId}', { params: { path: { id, chargeId } } })),
  documents: (id: Id) => call(client.GET('/api/v1/licenses/{id}/documents', path(id))),
  updateDocument: (id: Id, fileId: Id, body: S['LicenseDocumentChangeRequest']) =>
    call(client.PUT('/api/v1/licenses/{id}/documents/{fileId}', { params: { path: { id, fileId } }, body })),
  removeDocument: (id: Id, fileId: Id) =>
    call(client.DELETE('/api/v1/licenses/{id}/documents/{fileId}', { params: { path: { id, fileId } } })),
  /** The download, only through the item (Details): audited, never cached. */
  documentUrl: (id: Id, fileId: Id, name: string) =>
    `/api/v1/licenses/${id}/documents/${fileId}/${encodeURIComponent(name)}`,
  rename: (field: 'manufacturer' | 'vendor' | 'costCenter' | 'seatUnit', from: string, to: string) =>
    call(client.POST('/api/v1/licenses/names/rename', { body: { field, from, to } })),
  templates: () => call(client.GET('/api/v1/license-templates')),
  template: (id: Id) => call(client.GET('/api/v1/license-templates/{id}', path(id))),
  createTemplate: (body: LicenseTemplateRequest) => call(client.POST('/api/v1/license-templates', { body })),
  updateTemplate: (id: Id, body: LicenseTemplateRequest) =>
    call(client.PUT('/api/v1/license-templates/{id}', { ...path(id), body })),
  deleteTemplate: (id: Id) => call(client.DELETE('/api/v1/license-templates/{id}', path(id))),
  categories: () => call(client.GET('/api/v1/license-categories')),
  createCategory: (body: S['LicenseCategoryRequest']) => call(client.POST('/api/v1/license-categories', { body })),
  updateCategory: (id: Id, body: S['LicenseCategoryRequest']) =>
    call(client.PUT('/api/v1/license-categories/{id}', { ...path(id), body })),
  orderCategories: (ids: Id[]) => call(client.PUT('/api/v1/license-categories/order', { body: { ids } })),
  /** With its items and templates moved to `moveTo`, required while any are in it. */
  deleteCategory: (id: Id, moveTo?: Id) =>
    call(client.DELETE('/api/v1/license-categories/{id}', { params: { path: { id }, query: { moveTo } } })),
}

/**
 * A document's upload (§7.1): the file as the body, with its name, kind and charge or renewal, by XMLHttpRequest for the progress,
 * the CSRF header as client.ts sends it. Rejects with the problem's detail.
 */
export function uploadDocument(
  id: Id,
  file: Blob,
  name: string,
  extra: { kind?: LicenseDocumentKind; chargeId?: Id; renewalId?: Id } = {},
  onProgress?: (fraction: number) => void,
): Promise<LicenseDocument> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open('POST', `/api/v1/licenses/${id}/documents${queryString({ name, ...extra })}`)
    xhr.withCredentials = true
    xhr.setRequestHeader('Accept', 'application/json')
    xhr.setRequestHeader('Content-Type', 'application/octet-stream')
    const token = readCookie('XSRF-TOKEN')
    if (token) xhr.setRequestHeader('X-XSRF-TOKEN', token)
    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable && event.total > 0) onProgress?.(event.loaded / event.total)
    }
    xhr.onload = () => {
      let body: unknown = null
      try {
        body = JSON.parse(xhr.responseText)
      } catch {
        // Not JSON: a proxy's page.
      }
      if (xhr.status >= 200 && xhr.status < 300 && body) resolve(body as LicenseDocument)
      else
        reject(
          new Error(
            (body as { detail?: string } | null)?.detail ??
              (xhr.status === 413
                ? `${name} is larger than PNMT accepts.`
                : `${name} wasn’t uploaded (${xhr.status}).`),
          ),
        )
    }
    xhr.onerror = () => reject(new Error(`${name} wasn’t uploaded: PNMT can’t be reached.`))
    xhr.send(file)
  })
}

/** One prefix, so that any change refreshes the lists, the money views, the item, the panels and the sensors' strips. */
export const licenseKeys = {
  all: ['licenses'] as const,
  list: (type?: LicenseType, status?: LicenseStatus[]) => ['licenses', 'list', type ?? 'all', status ?? []] as const,
  overview: (filter: MoneyFilter) => ['licenses', 'overview', filter] as const,
  costs: (query: CostsQuery) => ['licenses', 'costs', query] as const,
  charges: (query: ChargesQuery) => ['licenses', 'charges', query] as const,
  upcoming: (months: number, type?: LicenseType, owner?: Id) =>
    ['licenses', 'upcoming', months, type ?? null, owner ?? null] as const,
  names: ['licenses', 'names'] as const,
  people: ['licenses', 'people'] as const,
  linked: (query: { host?: Id; location?: Id }) => ['licenses', 'linked', query] as const,
  item: (number: number) => ['licenses', 'item', number] as const,
  assignments: (id: Id) => ['licenses', 'assignments', id] as const,
  links: (id: Id) => ['licenses', 'links', id] as const,
  renewals: (id: Id) => ['licenses', 'renewals', id] as const,
  itemCharges: (id: Id) => ['licenses', 'item-charges', id] as const,
  documents: (id: Id) => ['licenses', 'documents', id] as const,
  history: (id: Id) => ['licenses', 'history', id] as const,
  templates: ['licenses', 'templates'] as const,
  categories: ['licenses', 'categories'] as const,
}

// --- words ------------------------------------------------------------------------------------------------------------------

/** People read "licence" (§Words); the code says license. */
export const typeLabels: Record<LicenseType, string> = { License: 'Licence', Service: 'Service', Hardware: 'Hardware' }
export const typePlurals: Record<LicenseType, string> = {
  License: 'Licences',
  Service: 'Services',
  Hardware: 'Hardware',
}
/** The tab of a type (§8.1). */
export const typeTabs: Record<LicenseType, 'licences' | 'services' | 'hardware'> = {
  License: 'licences',
  Service: 'services',
  Hardware: 'hardware',
}

export const kindLabels: Record<LicenseKind, string> = {
  Perpetual: 'Perpetual',
  Subscription: 'Subscription',
  Contract: 'Contract',
  Support: 'Support or maintenance',
  Usage: 'Paid by use',
  OneOff: 'One-off job',
  Purchase: 'Purchase',
  Lease: 'Lease',
  Trial: 'Trial',
  Free: 'Free',
  Other: 'Other',
}

/** The kinds a type offers (§3.3), as the server checks them. */
export const kindsOf: Record<LicenseType, LicenseKind[]> = {
  License: ['Perpetual', 'Subscription', 'Support', 'Trial', 'Free', 'Other'],
  Service: ['Subscription', 'Contract', 'Support', 'Usage', 'OneOff', 'Trial', 'Free', 'Other'],
  Hardware: ['Purchase', 'Lease', 'Support', 'Other'],
}

/** What the expiry is called, by kind (§3.3): Expires on, Warranty until, Lease ends on. */
export function expiryLabel(kind: LicenseKind): string {
  switch (kind) {
    case 'Purchase':
      return 'Warranty until'
    case 'Lease':
      return 'Lease ends on'
    case 'Contract':
    case 'Support':
      return 'Ends on'
    case 'Trial':
      return 'Trial ends on'
    default:
      return 'Expires on'
  }
}

/** The vendor is the provider for a service (§Words). */
export const vendorLabel = (type: LicenseType) => (type === 'Service' ? 'Provider' : 'Vendor')

export const documentKindLabels: Record<LicenseDocumentKind, string> = {
  Invoice: 'Invoice',
  Contract: 'Contract',
  Quote: 'Quote',
  LicenseFile: 'Licence file',
  KeyFile: 'Key file',
  Warranty: 'Warranty',
  Other: 'Other',
}

export const chargeKindLabels: Record<LicenseChargeKind, string> = {
  Purchase: 'Purchase',
  Period: 'Billing period',
  Extra: 'Extra',
  Credit: 'Credit',
}

/** The verdict's tone (§4): OK; a warning (445, 447, 449); failing (446, 448). Always shown with an icon and a word. */
export type VerdictTone = 'ok' | 'warn' | 'danger' | 'none'

export function verdictTone(code: number | null | undefined): VerdictTone {
  if (code === null || code === undefined) return 'none'
  if (code < 400) return 'ok'
  return code === 446 || code === 448 ? 'danger' : 'warn'
}

export function verdictLabel(code: number | null | undefined): string {
  switch (code) {
    case 245:
      return 'Valid'
    case 246:
      return 'Renewed'
    case 445:
      return 'About to expire'
    case 446:
      return 'Expired'
    case 447:
      return 'End of life soon'
    case 448:
      return 'Past its end of life'
    case 449:
      return 'Over-assigned'
    default:
      return code === null || code === undefined ? 'No state' : `Code ${code}`
  }
}

export const verdictIcon = (code: number | null | undefined) =>
  ({
    ok: 'pi pi-check-circle',
    warn: 'pi pi-exclamation-triangle',
    danger: 'pi pi-times-circle',
    none: 'pi pi-minus-circle',
  })[verdictTone(code)]

// --- money and days ----------------------------------------------------------------------------------------------------------

const moneyFormats = new Map<string, Intl.NumberFormat>()

/** EUR 3,960.00: the currency's code first, two decimals, as the plan writes amounts. Never converted (§6.6). */
export function money(amount: number | null | undefined, currency: string): string {
  if (amount === null || amount === undefined) return '–'
  let format = moneyFormats.get('n')
  if (!format) {
    format = new Intl.NumberFormat('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
    moneyFormats.set('n', format)
  }
  return `${currency} ${format.format(amount)}`
}

/** 28k, 1.2k, 950: an axis's figures. */
export function shortMoney(amount: number): string {
  const abs = Math.abs(amount)
  if (abs >= 1_000_000) return `${(amount / 1_000_000).toFixed(abs >= 10_000_000 ? 0 : 1)}M`
  if (abs >= 1000) return `${(amount / 1000).toFixed(abs >= 10_000 ? 0 : 1)}k`
  return `${Math.round(amount)}`
}

/** The main currency's amount, and the others in a line under it: "+ CHF 1,190.00". */
export function moneyLines(
  amounts: readonly MoneyAmount[] | null | undefined,
  main: string,
): { main: string; others: string[] } {
  const list = amounts ?? []
  const first = list.find((m) => m.currency === main)
  return {
    main: money(first?.amount ?? 0, main),
    others: list.filter((m) => m.currency !== main).map((m) => `+ ${money(m.amount, m.currency)}`),
  }
}

/** Days from today (local) to a yyyy-MM-dd day: 0 today, negative past. */
export function daysUntil(day: string, now: number): number {
  const [y, m, d] = day.slice(0, 10).split('-').map(Number)
  const target = Date.UTC(y, m - 1, d)
  const today = new Date(now)
  const start = Date.UTC(today.getFullYear(), today.getMonth(), today.getDate())
  return Math.round((target - start) / 86_400_000)
}

/** "35 days left", "1 day left", "today is its last day", "expired 2 days ago" (§8.7). */
export function daysLeftText(day: string | null | undefined, now: number): string {
  if (!day) return "Doesn't expire"
  const days = daysUntil(day, now)
  if (days > 1) return `${days} days left`
  if (days === 1) return '1 day left'
  if (days === 0) return 'today is its last day'
  return days === -1 ? 'expired yesterday' : `expired ${-days} days ago`
}

/** Today as yyyy-MM-dd in the browser's day. */
export function todayIso(now = Date.now()): string {
  const d = new Date(now)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/** A Date picked in a DatePicker as yyyy-MM-dd, the day it shows (no time zone shift). */
export function isoDay(date: Date | null | undefined): string | null {
  if (!date) return null
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

/** A yyyy-MM-dd day as a local Date at noon, for a DatePicker. */
export function dayDate(day: string | null | undefined): Date | null {
  if (!day) return null
  const [y, m, d] = day.slice(0, 10).split('-').map(Number)
  return new Date(y, m - 1, d, 12)
}

/** What the rows of Upcoming say (§8.4). */
export const upcomingLabels: Record<string, string> = {
  expires: 'expires',
  renews: 'renews automatically',
  cancelBy: 'cancel by',
  warrantyEnds: 'warranty ends',
  leaseEnds: 'lease ends',
  ends: 'ends (cancelled)',
  endOfLife: 'end of life',
  reviewDue: 'review due',
  expired: 'expired',
  pastEndOfLife: 'past its end of life',
}

/** A request from an item, for the inline fields: every field as it is, then the changes (as sensorRequest does). */
export function requestOf(item: LicenseDetail, patch: Partial<LicenseRequest> = {}): LicenseRequest {
  return {
    type: item.type,
    name: item.name,
    templateId: item.templateId,
    categoryId: item.categoryId,
    manufacturer: item.manufacturer,
    productName: item.productName,
    edition: item.edition,
    vendor: item.vendor,
    kind: item.kind,
    licensedTo: item.licensedTo,
    reference: item.reference,
    customerNumber: item.customerNumber,
    portalUrl: item.portalUrl,
    supportContact: item.supportContact,
    supportUrl: item.supportUrl,
    sla: item.sla,
    costCenter: item.costCenter,
    locationId: item.locationId,
    ownerId: item.ownerId,
    licenseKey: null,
    productKey: null,
    seats: item.seats,
    seatUnit: item.seatUnit,
    seatsFromHosts: item.seatsFromHosts,
    priceAmount: item.priceAmount,
    currency: item.currency,
    priceBasis: item.priceBasis,
    billing: item.billing,
    billingMonths: item.billingMonths,
    recordCharges: item.recordCharges,
    startsOn: item.startsOn,
    expiresOn: item.expiresOn,
    termMonths: item.termMonths,
    renewal: item.renewal,
    warnDays: item.warnDays,
    noticeDays: item.noticeDays,
    warnBeforeRenewal: item.warnBeforeRenewal,
    endOfLifeFromTemplate: item.endOfLifeFromTemplate,
    endOfLifeOn: item.ownEndOfLifeOn,
    endOfLifeNote: item.ownEndOfLifeNote,
    eolWarnDays: item.eolWarnDays,
    reviewedOn: item.reviewedOn,
    reviewMonths: item.reviewMonths,
    note: item.note,
    recordPastCharges: false,
    acknowledgeCredentials: false,
    ...patch,
  }
}
