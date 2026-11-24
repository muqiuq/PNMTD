import { onBeforeUnmount, onMounted, ref } from 'vue'
import { received, type ShownMessage } from '@/boards/messages'
import { BoardError, boardApi, type BoardView, type LoungeData } from './client'

/**
 * The board's polls (docs/boards-plan.md §6.4): the view every refresh interval, paused while the page is hidden and at once when it shows;
 * after a failure the last data stays, and retries back off over 5, 10, 20, 40 and 60 s. Three intervals without a good answer make the
 * board stale. A refusal (renewed, deleted, switched off, wrong network) is tried again every minute, so the board comes back by itself.
 * The lounge, where the board shows it, has its own poll: every 10 s, every 2 s while a voice types. The messages have theirs
 * (docs/board-messages-plan.md §6.1): every 5 s while the page shows, at once after the view's first answer and when the page shows again,
 * paused while the view is refused; a 304 keeps what the board has, and a 429 waits for its Retry-After.
 */
export function useBoardPolling(token: string, onVersion: (version: string) => void) {
  const view = ref<BoardView | null>(null)
  const error = ref<BoardError | null>(null)
  const lastOk = ref<number | null>(null)
  const lounge = ref<LoungeData | null>(null)
  const messages = ref<ShownMessage[]>([])
  let timer: ReturnType<typeof setTimeout> | undefined
  let loungeTimer: ReturnType<typeof setTimeout> | undefined
  let messagesTimer: ReturnType<typeof setTimeout> | undefined
  let messagesTag: string | null = null
  let messagesRunning = false
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
      if (!messagesRunning) void pollMessages()
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

  function scheduleMessages(ms: number) {
    clearTimeout(messagesTimer)
    if (!stopped) messagesTimer = setTimeout(() => void pollMessages(), ms)
  }

  async function pollMessages() {
    clearTimeout(messagesTimer)
    if (stopped || document.visibilityState === 'hidden') {
      messagesRunning = false
      return
    }
    // While the view is refused, the messages wait for it to come back.
    if (error.value?.refused) {
      messagesRunning = false
      messages.value = []
      return
    }
    messagesRunning = true
    let wait = 5000
    try {
      const answer = await boardApi.messages(token, messagesTag)
      if (answer) {
        messagesTag = answer.etag
        messages.value = received(answer.data.messages, Date.now())
      }
    } catch (e) {
      if (e instanceof BoardError && e.status === 429) wait = (e.retryAfter ?? 60) * 1000
      // Otherwise the view's poll says what is wrong; the messages keep counting down.
    }
    scheduleMessages(wait)
  }

  /** Stale after three intervals without a good answer: since then. */
  function staleSince(now: number): string | null {
    if (lastOk.value === null || !view.value) return null
    return now - lastOk.value > 3 * refresh() ? new Date(lastOk.value).toISOString() : null
  }

  function visibility() {
    if (document.visibilityState === 'visible') {
      void poll()
      if (view.value) void pollMessages()
    }
  }

  onMounted(() => {
    void poll()
    document.addEventListener('visibilitychange', visibility)
  })
  onBeforeUnmount(() => {
    stopped = true
    clearTimeout(timer)
    clearTimeout(loungeTimer)
    clearTimeout(messagesTimer)
    document.removeEventListener('visibilitychange', visibility)
  })

  return { view, error, lounge, messages, staleSince, poll }
}
