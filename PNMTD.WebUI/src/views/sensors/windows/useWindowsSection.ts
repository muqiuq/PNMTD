import { useQuery } from '@tanstack/vue-query'
import { computed, type Ref } from 'vue'
import { api, keys } from '@/api/resources'

/**
 * A server's section (docs/windows-sensor-plan.md §4), refreshed every minute; null until the agent reported it. Any other
 * failure is an error, shown by the section's bar: never "not reported yet" for a section PNMT could not read.
 */
export function useWindowsSection(id: Ref<string>, section: string) {
  const query = useQuery({
    queryKey: computed(() => [...keys.windowsServer(id.value), 'section', section]),
    queryFn: async () => {
      try {
        return await api.windows.section(id.value, section)
      } catch (e) {
        if ((e as { status?: number }).status === 404) return null
        throw e
      }
    },
    refetchInterval: 60_000,
  })
  return {
    query,
    data: computed(() => query.data.value ?? null),
    loading: computed(() => query.isPending.value),
    error: computed(() =>
      query.error.value ? (query.error.value as Error).message || 'PNMT could not read it.' : null,
    ),
  }
}
