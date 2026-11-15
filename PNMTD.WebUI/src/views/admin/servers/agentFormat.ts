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
 * changes name them. Linux adds unregistered keys (465) and pending security updates (462).
 */
export function platformCodes(platform: S['AgentPlatform'] | null | undefined) {
  return platform === 'Linux'
    ? { changed: 263, adminLogin: 264, loginFailures: 464, critical: 463, keyAdded: 465, securityUpdates: 462 }
    : { changed: 253, adminLogin: 254, loginFailures: 454, critical: 453, keyAdded: null, securityUpdates: null }
}

/** A login's kind in words: "RDP", "SSH", "SFTP", "sudo", "explicit credentials"; a router's "WinBox", "WebFig", "API". */
export const logonKindLabel = (k: S['LogonKind']) =>
  (
    ({
      Rdp: 'RDP',
      RdpConnection: 'RDP connection',
      Ssh: 'SSH',
      Sftp: 'SFTP',
      WinRm: 'WinRM',
      Explicit: 'explicit credentials',
      Winbox: 'WinBox',
      Web: 'WebFig',
      Api: 'API',
      Telnet: 'Telnet',
      Ftp: 'FTP',
    }) as Record<string, string>
  )[k] ?? k.toLowerCase()

/** The kinds of login each platform records (docs/windows-sensor-plan.md §4.7; docs/linux-sensor-plan.md §5.6). */
export const logonKinds: Record<S['AgentPlatform'], S['LogonKind'][]> = {
  Windows: [
    'Console',
    'Rdp',
    'RdpConnection',
    'Ssh',
    'WinRm',
    'Network',
    'Cached',
    'Unlock',
    'Explicit',
    'Batch',
    'Lockout',
  ],
  Linux: ['Ssh', 'Sftp', 'Console', 'Sudo', 'Su', 'Lockout'],
}

/** The kinds of login a router's log has (docs/cookie-access-plan.md §4.2): its console is the serial or local one. */
export const routerLogonKinds: S['LogonKind'][] = ['Winbox', 'Web', 'Api', 'Ssh', 'Telnet', 'Ftp', 'Console']

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

/** The Windows agent's update line, for PowerShell as an administrator (docs/agent-update-plan.md §8.3). */
export const windowsUpdateLine = '& "C:\\Program Files\\PNMT Sensor\\pnmt-sensor.exe" update'

/** The Linux agent's update line, as root. */
export const linuxUpdateLine = 'pnmt-agent update'

/**
 * How an agent updates (docs/agent-update-plan.md §6, §10): Linux with its line; Windows from 1.8 with its line too; a Windows
 * agent before 1.8 can't download from PNMT, so the new executable is run on the server once ('windows-legacy').
 */
export function updateHow(
  platform: S['AgentPlatform'],
  agentVersion: string | null | undefined,
): 'linux' | 'windows' | 'windows-legacy' {
  if (platform === 'Linux') return 'linux'
  const [major, minor] = (agentVersion ?? '').split(/[.+-]/).map((n) => Number.parseInt(n, 10))
  return Number.isFinite(major) && Number.isFinite(minor) && (major > 1 || (major === 1 && minor >= 8))
    ? 'windows'
    : 'windows-legacy'
}
