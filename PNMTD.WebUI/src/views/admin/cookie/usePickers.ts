import { useQuery } from '@tanstack/vue-query'
import { computed, ref, type Ref } from 'vue'
import { api, keys } from '@/api/resources'
import { useSession } from '@/stores/session'
import type { Option } from './cookieAdmin'

/**
 * What Admin → Cookie's pickers list: PNMT's users and groups (GET admin/people, for Cookie's Manage), and the hosts and
 * servers (Monitoring's View; without it, a text field). A chosen user or group that no longer exists shows as removed.
 */
function usePeople() {
  return useQuery({ queryKey: [...keys.cookie, 'admin', 'people'], queryFn: api.cookie.people, staleTime: 60_000 })
}

/** PNMT's active users by name, for the contacts of Access (docs/cookie-access-plan.md §6.2). */
export function useUserOptions() {
  const query = usePeople()
  const options = computed<Option[]>(() =>
    (query.data.value?.users ?? [])
      .filter((u) => u.isActive)
      .map((u) => ({ label: u.displayName || u.username, value: u.id })),
  )
  return { query, options, unknown: () => 'A removed or inactive user' }
}

export function useGroupOptions() {
  const query = usePeople()
  const options = computed<Option[]>(() =>
    (query.data.value?.groups ?? []).map((g) => ({ label: g.name, value: g.id })),
  )
  return { query, options, unknown: () => 'A removed group' }
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
  return { query, options, unknown: () => 'A removed host', listed }
}

/** The servers with an agent, by their sensor (Monitoring's View), loaded once one is needed (when). */
export function useServerOptions(when: Ref<boolean> = ref(true)) {
  const session = useSession()
  const listed = computed(() => session.can('Monitoring', 'View'))
  const query = useQuery({
    queryKey: [...keys.agents, 'all-for-access'],
    queryFn: () => api.agents.servers({ pageSize: 500, sort: 'name' }),
    enabled: computed(() => listed.value && when.value),
  })
  const options = computed<Option[]>(() =>
    (query.data.value?.items ?? []).map((s) => ({ label: s.host.name, value: s.sensorId })),
  )
  return { query, options, unknown: () => 'A removed server', listed }
}

export const accessRoutersKey = [...keys.cookie, 'admin', 'access', 'routers'] as const

/** Every router with its switch (docs/cookie-access-plan.md §4.2): Cookie's Manage, like the whole of Admin → Cookie. */
export function useAccessRouters() {
  const query = useQuery({ queryKey: accessRoutersKey, queryFn: api.cookie.accessRouters, refetchInterval: 60_000 })
  const options = computed<Option[]>(() =>
    (query.data.value ?? []).map((r) => ({ label: r.hostName, value: r.sensorId })),
  )
  return { query, options, unknown: () => 'A removed router' }
}
