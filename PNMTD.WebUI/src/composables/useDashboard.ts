import { type QueryClient, useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import { useSession } from '@/stores/session'

/**
 * The dashboard (§5.5), every 30 s and when the window regains focus. The shell reads it too, for
 * the failure badge and the tab title; the server answers polls from its cache. The app bar's *Refresh*
 * fetches a fresh build instead (refreshDashboard).
 */
export function useDashboard() {
  const session = useSession()
  return useQuery({
    queryKey: keys.dashboard,
    queryFn: () => api.dashboard(),
    refetchInterval: 30_000,
    refetchOnWindowFocus: true,
    // Monitoring's View (docs/audit-redmine-zabbix-plan.md §5.2): without it, the dashboard says so instead.
    enabled: computed(() => session.can('Monitoring', 'View')),
  })
}

/**
 * *Refresh* (the app bar): a build the server makes now, not the cached one a poll gets, so the click shows what is true
 * this moment. A poll still in flight is cancelled first, so its older answer can't land afterwards. A failure shows as the
 * query's error (*Connection lost*), as a failed poll does.
 */
export async function refreshDashboard(queryClient: QueryClient) {
  await queryClient.cancelQueries({ queryKey: keys.dashboard })
  try {
    await queryClient.fetchQuery({ queryKey: keys.dashboard, queryFn: () => api.dashboard(true), staleTime: 0 })
  } catch {
    // The query's error state says it.
  }
}
