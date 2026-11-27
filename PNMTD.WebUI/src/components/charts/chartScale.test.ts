import { describe, expect, it } from 'vitest'
import {
  axis,
  barWidth,
  columnLayout,
  columnPath,
  columnTable,
  labelEvery,
  meter,
  monthLabel,
  monthLong,
  niceStep,
  rankedLayout,
  spansYears,
  tickLabels,
  type ColumnDatum,
} from './chartScale'

const box = { width: 720, height: 220, left: 44, right: 8, top: 18, bottom: 24 }

describe("the charts' scale (docs/licences-plan.md §8.12)", () => {
  it('steps by 1, 2 or 5 times a power of ten', () => {
    expect(niceStep(19_260)).toBe(20_000)
    expect(niceStep(1605)).toBe(2000)
    expect(niceStep(250)).toBe(500)
    expect(niceStep(100)).toBe(100)
    expect(niceStep(0.3)).toBe(0.5)
    expect(niceStep(7)).toBe(10)
    expect(niceStep(0)).toBe(1)
    expect(niceStep(Number.NaN)).toBe(1)
  })

  it('tops the axis at a whole number of nice steps, at least the largest value', () => {
    expect(axis(77_040)).toEqual({ top: 80_000, step: 20_000, ticks: [0, 20_000, 40_000, 60_000, 80_000] })
    expect(axis(6420)).toEqual({ top: 8000, step: 2000, ticks: [0, 2000, 4000, 6000, 8000] })
    expect(axis(1000)).toEqual({ top: 1000, step: 500, ticks: [0, 500, 1000] })
    expect(axis(12.5).ticks).toEqual([0, 5, 10, 15])
    expect(axis(0.7).ticks).toEqual([0, 0.2, 0.4, 0.6, 0.8])
    for (const max of [1, 3, 99, 101, 2_345, 64_102, 1_234_567]) {
      const a = axis(max)
      expect(a.top).toBeGreaterThanOrEqual(max)
      expect(a.top - a.step).toBeLessThan(max)
      expect(a.ticks[0]).toBe(0)
      expect(a.ticks[a.ticks.length - 1]).toBe(a.top)
    }
  })

  it('shows only the baseline when there is nothing', () => {
    expect(axis(0)).toEqual({ top: 1, step: 1, ticks: [0] })
    expect(axis(-50)).toEqual({ top: 1, step: 1, ticks: [0] })
  })

  it('labels an axis in one style: in full below 10,000, in thousands from it', () => {
    expect(tickLabels([0, 2000, 4000, 6000, 8000])).toEqual(['0', '2,000', '4,000', '6,000', '8,000'])
    expect(tickLabels([0, 20_000, 40_000, 60_000, 80_000])).toEqual(['0', '20k', '40k', '60k', '80k'])
    expect(tickLabels([0, 5000, 10_000, 15_000])).toEqual(['0', '5k', '10k', '15k'])
    expect(tickLabels([0, 500_000, 1_000_000, 1_500_000])).toEqual(['0', '0.5M', '1M', '1.5M'])
    expect(tickLabels([0])).toEqual(['0'])
  })

  it('caps a column at 24 px and never fills its slot', () => {
    expect(barWidth(28.75)).toBe(17)
    expect(barWidth(60)).toBe(24)
    expect(barWidth(200)).toBe(24)
    expect(barWidth(10)).toBe(6)
    expect(barWidth(1)).toBe(2)
    for (const slot of [8, 12, 20, 30, 38]) expect(barWidth(slot)).toBeLessThan(slot)
  })

  it('labels every column whose label fits, else every other or third', () => {
    expect(labelEvery(60, 40)).toBe(1)
    expect(labelEvery(28, 40)).toBe(2)
    expect(labelEvery(11, 40)).toBe(4)
    expect(labelEvery(0, 40)).toBe(1)
  })

  it('rounds the data end of a column and keeps its baseline square', () => {
    expect(columnPath(10, 20, 16, 40)).toBe('M10,60 V24 Q10,20 14,20 H22 Q26,20 26,24 V60 Z')
    // A short column rounds no more than it is high.
    expect(columnPath(0, 0, 16, 2)).toBe('M0,2 V2 Q0,0 2,0 H14 Q16,0 16,2 V2 Z')
    expect(columnPath(0, 0, 16, 0)).toBe('')
  })
})

