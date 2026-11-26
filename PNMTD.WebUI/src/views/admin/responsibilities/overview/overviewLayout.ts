import { layoutGraph, type Layout, type Point } from '@/components/graph/forceLayout'
import type { ResponsibilityMap, ResponsibilityRole, ResponsibilityStep } from '@/api/responsibilities'

/**
 * The overview's graph and layout (docs/responsibilities-extensions-plan.md §6.3): responsibilities, the people they name and the
 * objects they link, on the shared force layout with every group null. Saved responsibilities stay exactly where they were saved;
 * notes are fixed bodies with no edges, so nothing is placed under one. The same data gives the same picture.
 */

export type NodeKind = 'responsibility' | 'person' | 'host' | 'location' | 'page'
export type EdgeKind = 'responsible' | 'deputy' | 'global' | 'link'
export type Layer = 'people' | 'hosts' | 'locations' | 'pages' | 'notes'
/**
 * Which people get a line (docs/responsibilities-mcp-and-map-plan.md §4): the first in each escalation order, the second, or the
 * first two. The order is one queue, so whoever is missing is replaced by the next.
 */
export type Lines = 'responsible' | 'deputy' | 'both'
export const allLines: readonly Lines[] = ['responsible', 'deputy', 'both']

/** The line's style says the person's role, not their step. */
const roleEdge: Record<ResponsibilityRole, EdgeKind> = {
  Responsible: 'responsible',
  Deputy: 'deputy',
  GlobalResponsible: 'global',
  GlobalDeputy: 'global',
}

/** The steps of an escalation order that get a line in a view. */
export function linedSteps(steps: readonly ResponsibilityStep[], lines: Lines): readonly ResponsibilityStep[] {
  return lines === 'responsible' ? steps.slice(0, 1) : lines === 'deputy' ? steps.slice(1, 2) : steps.slice(0, 2)
}

export interface OverviewNode {
  /** responsibility:<id>, person:<id>, host:<id>, location:<id>, page:<id>. */
  id: string
  kind: NodeKind
  name: string
  /** A person's short code or initials. */
  code?: string
  color: string | null
  /** A person deactivated since. */
  inactive?: boolean
  /** A page's number. */
  number?: number | null
  /** Saved: x and y; null: automatic. */
  saved: Point | null
  /** A responsibility's short description, for its tooltip. */
  summary?: string | null
  /** A responsibility that needs attention (§6.1: ⚠). */
  attention?: boolean
  /** A responsibility the global responsible stands in for (§6.1: G). */
  standsIn?: boolean
}

export interface OverviewEdge {
  source: string
  target: string
  kind: EdgeKind
}

export interface Obstacle {
  id: string
  x: number
  y: number
  r: number
}

export const nodeId = (kind: NodeKind | 'note', id: string) => `${kind}:${id}`

/** The radius drawn: a responsibility large, a person smaller, a linked object small. */
export function nodeRadius(kind: NodeKind): number {
  return kind === 'responsibility' ? 26 : kind === 'person' ? 15 : 8
}

/** Room around a node for its name below it. */
export const LABEL_ROOM = 24

export function bodyRadius(kind: NodeKind): number {
  return nodeRadius(kind) + LABEL_ROOM
}

/** "AM" for Anna Muster, "B" for Ben: for people without a short code in the availability plan. */
export function initials(name: string): string {
  const words = name
    .trim()
    .split(/\s+/)
    .filter((w) => /\p{L}/u.test(w))
  const letters = words.length > 1 ? [words[0]!, words[words.length - 1]!] : words.slice(0, 1)
  return letters.map((w) => [...w].find((c) => /\p{L}/u.test(c))!.toUpperCase()).join('') || '?'
}

/** A note's width in the map's units (§7.1). */
export const noteWidths = { Small: 160, Medium: 240, Large: 360 } as const
export type NoteWidth = keyof typeof noteWidths

