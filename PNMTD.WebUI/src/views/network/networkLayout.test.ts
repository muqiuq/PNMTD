import { describe, expect, it } from 'vitest'
import {
  encloseGroup,
  layout,
  nodeRadius,
  separate,
  type LayoutEdge,
  type LayoutNode,
  type Point,
} from './networkLayout'

const sites = ['site-a', 'site-b', 'site-c']

function world(): { nodes: LayoutNode[]; edges: LayoutEdge[] } {
  const nodes: LayoutNode[] = []
  const edges: LayoutEdge[] = []
  sites.forEach((site, s) => {
    nodes.push({ id: `router:${s}`, group: site, kind: 'Router' })
    for (let h = 0; h < 6; h++) {
      nodes.push({ id: `host:${s}-${h}`, group: site, kind: 'Host' })
      edges.push({ source: `router:${s}`, target: `host:${s}-${h}` })
    }
  })
  edges.push({ source: 'router:0', target: 'router:1' }, { source: 'router:1', target: 'router:2' })
  nodes.push({ id: 'router:loose', group: null, kind: 'Router' })
  return { nodes, edges }
}

describe('networkLayout (docs/network-map-plan.md §8.2)', () => {
  it('gives the same picture for the same data, whatever the order', () => {
    const { nodes, edges } = world()
    const a = layout(nodes, edges)
    const b = layout([...nodes].reverse(), [...edges].reverse())
    expect([...b.nodes.entries()].sort()).toEqual([...a.nodes.entries()].sort())
    expect(b.groups).toEqual(a.groups)
  })

  it('draws a circle per top-level location that encloses its members, and none for nodes without one', () => {
    const { nodes, edges } = world()
    const result = layout(nodes, edges)
    expect(result.groups.map((g) => g.id).sort()).toEqual(sites)
    for (const node of nodes.filter((n) => n.group)) {
      const circle = result.groups.find((g) => g.id === node.group)!
      const p = result.nodes.get(node.id)!
      expect(Math.hypot(p.x - circle.x, p.y - circle.y) + nodeRadius(node.kind)).toBeLessThanOrEqual(circle.r + 1e-6)
    }
    expect(result.nodes.has('router:loose')).toBe(true)
  })

  it('keeps the circles apart', () => {
    const { nodes, edges } = world()
    const { groups } = layout(nodes, edges)
    for (let i = 0; i < groups.length; i++) {
      for (let j = i + 1; j < groups.length; j++) {
        const a = groups[i]!
        const b = groups[j]!
        expect(Math.hypot(a.x - b.x, a.y - b.y)).toBeGreaterThanOrEqual(a.r + b.r - 1e-6)
      }
    }
  })

  it('separates circles that start on top of each other', () => {
    const circles = [
      { id: 'a', x: 0, y: 0, r: 50 },
      { id: 'b', x: 0, y: 0, r: 30 },
      { id: 'c', x: 5, y: 0, r: 40 },
    ]
    separate(circles, 10)
    for (let i = 0; i < circles.length; i++) {
      for (let j = i + 1; j < circles.length; j++) {
        const d = Math.hypot(circles[i]!.x - circles[j]!.x, circles[i]!.y - circles[j]!.y)
        expect(d).toBeGreaterThanOrEqual(circles[i]!.r + circles[j]!.r + 10 - 0.5)
      }
    }
  })

  it('lays out an empty map and a single node', () => {
    expect(layout([], []).nodes.size).toBe(0)
    const one = layout([{ id: 'router:x', group: 'g', kind: 'Router' }], [])
    expect(one.nodes.get('router:x')).toEqual({ x: 0, y: 0 })
    expect(one.groups).toHaveLength(1)
  })
})