describe('the columns and their table (§8.12)', () => {
  const months: ColumnDatum[] = [
    { key: '2026-10', label: 'Oct', spent: 6120, forecast: 0 },
    { key: '2026-11', label: 'Nov', spent: 4000, forecast: 2420 },
    { key: '2026-12', label: 'Dec', spent: 0, forecast: 6420 },
    { key: '2027-01', label: 'Jan', spent: 0, forecast: 0 },
  ]

  it('draws one mark per row of the table, in its order, as high as its figures', () => {
    const layout = columnLayout(months, box)
    const table = columnTable(months)
    const plotHeight = layout.plotBottom - layout.plotTop
    const px = (v: number) => (v / layout.axis.top) * plotHeight
    expect(layout.marks.map((m) => m.key)).toEqual(table.map((r) => r.key))
    expect(table.map((r) => r.total)).toEqual([6120, 6420, 6420, 0])
    layout.marks.forEach((mark, i) => {
      const row = table[i]!
      expect(mark.total).toBe(row.total)
      expect(mark.spent?.height ?? 0).toBeCloseTo(px(row.spent), 6)
      // The 2 px gap comes out of the forecast, so that the column is as high as its total.
      const gap = mark.spent && mark.forecast ? 2 : 0
      expect((mark.forecast?.height ?? 0) + gap).toBeCloseTo(px(row.forecast), 6)
      expect(layout.plotBottom - mark.top).toBeCloseTo(px(row.total), 6)
    })
  })

  it('stacks the forecast on the spent part with a 2 px surface gap, and draws nothing for nothing', () => {
    const [oct, nov, dec, jan] = columnLayout(months, box).marks
    expect(oct!.forecast).toBeNull()
    expect(nov!.spent!.y - (nov!.forecast!.y + nov!.forecast!.height)).toBeCloseTo(2, 6)
    expect(dec!.spent).toBeNull()
    expect(dec!.forecast!.y + dec!.forecast!.height).toBe(box.height - box.bottom)
    expect(jan!.spent).toBeNull()
    expect(jan!.forecast).toBeNull()
  })

  it('makes the hit area the whole slot, wider than the mark, and centres the mark in it', () => {
    const layout = columnLayout(months, box)
    for (const m of layout.marks) {
      expect(m.slotWidth).toBeGreaterThan(m.width)
      expect(m.x - m.slotX).toBeCloseTo((m.slotWidth - m.width) / 2, 6)
    }
    expect(layout.marks[0]!.slotX).toBe(box.left)
    const last = layout.marks[layout.marks.length - 1]!
    expect(last.slotX + last.slotWidth).toBeCloseTo(box.width - box.right, 6)
  })

  it('shows a value below a pixel as one, and a credit beyond the charges as nothing', () => {
    const layout = columnLayout(
      [
        { key: 'a', label: 'a', spent: 100_000, forecast: 0 },
        { key: 'b', label: 'b', spent: 1, forecast: 0 },
        { key: 'c', label: 'c', spent: -300, forecast: 0 },
      ],
      box,
    )
    expect(layout.marks[1]!.spent!.height).toBe(1)
    expect(layout.marks[2]!.spent).toBeNull()
    expect(columnTable([{ key: 'c', label: 'c', spent: -300, forecast: 0 }])[0]!.total).toBe(-300)
  })
})

describe('the ranked bars and the meters (§8.12)', () => {
  it('ranks largest first, keeps Other last, and sizes each bar against the largest', () => {
    const bars = rankedLayout(
      [
        { key: 'b', label: 'Telekom', amount: 16_200 },
        { key: 'a', label: 'Contoso', amount: 21_300 },
        { key: '', label: 'Other providers (14)', amount: 30_000 },
        { key: 'c', label: 'Hetzner', amount: 8400 },
      ],
      (r) => r.label.startsWith('Other providers'),
    )
    expect(bars.map((b) => b.label)).toEqual(['Contoso', 'Telekom', 'Hetzner', 'Other providers (14)'])
    expect(bars.map((b) => b.fraction)).toEqual([21_300 / 30_000, 16_200 / 30_000, 8400 / 30_000, 1])
  })

  it('draws no bar for nothing', () => {
    expect(rankedLayout([{ key: 'x', label: 'x', amount: 0 }])[0]!.fraction).toBe(0)
    expect(rankedLayout([])).toEqual([])
  })

  it('fills a meter by what is used, full and with how much over when over-assigned', () => {
    expect(meter(40, 50)).toEqual({ fraction: 0.8, over: 0, free: 10 })
    expect(meter(7, 5)).toEqual({ fraction: 1, over: 2, free: 0 })
    expect(meter(0, 5)).toEqual({ fraction: 0, over: 0, free: 5 })
    expect(meter(3, 0)).toEqual({ fraction: 1, over: 3, free: 0 })
  })
})

describe("the months' labels", () => {
  it('names months short, with the year where the columns span years, and long in tooltips', () => {
    expect(monthLabel('2025-12', true)).toBe('Dec 25')
    expect(monthLabel('2026-09', false)).toBe('Sep')
    expect(monthLabel('2026', true)).toBe('2026')
    expect(monthLong('2026-03')).toBe('March 2026')
    expect(monthLong('2024')).toBe('2024')
    expect(spansYears(['2026-01', '2026-12'])).toBe(false)
    expect(spansYears(['2025-12', '2026-01'])).toBe(true)
  })
})
