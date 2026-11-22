import {
  forceCollide,
  forceLink,
  forceManyBody,
  forceSimulation,
  forceX,
  forceY,
  type SimulationNodeDatum,
} from 'd3-force'
import { packEnclose } from 'd3-hierarchy'

/**
 * A force layout of nodes in location circles, shared by the network map (docs/network-map-plan.md §8.2) and the knowledge map
 * (docs/knowledge-base-extensions-plan.md §8.5). Each node brings its body radius (its circle and its name); with every group null
 * it is a plain force layout, each node a group of its own without a circle. In two stages so that the circles never overlap:
 *  1. each group's members are laid out around its centre, with their own simulation; the circle is the smallest one
 *     that encloses them (packEnclose), plus padding;
 *  2. the groups are placed as bodies of that size, pulled together by the connections between them, then pushed apart
 *     until no two circles overlap.
 * The same data gives the same picture: the input is sorted and the random source seeded.
 *
 * Saved routers (docs/network-map-positions-plan.md §4) stay exactly where they were saved. A group with one is anchored:
 * its other members are laid out around the saved ones, and the group never moves. The free groups are placed around the
 * anchored ones and pushed off them; two anchored circles stay as the person put them, even overlapping.
 */

export interface GraphNode {
  id: string
  /** The top-level location (the circle); null: outside every circle. */
  group: string | null
  /** The room the node takes: its circle and its name below it. */
  r: number
}

export interface LayoutEdge {
  source: string
  target: string
}

export interface Point {
  x: number
  y: number
}

export interface GroupCircle extends Point {
  id: string
  r: number
}

export interface Layout {
  nodes: Map<string, Point>
  groups: GroupCircle[]
  bounds: { minX: number; minY: number; maxX: number; maxY: number }
}

const GROUP_PADDING = 28
const GROUP_GAP = 48

/** A location's circle around its nodes: the smallest that encloses them, plus padding. */
export function encloseGroup(id: string, members: { x: number; y: number; r: number }[]): GroupCircle {
  const c = packEnclose(members.map((m) => ({ ...m })))
  return c ? { id, x: c.x, y: c.y, r: c.r + GROUP_PADDING } : { id, x: 0, y: 0, r: GROUP_PADDING }
}

/** A small LCG: d3's random source, seeded, so the same data lays out the same. */
export function seeded(seed = 0x2f6b3a1): () => number {
  let s = seed >>> 0
  return () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0
    return s / 4294967296
  }
}

interface Body extends SimulationNodeDatum {
  id: string
  r: number
}

/** Where a free body starts beside fixed ones: around their centre, on a sunflower spiral (deterministic). */
function spiral(centre: Point, i: number): Point {
  const angle = i * 2.399963
  const radius = 12 * Math.sqrt(0.5 + i)
  return { x: centre.x + radius * Math.cos(angle), y: centre.y + radius * Math.sin(angle) }
}

function centreOf(points: Point[]): Point {
  return points.length
    ? {
        x: points.reduce((s, p) => s + p.x, 0) / points.length,
        y: points.reduce((s, p) => s + p.y, 0) / points.length,
      }
    : { x: 0, y: 0 }
}

function settle(
  bodies: Body[],
  links: { source: string; target: string; distance: number }[],
  ticks: number,
  charge: number,
  collide: (b: Body) => number,
  centre: Point = { x: 0, y: 0 },
) {
  const simulation = forceSimulation<Body>(bodies)
    .randomSource(seeded())
    .force('charge', forceManyBody<Body>().strength(charge))
    .force(
      'link',
      forceLink<Body, { source: string; target: string; distance: number }>(links)
        .id((b) => b.id)
        .distance((l) => l.distance)
        .strength(0.4),
    )
    .force('collide', forceCollide<Body>(collide).iterations(2))
    .force('x', forceX<Body>(centre.x).strength(0.06))
    .force('y', forceY<Body>(centre.y).strength(0.06))
    .stop()
  for (let i = 0; i < ticks; i++) {
    simulation.tick()
  }
}

/**
 * Pushes overlapping circles apart until none overlap (or the rounds run out): the collision force alone can leave some.
 * A fixed circle never moves: its partner moves the whole distance, and two fixed ones stay as they are.
 */
export function separate(
  circles: GroupCircle[],
  gap = GROUP_GAP,
  rounds = 400,
  fixed: ReadonlySet<string> = new Set(),
): void {
  for (let round = 0; round < rounds; round++) {
    let moved = false
    for (let i = 0; i < circles.length; i++) {
      for (let j = i + 1; j < circles.length; j++) {
        const a = circles[i]!
        const b = circles[j]!
        const fa = fixed.has(a.id)
        const fb = fixed.has(b.id)
        if (fa && fb) continue
        let dx = b.x - a.x
        let dy = b.y - a.y
        let d = Math.hypot(dx, dy)
        const need = a.r + b.r + gap
        if (d >= need) continue
        if (d < 1e-6) {
          // On top of each other: apart along a fixed direction, by their order.
          dx = 1
          dy = (j - i) * 0.01
          d = Math.hypot(dx, dy)
        }
        const push = need - d + 0.02
        const pa = fa ? 0 : fb ? push : push / 2
        const pb = push - pa
        a.x -= (dx / d) * pa
        a.y -= (dy / d) * pa
        b.x += (dx / d) * pb
        b.y += (dy / d) * pb
        moved = true
      }
    }
    if (!moved) return
  }
}

