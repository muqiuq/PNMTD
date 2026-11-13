import { useQuery } from '@tanstack/vue-query'
import { computed, type Ref } from 'vue'
import { api, keys } from '@/api/resources'

/** A server's section (docs/windows-sensor-plan.md §4), refreshed every minute; null until the agent reported it. */
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
  return { query, data: computed(() => query.data.value ?? null) }
}
