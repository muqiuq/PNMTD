import { describe, expect, it } from 'vitest'
import {
  activeFilters,
  addMonths,
  costsQuery,
  dayOf,
  filterQuery,
  monthOf,
  monthsBetween,
  periodQuery,
  presetWindow,
  readCharges,
  readCostsView,
  readFilter,
  readPeriod,
  writeCharges,
  writeFilter,
} from './moneyFilter'

const category = '0b8f5a52-6a3e-4c1f-9d47-2f1e3c4b5a69'
const owner = '6E4D2C1B-0A9F-4E8D-8C7B-6A5F4E3D2C1B'
const today = '2026-11-27'

describe('the money filters in the URL (docs/licences-plan.md §8.2)', () => {
  it('reads the six filters, and ignores what is not one', () => {
    expect(
      readFilter({
        currency: 'chf',
        type: 'Service',
        category,
        costCenter: ' IT ',
        location: 'nowhere',
        owner,
        tab: 'x',
      }),
    ).toEqual({ currency: 'CHF', type: 'Service', category, costCenter: 'IT', owner: owner.toLowerCase() })
    expect(readFilter({ currency: 'EURO', type: 'Licence', category: '12' })).toEqual({})
    expect(readFilter({ type: ['Hardware', 'Service'] })).toEqual({ type: 'Hardware' })
    expect(readFilter({})).toEqual({})
  })

  it("writes them into the query, keeping the view's own and leaving the empty ones out", () => {
    expect(
      writeFilter({ show: 'charges', type: 'License', costCenter: 'IT' }, { type: 'Service', currency: 'EUR' }),
    ).toEqual({
      show: 'charges',
      type: 'Service',
      currency: 'EUR',
    })
    expect(filterQuery({ currency: 'EUR', type: undefined, owner })).toEqual({ currency: 'EUR', owner })
    expect(activeFilters({ currency: 'EUR', type: 'License', costCenter: 'IT' })).toBe(2)
  })

  it('reads back what it wrote', () => {
    const filter = {
      currency: 'CHF',
      type: 'Hardware' as const,
      category,
      costCenter: 'Sales',
      owner: owner.toLowerCase(),
    }
    expect(readFilter(writeFilter({}, filter) as never)).toEqual(filter)
  })
})

