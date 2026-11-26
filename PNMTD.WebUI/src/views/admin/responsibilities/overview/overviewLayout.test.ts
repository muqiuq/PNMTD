import { describe, expect, it } from 'vitest'
import type { ResponsibilityMap, ResponsibilityRole, ResponsibilityStep } from '@/api/responsibilities'
import {
  bodyRadius,
  buildGraph,
  initials,
  layoutOverview,
  noteObstacle,
  noteSize,
  type Layer,
  type Lines,
} from './overviewLayout'

const anna = { id: 'a1', name: 'Anna Muster', code: 'AMU', color: '#2f8fd8', active: true }
const ben = { id: 'b1', name: 'Ben Keller', code: null, color: null, active: false }
const cleo = { id: 'c1', name: 'Cleo Deputy', code: null, color: null, active: true }
const gabi = { id: 'g1', name: 'Gabi Global', code: null, color: null, active: true }

/** The escalation order as the server computes it (docs/responsibilities-mcp-and-map-plan.md §3): only active people. */
const order = (...steps: [{ id: string; name: string; active: boolean }, ResponsibilityRole][]): ResponsibilityStep[] =>
  steps.map(([p, role], i) => ({ step: i + 1, role, person: { id: p.id, name: p.name, active: p.active } }))

function map(saved: Partial<Record<string, { x: number; y: number }>> = {}): ResponsibilityMap {
  const node = (id: string, more: Partial<ResponsibilityMap['responsibilities'][number]> = {}) => ({
    id,
    name: id,
    summary: null,
    color: null,
    x: saved[id]?.x ?? null,
    y: saved[id]?.y ?? null,
    position: null,
    responsibleId: null,
    deputyId: null,
    globalStandsIn: false,
    attention: false,
    escalation: [] as ResponsibilityStep[],
    links: 0,
    combinations: 0,
    ...more,
  })
  return {
    responsibilities: [
      // Ben is inactive: the next in the queue, the global responsible, moves up.
      node('network', {
        responsibleId: anna.id,
        deputyId: ben.id,
        attention: true,
        escalation: order([anna, 'Responsible'], [gabi, 'GlobalResponsible']),
      }),
      node('backup', {
        responsibleId: anna.id,
        deputyId: cleo.id,
        escalation: order([anna, 'Responsible'], [cleo, 'Deputy'], [gabi, 'GlobalResponsible']),
      }),
      node('printers', { globalStandsIn: true, escalation: order([gabi, 'GlobalResponsible']) }),
    ],
    people: [anna, ben, cleo, gabi],
    objects: [
      {
        kind: 'Host',
        id: 'h1',
        name: 'sw-core',
        number: null,
        links: [
          { responsibilityId: 'network', linkId: 'l1' },
          { responsibilityId: 'backup', linkId: 'l2' },
        ],
      },
      { kind: 'Page', id: 'p1', name: 'WAN', number: 42, links: [{ responsibilityId: 'network', linkId: 'l3' }] },
    ],
    notes: [],
    global: {
      responsible: { id: gabi.id, name: gabi.name, active: true },
      deputy: null,
      updatedAt: '',
      updatedByName: null,
    },
    objectsTotal: 2,
    truncated: false,
    savedPositions: Object.keys(saved).length,
  }
}

const layers = (...l: Layer[]) => new Set<Layer>(l)
const personLines = (lines: Lines) =>
  buildGraph(map(), layers('people'), lines)
    .edges.map((e) => `${e.source.slice('person:'.length)}→${e.target.slice('responsibility:'.length)} ${e.kind}`)
    .sort()
const distance = (a: { x: number; y: number }, b: { x: number; y: number }) => Math.hypot(a.x - b.x, a.y - b.y)

describe("the overview's graph (docs/responsibilities-extensions-plan.md §6.1)", () => {
  it('draws the responsibilities always, people and objects by their layers', () => {
    expect(buildGraph(map(), layers()).nodes.map((n) => n.id)).toEqual([
      'responsibility:network',
      'responsibility:backup',
      'responsibility:printers',
    ])
    const { nodes, edges } = buildGraph(map(), layers('people', 'hosts'), 'both')
    expect(nodes.filter((n) => n.kind === 'person').map((n) => n.code)).toEqual(['AMU', 'CD', 'GG'])
    expect(nodes.some((n) => n.kind === 'page')).toBe(false)
    expect(edges.filter((e) => e.source === 'host:h1')).toHaveLength(2)
  })

  it('makes initials from a name', () => {
    expect(initials('Anna Muster')).toBe('AM')
    expect(initials('Carla de la Rossi')).toBe('CR')
    expect(initials('ben')).toBe('B')
    expect(initials('  ')).toBe('?')
  })
})

