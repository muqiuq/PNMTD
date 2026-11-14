import { useQuery } from '@tanstack/vue-query'
import { computed, ref, type Ref } from 'vue'
import { api, keys } from '@/api/resources'
import { useSession } from '@/stores/session'
import type { Option } from './cookieAdmin'

/**
 * What Admin → Cookie's pickers list: PNMT's users and groups (the admin flag: their lists are admins' only), hosts
 * (Monitoring's View). Without the right a list stays empty, and the chosen values show by their ids.
 */
export function useUserOptions() {
  const session = useSession()
  const query = useQuery({
    queryKey: [...keys.users, 'all'],
    queryFn: () => api.users.list({ pageSize: 500 }),
    enabled: computed(() => session.isAdmin),
  })
  const options = computed<Option[]>(() =>
    (query.data.value?.items ?? []).map((u) => ({
      label: u.displayName && u.displayName !== u.username ? `${u.displayName} (${u.username})` : u.username,
      value: u.id,
    })),
  )
  const unknown = (id: string) => (session.isAdmin ? `A removed user (${id.slice(0, 8)})` : `User ${id.slice(0, 8)}`)
  return { query, options, unknown, listed: computed(() => session.isAdmin) }
}

export function useGroupOptions() {
  const session = useSession()
  const query = useQuery({ queryKey: keys.groups, queryFn: api.groups.list, enabled: computed(() => session.isAdmin) })
  const options = computed<Option[]>(() => (query.data.value ?? []).map((g) => ({ label: g.name, value: g.id })))
  const unknown = (id: string) => (session.isAdmin ? `A removed group (${id.slice(0, 8)})` : `Group ${id.slice(0, 8)}`)
  return { query, options, unknown, listed: computed(() => session.isAdmin) }
}

/** The hosts, loaded once one is needed (when). */
export function useHostOptions(when: Ref<boolean> = ref(true)) {
  const session = useSession()
  const listed = computed(() => session.can('Monitoring', 'View'))
  const query = useQuery({
    queryKey: [...keys.hosts, 'all-for-scopes'],
    queryFn: () => api.hosts.list({ pageSize: 500, sort: 'name' }),
    enabled: computed(() => listed.value && when.value),
  })
  const options = computed<Option[]>(() => (query.data.value?.items ?? []).map((h) => ({ label: h.name, value: h.id })))
  return { query, options, unknown: (id: string) => `Host ${id.slice(0, 8)}`, listed }
}
