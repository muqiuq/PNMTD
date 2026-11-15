import { ref } from 'vue'

/**
 * The day's 2FA code (docs/mcp-plan.md §3.3): a 403 totp_due anywhere opens the dialog over the current page, so nothing
 * typed is lost. After the code, what failed is fetched again.
 */
const open = ref(false)

export function useDailyTotp() {
  return {
    open,
    ask: () => {
      open.value = true
    },
    close: () => {
      open.value = false
    },
  }
}
