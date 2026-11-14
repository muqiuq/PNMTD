import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'

/**
 * The whole tree of locations, flat with paths (docs/locations-knowledge-plan.md §3.6), for the pickers and filters;
 * the server keeps it in memory, so it is cheap to ask again.
 */
export function useLocations() {
  return useQuery({ queryKey: [...keys.locations, 'list'], queryFn: () => api.locations.list(), staleTime: 30_000 })
}

/** The fields' definitions (§4.2), which every location page renders with; archived ones included. */
export function useLocationFields() {
  const query = useQuery({ queryKey: keys.locationFields, queryFn: api.locationFields.list, staleTime: 60_000 })
  const active = computed(() => (query.data.value?.items ?? []).filter((f) => !f.archivedAt))
  return { query, active }
}
