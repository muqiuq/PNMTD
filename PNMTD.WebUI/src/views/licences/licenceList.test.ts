import { describe, expect, it } from 'vitest'
import type { LicenseRow } from '@/api/licenses'
import {
  addDays,
  cleared,
  daysBetween,
  emptyList,
  expiresCell,
  filtering,
  formatSort,
  hostsCell,
  listFromQuery,
  listTotals,
  listToQuery,
  loadColumns,
  matchesFilters,
  matchesSearch,
  moreSet,
  parseSort,
  periodPrice,
  saveColumns,
  shownRows,
  sortFields,
  sortRows,
  termLines,
  totalsText,
  without,
  type ListState,
} from './licenceList'

function row(number: number, more: Partial<LicenseRow> = {}): LicenseRow {
  return {
    id: `id-${number}`,
    number,
    display: `LIC-${number}`,
    type: 'License',
    name: `Item ${number}`,
    kind: 'Subscription',
    status: 'Active',
    categoryId: 'cat-software',
    categoryName: 'Software',
    templateId: null,
    manufacturer: null,
    productName: null,
    edition: null,
    vendor: null,
    licensedTo: null,
    reference: null,
    customerNumber: null,
    costCenter: null,
    locationId: null,
    locationName: null,
    ownerId: null,
    ownerName: null,
    seats: null,
    seatUnit: 'devices',
    seatsFromHosts: false,
    usedSeats: 0,
    hosts: 0,
    hostNames: [],
    priceAmount: null,
    currency: 'EUR',
    priceBasis: 'Total',
    billing: 'Recurring',
    billingMonths: 12,
    perMonth: 0,
    perYear: 0,
    startsOn: null,
    expiresOn: null,
    termMonths: null,
    renewal: 'None',
    noticeDays: null,
    cancelBy: null,
    endOfLifeOn: null,
    reviewedOn: null,
    reviewDueOn: null,
    reviewDue: false,
    verdictCode: 245,
    verdictMessage: null,
    findings: [],
    nobodyNotified: false,
    licenseKeyHint: null,
    productKeyHint: null,
    updatedAt: '2026-11-20T10:00:00Z',
    ...more,
  }
}

const today = '2026-11-27'
const ctx = { today, userId: 'me' }
const numbers = (rows: LicenseRow[]) => rows.map((r) => r.number)
const state = (more: Partial<ListState>): ListState => ({ ...emptyList(), ...more })

describe("the list's state in the URL (docs/licences-plan.md §8.3)", () => {
  it('reads the names the links use, and ignores what it does not know', () => {
    const s = listFromQuery({
      q: ' LIC-12 ',
      sort: '-perYear',
      state: 'attention,failing,bogus',
      retired: '1',
      kind: 'Contract,Nonsense',
      category: 'c1',
      owner: 'mine',
      host: 'h1',
      noHost: '1',
      nobody: '1',
      expires: '90',
      reviewDue: '1',
      noPrice: '1',
      unusedSeats: '1',
      pastEol: '1',
    })
    expect(s).toMatchObject({
      q: 'LIC-12',
      sort: { field: 'perYear', order: -1 },
      state: ['attention', 'failing'],
      retired: true,
      kind: ['Contract'],
      category: 'c1',
      owner: 'mine',
      host: 'h1',
      noHost: true,
      nobody: true,
      expires: 90,
      reviewDue: true,
      noPrice: true,
      unusedSeats: true,
      pastEol: true,
    })
    expect(listFromQuery({ expires: '45', sort: 'note' })).toMatchObject({ expires: null, sort: null })
  })

  it('writes only what differs from the defaults, and reads it back the same', () => {
    expect(Object.values(listToQuery(emptyList())).filter((v) => v !== undefined)).toEqual([])
    const s = state({ q: 'veeam', sort: { field: 'expires', order: 1 }, state: ['ok'], unusedSeats: true, expires: 30 })
    const query = listToQuery(s)
    expect(query).toMatchObject({ q: 'veeam', sort: 'expires', state: 'ok', unusedSeats: '1', expires: '30' })
    expect(listFromQuery(query)).toEqual(s)
  })

  it('knows what More filters holds, clears one, or all but the sort', () => {
    const s = state({ q: 'x', state: ['ok'], kind: ['Free'], pastEol: true, sort: { field: 'name', order: 1 } })
    expect(moreSet(s)).toEqual(['kind', 'pastEol'])
    expect(moreSet(without(s, 'kind'))).toEqual(['pastEol'])
    expect(filtering(s)).toBe(true)
    expect(cleared(s)).toEqual({ ...emptyList(), sort: { field: 'name', order: 1 } })
    expect(filtering(cleared(s))).toBe(false)
  })

  it('reads and writes the sort', () => {
    expect(parseSort('-eol', sortFields)).toEqual({ field: 'eol', order: -1 })
    expect(parseSort(['name'], sortFields)).toBeNull()
    expect(formatSort({ field: 'hosts', order: 1 })).toBe('hosts')
  })
})

