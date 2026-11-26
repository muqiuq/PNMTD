import { packSiblings } from 'd3-hierarchy'
import type { Point } from '@/components/graph/forceLayout'
import type { ResponsibilityStep } from '@/api/responsibilities'

/**
 * *Arrange* on the overview (docs/responsibilities-mcp-and-map-plan.md §5): every responsibility in a circle, in clusters around their
 * main responsible, or in a grid. One cell size for all, so that no two circles or names overlap; the same data gives the same form.
 * The result is moves like a drag's: nothing is saved until *Save layout*.
 */

export type Form = 'circle' | 'clusters' | 'grid'

export interface ArrangeItem {
  id: string
  name: string
  escalation: readonly ResponsibilityStep[]
}

export interface Cell {
  w: number
  h: number
}

/** A note's rectangle: its top-left corner and size. */
export interface NoteBox {
  x: number
  y: number
  w: number
  h: number
}

/** The map cuts names here (OverviewCanvas's MAX_LABEL). */
const MAX_LABEL = 28
/** About a character's width of a responsibility's name, 11 px bold. */
const CHAR = 6.6
/** A responsibility's circle: radius 26. */
const RADIUS = 26
const MAX_LABEL_WIDTH = 200
const GAP = 24
/** The circle, its name below and a gap. */
export const CELL_HEIGHT = 110
/** Above this many, the circle becomes rings around each other. */
export const ONE_RING = 24
/** The smallest ring: room for people inside. */
const MIN_RING = 120
/** A cluster's ring: its person in the middle, never pushed out by the layout (39 + 50 + 48 for the gap, and some). */
const CLUSTER_RING = 150
const CLUSTER_GAP = 40
/** Clear of a note by this much. */
const NOTE_MARGIN = 24

const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' })
const byName = (a: ArrangeItem, b: ArrangeItem) => collator.compare(a.name, b.name) || (a.id < b.id ? -1 : 1)
const main = (item: ArrangeItem) => item.escalation[0]?.person ?? null

/** One cell for every responsibility: its longest name as the map draws it, between the circle's width and 200, plus a gap. */
export function cellSize(names: readonly string[]): Cell {
  const longest = names.reduce((max, n) => Math.max(max, Math.min([...n].length, MAX_LABEL)), 0)
  return { w: Math.min(MAX_LABEL_WIDTH, Math.max(2 * RADIUS, longest * CHAR)) + GAP, h: CELL_HEIGHT }
}

/**
 * The smallest ring, at least `min`, on which these cells overlap neither each other nor those already `placed`: from where
 * neighbours are a cell's width apart, outwards in steps of 2. One alone sits in the middle.
 */
export function fitRing(
  ids: readonly string[],
  cell: Cell,
  min: number,
  placed: ReadonlyMap<string, Point> = new Map(),
  centre: Point = { x: 0, y: 0 },
): number {
  if (ids.length === 1 && !placed.size && min === 0) return 0
  let radius = Math.max(min, ids.length > 1 ? cell.w / (2 * Math.sin(Math.PI / ids.length)) : 0)
  // Where every pair is at least a cell's diagonal apart nothing can overlap: the search ends there at the latest.
  const enough = Math.max(
    radius,
    ids.length > 1 ? Math.hypot(cell.w, cell.h) / (2 * Math.sin(Math.PI / ids.length)) : 0,
  )
  while (radius < enough + 1e-6 || placed.size) {
    if (!cellsOverlap(new Map([...placed, ...ring(ids, radius, centre)]), cell)) return radius
    radius += 2
  }
  return radius
}

/** n places on a ring, from 12 o'clock clockwise (the map's y grows downwards). */
function ring(ids: readonly string[], radius: number, centre: Point = { x: 0, y: 0 }): Map<string, Point> {
  const out = new Map<string, Point>()
  ids.forEach((id, i) => {
    const angle = -Math.PI / 2 + (2 * Math.PI * i) / ids.length
    out.set(id, { x: centre.x + radius * Math.cos(angle), y: centre.y + radius * Math.sin(angle) })
  })
  return out
}

