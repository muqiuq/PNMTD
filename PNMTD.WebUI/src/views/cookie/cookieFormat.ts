import type { S } from '@/api/resources'

/** Cookie's words for its states, kinds and channels (docs/cookie-plan.md §4.1, §7, §8, §19.1). */

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary'

export type DutyKind = NonNullable<S['CookieDutyKind']>

export const overallLabel: Record<S['OverallState'], string> = {
  AllFine: 'All fine',
  Info: 'Info',
  Warning: 'Warning',
  Alert: 'Alert',
}

export const overallSeverity = (s: S['OverallState']): Severity =>
  s === 'AllFine' ? 'success' : s === 'Info' ? 'info' : s === 'Warning' ? 'warn' : 'danger'

/** Where the overall state comes from: Cookie's own judgement while it runs, PNMT's code otherwise (§8). */
export const sourceLabel = (source: string) => (source === 'cookie' ? 'as Cookie sees it' : 'computed by PNMT')

export const cookieStateSeverity = (s: S['CookieState']): Severity =>
  s === 'Working' ? 'info' : s === 'Sleeping' ? 'success' : s === 'Paused' ? 'warn' : 'secondary'

export const caseKinds: S['CookieCaseKind'][] = ['Problem', 'Ticket', 'Request']

export const dutyKinds: DutyKind[] = [
  'FirstAnswerReminder',
  'FirstAnswerMissed',
  'UpdateReminder',
  'UpdateMissed',
  'ProblemTicket',
  'ClosingNote',
]

export const dutyLabel: Record<DutyKind, string> = {
  FirstAnswerReminder: 'First answer: reminder',
  FirstAnswerMissed: 'First answer: missed',
  UpdateReminder: 'Update: reminder',
  UpdateMissed: 'Update: missed',
  ProblemTicket: 'Problem: ticket',
  ClosingNote: 'Closing note',
}

export const dutyName = (kind: S['CookieDutyKind'] | null | undefined) => (kind ? dutyLabel[kind] : '–')

export const dutyStateLabel: Record<S['CookieDutyState'], string> = {
  Due: 'Due',
  Done: 'Done',
  HeldBack: 'Held back',
  Off: 'Off',
  NotPossible: 'Not possible',
  Failed: 'Failed',
}

export const dutyStateName = (s: S['CookieDutyState']) => dutyStateLabel[s] ?? s

export const dutyStateSeverity = (s: S['CookieDutyState']): Severity =>
  s === 'Done'
    ? 'success'
    : s === 'Due'
      ? 'info'
      : s === 'HeldBack' || s === 'NotPossible'
        ? 'warn'
        : s === 'Failed'
          ? 'danger'
          : 'secondary'

export const channels: S['CookieChannel'][] = [
  'Email',
  'Pushover',
  'RedmineNote',
  'RedminePrivateNote',
  'RedmineTicket',
]

export const channelLabel: Record<S['CookieChannel'], string> = {
  Email: 'E-mail',
  Pushover: 'Pushover',
  RedmineNote: 'Note',
  RedminePrivateNote: 'Private note',
  RedmineTicket: 'Ticket',
}

export const channelName = (c: S['CookieChannel']) => channelLabel[c] ?? c

export const outboxStates: S['CookieOutboxState'][] = ['Sent', 'HeldBack', 'Failed', 'Simulated']

export const outboxStateLabel: Record<S['CookieOutboxState'], string> = {
  Sent: 'Sent',
  HeldBack: 'Held back',
  Failed: 'Failed',
  Simulated: 'Simulated',
}

export const outboxStateName = (s: S['CookieOutboxState']) => outboxStateLabel[s] ?? s

export const outboxStateSeverity = (s: S['CookieOutboxState']): Severity =>
  s === 'Sent' ? 'success' : s === 'HeldBack' ? 'warn' : s === 'Failed' ? 'danger' : 'secondary'

/** Who wrote a timeline entry: PNMT's code, Cookie, or a person by name. */
export function actorLabel(entry: Pick<S['CookieEntryItem'], 'actor' | 'actorName'>): string {
  if (entry.actorName) return entry.actorName
  return entry.actor === 'code' ? 'PNMT' : entry.actor === 'cookie' ? 'Cookie' : entry.actor
}
