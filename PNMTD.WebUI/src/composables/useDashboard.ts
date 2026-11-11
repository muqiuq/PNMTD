import { useQuery } from '@tanstack/vue-query'
import { api, keys } from '@/api/resources'

/**
 * The dashboard (§5.5), every 30 s and when the window regains focus. The shell reads it too, for
 * the failure badge and the tab title; the server answers polls from its cache.
 */
export function useDashboard() {
  return useQuery({
    queryKey: keys.dashboard,
    queryFn: api.dashboard,
    refetchInterval: 30_000,
    refetchOnWindowFocus: true,
  })
}
