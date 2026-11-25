import { getCurrentInstance, onBeforeUnmount, type Ref } from 'vue'
import type { Point } from './forceLayout'

/**
 * Moving nodes on a map by hand (docs/network-map-positions-plan.md §2), shared by the network map and the responsibilities' overview
 * (docs/responsibilities-extensions-plan.md §6.5): a drag moves one node or a set of them (a location by its name), a focused node
 * moves with the arrow keys (8 units, 40 with Shift), and Escape during a drag puts it back. The moves are the page's `moved`
 * map, kept until they are saved or discarded.
 */
export interface MovingOptions {
  /** Where a node is now: its move, its saved place, or the layout's. */
  at: (id: string) => Point
  /** The map's point under the pointer. */
  toWorld: (e: PointerEvent) => Point
}

const arrows: Record<string, Point> = {
  ArrowLeft: { x: -1, y: 0 },
  ArrowRight: { x: 1, y: 0 },
  ArrowUp: { x: 0, y: -1 },
  ArrowDown: { x: 0, y: 1 },
}

export function useMoving(moved: Ref<Map<string, Point>>, options: MovingOptions) {
  function place(points: Map<string, Point>) {
    const next = new Map(moved.value)
    for (const [id, p] of points) next.set(id, p)
    moved.value = next
  }

  /** A drag: the nodes it moves, where they were, and the moves they had before (Escape puts those back). */
  let dragging: {
    ids: string[]
    from: Map<string, Point>
    before: Map<string, Point | undefined>
    start: Point
  } | null = null
  let dragMoved = false

  function begin(e: PointerEvent, ids: string[]) {
    e.stopPropagation()
    try {
      ;(e.currentTarget as Element).setPointerCapture(e.pointerId)
    } catch {
      // A pointer the browser doesn't track (a synthetic event): the drag works without capture.
    }
    dragging = {
      ids,
      from: new Map(ids.map((id) => [id, options.at(id)])),
      before: new Map(ids.map((id) => [id, moved.value.get(id)])),
      start: options.toWorld(e),
    }
    dragMoved = false
    window.addEventListener('keydown', onEscape)
  }

  function move(e: PointerEvent) {
    if (!dragging) return
    const now = options.toWorld(e)
    const dx = now.x - dragging.start.x
    const dy = now.y - dragging.start.y
    if (Math.abs(dx) + Math.abs(dy) > 2) dragMoved = true
    if (!dragMoved) return
    const d = dragging
    place(new Map(d.ids.map((id) => [id, { x: d.from.get(id)!.x + dx, y: d.from.get(id)!.y + dy }])))
  }

  function end() {
    dragging = null
    window.removeEventListener('keydown', onEscape)
  }

  function onEscape(e: KeyboardEvent) {
    if (e.key !== 'Escape' || !dragging) return
    e.preventDefault()
    const next = new Map(moved.value)
    for (const [id, p] of dragging.before) {
      if (p) next.set(id, p)
      else next.delete(id)
    }
    moved.value = next
    end()
  }
  if (getCurrentInstance()) onBeforeUnmount(end)

  /** A pointer went up on a node: true when it was a click, not a drag (a click selects). Ends the drag. */
  function release(): boolean {
    const click = dragging !== null && !dragMoved
    end()
    return click
  }

  /** The arrow keys move a focused node or set: 8 units, 40 with Shift. */
  function nudge(e: KeyboardEvent, ids: string[]) {
    const d = arrows[e.key]
    if (!d || !ids.length) return
    e.preventDefault()
    const step = e.shiftKey ? 40 : 8
    place(new Map(ids.map((id) => [id, { x: options.at(id).x + d.x * step, y: options.at(id).y + d.y * step }])))
  }

  return { place, begin, move, end, release, nudge }
}