describe('the search (§8.3)', () => {
  const r = row(12, {
    name: 'M365 Business Standard',
    manufacturer: 'Microsoft',
    vendor: 'Contoso',
    reference: 'INV-2026-77',
    licenseKeyHint: 'CC9DR',
    hostNames: ['srv-file-01'],
  })

  it('finds the number with or without a prefix, and never a longer one', () => {
    expect(matchesSearch(r, 'LIC-12')).toBe(true)
    expect(matchesSearch(r, 'svc-12')).toBe(true)
    expect(matchesSearch(r, '12')).toBe(true)
    expect(matchesSearch(r, 'LIC-1')).toBe(false)
    // A number is looked for in the words too ("2016" in Windows Server 2016), but not in the number's display.
    expect(matchesSearch(row(12, { name: 'Acrobat' }), '1')).toBe(false)
    expect(matchesSearch(r, '365')).toBe(true)
  })

  it('finds every word in the fields, host names and key hints, not in what it does not search', () => {
    expect(matchesSearch(r, 'microsoft contoso')).toBe(true)
    expect(matchesSearch(r, 'microsoft hetzner')).toBe(false)
    expect(matchesSearch(r, 'inv-2026')).toBe(true)
    expect(matchesSearch(r, 'file-01')).toBe(true)
    expect(matchesSearch(r, 'cc9dr')).toBe(true)
    expect(matchesSearch(r, 'NJ4MX-VQQ7Q-FKK44-W8FV9-CC9DR')).toBe(true)
    expect(matchesSearch(r, '  ')).toBe(true)
  })
})

