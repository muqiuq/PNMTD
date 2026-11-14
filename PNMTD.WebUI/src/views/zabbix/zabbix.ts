import type { S } from '@/api/resources'

/** Zabbix's severities, worst first (docs/audit-redmine-zabbix-plan.md §7.4). */
export const severities: S['ZabbixSeverity'][] = [
  'Disaster',
  'High',
  'Average',
  'Warning',
  'Information',
  'NotClassified',
]

/** Their index in Zabbix (0 Not classified … 5 Disaster), as the summary's matrix counts them. */
export const severityIndex = (s: S['ZabbixSeverity']) => 5 - severities.indexOf(s)

export const severityLabel = (s: S['ZabbixSeverity']) => (s === 'NotClassified' ? 'Not classified' : s)

export const severityOptions = severities.map((s) => ({ value: s, label: severityLabel(s) }))

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
