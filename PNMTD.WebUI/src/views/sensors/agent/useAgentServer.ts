import { useQuery } from '@tanstack/vue-query'
import { computed, type Ref } from 'vue'
import { api, keys } from '@/api/resources'

/** A server's agent detail, of either platform, shared by its tabs (one query, refreshed every 15 s). */
export function useAgentServer(id: Ref<string>, enabled: Ref<boolean>) {
  const query = useQuery({
    queryKey: computed(() => keys.agentServer(id.value)),
    queryFn: () => api.agents.server(id.value),
    refetchInterval: 15_000,
    enabled,
  })
  return { query, server: computed(() => query.data.value) }
}
