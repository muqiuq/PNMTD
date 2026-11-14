import { useQuery } from '@tanstack/vue-query'
import { computed, type Ref } from 'vue'
import { api, keys, type S } from '@/api/resources'

/**
 * A server's section (docs/windows-sensor-plan.md §4), refreshed every minute; null until the agent reported it. Any other
 * failure is an error, shown by the section's bar: never "not reported yet" for a section PNMT could not read. Typed by the
 * server's platform: Windows' are served now; Linux's come with its agent (docs/linux-sensor-plan.md §10, L3), and until
 * then a Linux server's sections are not reported yet.
 */
export function useAgentSection(id: Ref<string>, section: string, platform?: Ref<S['AgentPlatform'] | undefined>) {
  const query = useQuery({
    queryKey: computed(() => [...keys.agentServer(id.value), 'section', section]),
    queryFn: async () => {
      if (platform?.value === 'Linux') return null
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