export function layoutGraph(
  nodes: GraphNode[],
  edges: LayoutEdge[],
  saved: ReadonlyMap<string, Point> = new Map(),
): Layout {
  const sorted = [...nodes].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
  const byId = new Map(sorted.map((n) => [n.id, n]))
  const known = edges
    .filter((e) => byId.has(e.source) && byId.has(e.target) && e.source !== e.target)
    .sort((a, b) => `${a.source}|${a.target}`.localeCompare(`${b.source}|${b.target}`))
  // Nodes without a location are each a group of their own, without a circle.
  const groupOf = (n: GraphNode) => n.group ?? `node:${n.id}`
  const members = new Map<string, GraphNode[]>()
  for (const n of sorted) {
    const g = groupOf(n)
    members.set(g, [...(members.get(g) ?? []), n])
  }

  // 1. Each group's members: a free group's around (0, 0), an anchored group's around its saved routers, where they are.
  const local = new Map<string, Point>()
  const absolute = new Map<string, Point>()
  const sizes = new Map<string, number>()
  const anchors = new Map<string, Point>()
  for (const [group, list] of [...members.entries()].sort(([a], [b]) => (a < b ? -1 : 1))) {
    const pins = list.filter((n) => saved.has(n.id)).map((n) => saved.get(n.id)!)
    const centre = centreOf(pins)
    let free = 0
    const bodies: Body[] = list.map((n) => {
      const pin = saved.get(n.id)
      if (pin) return { id: n.id, r: n.r, x: pin.x, y: pin.y, fx: pin.x, fy: pin.y }
      return pins.length ? { id: n.id, r: n.r, ...spiral(centre, free++) } : { id: n.id, r: n.r }
    })
    const inside = new Set(list.map((n) => n.id))
    const links = known
      .filter((e) => inside.has(e.source) && inside.has(e.target))
      .map((e) => ({ source: e.source, target: e.target, distance: 70 }))
    if (bodies.length > 1 && pins.length < bodies.length) {
      settle(bodies, links, 220, -160, (b) => b.r, centre)
    } else if (!pins.length) {
      bodies[0]!.x = 0
      bodies[0]!.y = 0
    }
    const enclose = packEnclose(bodies.map((b) => ({ x: b.x ?? 0, y: b.y ?? 0, r: b.r }))) ?? {
      x: 0,
      y: 0,
      r: bodies[0]!.r,
    }
    sizes.set(group, enclose.r + (group.startsWith('node:') ? 0 : GROUP_PADDING))
    if (pins.length) {
      anchors.set(group, { x: enclose.x, y: enclose.y })
      for (const b of bodies) absolute.set(b.id, saved.get(b.id) ?? { x: b.x ?? 0, y: b.y ?? 0 })
    } else {
      for (const b of bodies) local.set(b.id, { x: (b.x ?? 0) - enclose.x, y: (b.y ?? 0) - enclose.y })
    }
  }

  // 2. The groups, pulled by the connections between them, then apart; the anchored ones stay.
  const groupIds = [...sizes.keys()].sort()
  const anchorCentre = centreOf([...anchors.values()])
  let freeGroups = 0
  const groupBodies: Body[] = groupIds.map((g) => {
    const anchor = anchors.get(g)
    if (anchor) return { id: g, r: sizes.get(g)!, x: anchor.x, y: anchor.y, fx: anchor.x, fy: anchor.y }
    return anchors.size
      ? { id: g, r: sizes.get(g)!, ...spiral(anchorCentre, freeGroups++) }
      : { id: g, r: sizes.get(g)! }
  })
  const between = new Map<string, number>()
  for (const e of known) {
    const a = groupOf(byId.get(e.source)!)
    const b = groupOf(byId.get(e.target)!)
    if (a === b) continue
    const key = a < b ? `${a}|${b}` : `${b}|${a}`
    between.set(key, (between.get(key) ?? 0) + 1)
  }
  const groupLinks = [...between.keys()].sort().map((key) => {
    const [a, b] = key.split('|') as [string, string]
    return { source: a, target: b, distance: sizes.get(a)! + sizes.get(b)! + GROUP_GAP * 1.5 }
  })
  if (groupBodies.length > 1 && anchors.size < groupBodies.length) {
    settle(groupBodies, groupLinks, 300, -400, (b) => b.r + GROUP_GAP / 2, anchorCentre)
  } else if (groupBodies.length === 1 && !anchors.size) {
    groupBodies[0]!.x = 0
    groupBodies[0]!.y = 0
  }
  const circles: GroupCircle[] = groupBodies.map((b) => ({ id: b.id, x: b.x ?? 0, y: b.y ?? 0, r: b.r }))
  separate(circles, GROUP_GAP, 400, new Set(anchors.keys()))
  const centre = new Map(circles.map((c) => [c.id, c]))

  const positions = new Map<string, Point>()
  for (const n of sorted) {
    const fixed = absolute.get(n.id)
    if (fixed) {
      positions.set(n.id, fixed)
      continue
    }
    const c = centre.get(groupOf(n))!
    const p = local.get(n.id)!
    positions.set(n.id, { x: c.x + p.x, y: c.y + p.y })
  }
  const bounds = { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity }
  for (const c of circles) {
    bounds.minX = Math.min(bounds.minX, c.x - c.r)
    bounds.minY = Math.min(bounds.minY, c.y - c.r)
    bounds.maxX = Math.max(bounds.maxX, c.x + c.r)
    bounds.maxY = Math.max(bounds.maxY, c.y + c.r)
  }
  if (!circles.length) {
    Object.assign(bounds, { minX: -100, minY: -100, maxX: 100, maxY: 100 })
  }
  return {
    nodes: positions,
    groups: circles.filter((c) => !c.id.startsWith('node:')),
    bounds,
  }
}
