import type { S } from '@/api/resources'

/** A body as indented JSON when it is JSON, else as it is. */
export function pretty(text: string | null | undefined): string {
  if (!text) return ''
  try {
    return JSON.stringify(JSON.parse(text), null, 2)
  } catch {
    return text
  }
}

/** USD, with up to 6 decimals for the small amounts OpenRouter reports per request. */
export function usd(value: number | null | undefined): string {
  if (value === null || value === undefined) return '–'
  return `$${value < 0.01 && value > 0 ? value.toFixed(6) : value.toFixed(2)}`
}

export const outcomeSeverity = (o: S['AiOutcome']): 'success' | 'danger' | 'warn' =>
  o === 'Ok' ? 'success' : o === 'BlockedByGuard' || o === 'InvalidOutput' ? 'warn' : 'danger'

export const stateSeverity = (s: S['AiWorkState']): 'success' | 'danger' | 'warn' | 'info' | 'secondary' =>
  s === 'Succeeded'
    ? 'success'
    : s === 'Failed'
      ? 'danger'
      : s === 'WaitingRetry' || s === 'WaitingBudget'
        ? 'warn'
        : s === 'Cancelled'
          ? 'secondary'
          : 'info'

/** The situation analysis (§9.3): its status, a finding's severity, and why a run started. */
export const situationSeverity = (
  s: S['SituationStatus'] | null | undefined,
): 'success' | 'warn' | 'danger' | 'secondary' =>
  s === 'Normal' ? 'success' : s === 'Degraded' ? 'warn' : s === 'Incident' ? 'danger' : 'secondary'

export const findingSeverity = (s: S['FindingSeverity']): 'danger' | 'warn' | 'info' =>
  s === 'Critical' ? 'danger' : s === 'Warning' ? 'warn' : 'info'

export const triggerLabel = (t: string): string =>
  ({
    interval: 'Scheduled, something changed',
    storm: 'Many sensors failed',
    'uplink-back': 'The uplink came back',
    'gateway-down': 'The gateway went down',
    manual: 'Run by hand',
  })[t] ?? t
