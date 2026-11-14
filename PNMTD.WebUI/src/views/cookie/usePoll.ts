import { onBeforeUnmount } from 'vue'

/**
 * Calls a function again and again, each time after the delay it names then (2 s while Cookie works, longer otherwise),
 * one call at a time, until the component goes. restart() calls it at once and starts the delay anew.
 */
export function usePoll(tick: () => Promise<void>, delay: () => number) {
  let timer: ReturnType<typeof setTimeout> | undefined
  let stopped = false

  function schedule() {
    clearTimeout(timer)
    if (stopped) return
    timer = setTimeout(async () => {
      await tick()
      schedule()
    }, delay())
  }

  async function restart() {
    clearTimeout(timer)
    await tick()
    schedule()
  }

  onBeforeUnmount(() => {
    stopped = true
    clearTimeout(timer)
  })

  return { restart, schedule }
}