describe("the costs' period in the URL (§8.5)", () => {
  it('counts months', () => {
    expect(addMonths('2026-11', 2)).toBe('2027-01')
    expect(addMonths('2026-01', -1)).toBe('2025-12')
    expect(addMonths('2026-11', -11)).toBe('2025-12')
    expect(monthsBetween('2026-01', '2026-12')).toBe(12)
    expect(monthsBetween('2025-12', '2027-11')).toBe(24)
    expect(monthOf('2026-03')).toBe('2026-03')
    expect(monthOf('2026-03-15')).toBe('2026-03')
    expect(monthOf('2026-13')).toBeUndefined()
    expect(monthOf('March')).toBeUndefined()
  })

  it('knows the presets from today', () => {
    expect(presetWindow('thisYear', today)).toEqual({ from: '2026-01', to: '2026-12' })
    expect(presetWindow('lastYear', today)).toEqual({ from: '2025-01', to: '2025-12' })
    expect(presetWindow('last12', today)).toEqual({ from: '2025-12', to: '2026-11' })
    expect(presetWindow('next12', today)).toEqual({ from: '2026-12', to: '2027-11' })
  })

  it('reads this year without a period, a window as the preset it matches, and five years with ?years=1', () => {
    expect(readPeriod({}, today)).toEqual({ preset: 'thisYear', from: '2026-01', to: '2026-12', years: false })
    expect(readPeriod({ from: '2025-01', to: '2025-12' }, today).preset).toBe('lastYear')
    expect(readPeriod({ from: '2025-12', to: '2026-11' }, today).preset).toBe('last12')
    expect(readPeriod({ from: '2026-12', to: '2027-11' }, today).preset).toBe('next12')
    expect(readPeriod({ from: '2026-03', to: '2026-03' }, today)).toEqual({
      preset: 'range',
      from: '2026-03',
      to: '2026-03',
      years: false,
    })
    expect(readPeriod({ years: '1', from: '2026-03' }, today)).toMatchObject({ preset: 'years', years: true })
    // The charges' days read as their months.
    expect(readPeriod({ from: '2026-03-01', to: '2026-04-30' }, today)).toMatchObject({
      from: '2026-03',
      to: '2026-04',
    })
    // A year's start alone is that year.
    expect(readPeriod({ from: '2024-01' }, today)).toMatchObject({ from: '2024-01', to: '2024-12' })
  })

  it('takes a window that ends before it starts, or is longer than 60 months, for this year', () => {
    expect(readPeriod({ from: '2026-05', to: '2026-02' }, today).preset).toBe('thisYear')
    expect(readPeriod({ from: '2020-01', to: '2026-12' }, today).preset).toBe('thisYear')
    expect(readPeriod({ from: 'soon', to: 'later' }, today).preset).toBe('thisYear')
  })

  it('writes this year as nothing, and reads back every other period', () => {
    expect(periodQuery({ preset: 'thisYear', from: '2026-01', to: '2026-12' })).toEqual({})
    expect(periodQuery({ preset: 'years', from: '2026-01', to: '2026-12' })).toEqual({ years: '1' })
    for (const preset of ['lastYear', 'last12', 'next12'] as const) {
      const window = presetWindow(preset, today)
      expect(readPeriod(periodQuery({ preset, ...window }), today)).toEqual({ preset, ...window, years: false })
    }
    expect(readPeriod(periodQuery({ preset: 'range', from: '2026-02', to: '2026-07' }), today)).toMatchObject({
      preset: 'range',
      from: '2026-02',
      to: '2026-07',
    })
  })

  it("reads the costs' view: the grouping, spent or forecast, the group to open, the Charges view", () => {
    expect(readCostsView({})).toEqual({ group: 'category', show: 'both', open: undefined, charges: false })
    expect(readCostsView({ group: 'costCenter', costs: 'forecast', open: 'Contoso', show: 'charges' })).toEqual({
      group: 'costCenter',
      show: 'forecast',
      open: 'Contoso',
      charges: true,
    })
    expect(readCostsView({ group: 'colour', costs: 'all' })).toMatchObject({ group: 'category', show: 'both' })
  })

  it('asks the server for the window, or for the years, with the filters and without the defaults', () => {
    const period = readPeriod({ from: '2026-03', to: '2026-05' }, today)
    expect(costsQuery(period, { group: 'provider', show: 'spent' }, { currency: 'EUR', type: 'Service' })).toEqual({
      currency: 'EUR',
      type: 'Service',
      from: '2026-03',
      to: '2026-05',
      group: 'provider',
      show: 'spent',
    })
    expect(costsQuery(readPeriod({ years: '1' }, today), { group: 'category', show: 'both' }, {})).toEqual({
      years: true,
    })
  })
})

describe("the charges' filters in the URL (§8.5)", () => {
  it('reads days, and a month as its first and last day', () => {
    expect(dayOf('2026-02-28')).toBe('2026-02-28')
    expect(dayOf('2026-02-30')).toBeUndefined()
    expect(dayOf('2026-02')).toBe('2026-02-01')
    expect(dayOf('2026-02', true)).toBe('2026-02-28')
    expect(dayOf('2028-02', true)).toBe('2028-02-29')
    expect(dayOf('2026-12', true)).toBe('2026-12-31')
    expect(dayOf(undefined)).toBeUndefined()
  })

  it('reads the period, the shared type and category, the item, kind, source and estimated', () => {
    const item = '1f2e3d4c-5b6a-4978-8695-a4b3c2d1e0f9'
    expect(
      readCharges({
        show: 'charges',
        from: '2026-03',
        to: '2026-03',
        type: 'License',
        category,
        item,
        kind: 'Credit',
        source: 'Person',
        estimated: 'true',
        currency: 'EUR',
      }),
    ).toEqual({
      from: '2026-03-01',
      to: '2026-03-31',
      type: 'License',
      category,
      license: item,
      kind: 'Credit',
      source: 'Person',
      estimated: true,
    })
    expect(readCharges({ estimated: 'false' })).toEqual({ estimated: false })
    expect(readCharges({ estimated: 'maybe', kind: 'Refund', source: 'Robot', item: 'LIC-4' })).toEqual({})
  })

  it('writes them back, and reads what it wrote', () => {
    const charges = { from: '2026-01-01', to: '2026-06-30', kind: 'Period' as const, estimated: false }
    const query = writeCharges({ show: 'charges', estimated: 'true', item: 'x' }, charges)
    expect(query).toEqual({ show: 'charges', from: '2026-01-01', to: '2026-06-30', kind: 'Period', estimated: 'false' })
    expect(readCharges(query as never)).toEqual(charges)
  })
})
