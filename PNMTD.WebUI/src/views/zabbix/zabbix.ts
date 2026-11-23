import type { S } from '@/api/resources'

// The severities live in src/shared (docs/boards-plan.md §4.3): the board app uses them too.
export { severities, severityIndex, severityLabel, severityOptions } from '@/shared/zabbix'

/** An interface type's icon and what Zabbix calls it. */
export const interfaceIcon: Record<string, string> = {
  Agent: 'pi pi-desktop',
  SNMP: 'pi pi-sitemap',
  IPMI: 'pi pi-bolt',
  JMX: 'pi pi-code',
  Other: 'pi pi-circle',
}

export const availabilityLabel: Record<S['ZabbixAvailability'], string> = {
  Available: 'available',
  Unavailable: 'unavailable',
  Unknown: 'unknown',
}
