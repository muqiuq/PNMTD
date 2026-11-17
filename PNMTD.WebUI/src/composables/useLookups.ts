import { useQuery } from '@tanstack/vue-query'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'

/** "HEARTBEAT_MISSING" → "Heartbeat missing", "SSL_ABOUT_TO_EXPIRE" → "SSL about to expire". */
export function humanize(name: string): string {
  const words = name
    .toLowerCase()
    .replace(/_/g, ' ')
    .replace(/\bsuccessfull\b/, 'successful')
    .replace(/\bssl\b/g, 'SSL')
  return words.charAt(0).toUpperCase() + words.slice(1)
}

const forever = { staleTime: Infinity, gcTime: Infinity }

/** The status codes with their names (§5.3: "460 Heartbeat missing"); other codes by range. */
export function useStatusCodes() {
  const query = useQuery({ queryKey: [...keys.system, 'status-codes'], queryFn: api.system.statusCodes, ...forever })
  const byCode = computed(() => new Map((query.data.value ?? []).map((c) => [c.code, c])))
  return {
    query,
    list: computed(() => query.data.value ?? []),
    nameOf(code: number | null | undefined): string | undefined {
      if (code === null || code === undefined) {
        return undefined
      }
      const known = byCode.value.get(code)
      if (known) {
        return humanize(known.name)
      }
      return code < 0 ? 'Never reported' : code <= 399 ? 'Success' : 'Failure'
    },
  }
}

export function useSensorTypes() {
  const query = useQuery({ queryKey: [...keys.system, 'sensor-types'], queryFn: api.system.sensorTypes, ...forever })
  return {
    query,
    list: computed(() => query.data.value ?? []),
    info: (type: string | null | undefined) => (query.data.value ?? []).find((t) => t.type === type),
  }
}

export function useRuleTypes() {
  const query = useQuery({ queryKey: [...keys.system, 'rule-types'], queryFn: api.system.ruleTypes, ...forever })
  return { query, list: computed(() => query.data.value ?? []) }
}

/** The label of a sensor type: "HEARTBEAT_VALUECHECK" → "Heartbeat valuecheck". */
export const typeLabel = (type: string) =>
  type === 'ENCAPSULADED'
    ? 'Encapsulated (JSON)'
    : type === 'UDP_NODE'
      ? 'UDP node'
      : type === 'MIKROTIK'
        ? 'MikroTik router'
        : type === 'SYSTEM'
          ? 'PNMT system'
          : type === 'WINDOWS'
            ? 'Windows server'
            : type === 'LINUX'
              ? 'Linux server'
              : type === 'ZABBIX'
                ? 'Zabbix host'
                : type === 'SSL_CERTIFICATE'
                  ? 'SSL certificate'
                  : humanize(type)

/**
 * Types that report themselves and turn 460 after Interval + GracePeriod without a report, and MikroTik
 * routers, which turn 440 after failing polls for the GracePeriod (docs/mikrotik-plan.md §5.2).
 */
export const usesGracePeriod = (type: string | null | undefined) =>
  type === 'HEARTBEAT' ||
  type === 'HEARTBEAT_VALUECHECK' ||
  type === 'UDP_NODE' ||
  type === 'MIKROTIK' ||
  type === 'WINDOWS' ||
  type === 'LINUX' ||
  type === 'DIAGNOSTICS'

/** The PrimeIcon of a sensor type. */
export function typeIcon(type: string): string {
  switch (type) {
    case 'PING':
    case 'UDP_NODE':
      return 'pi pi-wifi'
    case 'HEARTBEAT':
    case 'HEARTBEAT_VALUECHECK':
      return 'pi pi-heart'
    case 'VALUECHECK':
      return 'pi pi-check-square'
    case 'DNS':
      return 'pi pi-globe'
    case 'ENCAPSULADED':
      return 'pi pi-sitemap'
    case 'MIKROTIK':
      return 'pi pi-server'
    case 'SYSTEM':
      return 'pi pi-cog'
    case 'WINDOWS':
      return 'pi pi-microsoft'
    case 'LINUX':
      return 'pi pi-server'
    case 'DIAGNOSTICS':
      return 'pi pi-check-circle'
    case 'ZABBIX':
      return 'pi pi-chart-bar'
    case 'SSL_CERTIFICATE':
      return 'pi pi-lock'
    case 'ONE_WITHIN_TIMESPAN':
    case 'ALL_WITHIN_TIMESPAN':
      return 'pi pi-clock'
    default:
      return 'pi pi-bolt'
  }
}
