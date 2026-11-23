import { onBeforeUnmount, onMounted, ref } from 'vue'
import { BoardError, boardApi, type BoardView, type LoungeData } from './client'

/**
 * The board's polls (docs/boards-plan.md §6.4): the view every refresh interval, paused while the page is hidden and at once when it shows;
 * after a failure the last data stays, and retries back off over 5, 10, 20, 40 and 60 s. Three intervals without a good answer make the
 * board stale. A refusal (renewed, deleted, switched off, wrong network) is tried again every minute, so the board comes back by itself.
 * The lounge, where the board shows it, has its own poll: every 10 s, every 2 s while a voice types.
 */
export function useBoardPolling(token: string, onVersion: (version: string) => void) {
  const view = ref<BoardView | null>(null)
  const error = ref<BoardError | null>(null)
  const lastOk = ref<number | null>(null)
  const lounge = ref<LoungeData | null>(null)
  let timer: ReturnType<typeof setTimeout> | undefined
  let loungeTimer: ReturnType<typeof setTimeout> | undefined
  let failures = 0
  let stopped = false

  const refresh = () => (view.value?.layout.refreshSeconds ?? 30) * 1000

  function schedule(ms: number) {
    clearTimeout(timer)
    if (!stopped) timer = setTimeout(() => void poll(), ms)
  }

  async function poll() {
    if (document.visibilityState === 'hidden') return
    try {
      const next = await boardApi.view(token)
      view.value = next
      error.value = null
      lastOk.value = Date.now()
      failures = 0
      onVersion(next.appVersion)
      schedule(refresh())
      ensureLounge()
    } catch (e) {
      const failure = e instanceof BoardError ? e : new BoardError(0, 'network', String(e))
      error.value = failure
      failures++
      schedule(failure.refused ? 60_000 : [5, 10, 20, 40, 60][Math.min(failures - 1, 4)]! * 1000)
    }
  }

  function ensureLounge() {
    const shows = view.value?.layout.columns.some((c) => c.elements.some((e) => e.type === 'Lounge'))
    if (!shows) {
      clearTimeout(loungeTimer)
      lounge.value = null
      return
    }
    if (loungeTimer === undefined) loungeTimer = setTimeout(() => void pollLounge(), 10_000)
  }

  async function pollLounge() {
    loungeTimer = undefined
    if (stopped || document.visibilityState === 'hidden') return
    try {
      lounge.value = await boardApi.lounge(token)
    } catch {
      // The view's poll says what is wrong; the lounge waits for it.
    }
    if (!stopped) loungeTimer = setTimeout(() => void pollLounge(), lounge.value?.lounge?.typing ? 2000 : 10_000)
  }

  /** Stale after three intervals without a good answer: since then. */
  function staleSince(now: number): string | null {
    if (lastOk.value === null || !view.value) return null
    return now - lastOk.value > 3 * refresh() ? new Date(lastOk.value).toISOString() : null
  }

  function visibility() {
    if (document.visibilityState === 'visible') void poll()
  }

  onMounted(() => {
    void poll()
    document.addEventListener('visibilitychange', visibility)
  })
  onBeforeUnmount(() => {
    stopped = true
    clearTimeout(timer)
    clearTimeout(loungeTimer)
    document.removeEventListener('visibilitychange', visibility)
  })

  return { view, error, lounge, staleSince, poll }
}
