import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { api, keys, type S } from '@/api/resources'

const tones = { Success: 'success', Warn: 'warn', Danger: 'danger', Neutral: 'secondary' } as const

/**
 * The audit log's catalog (docs/audit-redmine-zabbix-plan.md §3.9), from the server: every event type with its category,
 * label and tone, so that the UI never keeps a list of its own. Only for those who may read the audit log; the
 * History tabs fall back to the type's name, split into words.
 */
export function useAuditCatalog(enabled = computed(() => true)) {
  const catalog = useQuery({
    queryKey: [...keys.audit, 'types'],
    queryFn: api.audit.types,
    staleTime: Infinity,
    enabled,
  })
  const byType = computed(() => new Map((catalog.data.value ?? []).map((t) => [t.type, t])))
  const label = (type: string) =>
    byType.value.get(type)?.label ??
    type.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/^(\w)(.*)$/, (_, a: string, b: string) => a + b.toLowerCase())
  const severity = (type: string) => tones[byType.value.get(type)?.tone ?? 'Neutral']
  return { catalog, byType, label, severity }
}

export type AuditItem = S['AuditLogItem']
