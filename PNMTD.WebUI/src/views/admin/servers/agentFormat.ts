import type { S } from '@/api/resources'

/** Labels and colours for servers with an agent, Windows and Linux (docs/windows-sensor-plan.md §10; docs/linux-sensor-plan.md §11). */

export type TagSeverity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast'

export const logKindSeverity = (kind: S['AgentLogKind']): TagSeverity =>
  kind === 'Joined' || kind === 'Connected'
    ? 'success'
    : kind === 'Problem' || kind === 'Warning' || kind === 'Disconnected'
      ? 'warn'
      : kind === 'Crashed' || kind === 'Error' || kind === 'AuthFailed' || kind === 'Revoked'
        ? 'danger'
        : 'secondary'

export const logKindLabel = (kind: S['AgentLogKind']) =>
  kind === 'AuthFailed' ? 'auth failed' : kind === 'ConfigSent' ? 'config sent' : kind.toLowerCase()

export const platforms: S['AgentPlatform'][] = ['Windows', 'Linux']

export const platformIcon = (platform: S['AgentPlatform'] | null | undefined) =>
  platform === 'Linux' ? 'pi pi-server' : 'pi pi-microsoft'

/**
 * A platform's event codes (docs/linux-sensor-plan.md §9.2): Windows 25x and 45x, Linux 26x and 46x. The switches and the
 * changes name them.
 */
export function platformCodes(platform: S['AgentPlatform'] | null | undefined) {
  return platform === 'Linux'
    ? { changed: 263, adminLogin: 264, loginFailures: 464, critical: 463 }
    : { changed: 253, adminLogin: 254, loginFailures: 454, critical: 453 }
}

export const tokenStateSeverity = (state: S['AgentJoinTokenState']): TagSeverity =>
  state === 'Pending' ? 'info' : state === 'Used' ? 'success' : 'secondary'

export const logKinds: S['AgentLogKind'][] = [
  'Joined',
  'Connected',
  'Disconnected',
  'AuthFailed',
  'Revoked',
  'Uninstalled',
  'ConfigSent',
  'Request',
  'Problem',
  'Crashed',
  'Error',
  'Warning',
]

/** "the clock is 7 min ahead", or null when it is right. */
export function skewText(seconds: number | null | undefined): string | null {
  if (seconds === null || seconds === undefined || Math.abs(seconds) < 5) return null
  const abs = Math.abs(seconds)
  const amount = abs >= 120 ? `${Math.round(abs / 60)} min` : `${abs} s`
  return `${amount} ${seconds > 0 ? 'ahead' : 'behind'}`
}

/** The connection as one word, with a colour. */
export function connection(s: Pick<S['AgentServerItem'], 'connected' | 'revoked' | 'uninstalled'>): {
  label: string
  severity: TagSeverity
} {
  if (s.revoked) return { label: 'revoked', severity: 'danger' }
  if (s.uninstalled) return { label: 'uninstalled', severity: 'secondary' }
  return s.connected ? { label: 'connected', severity: 'success' } : { label: 'offline', severity: 'warn' }
}

/** "3 d 4 h" since a boot time. */
export function uptime(boot: string | null | undefined): string | null {
  if (!boot) return null
  const minutes = Math.max(0, Math.floor((Date.now() - new Date(boot).getTime()) / 60_000))
  const days = Math.floor(minutes / 1440)
  const hours = Math.floor((minutes % 1440) / 60)
  return days > 0 ? `${days} d ${hours} h` : hours > 0 ? `${hours} h ${minutes % 60} min` : `${minutes} min`
}

/** Bytes as "16 GiB", "812 MiB". */
export function bytes(value: number | null | undefined): string {
  if (value === null || value === undefined) return '–'
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB']
  let v = value
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}`
}

/** Bits per second as "10 Gbit/s". */
export function speed(bps: number | null | undefined): string {
  if (!bps) return '–'
  return bps >= 1e9 ? `${bps / 1e9} Gbit/s` : bps >= 1e6 ? `${bps / 1e6} Mbit/s` : `${bps / 1e3} kbit/s`
}
