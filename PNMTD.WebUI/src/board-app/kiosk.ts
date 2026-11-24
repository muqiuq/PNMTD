import { onBeforeUnmount, onMounted } from 'vue'

/**
 * A wall's screen (docs/boards-plan.md §6.4): kept awake (the Screen Wake Lock API, HTTPS or localhost only; without it nothing happens),
 * the cursor hidden after three seconds without moving, and full screen on F or a double click.
 */
export function useKiosk() {
  let lock: { release: () => Promise<void> } | null = null
  let cursorTimer: ReturnType<typeof setTimeout> | undefined

  async function wake() {
    const wakeLock = (
      navigator as Navigator & { wakeLock?: { request: (type: 'screen') => Promise<{ release: () => Promise<void> }> } }
    ).wakeLock
    if (!wakeLock || document.visibilityState !== 'visible') return
    try {
      lock = await wakeLock.request('screen')
    } catch {
      lock = null
    }
  }

  function moved() {
    document.body.classList.remove('board-cursor-hidden')
    clearTimeout(cursorTimer)
    cursorTimer = setTimeout(() => document.body.classList.add('board-cursor-hidden'), 3000)
  }

  function toggleFullscreen() {
    if (document.fullscreenElement) void document.exitFullscreen()
    else void document.documentElement.requestFullscreen?.().catch(() => undefined)
  }

  function key(event: KeyboardEvent) {
    if ((event.key === 'f' || event.key === 'F') && !event.metaKey && !event.ctrlKey && !event.altKey)
      toggleFullscreen()
  }

  function visibility() {
    // A lock is released when the page is hidden; asked again when it shows.
    if (document.visibilityState === 'visible') void wake()
  }

  onMounted(() => {
    void wake()
    moved()
    window.addEventListener('mousemove', moved)
    window.addEventListener('keydown', key)
    window.addEventListener('dblclick', toggleFullscreen)
    document.addEventListener('visibilitychange', visibility)
  })
  onBeforeUnmount(() => {
    void lock?.release()
    clearTimeout(cursorTimer)
    window.removeEventListener('mousemove', moved)
    window.removeEventListener('keydown', key)
    window.removeEventListener('dblclick', toggleFullscreen)
    document.removeEventListener('visibilitychange', visibility)
  })
}

const symbols = { Green: '🟢', Yellow: '🟡', Red: '🔴', Grey: '⚪' } as const
const fills = { Green: '#198754', Yellow: '#ffc107', Red: '#dc3545', Grey: '#6c757d' } as const

/**
 * The tab's title and icon in the bar's colour (§6.4): a board is often one tab of many. An open alert or warning goes first, with "⚠"
 * (docs/board-messages-plan.md §5.4); the icon keeps the bar's colour.
 */
export function showColour(colour: keyof typeof symbols, name: string, warning: string | null = null) {
  document.title = warning ? `⚠ ${warning} · ${name}` : `${symbols[colour]} ${name}`
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><circle cx="8" cy="8" r="7" fill="${fills[colour]}"/></svg>`
  let link = document.querySelector<HTMLLinkElement>('link[rel="icon"]')
  if (!link) {
    link = document.createElement('link')
    link.rel = 'icon'
    document.head.appendChild(link)
  }
  link.href = `data:image/svg+xml,${encodeURIComponent(svg)}`
}
