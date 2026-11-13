import { useQuery } from '@tanstack/vue-query'
import { computed, type Ref } from 'vue'
import { api, keys } from '@/api/resources'

/** A Windows server's agent detail, shared by its tabs (one query, refreshed every 15 s). */
export function useWindowsServer(id: Ref<string>, enabled: Ref<boolean>) {
  const query = useQuery({
    queryKey: computed(() => keys.windowsServer(id.value)),
    queryFn: () => api.windows.server(id.value),
    refetchInterval: 15_000,
    enabled,
  })
  return { query, server: computed(() => query.data.value) }
}