/** By main responsible, then by name; nobody last. A person's responsibilities sit together, so their lines stay short. */
export function circleOrder(items: readonly ArrangeItem[]): ArrangeItem[] {
  return [...items].sort((a, b) => {
    const pa = main(a)
    const pb = main(b)
    if (!pa || !pb) return pa ? -1 : pb ? 1 : byName(a, b)
    return collator.compare(pa.name, pb.name) || (pa.id < pb.id ? -1 : pa.id > pb.id ? 1 : 0) || byName(a, b)
  })
}

/** How many fit on a ring of this radius with every pair a cell's diagonal apart, so the ring needn't grow for them. */
function capacity(radius: number, cell: Cell): number {
  const diagonal = Math.hypot(cell.w, cell.h)
  return diagonal >= 2 * radius ? 1 : Math.max(1, Math.floor(Math.PI / Math.asin(diagonal / (2 * radius))))
}

/**
 * One ring up to ONE_RING; beyond, rings around each other (a compact disc), filled from the inside, each as small as it can be
 * clear of the ones inside it; the outermost spreads what is left.
 */
export function circle(items: readonly ArrangeItem[], cell: Cell): Map<string, Point> {
  const ids = circleOrder(items).map((i) => i.id)
  if (ids.length <= ONE_RING) return ring(ids, fitRing(ids, cell, ids.length > 1 ? MIN_RING : 0))
  const out = new Map<string, Point>()
  let radius = Math.max(MIN_RING, Math.hypot(cell.w, cell.h))
  let next = 0
  while (next < ids.length) {
    const take = Math.min(capacity(radius, cell), ids.length - next)
    const slice = ids.slice(next, next + take)
    radius = fitRing(slice, cell, radius, out)
    for (const [id, p] of ring(slice, radius)) out.set(id, p)
    next += take
    radius += cell.h
  }
  return out
}

/**
 * A ring for each main responsible with that person in the middle, the largest first; those with nobody are a cluster of their
 * own, last. The clusters are packed as circles, CLUSTER_GAP apart.
 */
export function clusters(items: readonly ArrangeItem[], cell: Cell): Map<string, Point> {
  const groups = new Map<string, ArrangeItem[]>()
  for (const item of items) {
    const key = main(item)?.id ?? ''
    groups.set(key, [...(groups.get(key) ?? []), item])
  }
  const nameOf = (key: string) => groups.get(key)![0]!.escalation[0]?.person.name ?? ''
  const keys = [...groups.keys()].sort((a, b) => {
    if (!a || !b) return a ? -1 : b ? 1 : 0
    return groups.get(b)!.length - groups.get(a)!.length || collator.compare(nameOf(a), nameOf(b)) || (a < b ? -1 : 1)
  })
  const reach = Math.hypot(cell.w, cell.h) / 2
  const circles = keys.map((key) => {
    const members = [...groups.get(key)!].sort(byName)
    // With a person in the middle, even one responsibility keeps them room; nobody's ring is as small as it can be.
    const ids = members.map((m) => m.id)
    const radius = key
      ? members.length === 1
        ? CLUSTER_RING
        : fitRing(ids, cell, CLUSTER_RING)
      : fitRing(ids, cell, 0)
    const lone = !!key && members.length === 1
    return { key, members, radius, lone, r: (lone ? radius : radius + reach) + CLUSTER_GAP / 2, x: 0, y: 0 }
  })
  packSiblings(circles)
  const out = new Map<string, Point>()
  for (const c of circles) {
    // One responsibility beside its person: to the right of the middle.
    if (c.lone) out.set(c.members[0]!.id, { x: c.x + c.radius, y: c.y })
    else
      for (const [id, p] of ring(
        c.members.map((m) => m.id),
        c.radius,
        c,
      ))
        out.set(id, p)
  }
  // Around (0, 0), as the other forms.
  const all = [...out.values()]
  const mid = {
    x: (Math.min(...all.map((p) => p.x)) + Math.max(...all.map((p) => p.x))) / 2,
    y: (Math.min(...all.map((p) => p.y)) + Math.max(...all.map((p) => p.y))) / 2,
  }
  return new Map([...out].map(([id, p]) => [id, { x: p.x - mid.x, y: p.y - mid.y }]))
}

