import { describe, expect, it } from 'vitest'
import type { ResponsibilityStep } from '@/api/responsibilities'
import {
  arrange,
  cellSize,
  cellsOverlap,
  circleOrder,
  CELL_HEIGHT,
  fitRing,
  grid,
  ONE_RING,
  place,
  type ArrangeItem,
  type Form,
} from './arrange'

const person = (id: string, name: string) => ({ id, name, active: true })
const anna = person('a', 'Anna Muster')
const ben = person('b', 'Ben Keller')
const step = (p: { id: string; name: string; active: boolean }): ResponsibilityStep[] => [
  { step: 1, role: 'Responsible', person: p },
]

/** n responsibilities: every third Anna's, every third Ben's, the rest nobody's; names of varying length. */
function items(n: number): ArrangeItem[] {
  return Array.from({ length: n }, (_, i) => ({
    id: `responsibility:${String(i).padStart(3, '0')}`,
    name: i % 4 === 0 ? `Responsibility with a long name ${i}` : `R${i}`,
    escalation: i % 3 === 0 ? step(anna) : i % 3 === 1 ? step(ben) : [],
  }))
}

const forms: Form[] = ['circle', 'clusters', 'grid']
const sizes = [1, 2, 3, 4, 5, 7, 10, 17, 24, 25, 40, 61, 100, 120]

describe('arranging the overview (docs/responsibilities-mcp-and-map-plan.md §5.2)', () => {
  it('gives every responsibility one cell: its longest name as drawn, cut at 28 characters, plus a gap', () => {
    expect(cellSize(['IT'])).toEqual({ w: 52 + 24, h: CELL_HEIGHT })
    expect(cellSize(['Network', 'x'.repeat(20)]).w).toBeCloseTo(20 * 6.6 + 24)
    expect(cellSize(['x'.repeat(60)]).w).toBeCloseTo(28 * 6.6 + 24)
  })

  for (const form of forms)
    it(`never overlaps two responsibilities, and gives the same form for the same data: ${form}`, () => {
      for (const n of sizes) {
        const list = items(n)
        const points = arrange(form, list)
        expect(points.size, `${form} ${n}`).toBe(n)
        expect(cellsOverlap(points, cellSize(list.map((i) => i.name))), `${form} ${n}`).toBe(false)
        expect([...arrange(form, [...list].reverse())].sort()).toEqual([...points].sort())
      }
    })

  it('puts one responsibility in the middle, and starts a circle at 12 o’clock', () => {
    expect(arrange('circle', items(1)).get('responsibility:000')).toEqual({ x: 0, y: 0 })
    const three = arrange('circle', items(3))
    const first = three.get(circleOrder(items(3))[0]!.id)!
    expect(first.x).toBeCloseTo(0)
    expect(first.y).toBeLessThan(0)
    expect([...three.values()].reduce((s, p) => s + p.x, 0)).toBeCloseTo(0)
  })

  it('orders the circle by main responsible, then by name, nobody last', () => {
    const order = circleOrder([
      { id: '1', name: 'Zeta', escalation: step(anna) },
      { id: '2', name: 'Alpha', escalation: [] },
      { id: '3', name: 'Beta', escalation: step(ben) },
      { id: '4', name: 'Alpha', escalation: step(anna) },
    ])
    expect(order.map((i) => i.id)).toEqual(['4', '1', '3', '2'])
  })

  it('makes rings around each other above one ring’s worth', () => {
    const many = arrange('circle', items(ONE_RING + 6))
    const radii = new Set([...many.values()].map((p) => Math.round(Math.hypot(p.x, p.y))))
    expect(radii.size).toBeGreaterThan(1)
    expect(arrange('circle', items(ONE_RING)).size).toBe(ONE_RING)
    const one = new Set([...arrange('circle', items(ONE_RING)).values()].map((p) => Math.round(Math.hypot(p.x, p.y))))
    expect(one.size).toBe(1)
  })

  it('keeps a cluster’s ring big enough for its person in the middle', () => {
    const cell = cellSize(['Network', 'Backup'])
    expect(fitRing(['a', 'b'], cell, 150)).toBeGreaterThanOrEqual(150)
    // Anna's two around one middle, Ben's one beside his.
    const points = arrange('clusters', [
      { id: 'n', name: 'Network', escalation: step(anna) },
      { id: 'b', name: 'Backup', escalation: step(anna) },
      { id: 'p', name: 'Printers', escalation: step(ben) },
    ])
    const n = points.get('n')!
    const b = points.get('b')!
    expect(Math.hypot(n.x - b.x, n.y - b.y)).toBeGreaterThanOrEqual(2 * 150 - 0.01)
  })

  it('fills a grid row by row, about 16:10', () => {
    const list = items(30)
    const cell = cellSize(list.map((i) => i.name))
    const points = grid(list, cell)
    const columns = new Set([...points.values()].map((p) => Math.round(p.x))).size
    const rows = new Set([...points.values()].map((p) => Math.round(p.y))).size
    expect(columns * rows).toBeGreaterThanOrEqual(30)
    expect((columns * cell.w) / (rows * cell.h)).toBeGreaterThan(1)
  })

  it('arranges 100 responsibilities quickly', () => {
    const list = items(100)
    const started = performance.now()
    for (const form of forms) arrange(form, list)
    // The plan's budget is 20 ms a form; generous here, since test machines vary.
    expect(performance.now() - started).toBeLessThan(600)
  })
})

describe('placing a form (§5.3)', () => {
  const form = new Map([
    ['responsibility:a', { x: -100, y: 0 }],
    ['responsibility:b', { x: 100, y: 0 }],
  ])
  const names = ['Alpha', 'Beta']

  it('centres it where the responsibilities are now', () => {
    const { points, clear } = place(form, { x: 500, y: 300 }, [], names)
    expect(clear).toBe(true)
    expect(points.get('responsibility:a')).toEqual({ x: 400, y: 300 })
  })

  it('moves the whole form off a note, keeping its shape', () => {
    const note = { x: 350, y: 250, w: 100, h: 100 }
    const { points, clear } = place(form, { x: 500, y: 300 }, [note], names)
    expect(clear).toBe(true)
    const a = points.get('responsibility:a')!
    const b = points.get('responsibility:b')!
    expect(b.x - a.x).toBeCloseTo(200)
    expect(b.y - a.y).toBeCloseTo(0)
    expect(a).not.toEqual({ x: 400, y: 300 })
  })

  it('stays centred when no place is clear', () => {
    const everywhere = { x: -1e5, y: -1e5, w: 2e5, h: 2e5 }
    const { points, clear } = place(form, { x: 0, y: 0 }, [everywhere], names)
    expect(clear).toBe(false)
    expect(points.get('responsibility:b')).toEqual({ x: 100, y: 0 })
  })
})
