import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, type Ref } from 'vue'
import { api, keys, type S } from '@/api/resources'

/**
 * A Linux server's OS updates (docs/linux-os-updates-plan.md §9, §10.1): one query for the tab and its badge, every 5 s while a
 * run is in progress (its log has a poll of its own), every 30 s otherwise.
 */
export function useOsUpdates(id: Ref<string>, enabled: Ref<boolean>) {
  const queryClient = useQueryClient()
  const query = useQuery({
    queryKey: computed(() => keys.osUpdatesServer(id.value)),
    queryFn: () => api.linux.osUpdates.get(id.value),
    refetchInterval: (q) => (q.state.data?.current ? 5000 : 30_000),
    enabled,
  })
  /** An answer that is the whole view (settings, acknowledging): shown at once. */
  const set = (view: S['OsUpdatesView']) => queryClient.setQueryData(keys.osUpdatesServer(id.value), view)
  return { query, view: computed(() => query.data.value), set }
}

/** What a change of a server's OS updates refreshes: its view and runs, the sensor (466, 467), the dashboard's card. */
export const osUpdatesInvalidate = (id: string) => [keys.osUpdatesServer(id), keys.sensor(id), keys.osUpdates] as const
