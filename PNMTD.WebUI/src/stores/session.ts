import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { authApi, type CurrentUser, type SessionFlags, type SystemInfo } from '@/api/auth'
import { ApiError } from '@/api/client'
import { holds, meets, type Area, type Level, type Right } from '@/api/rights'
import { clearBackupKey } from '@/composables/useBackupKey'

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

  const identity = computed(() => info.value?.identity ?? '')

  /** The admin flag: every right, and users and groups (docs/audit-redmine-zabbix-plan.md §5.1). */
  const isAdmin = computed(() => user.value?.isAdmin ?? false)

  /** This level in this area, or more (§5.2), from the rights /auth/me returned. */
  function can(area: Area, level: Level): boolean {
    return user.value !== null && holds(user.value.isAdmin, user.value.rights, area, level)
  }

  /** A route's, a drawer entry's or a button's right. */
  function allows(right: Right): boolean {
    return meets(right, isAdmin.value, can)
  }

  function set(next: CurrentUser | null, nextFlags: SessionFlags | null) {
    // Signed out: a backup key loaded in this tab goes with the session (docs/mikrotik-backup-plan.md §3.6).
    if (!next) clearBackupKey()
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

  return { user, flags, info, loaded, identity, isAdmin, can, allows, set, load, loadInfo, logout }
})
