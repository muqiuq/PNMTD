import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import { nextTick } from 'vue'
import { usePreferences } from '@/stores/preferences'
import { MAX_WIDTH, MIN_WIDTH, useColumnWidths } from './useColumnWidths'

/** A header cell 100 px wide with its edge, the edge's events going to the composable as ColumnEdge sends them. */
function header(widths: ReturnType<typeof useColumnWidths>, column: string) {
  const th = document.createElement('th')
  const edge = document.createElement('span')
  th.appendChild(edge)
  document.body.appendChild(th)
  th.getBoundingClientRect = () => ({ width: 100 }) as DOMRect
  edge.setPointerCapture = () => {}
  edge.addEventListener('pointerdown', (e) => widths.start(e as PointerEvent, column))
  const pointer = (type: string, clientX: number) => edge.dispatchEvent(new MouseEvent(type, { clientX, button: 0 }))
  return { pointer }
}

describe('useColumnWidths', () => {
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
  })

  it('follows the pointer while dragging and keeps the width when released', async () => {
    const widths = useColumnWidths('plan')
    const { pointer } = header(widths, 'comment')
    expect(widths.width('comment')).toBeUndefined()

    pointer('pointerdown', 10)
    pointer('pointermove', 50)
    expect(widths.width('comment')).toBe('140px')
    expect(widths.resizing.value).toBe('comment')
    expect(usePreferences().tableWidths.plan, 'stored only when released').toBeUndefined()

    pointer('pointerup', 50)
    expect(widths.resizing.value).toBeNull()
    expect(usePreferences().tableWidths).toEqual({ plan: { comment: 140 } })

    // The next page load reads it back (the store writes on the next tick).
    await nextTick()
    setActivePinia(createPinia())
    expect(useColumnWidths('plan').width('comment')).toBe('140px')
  })

  it('stays within its limits, and a double-click gives the default back', () => {
    const widths = useColumnWidths('plan')
    const { pointer } = header(widths, 'day')
    pointer('pointerdown', 500)
    pointer('pointermove', 0)
    pointer('pointerup', 0)
    expect(widths.width('day')).toBe(`${MIN_WIDTH}px`)

    pointer('pointerdown', 0)
    pointer('pointermove', 5000)
    pointer('pointerup', 5000)
    expect(widths.width('day')).toBe(`${MAX_WIDTH}px`)

    widths.reset('day')
    expect(widths.width('day')).toBeUndefined()
    expect(usePreferences().tableWidths).toEqual({})
  })
})
