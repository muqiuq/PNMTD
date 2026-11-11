import type { Status } from '@/components/StatusBadge.vue'

interface ZoneLike {
  enabled: boolean
  recordsMatch: boolean
  requiresProcessing: boolean
  forceUpdate: boolean
  lastChecked: string | null
}

/** A zone's state: off, waiting for its first check, records differ, or all match. */
export function zoneStatus(z: ZoneLike): { status: Status; label: string } {
  if (!z.enabled) return { status: 'Disabled', label: 'Disabled' }
  if (z.requiresProcessing || z.forceUpdate || !z.lastChecked) return { status: 'NoData', label: 'Check pending' }
  return z.recordsMatch ? { status: 'Ok', label: 'Records match' } : { status: 'Failing', label: 'Mismatch' }
}

export const logLabel: Record<string, string> = {
  NEW_ENTRY: 'Record added',
  REMOVED_ENTRY: 'Record removed',
  ENTRY_DISCREPANCY: 'Mismatch',
}
