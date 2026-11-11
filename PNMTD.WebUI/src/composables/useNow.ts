import { onScopeDispose, ref, type Ref } from 'vue'

const now = ref(Date.now())
let users = 0
let timer: ReturnType<typeof setInterval> | undefined

/**
 * One shared clock for relative times ("5 min ago"), ticking every 10 s while anything uses it.
 * Tests drive it with fake timers.
 */
export function useNow(): Ref<number> {
  users++
  if (!timer) {
    now.value = Date.now()
    timer = setInterval(() => (now.value = Date.now()), 10_000)
  }
  onScopeDispose(() => {
    users--
    if (users === 0 && timer) {
      clearInterval(timer)
      timer = undefined
    }
  })
  return now
}