/**
 * About how large a note is drawn: its width, and a height that follows its text (about 7 units a character at 12 px, 17 a
 * line), at most three times its width; a longer text is cut, and the side panel shows all of it.
 */
export function noteSize(text: string, width: NoteWidth): { w: number; h: number } {
  const w = noteWidths[width] ?? noteWidths.Medium
  const perLine = Math.max(8, Math.floor((w - 20) / 7))
  const lines = text.split('\n').reduce((sum, line) => sum + Math.max(1, Math.ceil(line.length / perLine)), 0)
  return { w, h: Math.min(3 * w, Math.max(44, 20 + lines * 17)) }
}

/** A note as the layout sees it: a fixed body around its rectangle, so nothing is placed under it. */
export function noteObstacle(note: { id: string; x: number; y: number; w: number; h: number }): Obstacle {
  return { id: note.id, x: note.x + note.w / 2, y: note.y + note.h / 2, r: Math.hypot(note.w, note.h) / 2 }
}

/**
 * Which layers are on: people and linked objects; responsibilities always. People are those with a line in the chosen view of
 * the escalation order.
 */
export function buildGraph(
  map: ResponsibilityMap,
  layers: ReadonlySet<Layer>,
  lines: Lines = 'responsible',
): { nodes: OverviewNode[]; edges: OverviewEdge[] } {
  const nodes: OverviewNode[] = map.responsibilities.map((r) => ({
    id: nodeId('responsibility', r.id),
    kind: 'responsibility',
    name: r.name,
    color: r.color ?? null,
    saved: r.x != null && r.y != null ? { x: r.x, y: r.y } : null,
    summary: r.summary ?? null,
    attention: r.attention,
    standsIn: r.globalStandsIn,
  }))
  const edges: OverviewEdge[] = []
  if (layers.has('people')) {
    const people = new Map(map.people.map((p) => [p.id, p]))
    const shown = new Set<string>()
    const add = (personId: string | null | undefined, responsibilityId: string, kind: EdgeKind) => {
      if (!personId || !people.has(personId)) return
      shown.add(personId)
      edges.push({ source: nodeId('person', personId), target: nodeId('responsibility', responsibilityId), kind })
    }
    for (const r of map.responsibilities)
      for (const step of linedSteps(r.escalation, lines)) add(step.person.id, r.id, roleEdge[step.role])
    for (const id of [...shown].sort()) {
      const p = people.get(id)!
      nodes.push({
        id: nodeId('person', id),
        kind: 'person',
        name: p.name,
        code: p.code ?? initials(p.name),
        color: p.color ?? null,
        inactive: !p.active,
        saved: null,
      })
    }
  }
  const kinds = { Host: 'host', Location: 'location', Page: 'page' } as const
  for (const o of map.objects) {
    const kind = kinds[o.kind as keyof typeof kinds]
    if (!kind || !layers.has(`${kind}s` as Layer)) continue
    const id = nodeId(kind, o.id)
    nodes.push({ id, kind, name: o.name, number: o.number ?? null, color: null, saved: null })
    for (const l of o.links)
      edges.push({ source: id, target: nodeId('responsibility', l.responsibilityId), kind: 'link' })
  }
  return { nodes, edges }
}

/** The layout: saved responsibilities fixed where they were saved, notes fixed bodies; everything else around them. */
export function layoutOverview(
  nodes: readonly OverviewNode[],
  edges: readonly OverviewEdge[],
  obstacles: readonly Obstacle[] = [],
): Layout {
  const saved = new Map<string, Point>()
  for (const n of nodes) if (n.saved) saved.set(n.id, n.saved)
  for (const o of obstacles) saved.set(o.id, { x: o.x, y: o.y })
  return layoutGraph(
    [
      ...nodes.map((n) => ({ id: n.id, group: null, r: bodyRadius(n.kind) })),
      ...obstacles.map((o) => ({ id: o.id, group: null, r: o.r })),
    ],
    edges.map((e) => ({ source: e.source, target: e.target })),
    saved,
  )
}
