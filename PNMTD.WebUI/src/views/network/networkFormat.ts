import type { S } from '@/api/resources'
import { parseFocus as parseGraphFocus } from '@/components/graph/focus'

/** The network map's words, colours and widths (docs/network-map-plan.md §8). */

export type Edge = S['NetworkEdge']
export type Node = S['NetworkNode']
export type End = S['NetworkEndItem']

/** "office-gw · ether1", or the name alone. */
export function endLabel(end: Pick<End, 'name' | 'interface'>): string {
  return end.interface ? `${end.name} · ${end.interface}` : end.name
}

/** "router:{id}" / "host:{id}": a node of the map, and the focus in the URL. */
export function deviceRef(kind: S['NetworkDeviceKind'], id: string): string {
  return `${kind === 'Router' ? 'router' : 'host'}:${id}`
}

/** The letter in a node's circle, on the map and in the panel: ⇄ a router, W/L a server, nothing a plain host. */
export function nodeGlyph(kind: S['NetworkDeviceKind'], os: string | null | undefined): string {
  if (kind === 'Router') return '⇄'
  return os === 'Windows' ? 'W' : os ? 'L' : ''
}

/** "Router", "Linux server", "Host". */
export function kindLabel(kind: S['NetworkDeviceKind'], os: string | null | undefined): string {
  return kind === 'Router' ? 'Router' : os ? `${os} server` : 'Host'
}

/** A connection's ports from one end: "ether1 → sfp1", "ether1", "→ sfp1", or ''. */
export function portsLabel(near: string | null | undefined, far: string | null | undefined): string {
  if (near && far) return `${near} → ${far}`
  return near || (far ? `→ ${far}` : '')
}

/** The id and kind of an end, as the API's device endpoints take them. */
export function endDevice(end: End): { kind: S['NetworkDeviceKind']; id: string } {
  return end.kind === 'Router' && end.sensorId ? { kind: 'Router', id: end.sensorId } : { kind: 'Host', id: end.hostId }
}

/** Parses ?focus=router:{id}, host:{id} or connection:{id}. */
export function parseFocus(value: unknown): { kind: 'router' | 'host' | 'connection'; id: string } | null {
  return parseGraphFocus(value, ['router', 'host', 'connection'] as const)
}

export const mediumLabels: Record<S['ConnectionMedium'], string> = {
  BaseT: 'BASE-T',
  Dac: 'DAC',
  Aoc: 'AOC',
  FiberMmf: 'Fiber MMF',
  FiberSmf: 'Fiber SMF',
  Wifi: 'Wi-Fi',
  WireGuard: 'WireGuard',
  Tunnel: 'Tunnel',
  VirtualMachine: 'Virtual machine',
  Other: 'Other',
}

/** The line's width grows with the speed (§8.2): ≤100M, 1G, 2.5–10G, ≥25G. */
export function edgeWidth(speedMbps: number | null | undefined): number {
  if (speedMbps == null) return 1.5
  if (speedMbps <= 100) return 1
  if (speedMbps <= 1000) return 2
  if (speedMbps <= 10_000) return 3
  return 4.5
}

/** Virtual dashed, Wi-Fi dotted, a suggestion thin and dotted. */
export function edgeDash(edge: Pick<Edge, 'physical' | 'type' | 'suggestion'>): string | undefined {
  if (edge.suggestion) return '1 5'
  if (edge.type === 'wifi') return '2 4'
  if (!edge.physical) return '7 5'
  return undefined
}

export function edgeTone(edge: Pick<Edge, 'scan' | 'suggestion'>): 'warn' | 'muted' | 'normal' {
  if (edge.suggestion) return 'muted'
  return edge.scan === 'Unconfirmed' ? 'warn' : 'normal'
}

export function scanLabel(scan: S['NetworkScanEvidence']): string {
  return scan === 'Confirmed' ? 'Confirmed by the scan' : scan === 'Unconfirmed' ? 'No longer seen' : 'Made by hand'
}

export function confidenceSeverity(c: S['Confidence']): 'success' | 'info' | 'secondary' {
  return c === 'High' ? 'success' : c === 'Medium' ? 'info' : 'secondary'
}

export const sourceLabels: Record<string, { label: string; icon: string }> = {
  MacTable: { label: 'MAC table', icon: 'pi pi-table' },
  Arp: { label: 'ARP', icon: 'pi pi-arrows-h' },
  Dhcp: { label: 'DHCP', icon: 'pi pi-inbox' },
  Neighbor: { label: 'Neighbor discovery', icon: 'pi pi-share-alt' },
  Wifi: { label: 'Wi-Fi', icon: 'pi pi-wifi' },
}

export function matchLabel(
  i: Pick<S['NetworkInterfaceItem'], 'match' | 'matchName' | 'matchInterface' | 'assignedByName'>,
): string {
  const on = i.matchInterface ? ` · ${i.matchInterface}` : ''
  switch (i.match) {
    case 'RouterPort':
      return `${i.matchName ?? 'a router'}${on}`
    case 'ServerAdapter':
      return `${i.matchName ?? 'a server'}${on}`
    case 'Manual':
      return `${i.matchName ?? 'a host'}${on}${i.assignedByName ? ` (by ${i.assignedByName})` : ''}`
    case 'Ambiguous':
      return 'Several matches'
    default:
      return 'Unassigned'
  }
}

/** "office-gw · ether7 · VLAN 20", or what it is behind. */
export function attachedLabel(
  i: Pick<S['NetworkInterfaceItem'], 'attachedRouter' | 'attachedPort' | 'attachedVlan' | 'attachedNote'>,
): string {
  const at = [i.attachedRouter, i.attachedPort, i.attachedVlan != null ? `VLAN ${i.attachedVlan}` : null]
    .filter(Boolean)
    .join(' · ')
  if (i.attachedNote) return at ? `${at} (${i.attachedNote})` : i.attachedNote
  return at || '–'
}

export function stepLabel(scan: Pick<S['NetworkScanRow'], 'currentStep' | 'currentStepNumber' | 'stepCount'>): string {
  if (!scan.currentStep) return ''
  return scan.currentStepNumber
    ? `${scan.currentStep} (step ${scan.currentStepNumber} of ${scan.stepCount})`
    : scan.currentStep
}

/** "12 of 40 routers · 1 failed · 2 skipped · 25 left". */
export function progressLabel(scan: Pick<S['NetworkScanRow'], 'total' | 'succeeded' | 'failed' | 'skipped'>): string {
  const done = scan.succeeded + scan.failed + scan.skipped
  const parts = [`${done} of ${scan.total} router${scan.total === 1 ? '' : 's'}`]
  if (scan.failed) parts.push(`${scan.failed} failed`)
  if (scan.skipped) parts.push(`${scan.skipped} skipped`)
  if (scan.total - done > 0) parts.push(`${scan.total - done} left`)
  return parts.join(' · ')
}

export function secondsLabel(seconds: number | null | undefined): string {
  if (seconds == null) return ''
  if (seconds < 90) return `about ${Math.max(1, Math.round(seconds))} s left`
  return `about ${Math.round(seconds / 60)} min left`
}

/** The hour as the schedule shows it: 02:00. */
export function hourLabel(hour: number): string {
  return `${String(hour).padStart(2, '0')}:00`
}
