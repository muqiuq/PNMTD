import type { Router } from 'vue-router'
import { onApiError } from '@/api/client'
import { queryClient } from '@/api/queries'
import { useSession } from '@/stores/session'

/**
 * What every failed call means for the session (§5.7): a 401 outside the sign-in ends it and opens
 * the login with the current page as returnUrl; 403 must_change_password or totp_setup_required
 * (another tab, a policy change) reloads the flags, and the guard opens the matching screen.
 */
export function installSessionHandling(router: Router) {
  onApiError(async (error, path) => {
    if (path.startsWith('/api/v1/auth/')) {
      return
    }
    const session = useSession()
    const current = router.currentRoute.value
    if (error.status === 401 && session.user) {
      session.set(null, null)
      queryClient.clear()
      if (!current.meta.public) {
        await router.replace({ name: 'login', query: { returnUrl: current.fullPath } })
      }
    } else if (
      error.status === 403 &&
      (error.code === 'must_change_password' || error.code === 'totp_setup_required')
    ) {
      await session.load(true)
      await router.replace(current.fullPath)
    }
  })
}
