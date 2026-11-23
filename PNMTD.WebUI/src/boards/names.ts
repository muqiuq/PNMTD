import type { BoardElementType, BoardMetric } from './types'

/** Each element type's own title (docs/boards-plan.md §7.1), as the catalog names it: an element without a title of its own shows it. */
export const typeNames: Record<BoardElementType, string> = {
  RedmineOverview: 'Redmine overview',
  RedmineNewest: 'Newest tickets',
  RedmineReceived: 'Tickets received today',
  RedmineSolved: 'Tickets solved today',
  ZabbixCount: 'Zabbix problems',
  ZabbixHistory: 'Latest Zabbix problems',
  Lounge: 'The lounge',
  SensorSummary: 'Sensors',
  FailingSensors: 'Failing sensors',
  StatusTiles: 'Status',
  HostBars: 'Hosts',
  RecentChanges: 'Recent changes',
  Uplink: 'Uplink',
  Clock: 'Clock',
  Note: 'Note',
  OnDuty: 'On duty today',
  Certificates: 'Expiring certificates',
}

export const metricNames: Record<BoardMetric, string> = {
  SensorsFailing: 'Failing sensors',
  HostsFailing: 'Hosts with a failing sensor',
  UplinkDown: 'The uplink is down',
  LateTasks: 'Late background tasks',
  DnsMismatches: 'DNS zones that differ',
  ZabbixProblems: 'Zabbix problems',
  RedmineOpen: 'Redmine: open tickets',
  RedmineUrgent: 'Redmine: urgent tickets',
  RedmineUnassigned: 'Redmine: new, no one assigned',
  RedmineOverdue: 'Redmine: overdue tickets',
  RedmineLinkedToProblem: 'Redmine: tickets linked to a problem',
  RedmineReceivedToday: 'Redmine: tickets received today',
  CertificatesExpiring: 'Certificates expiring',
}

/** The bar's symbol beside its colour: never colour alone (§9.4). */
export const colourSymbol = { Green: '●', Yellow: '▲', Red: '■', Grey: '?' } as const
