import { describe, expect, it } from 'vitest'
import { ref } from 'vue'
import type { Point } from './forceLayout'
import { useMoving } from './useMoving'

/** A pointer event as the map gets one: at a point, on an element that can capture it. */
function pointer(x: number, y: number): PointerEvent {
  return {
    clientX: x,
    clientY: y,
    pointerId: 1,
    currentTarget: { setPointerCapture: () => undefined },
    stopPropagation: () => undefined,
  } as unknown as PointerEvent
}

function setup() {
  const laidOut = new Map<string, Point>([
    ['a', { x: 0, y: 0 }],
    ['b', { x: 100, y: 0 }],
  ])
  const moved = ref(new Map<string, Point>())
  const at = (id: string) => moved.value.get(id) ?? laidOut.get(id)!
  const moving = useMoving(moved, { at, toWorld: (e) => ({ x: e.clientX, y: e.clientY }) })
  return { moved, moving }
}

describe('useMoving (docs/network-map-positions-plan.md §2, docs/responsibilities-extensions-plan.md §6.5)', () => {
  it('drags one node, or a set of them together', () => {
    const { moved, moving } = setup()
    moving.begin(pointer(0, 0), ['a'])
    moving.move(pointer(30, 40))
    expect(moving.release()).toBe(false)
    expect(moved.value.get('a')).toEqual({ x: 30, y: 40 })

    moving.begin(pointer(0, 0), ['a', 'b'])
    moving.move(pointer(10, 0))
    moving.end()
    expect(moved.value.get('a')).toEqual({ x: 40, y: 40 })
    expect(moved.value.get('b')).toEqual({ x: 110, y: 0 })
  })

  it('takes a press without a move for a click, and moves nothing', () => {
    const { moved, moving } = setup()
    moving.begin(pointer(5, 5), ['a'])
    moving.move(pointer(6, 6))
    expect(moving.release()).toBe(true)
    expect(moved.value.size).toBe(0)
  })

  it('puts a drag back on Escape, with the moves from before', () => {
    const { moved, moving } = setup()
    moving.place(new Map([['a', { x: 5, y: 5 }]]))
    moving.begin(pointer(0, 0), ['a', 'b'])
    moving.move(pointer(50, 50))
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    expect(moved.value.get('a')).toEqual({ x: 5, y: 5 })
    expect(moved.value.has('b')).toBe(false)
    expect(moving.release()).toBe(false)
  })

  it('moves a focused node with the arrow keys, further with Shift', () => {
    const { moved, moving } = setup()
    moving.nudge(new KeyboardEvent('keydown', { key: 'ArrowRight' }), ['a'])
    expect(moved.value.get('a')).toEqual({ x: 8, y: 0 })
    moving.nudge(new KeyboardEvent('keydown', { key: 'ArrowUp', shiftKey: true }), ['a'])
    expect(moved.value.get('a')).toEqual({ x: 8, y: -40 })
    moving.nudge(new KeyboardEvent('keydown', { key: 'Enter' }), ['a'])
    expect(moved.value.get('a')).toEqual({ x: 8, y: -40 })
  })
})
