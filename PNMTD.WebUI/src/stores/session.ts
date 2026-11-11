import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { authApi, type CurrentUser, type Role, type SessionFlags, type SystemInfo } from '@/api/auth'
import { ApiError } from '@/api/client'

const rank: Record<Role, number> = { Viewer: 1, User: 2, Admin: 3 }

/**
 * Who is signed in, with what flags (docs/webui-migration-plan.md §4.5), and what the instance is
 * (identity, version). Loaded once from /auth/me, which also issues the XSRF token; refreshed after
 * every change of the own account.
 */
export const useSession = defineStore('session', () => {
  const user = ref<CurrentUser | null>(null)
  const flags = ref<SessionFlags | null>(null)
  const info = ref<SystemInfo | null>(null)
  const loaded = ref(false)

  const role = computed(() => (user.value?.role as Role | undefined) ?? null)
  const identity = computed(() => info.value?.identity ?? '')

  /** At least this role (Viewer < User < Admin). */
  function can(minimum: Role): boolean {
    return role.value !== null && rank[role.value] >= rank[minimum]
  }

  function set(next: CurrentUser | null, nextFlags: SessionFlags | null) {
    user.value = next
    flags.value = nextFlags
    loaded.value = true
  }

  async function loadInfo() {
    try {
      info.value = await authApi.info()
    } catch {
      // The login screen works without the identity.
    }
  }

  /** /auth/me: the user and flags, or signed out (401). Once, unless forced. */
  async function load(force = false) {
    if (loaded.value && !force) {
      return
    }
    try {
      const me = await authApi.me()
      set(me.user, me.flags)
    } catch (e) {
      if (!(e instanceof ApiError) || e.status !== 401) {
        throw e
      }
      set(null, null)
    }
    if (!info.value || force) {
      await loadInfo()
    }
  }

  async function logout() {
    try {
      await authApi.logout()
    } catch {
      // Signed out on the server already, or unreachable: the session ends here either way.
    }
    set(null, null)
  }

  return { user, flags, info, loaded, role, identity, can, set, load, loadInfo, logout }
})
