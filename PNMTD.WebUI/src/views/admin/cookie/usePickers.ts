import { useQuery } from '@tanstack/vue-query'
import { computed, ref, type Ref } from 'vue'
import { api, keys } from '@/api/resources'
import { useSession } from '@/stores/session'
import type { Option } from './cookieAdmin'

/**
 * What Admin → Cookie's pickers list: PNMT's users and groups (GET admin/people, for Cookie's Manage), and the hosts
 * (Monitoring's View; without it, a text field). A chosen user or group that no longer exists shows as removed.
 */
function usePeople() {
  return useQuery({ queryKey: [...keys.cookie, 'admin', 'people'], queryFn: api.cookie.people, staleTime: 60_000 })
}

export function useUserOptions() {
  const query = usePeople()
  const options = computed<Option[]>(() =>
    (query.data.value?.users ?? []).map((u) => ({
      label:
        (u.displayName && u.displayName !== u.username ? `${u.displayName} (${u.username})` : u.username) +
        (u.isActive ? '' : ', deactivated'),
      value: u.id,
    })),
  )
  return { query, options, unknown: () => 'A removed user' }
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
