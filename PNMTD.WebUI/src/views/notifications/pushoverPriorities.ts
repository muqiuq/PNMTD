import type { S } from '@/api/resources'

/**
 * The Pushover priorities (docs/cookie-object-notes-plan.md §5.4): Pushover's five, with what each does on the phone, and
 * the checks the server makes, so that the tab can say what is wrong before saving.
 */
export const EMERGENCY = 2

export const pushoverPriorities: { value: number; label: string; hint: string }[] = [
  { value: -2, label: 'Lowest (−2)', hint: 'No notification, only the badge' },
  { value: -1, label: 'Low (−1)', hint: 'Quiet: no sound, no vibration' },
  { value: 0, label: 'Normal (0)', hint: 'A normal notification' },
  { value: 1, label: 'High (1)', hint: "Bypasses the person's quiet hours" },
  { value: EMERGENCY, label: 'Emergency (2)', hint: 'Repeats until acknowledged' },
]

/** Pushover's limits for an emergency's retry and expiry, as the server checks them. */
export const MIN_RETRY = 30
export const MAX_RETRY = 3600
export const MAX_EXPIRE = 10800

/** The defaults of a new installation: the fixed mapping of before 1.10. */
export const DEFAULT_RETRY = 60
export const DEFAULT_EXPIRE = 3600

export interface PushoverDraft {
  pushoverLow: number
  pushoverNormal: number
  pushoverHigh: number
  /** Null while the field is cleared. */
  emergencyRetrySeconds: number | null
  emergencyExpireSeconds: number | null
}

/** The importances, in the order of the table, and who sends at each (§1.6 of the plan). */
export const importanceRows: { key: 'pushoverLow' | 'pushoverNormal' | 'pushoverHigh'; label: string; hint: string }[] =
  [
    {
      key: 'pushoverHigh',
      label: 'High',
      hint: 'High sensors; what Cookie finds urgent; suspicious and alarming logins; MCP strike notices',
    },
    {
      key: 'pushoverNormal',
      label: 'Normal',
      hint: 'Normal sensors; what Cookie finds important; unusual logins; test messages',
    },
    { key: 'pushoverLow', label: 'Low', hint: 'Low sensors' },
  ]

export function draftOf(s: S['NotificationSettings']): PushoverDraft {
  return {
    pushoverLow: s.pushoverLow,
    pushoverNormal: s.pushoverNormal,
    pushoverHigh: s.pushoverHigh,
    emergencyRetrySeconds: s.emergencyRetrySeconds,
    emergencyExpireSeconds: s.emergencyExpireSeconds,
  }
}

/** The request: a cleared repeat field, which only matters with an emergency, goes back to its default. */
export function requestOf(d: PushoverDraft): S['NotificationSettingsRequest'] {
  return {
    pushoverLow: d.pushoverLow,
    pushoverNormal: d.pushoverNormal,
    pushoverHigh: d.pushoverHigh,
    emergencyRetrySeconds: d.emergencyRetrySeconds ?? DEFAULT_RETRY,
    emergencyExpireSeconds: d.emergencyExpireSeconds ?? DEFAULT_EXPIRE,
  }
}

/** Whether any importance is sent as an emergency, so that the repeat fields matter. */
export function usesEmergency(d: PushoverDraft): boolean {
  return d.pushoverLow === EMERGENCY || d.pushoverNormal === EMERGENCY || d.pushoverHigh === EMERGENCY
}

/** What the server would refuse: a message per field. */
export function pushoverErrors(d: PushoverDraft): Partial<Record<keyof PushoverDraft, string>> {
  const errors: Partial<Record<keyof PushoverDraft, string>> = {}
  if (!usesEmergency(d)) return errors
  const retry = d.emergencyRetrySeconds
  const expire = d.emergencyExpireSeconds
  if (retry === null || !Number.isInteger(retry) || retry < MIN_RETRY || retry > MAX_RETRY) {
    errors.emergencyRetrySeconds = `${MIN_RETRY} to ${MAX_RETRY} seconds.`
  }
  if (expire === null || !Number.isInteger(expire) || expire < (retry ?? MIN_RETRY) || expire > MAX_EXPIRE) {
    errors.emergencyExpireSeconds = `At least the repeat, at most ${MAX_EXPIRE} seconds.`
  }
  return errors
}