describe("the overview's lines (docs/responsibilities-mcp-and-map-plan.md §4)", () => {
  it('draws only the first in each escalation order by default', () => {
    expect(personLines('responsible')).toEqual([
      'a1→backup responsible',
      'a1→network responsible',
      'g1→printers global',
    ])
    const people = buildGraph(map(), layers('people')).nodes.filter((n) => n.kind === 'person')
    expect(people.map((n) => n.id)).toEqual(['person:a1', 'person:g1'])
  })

  it('draws the second for Deputy: whoever comes next in the queue, in the style of their role', () => {
    expect(personLines('deputy')).toEqual(['c1→backup deputy', 'g1→network global'])
  })

  it('draws the first two for Both, and never an inactive person', () => {
    expect(personLines('both')).toEqual([
      'a1→backup responsible',
      'a1→network responsible',
      'c1→backup deputy',
      'g1→network global',
      'g1→printers global',
    ])
    expect(buildGraph(map(), layers('people'), 'both').nodes.some((n) => n.id === 'person:b1')).toBe(false)
  })

  it('draws no person without the People layer', () => {
    expect(buildGraph(map(), layers('hosts'), 'both').edges.every((e) => e.kind === 'link')).toBe(true)
  })
})

describe("the overview's layout (§6.3)", () => {
  it('keeps saved responsibilities exactly where they were saved', () => {
    const saved = { network: { x: 120.5, y: -40.25 }, backup: { x: -300, y: 210 } }
    const { nodes, edges } = buildGraph(map(saved), layers('people', 'hosts', 'pages'))
    const layout = layoutOverview(nodes, edges)
    expect(layout.nodes.get('responsibility:network')).toEqual(saved.network)
    expect(layout.nodes.get('responsibility:backup')).toEqual(saved.backup)
  })

  it('gives the same picture for the same data', () => {
    const a = buildGraph(map(), layers('people', 'hosts'))
    const b = buildGraph(map(), layers('people', 'hosts'))
    expect([...layoutOverview(a.nodes, a.edges).nodes]).toEqual([...layoutOverview(b.nodes, b.edges).nodes])
  })

  it('places nothing under a note', () => {
    const note = { id: 'note:n1', x: 0, y: 0, r: 180 }
    const { nodes, edges } = buildGraph(map(), layers('people', 'hosts', 'pages'))
    const layout = layoutOverview(nodes, edges, [note])
    expect(layout.nodes.get('note:n1')).toEqual({ x: 0, y: 0 })
    for (const n of nodes) {
      expect(distance(layout.nodes.get(n.id)!, note)).toBeGreaterThanOrEqual(note.r + bodyRadius(n.kind) - 0.5)
    }
  })
})

describe("a note's size (§7.1)", () => {
  it('is as wide as chosen, and as tall as its text, within limits', () => {
    expect(noteSize('Hi', 'Small')).toEqual({ w: 160, h: 44 })
    const long = noteSize('word '.repeat(200), 'Medium')
    expect(long.w).toBe(240)
    expect(long.h).toBeGreaterThan(200)
    expect(noteSize('x\n'.repeat(500), 'Large').h).toBe(3 * 360)
    expect(noteSize('a\nb\nc', 'Medium').h).toBe(20 + 3 * 17)
  })

  it('is a body around its rectangle for the layout', () => {
    expect(noteObstacle({ id: 'note:n', x: 0, y: 0, w: 60, h: 80 })).toEqual({ id: 'note:n', x: 30, y: 40, r: 50 })
  })
})

describe('the layout after arranging (docs/responsibilities-mcp-and-map-plan.md §5.4)', () => {
  it('keeps arranged places fixed, as saved ones', () => {
    const { nodes, edges } = buildGraph(map({ network: { x: 0, y: 0 } }), layers('people'), 'both')
    const anchors = new Map([
      ['responsibility:network', { x: 900, y: 900 }],
      ['responsibility:backup', { x: 1200, y: 900 }],
    ])
    const layout = layoutOverview(nodes, edges, [], anchors)
    expect(layout.nodes.get('responsibility:network')).toEqual({ x: 900, y: 900 })
    expect(layout.nodes.get('responsibility:backup')).toEqual({ x: 1200, y: 900 })
  })

  it('starts a person next to their fixed responsibilities, so they settle there', () => {
    // Anna's two far from the middle, on a ring around (2000, 2000): she ends up near its middle, not near (0, 0).
    const saved = { network: { x: 2000, y: 1850 }, backup: { x: 2000, y: 2150 }, printers: { x: -2000, y: -2000 } }
    const { nodes, edges } = buildGraph(map(saved), layers('people'))
    const anna = layoutOverview(nodes, edges).nodes.get('person:a1')!
    expect(distance(anna, { x: 2000, y: 2000 })).toBeLessThan(250)
  })
})
