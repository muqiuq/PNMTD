import type { S } from '@/api/resources'

/**
 * A router's settings as the sensor form and the settings dialog edit them (docs/mikrotik-plan.md
 * §11.1), and what they send as SensorRequest.mikrotik.
 */
export interface MikrotikDraft {
  mode: S['MikrotikMode']
  address: string
  sshPort: number | null
  sourceAddress: string
  backupIntervalHours: number | null
  managementAccess: boolean
  includeSecrets: boolean
  diagnostics: boolean
}

export const MODES: { value: S['MikrotikMode']; label: string; hint: string; needsGateway: boolean }[] = [
  {
    value: 'WireGuardSsh',
    label: 'WireGuard + SSH',
    hint: 'The router dials PNMTD; PNMTD logs in through the tunnel. Every feature.',
    needsGateway: true,
  },
  {
    value: 'WireGuardOnly',
    label: 'WireGuard only',
    hint: 'The tunnel alone: its state is the sensor’s state.',
    needsGateway: true,
  },
  {
    value: 'DirectSsh',
    label: 'Direct SSH',
    hint: 'PNMTD logs in at an address it can reach. No tunnel.',
    needsGateway: false,
  },
]

export const modeLabel = (mode: S['MikrotikMode'] | undefined) => MODES.find((m) => m.value === mode)?.label ?? '–'

/** The fields the server validates, for its errors (field names as in its problem details). */
export const MIKROTIK_FIELDS = [
  'mikrotik',
  'mikrotik.mode',
  'mikrotik.address',
  'mikrotik.sshPort',
  'mikrotik.sourceAddress',
  'mikrotik.backupIntervalHours',
  'mikrotik.managementAccess',
] as const

/** WireGuard + SSH by default where the gateway runs (§3.1), Direct SSH without it. */
export function newMikrotikDraft(wireGuard = false): MikrotikDraft {
  return {
    mode: wireGuard ? 'WireGuardSsh' : 'DirectSsh',
    address: '',
    sshPort: 22,
    sourceAddress: '',
    backupIntervalHours: 24,
    managementAccess: false,
    includeSecrets: false,
    diagnostics: false,
  }
}

export function draftFromRouter(m: S['MikrotikRouterDetail']): MikrotikDraft {
  return {
    mode: m.mode,
    address: m.address ?? '',
    sshPort: m.sshPort,
    sourceAddress: m.sourceAddress ?? '',
    backupIntervalHours: m.backupIntervalHours,
    managementAccess: m.managementAccess,
    includeSecrets: m.includeSecrets,
    diagnostics: m.diagnostics,
  }
}

export function mikrotikRequestFromDraft(d: MikrotikDraft): S['MikrotikRouterRequest'] {
  const direct = d.mode === 'DirectSsh'
  return {
    mode: d.mode,
    address: direct ? d.address.trim() || null : null,
    sshPort: d.sshPort ?? 22,
    sourceAddress: direct ? d.sourceAddress.trim() || null : null,
    backupIntervalHours: d.backupIntervalHours ?? 24,
    managementAccess: direct ? false : d.managementAccess,
    includeSecrets: d.includeSecrets,
    diagnostics: d.diagnostics,
  }
}

/** The checks the form makes before it asks the server, which checks everything again. */
export function draftErrors(d: MikrotikDraft): Record<string, string | undefined> {
  const address = d.address.trim()
  return {
    'mikrotik.address':
      d.mode === 'DirectSsh' && !address
        ? 'The host name or address PNMTD connects to.'
        : /\s/.test(address)
          ? 'A host name or an address, without spaces.'
          : undefined,
    'mikrotik.sshPort': d.sshPort === null || d.sshPort < 1 || d.sshPort > 65535 ? 'Between 1 and 65535.' : undefined,
    'mikrotik.backupIntervalHours':
      d.backupIntervalHours === null || d.backupIntervalHours < 1 || d.backupIntervalHours > 168
        ? 'Between 1 and 168 hours.'
        : undefined,
  }
}

/** What a change of the settings means for the router: what the admin has to do on it. */
export function changeHints(m: S['MikrotikRouterDetail'], d: MikrotikDraft): string[] {
  const hints: string[] = []
  if (m.mode !== d.mode) {
    hints.push('A new mode needs the new join script on the router.')
  } else if (d.mode === 'DirectSsh' && (m.address ?? '') !== d.address.trim()) {
    hints.push('Another address: PNMTD pins the host key again at its next login.')
  }
  if ((m.sourceAddress ?? '') !== d.sourceAddress.trim()) {
    hints.push('A new source address takes effect when the new join script is pasted on the router.')
  }
  if (m.diagnostics !== d.diagnostics || m.includeSecrets !== d.includeSecrets) {
    hints.push('The group pnmt-read changes with the new join script: paste it on the router.')
  }
  return hints
}