describe('networkLayout with saved routers (docs/network-map-positions-plan.md §4)', () => {
  const overlap = (a: { x: number; y: number; r: number }, b: { x: number; y: number; r: number }) =>
    Math.hypot(a.x - b.x, a.y - b.y) < a.r + b.r - 1e-6

  it('puts saved routers exactly where they were saved, and their hosts around them', () => {
    const { nodes, edges } = world()
    const saved = new Map<string, Point>([
      ['router:0', { x: 812.25, y: -403.5 }],
      ['router:loose', { x: -1500, y: 20 }],
    ])
    const result = layout(nodes, edges, saved)
    expect(result.nodes.get('router:0')).toEqual({ x: 812.25, y: -403.5 })
    expect(result.nodes.get('router:loose')).toEqual({ x: -1500, y: 20 })
    // site-a is anchored: its circle encloses the saved router and its six hosts.
    const circle = result.groups.find((g) => g.id === 'site-a')!
    for (const node of nodes.filter((n) => n.group === 'site-a')) {
      const p = result.nodes.get(node.id)!
      expect(Math.hypot(p.x - circle.x, p.y - circle.y) + nodeRadius(node.kind)).toBeLessThanOrEqual(circle.r + 1e-6)
    }
    expect(Math.hypot(result.nodes.get('host:0-0')!.x - 812.25, result.nodes.get('host:0-0')!.y + 403.5)).toBeLessThan(
      300,
    )
  })

  it('keeps the free circles off the anchored ones', () => {
    const { nodes, edges } = world()
    // site-b's router saved right where the automatic layout would put others.
    const result = layout(nodes, edges, new Map([['router:1', { x: 0, y: 0 }]]))
    const anchored = result.groups.find((g) => g.id === 'site-b')!
    for (const g of result.groups.filter((g) => g.id !== 'site-b')) expect(overlap(g, anchored)).toBe(false)
  })

  it('gives back the saved picture when every router is saved, the same each time', () => {
    const nodes: LayoutNode[] = [
      { id: 'router:a', group: 'x', kind: 'Router' },
      { id: 'router:b', group: 'y', kind: 'Router' },
      { id: 'router:c', group: null, kind: 'Router' },
    ]
    const saved = new Map<string, Point>([
      ['router:a', { x: 10, y: 10 }],
      ['router:b', { x: 30, y: 0 }],
      ['router:c', { x: -400, y: 300 }],
    ])
    const edges = [{ source: 'router:a', target: 'router:b' }]
    const one = layout(nodes, edges, saved)
    const two = layout([...nodes].reverse(), edges, saved)
    for (const [id, p] of saved) expect(one.nodes.get(id)).toEqual(p)
    expect(two.nodes).toEqual(one.nodes)
    // Two anchored circles stay as the person put them, even overlapping.
    expect(
      overlap(
        one.groups.find((g) => g.id === 'x')!,
        one.groups.find((g) => g.id === 'y')!,
      ),
    ).toBe(true)
  })

  it('moves only the free circle when it overlaps a fixed one', () => {
    const circles = [
      { id: 'fixed', x: 0, y: 0, r: 50 },
      { id: 'free', x: 10, y: 0, r: 30 },
      { id: 'fixed-2', x: 20, y: 0, r: 20 },
    ]
    separate(circles, 10, 400, new Set(['fixed', 'fixed-2']))
    expect(circles[0]).toEqual({ id: 'fixed', x: 0, y: 0, r: 50 })
    expect(circles[2]).toEqual({ id: 'fixed-2', x: 20, y: 0, r: 20 })
    expect(Math.hypot(circles[1]!.x, circles[1]!.y)).toBeGreaterThanOrEqual(50 + 30 + 10 - 0.5)
  })

  it('encloses a location around where its nodes are now', () => {
    const c = encloseGroup('g', [
      { x: -100, y: 0, r: 10 },
      { x: 100, y: 0, r: 10 },
    ])
    expect(c.id).toBe('g')
    expect(c.x).toBeCloseTo(0)
    expect(c.r).toBeGreaterThan(110)
  })
})