/** Rows and columns by name, about as wide as a screen (16:10). */
export function grid(items: readonly ArrangeItem[], cell: Cell): Map<string, Point> {
  const sorted = [...items].sort(byName)
  const columns = Math.max(1, Math.ceil(Math.sqrt((sorted.length * 1.6 * cell.h) / cell.w)))
  const rows = Math.ceil(sorted.length / columns)
  const out = new Map<string, Point>()
  sorted.forEach((item, i) => {
    const column = i % columns
    const row = Math.floor(i / columns)
    out.set(item.id, { x: (column - (columns - 1) / 2) * cell.w, y: (row - (rows - 1) / 2) * cell.h })
  })
  return out
}

/** The form, around (0, 0). */
export function arrange(form: Form, items: readonly ArrangeItem[]): Map<string, Point> {
  if (!items.length) return new Map()
  const cell = cellSize(items.map((i) => i.name))
  return form === 'circle' ? circle(items, cell) : form === 'clusters' ? clusters(items, cell) : grid(items, cell)
}

/** What a responsibility at p covers: its circle and its name below, as wide as the cell without its gap. */
function box(p: Point, cell: Cell): NoteBox {
  const half = (cell.w - GAP) / 2
  return { x: p.x - half, y: p.y - RADIUS, w: 2 * half, h: CELL_HEIGHT - GAP - 10 }
}

function overlaps(a: NoteBox, b: NoteBox, margin: number): boolean {
  return a.x < b.x + b.w + margin && b.x < a.x + a.w + margin && a.y < b.y + b.h + margin && b.y < a.y + a.h + margin
}

/**
 * The form moved to `centre`, and from there, if a responsibility would lie under a note, as a whole along a fixed spiral (steps of
 * about 60) to the nearest place clear of every note. `clear` false: none found in 200 tries, so it stays at the centre.
 */
export function place(
  form: ReadonlyMap<string, Point>,
  centre: Point,
  notes: readonly NoteBox[],
  names: readonly string[],
): { points: Map<string, Point>; clear: boolean } {
  const cell = cellSize(names)
  const at = (offset: Point) =>
    new Map([...form].map(([id, p]) => [id, { x: p.x + centre.x + offset.x, y: p.y + centre.y + offset.y }]))
  const free = (points: Map<string, Point>) =>
    [...points.values()].every((p) => notes.every((n) => !overlaps(box(p, cell), n, NOTE_MARGIN)))
  for (let i = 0; i <= 200; i++) {
    const angle = i * 2.399963
    const radius = 60 * Math.sqrt(i)
    const points = at({ x: radius * Math.cos(angle), y: radius * Math.sin(angle) })
    if (free(points)) return { points: round(points), clear: true }
  }
  return { points: round(at({ x: 0, y: 0 })), clear: false }
}

/** Two decimals, as the places are saved. */
function round(points: Map<string, Point>): Map<string, Point> {
  const r = (v: number) => Math.round(v * 100) / 100
  return new Map([...points].map(([id, p]) => [id, { x: r(p.x), y: r(p.y) }]))
}

/** Whether two placed responsibilities' cells overlap: for the tests, and a check of the forms. */
export function cellsOverlap(points: ReadonlyMap<string, Point>, cell: Cell): boolean {
  const list = [...points.values()]
  for (let i = 0; i < list.length; i++)
    for (let j = i + 1; j < list.length; j++) {
      const a = list[i]!
      const b = list[j]!
      if (Math.abs(a.x - b.x) < cell.w - 0.01 && Math.abs(a.y - b.y) < cell.h - 0.01) return true
    }
  return false
}
