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
