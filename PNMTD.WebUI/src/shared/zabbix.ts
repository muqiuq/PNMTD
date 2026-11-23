import type { components } from '@/api/schema'

/**
 * Zabbix's severities (docs/audit-redmine-zabbix-plan.md §7.4), shared by the admin UI and the board app (docs/boards-plan.md §4.3):
 * nothing here imports more than the API's types.
 */
export type ZabbixSeverity = components['schemas']['ZabbixSeverity']

/** Worst first. */
export const severities: ZabbixSeverity[] = ['Disaster', 'High', 'Average', 'Warning', 'Information', 'NotClassified']

/** Their index in Zabbix (0 Not classified … 5 Disaster), as the summary's matrix counts them. */
export const severityIndex = (s: ZabbixSeverity) => 5 - severities.indexOf(s)

export const severityLabel = (s: ZabbixSeverity) => (s === 'NotClassified' ? 'Not classified' : s)

export const severityOptions = severities.map((s) => ({ value: s, label: severityLabel(s) }))
