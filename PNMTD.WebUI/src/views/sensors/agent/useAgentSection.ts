import { useQuery } from '@tanstack/vue-query'
import { computed, type Ref } from 'vue'
import { api, keys, type S } from '@/api/resources'

type View = S['WindowsSectionView'] | S['LinuxSectionView']

/**
 * A section's query, refreshed every minute; null until the agent reported it (404). Any other failure is an error, shown by the
 * section's bar: never "not reported yet" for a section PNMT could not read.
 */
function useSection<T extends View>(id: Ref<string>, section: string, fetch: () => Promise<T>, enabled?: Ref<boolean>) {
  const query = useQuery({
    queryKey: computed(() => [...keys.agentServer(id.value), 'section', section]),
    queryFn: async () => {
      try {
        return await fetch()
      } catch (e) {
        if ((e as { status?: number }).status === 404) return null
        throw e
      }
    },
    refetchInterval: 60_000,
    enabled: enabled ?? true,
  })
  return {
    query,
    data: computed(() => query.data.value ?? null),
    loading: computed(() => query.isPending.value && (enabled?.value ?? true)),
    error: computed(() =>
      query.error.value ? (query.error.value as Error).message || 'PNMT could not read it.' : null,
    ),
  }
}

/**
 * A server's section (docs/windows-sensor-plan.md §4; docs/linux-sensor-plan.md §5). Without a platform, a Windows server's,
 * typed by Windows' records. With one, a shared section (hardware, network) of either platform: from
 * /api/v1/linux/servers/ for Linux, /api/v1/windows/servers/ for Windows.
 */
export function useAgentSection(
  id: Ref<string>,
  section: string,
): ReturnType<typeof useSection<S['WindowsSectionView']>>
export function useAgentSection(
  id: Ref<string>,
  section: string,
  platform: Ref<S['AgentPlatform'] | undefined>,
): ReturnType<typeof useSection<View>>
export function useAgentSection(id: Ref<string>, section: string, platform?: Ref<S['AgentPlatform'] | undefined>) {
  return useSection<View>(id, section, () =>
    platform?.value === 'Linux' ? api.linux.section(id.value, section) : api.windows.section(id.value, section),
  )
}

/**
 * A Linux server's section (docs/linux-sensor-plan.md §5), typed by Linux's records. access, journal and ssh-keys need the User
 * role: pass enabled so a viewer's page doesn't ask for them.
 */
export function useLinuxSection(id: Ref<string>, section: string, enabled?: Ref<boolean>) {
  return useSection<S['LinuxSectionView']>(id, section, () => api.linux.section(id.value, section), enabled)
}
