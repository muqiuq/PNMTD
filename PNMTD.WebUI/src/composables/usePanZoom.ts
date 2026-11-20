import { ref, type Ref } from 'vue'

/**
 * Pan and zoom for an SVG (docs/network-map-plan.md §8.2): the wheel and pinch zoom around the pointer, dragging the
 * background pans, and buttons zoom around the middle or fit the whole drawing. Our own, without a library: the maths is
 * in pure functions with their tests.
 */

export interface View {
  x: number
  y: number
  k: number
}

export interface Box {
  minX: number
  minY: number
  maxX: number
  maxY: number
}

export const MIN_ZOOM = 0.1
export const MAX_ZOOM = 4

/** The view zoomed by <code>factor</code>, keeping the screen point <code>at</code> where it is. */
export function zoomAround(
  view: View,
  at: { x: number; y: number },
  factor: number,
  min = MIN_ZOOM,
  max = MAX_ZOOM,
): View {
  const k = Math.min(max, Math.max(min, view.k * factor))
  const f = k / view.k
  return { k, x: at.x - (at.x - view.x) * f, y: at.y - (at.y - view.y) * f }
}

/** The view that shows the whole box in a viewport of width × height, with padding, at most at zoom 1.5. */
export function fitTo(box: Box, width: number, height: number, padding = 32, max = 1.5): View {
  const w = Math.max(1, box.maxX - box.minX)
  const h = Math.max(1, box.maxY - box.minY)
  const k = Math.max(MIN_ZOOM, Math.min(max, (width - 2 * padding) / w, (height - 2 * padding) / h))
  return { k, x: width / 2 - (box.minX + w / 2) * k, y: height / 2 - (box.minY + h / 2) * k }
}

/** A screen point in the drawing's coordinates. */
export function toWorld(view: View, at: { x: number; y: number }): { x: number; y: number } {
  return { x: (at.x - view.x) / view.k, y: (at.y - view.y) / view.k }
}

/** The view centred on a drawing point, at the zoom it has (at least 1). */
export function centreOn(view: View, point: { x: number; y: number }, width: number, height: number): View {
  const k = Math.max(1, view.k)
  return { k, x: width / 2 - point.x * k, y: height / 2 - point.y * k }
}

export function usePanZoom(svg: Ref<SVGSVGElement | null>) {
  const view = ref<View>({ x: 0, y: 0, k: 1 })
  const pointers = new Map<number, { x: number; y: number }>()
  let pinch: { distance: number; view: View } | null = null
  let panning = false

  function local(e: { clientX: number; clientY: number }) {
    const rect = svg.value?.getBoundingClientRect()
    return { x: e.clientX - (rect?.left ?? 0), y: e.clientY - (rect?.top ?? 0) }
  }

  function size() {
    const rect = svg.value?.getBoundingClientRect()
    return { width: rect?.width ?? 800, height: rect?.height ?? 600 }
  }

  function onWheel(e: WheelEvent) {
    e.preventDefault()
    // A trackpad's small steps zoom gently, a mouse wheel's notch by about 20 %.
    const factor = Math.exp(-Math.max(-60, Math.min(60, e.deltaY)) * 0.003)
    view.value = zoomAround(view.value, local(e), factor)
  }

  /** On the background only: a node's own pointerdown stops it, to drag the node instead. */
  function onPointerDown(e: PointerEvent) {
    pointers.set(e.pointerId, local(e))
    try {
      ;(e.currentTarget as Element | null)?.setPointerCapture?.(e.pointerId)
    } catch {
      // A pointer the browser doesn't track: panning works without capture.
    }
    if (pointers.size === 2) {
      const [a, b] = [...pointers.values()] as [{ x: number; y: number }, { x: number; y: number }]
      pinch = { distance: Math.hypot(a.x - b.x, a.y - b.y), view: view.value }
      panning = false
    } else {
      panning = true
    }
  }

  function onPointerMove(e: PointerEvent) {
    const before = pointers.get(e.pointerId)
    if (!before) return
    const now = local(e)
    pointers.set(e.pointerId, now)
    if (pinch && pointers.size === 2) {
      const [a, b] = [...pointers.values()] as [{ x: number; y: number }, { x: number; y: number }]
      const distance = Math.hypot(a.x - b.x, a.y - b.y)
      view.value = zoomAround(
        pinch.view,
        { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 },
        distance / Math.max(1, pinch.distance),
      )
    } else if (panning) {
      view.value = { ...view.value, x: view.value.x + now.x - before.x, y: view.value.y + now.y - before.y }
    }
  }

  function onPointerUp(e: PointerEvent) {
    pointers.delete(e.pointerId)
    if (pointers.size < 2) pinch = null
    if (pointers.size === 0) panning = false
  }

  function zoomBy(factor: number) {
    const { width, height } = size()
    view.value = zoomAround(view.value, { x: width / 2, y: height / 2 }, factor)
  }

  function fit(box: Box) {
    const { width, height } = size()
    view.value = fitTo(box, width, height)
  }

  function centre(point: { x: number; y: number }) {
    const { width, height } = size()
    view.value = centreOn(view.value, point, width, height)
  }

  return {
    view,
    onWheel,
    onPointerDown,
    onPointerMove,
    onPointerUp,
    zoomBy,
    fit,
    centre,
    toWorld: (e: PointerEvent) => toWorld(view.value, local(e)),
  }
}
