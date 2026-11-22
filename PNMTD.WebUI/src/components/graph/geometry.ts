import type { Point } from './forceLayout'

/**
 * Drawing a graph, shared by the network map and the knowledge map (docs/knowledge-base-extensions-plan.md §8.5): the path of an
 * edge, how parallel edges bend apart, and the bounds of what is drawn.
 */

export interface Box {
  minX: number
  minY: number
  maxX: number
  maxY: number
}

/** How far the `index`-th of `count` edges between the same two nodes bends sideways: none when it is alone. */
export function bendOf(index: number, count: number, step = 18): number {
  return count > 1 ? (index - (count - 1) / 2) * step : 0
}

/** A straight line, or a quadratic curve bent sideways by `bend` units at its middle. */
export function edgePath(a: Point, b: Point, bend = 0): string {
  if (!bend) return `M ${a.x} ${a.y} L ${b.x} ${b.y}`
  const mx = (a.x + b.x) / 2
  const my = (a.y + b.y) / 2
  const dx = b.x - a.x
  const dy = b.y - a.y
  const d = Math.max(1, Math.hypot(dx, dy))
  return `M ${a.x} ${a.y} Q ${mx - (dy / d) * bend} ${my + (dx / d) * bend} ${b.x} ${b.y}`
}

/** The box around circles (a node's body, a location's circle), or `fallback` when there are none. */
export function boundsOf(circles: Iterable<{ x: number; y: number; r: number }>, fallback: Box): Box {
  const b = { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity }
  for (const c of circles) {
    b.minX = Math.min(b.minX, c.x - c.r)
    b.minY = Math.min(b.minY, c.y - c.r)
    b.maxX = Math.max(b.maxX, c.x + c.r)
    b.maxY = Math.max(b.maxY, c.y + c.r)
  }
  return Number.isFinite(b.minX) ? b : fallback
}