describe('the filters (§8.3)', () => {
  it('filters by state: needs attention, failing, OK', () => {
    const rows = [
      row(1, { verdictCode: 245 }),
      row(2, { verdictCode: 445 }),
      row(3, { verdictCode: 446 }),
      row(4, { verdictCode: null }),
    ]
    const pick = (s: Partial<ListState>) => numbers(rows.filter((r) => matchesFilters(r, state(s), ctx)))
    expect(pick({ state: ['attention'] })).toEqual([2])
    expect(pick({ state: ['failing'] })).toEqual([3])
    expect(pick({ state: ['ok'] })).toEqual([1])
    expect(pick({ state: ['attention', 'failing'] })).toEqual([2, 3])
    expect(pick({})).toEqual([1, 2, 3, 4])
  })

  it('filters by owner, mine too, and by the items on a host', () => {
    const rows = [row(1, { ownerId: 'me' }), row(2, { ownerId: 'anna' }), row(3)]
    expect(numbers(rows.filter((r) => matchesFilters(r, state({ owner: 'mine' }), ctx)))).toEqual([1])
    expect(numbers(rows.filter((r) => matchesFilters(r, state({ owner: 'anna' }), ctx)))).toEqual([2])
    const host = { ...ctx, hostItems: new Set(['id-3']) }
    expect(numbers(rows.filter((r) => matchesFilters(r, state({ host: 'h1' }), host)))).toEqual([3])
    // Not loaded yet: nothing, rather than everything.
    expect(numbers(rows.filter((r) => matchesFilters(r, state({ host: 'h1' }), ctx)))).toEqual([])
  })

  it('filters by the days: expires within, past its end of life, review due', () => {
    const rows = [
      row(1, { expiresOn: '2026-12-27' }),
      row(2, { expiresOn: '2027-02-01' }),
      row(3, { expiresOn: '2026-11-01' }),
      row(4, { endOfLifeOn: '2026-11-26' }),
      row(5, { endOfLifeOn: today, reviewDue: true }),
    ]
    const pick = (s: Partial<ListState>) => numbers(rows.filter((r) => matchesFilters(r, state(s), ctx)))
    expect(pick({ expires: 30 })).toEqual([1])
    expect(pick({ expires: 90 })).toEqual([1, 2])
    expect(pick({ pastEol: true })).toEqual([4])
    expect(pick({ reviewDue: true })).toEqual([5])
  })

  it('filters the worth-a-look lists: without a price, unused seats, nobody notified, on no host', () => {
    const rows = [
      row(1, { priceAmount: 100 }),
      row(2),
      row(3, { kind: 'Free' }),
      row(4, { kind: 'Trial', seatsFromHosts: true, seats: 5, usedSeats: 3, hosts: 3 }),
      row(5, { seatsFromHosts: true, seats: 5, usedSeats: 5, hosts: 5, nobodyNotified: true, priceAmount: 0 }),
    ]
    const pick = (s: Partial<ListState>) => numbers(rows.filter((r) => matchesFilters(r, state(s), ctx)))
    expect(pick({ noPrice: true })).toEqual([2])
    expect(pick({ unusedSeats: true })).toEqual([4])
    expect(pick({ nobody: true })).toEqual([5])
    expect(pick({ noHost: true })).toEqual([1, 2, 3])
  })

  it('filters by kind, names (case ignored), category, template and location', () => {
    const rows = [
      row(1, { kind: 'Contract', vendor: 'Swisscom', categoryId: 'c2', templateId: 't1', locationId: 'l1' }),
      row(2, { vendor: 'Hetzner', costCenter: 'IT' }),
    ]
    const pick = (s: Partial<ListState>) => numbers(rows.filter((r) => matchesFilters(r, state(s), ctx)))
    expect(pick({ kind: ['Contract'] })).toEqual([1])
    expect(pick({ vendor: 'swisscom' })).toEqual([1])
    expect(pick({ costCenter: 'it' })).toEqual([2])
    expect(pick({ category: 'c2' })).toEqual([1])
    expect(pick({ template: 't1' })).toEqual([1])
    expect(pick({ location: 'l1' })).toEqual([1])
    expect(pick({ manufacturer: 'Microsoft' })).toEqual([])
  })
})

describe('the sort (§8.3)', () => {
  it('sorts days with no day last both ways, ties by name', () => {
    const rows = [
      row(1, { name: 'B', expiresOn: '2027-03-01' }),
      row(2, { name: 'C' }),
      row(3, { name: 'A', expiresOn: '2026-12-31' }),
      row(4, { name: 'D', expiresOn: '2026-12-31' }),
    ]
    expect(numbers(sortRows(rows, { field: 'expires', order: 1 }))).toEqual([3, 4, 1, 2])
    expect(numbers(sortRows(rows, { field: 'expires', order: -1 }))).toEqual([1, 3, 4, 2])
  })

  it('sorts names as people read them, money as numbers, an unknown price last', () => {
    const rows = [
      row(1, { name: 'backup 10', priceAmount: 10, perYear: 120 }),
      row(2, { name: 'Backup 2', priceAmount: 1000, perYear: 1000 }),
      row(3, { name: 'alpha' }),
    ]
    expect(numbers(sortRows(rows, { field: 'name', order: 1 }))).toEqual([3, 2, 1])
    expect(numbers(sortRows(rows, { field: 'perYear', order: -1 }))).toEqual([2, 1, 3])
    expect(numbers(sortRows(rows, { field: 'perYear', order: 1 }))).toEqual([1, 2, 3])
  })

  it('keeps the newest first without a sort, and searches, filters and sorts together', () => {
    const rows = [row(1, { vendor: 'Contoso' }), row(7, { vendor: 'Contoso' }), row(3)]
    expect(numbers(sortRows(rows, null))).toEqual([7, 3, 1])
    expect(numbers(shownRows(rows, state({ q: 'contoso', sort: { field: 'number', order: 1 } }), ctx))).toEqual([1, 7])
  })
})

