import { computed, ref } from 'vue'
import { usePreferences } from '@/stores/preferences'

/** The narrowest and widest a dragged column gets, in px. */
export const MIN_WIDTH = 28
export const MAX_WIDTH = 640

const clamp = (px: number) => Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, Math.round(px)))

/**
 * Columns a person can make wider or narrower (docs/adjustments1.md): drag a header's right edge (ColumnEdge); a
 * double-click gives the column its default back. The widths stay in this browser (usePreferences), per
 * table and column key. `width(key)` is a CSS length for a `<col>`, or undefined while the column has its default.
 * While dragging, the width only lives here, and goes to the preferences when the pointer is released.
 */
export function useColumnWidths(table: string) {
  const preferences = usePreferences()
  const stored = computed(() => preferences.tableWidths[table] ?? {})
  const live = ref<{ key: string; px: number } | null>(null)

  function px(key: string): number | undefined {
    return live.value?.key === key ? live.value.px : stored.value[key]
  }

  const width = (key: string): string | undefined => {
    const w = px(key)
    return w === undefined ? undefined : `${w}px`
  }

  function save(key: string, value: number) {
    preferences.tableWidths = { ...preferences.tableWidths, [table]: { ...stored.value, [key]: clamp(value) } }
  }

  function reset(key: string) {
    const rest = { ...stored.value }
    delete rest[key]
    const all = { ...preferences.tableWidths }
    if (Object.keys(rest).length) all[table] = rest
    else delete all[table]
    preferences.tableWidths = all
  }

  /** The width the column has now: its header cell's, the handle sitting inside it. */
  const current = (handle: HTMLElement) => handle.closest('th')?.getBoundingClientRect().width ?? 0

  function start(e: PointerEvent, key: string) {
    if (e.button !== 0) return
    const handle = e.currentTarget as HTMLElement
    const from = e.clientX
    const was = current(handle)
    if (!was) return
    e.preventDefault()
    handle.setPointerCapture?.(e.pointerId)
    live.value = { key, px: clamp(was) }
    const move = (m: PointerEvent) => (live.value = { key, px: clamp(was + m.clientX - from) })
    const end = () => {
      handle.removeEventListener('pointermove', move)
      handle.removeEventListener('pointerup', end)
      handle.removeEventListener('pointercancel', end)
      if (live.value) save(key, live.value.px)
      live.value = null
    }
    handle.addEventListener('pointermove', move)
    handle.addEventListener('pointerup', end)
    handle.addEventListener('pointercancel', end)
  }

  return { width, start, reset, resizing: computed(() => live.value?.key ?? null) }
}
