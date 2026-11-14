import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import { useSession } from '@/stores/session'

/**
 * The dashboard (§5.5), every 30 s and when the window regains focus. The shell reads it too, for
 * the failure badge and the tab title; the server answers polls from its cache.
 */
export function useDashboard() {
  const session = useSession()
  return useQuery({
    queryKey: keys.dashboard,
    queryFn: api.dashboard,
    refetchInterval: 30_000,
    refetchOnWindowFocus: true,
    // Monitoring's View (docs/audit-redmine-zabbix-plan.md §5.2): without it, the dashboard says so instead.
    enabled: computed(() => session.can('Monitoring', 'View')),
  })
}