describe('the totals (§8.3)', () => {
  it('adds the running cost per currency and this year’s one-time purchases, never converted', () => {
    const rows = [
      row(1, { priceAmount: 330, perYear: 3960, currency: 'EUR' }),
      row(2, { priceAmount: 1190, perYear: 1190, currency: 'CHF' }),
      row(3, { billing: 'OneTime', priceAmount: 89, priceBasis: 'PerSeat', seats: 10, startsOn: '2026-03-01' }),
      row(4, { billing: 'OneTime', priceAmount: 500, startsOn: '2025-03-01' }),
    ]
    const totals = listTotals(rows, today)
    expect(totals.perYear).toEqual([
      { currency: 'EUR', amount: 3960 },
      { currency: 'CHF', amount: 1190 },
    ])
    expect(totals.oneTime).toEqual([{ currency: 'EUR', amount: 890 }])
    expect(totalsText(totals, 'License')).toBe(
      '4 licences · per year: EUR 3,960.00 · CHF 1,190.00 · one-time this year: EUR 890.00',
    )
    expect(totalsText(listTotals([], today), 'Hardware')).toBe('0 devices')
  })
})

describe('the cells (§8.3)', () => {
  it('words the expiry, the price and the hosts', () => {
    expect(expiresCell(row(1, { expiresOn: '2027-01-01' }), today)).toMatchObject({ extra: '35 d', tone: null })
    expect(expiresCell(row(1, { expiresOn: '2027-01-01', renewal: 'Automatic' }), today).extra).toBe('auto')
    expect(expiresCell(row(1, { expiresOn: '2026-11-14' }), today)).toMatchObject({ extra: 'expired', tone: 'danger' })
    expect(expiresCell(row(1), today)).toEqual({ day: null, extra: "doesn't expire", tone: null })
    expect(periodPrice(row(1, { priceAmount: 330, perYear: 3960 }), 'year')).toBe('EUR 3,960.00')
    expect(periodPrice(row(1, { priceAmount: 1100, perMonth: 1100, billing: 'Variable' }), 'month')).toBe(
      '~EUR 1,100.00',
    )
    expect(periodPrice(row(1, { priceAmount: 500, billing: 'OneTime' }), 'year')).toBe('one-time')
    expect(periodPrice(row(1, { kind: 'Free' }), 'year')).toBe('free')
    expect(hostsCell(row(1, { hosts: 3 }))).toMatchObject({ text: '3', over: false })
    expect(hostsCell(row(1))).toMatchObject({ text: '–' })
    expect(hostsCell(row(1, { hosts: 7, seatsFromHosts: true, seats: 5, usedSeats: 7 }))).toMatchObject({
      text: '7/5',
      over: true,
    })
  })

  it("words a service's term", () => {
    const contract = row(31, { renewal: 'Automatic', termMonths: 12, expiresOn: '2027-06-30', cancelBy: '2027-04-02' })
    expect(termLines(contract, today)).toHaveLength(2)
    expect(termLines(contract, today)[1]).toMatch(/^cancel by /)
    expect(termLines(row(20, { renewal: 'Automatic', termMonths: 1, expiresOn: '2026-11-30' }), today)).toEqual([
      'renews monthly',
    ])
    expect(termLines(row(35, { kind: 'Usage', billing: 'Variable' }), today)).toEqual(['usage'])
    expect(termLines(row(36, { status: 'Cancelled', expiresOn: '2026-12-31' }), today)[0]).toMatch(/\(cancelled\)$/)
  })

  it('counts days between days, across months and years', () => {
    expect(daysBetween(today, '2027-01-01')).toBe(35)
    expect(daysBetween(today, '2026-11-26')).toBe(-1)
    expect(addDays('2027-06-30', 1)).toBe('2027-07-01')
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01')
  })
})

describe('the columns (§8.3)', () => {
  it("keeps a type's choice in the browser, in the type's order, and the defaults until then", () => {
    const store = new Map<string, string>()
    const storage = {
      getItem: (k: string) => store.get(k) ?? null,
      setItem: (k: string, v: string) => void store.set(k, v),
    }
    expect(loadColumns('Service', storage)).toEqual(['vendor', 'category', 'location', 'term', 'perMonth', 'owner'])
    saveColumns('Service', ['owner', 'vendor', 'updated'], storage)
    expect(loadColumns('Service', storage)).toEqual(['vendor', 'owner', 'updated'])
    expect(loadColumns('License', storage)).toContain('reviewed')
    store.set('pnmt.licences.columns.Hardware', 'not json')
    expect(loadColumns('Hardware', storage)).toContain('price')
  })
})
