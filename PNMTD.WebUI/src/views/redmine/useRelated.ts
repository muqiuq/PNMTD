import { useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api, keys, type RedmineTarget } from '@/api/resources'
import { useSession } from '@/stores/session'

/**
 * An object's tickets from PNMT's copy (docs/audit-redmine-zabbix-plan.md §6.6, §6.7), shared by its panel and its
 * header's chip. Nothing is asked without Redmine's View, or when Redmine isn't configured.
 */
export function useRelated(target: MaybeRefOrGetter<RedmineTarget>, deep: MaybeRefOrGetter<boolean> = false) {
  const session = useSession()
  const shown = computed(() => !!session.info?.redmine && session.can('Redmine', 'View'))
  const query = useQuery({
    queryKey: computed(() => [...keys.redmine, 'related', toValue(target), toValue(deep)]),
    queryFn: () => api.redmine.related({ ...toValue(target), deep: toValue(deep) || undefined }),
    enabled: shown,
    refetchInterval: 120_000,
  })
  const openCount = computed(() => (query.data.value?.own ?? []).filter((t) => !t.ticket.isClosed).length)
  return { shown, query, openCount }
}
