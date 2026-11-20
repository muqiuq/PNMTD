import type { S } from '@/api/resources'

/**
 * Cookie acting (docs/cookie-plan.md §6, §10.3, §11, §21): the decision log's kinds and outcomes, Seraph's verdicts, and
 * mute, suppress and instructions, in the words the window and Admin → Cookie show them.
 */

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary'

export type DecisionItem = S['CookieDecisionItem']
export type InstructionItem = S['CookieInstructionItem']

// --- the decision log (§11, §21.1) ----------------------------------------------------------------------------------

/**
 * What an entry of the decision log is about: an action of §6, a duty's text, the big brother, Seraph's drift report; and
 * the access watch's verdict on a login and its notice (docs/cookie-access-plan.md §5.4, §6.4).
 */
export const decisionKinds = [
  'pushover',
  'email',
  'ticket',
  'private-note',
  'public-answer',
  'object-note',
  'mail-event',
  'hand-to-person',
  'duty-text',
  'big-brother',
  'drift',
  'access',
  'access-notice',
  'lounge-email',
] as const

const kindLabels: Record<string, string> = {
  pushover: 'Pushover',
  email: 'E-mail',
  ticket: 'Ticket',
  'private-note': 'Private note',
  'public-answer': 'Public answer',
  'object-note': 'Note on a host or sensor',
  'mail-event': 'Mail rule event',
  'hand-to-person': 'For a person',
  'duty-text': "A duty's text",
  'big-brother': 'Big brother',
  drift: 'Drift report',
  access: 'Access verdict',
  'access-notice': 'Access notice',
  'lounge-email': 'Lounge e-mail',
}

export const decisionKindLabel = (kind: string) => kindLabels[kind] ?? kind

/**
 * What came of it: code's steps of §21.1 in their order, then what people and the outbox made of it; an access verdict's
 * outcome is the verdict itself (docs/cookie-access-plan.md §5.4).
 */
export const decisionOutcomes = [
  'done',
  'proposed',
  'approved',
  'declined',
  'rejected',
  'revised',
  'off',
  'below-threshold',
  'refused',
  'duplicate',
  'muted',
  'held-back',
  'failed',
  'noted',
  'normal',
  'unusual',
  'suspicious',
  'alarming',
] as const

export const decisionOutcomeLabel = (outcome: string) => outcome.replace(/-/g, ' ')

/** What came of an order from the chat (docs/cookie-powers-plan.md §7), under Cookie's reply: a doubt waits, a sure reject stopped it. */
export const orderOutcomeLabel = (outcome: string) =>
  outcome === 'proposed'
    ? 'waits in the inbox'
    : outcome === 'rejected'
      ? 'stopped by Seraph'
      : decisionOutcomeLabel(outcome)

/**
 * Green for what happened, blue for what waits for a person, amber for what was held, red for what was refused or
 * failed, grey for what code dropped on purpose. An access verdict by how suspicious it looks: normal green, unusual
 * blue, suspicious amber, alarming red.
 */
export function decisionOutcomeSeverity(outcome: string): Severity {
  switch (outcome) {
    case 'done':
    case 'approved':
    case 'normal':
      return 'success'
    case 'proposed':
    case 'below-threshold':
    case 'unusual':
      return 'info'
    case 'declined':
    case 'revised':
    case 'held-back':
    case 'suspicious':
      return 'warn'
    case 'rejected':
    case 'refused':
    case 'failed':
    case 'alarming':
      return 'danger'
    default:
      return 'secondary'
  }
}

/** Seraph's verdicts (§5): approve, revise, reject, ask a person. */
export const verdictLabel = (verdict: string) => (verdict === 'ask' ? 'ask a person' : verdict)

export const verdictSeverity = (verdict: string): Severity =>
  verdict === 'approve' ? 'success' : verdict === 'reject' ? 'danger' : verdict === 'revise' ? 'warn' : 'info'

/** A confidence of 0 to 1 as "0.82". */
export const formatConfidence = (value: number | null | undefined) =>
  value === null || value === undefined ? '–' : value.toFixed(2)

// --- mute, suppress and instructions (§10.3, §21.1) -----------------------------------------------------------------

export type InstructionKind = 'mute' | 'suppress' | 'note'

export const instructionKinds: { value: InstructionKind; label: string }[] = [
  { value: 'mute', label: 'Mute' },
  { value: 'suppress', label: 'Suppress' },
  { value: 'note', label: 'Note' },
]

export const muteScopes = ['host', 'sensor', 'location', 'case'] as const
export const suppressScopes = ['channel', 'sensor-type', 'case-kind'] as const
export const suppressChannels = ['pushover', 'email', 'ticket', 'redmine-note', 'access'] as const
export const suppressCaseKinds = ['problem', 'ticket'] as const

export type InstructionScope = (typeof muteScopes)[number] | (typeof suppressScopes)[number]

const scopeLabels: Record<string, string> = {
  host: 'Host',
  sensor: 'Sensor',
  location: 'Location',
  case: 'Case',
  channel: 'Channel',
  'sensor-type': 'Sensor type',
  'case-kind': 'Kind of case',
}

export const instructionScopeLabel = (scope: string) => scopeLabels[scope] ?? scope

const channelLabels: Record<string, string> = {
  pushover: 'Pushover',
  email: 'E-mail',
  ticket: 'New tickets',
  'redmine-note': 'Notes on tickets',
  access: 'Access notices',
}

export const suppressChannelLabel = (channel: string) => channelLabels[channel] ?? channel

const caseKindLabels: Record<string, string> = { problem: 'Problems', ticket: 'Tickets' }

export const suppressCaseKindLabel = (kind: string) => caseKindLabels[kind.toLowerCase()] ?? kind

export const instructionKindLabel = (kind: string) => instructionKinds.find((k) => k.value === kind)?.label ?? kind

export const instructionSourceLabel = (source: string) =>
  source === 'chat'
    ? 'from the chat'
    : source === 'lesson'
      ? 'from a lesson'
      : source === 'admin'
        ? 'by an admin'
        : source

/** What it is about, in words: "Host srv-01", "Channel Pushover"; a note is its text. */
export function instructionTarget(i: Pick<InstructionItem, 'kind' | 'scope' | 'value' | 'label'>): string {
  if (i.kind === 'note') return ''
  const value =
    i.scope === 'channel'
      ? suppressChannelLabel(i.value ?? '')
      : i.scope === 'case-kind'
        ? suppressCaseKindLabel(i.value ?? '')
        : i.label?.trim() || i.value || '?'
  return `${instructionScopeLabel(i.scope)} ${value}`
}

export type InstructionState = 'active' | 'draft' | 'ended' | 'expired'

/** A draft from the chat waits for its person to confirm it; one that ended or ran out counts no more. */
export function instructionState(
  i: Pick<InstructionItem, 'confirmed' | 'endedAt' | 'until'>,
  now: number,
): InstructionState {
  if (i.endedAt) return 'ended'
  if (new Date(i.until).getTime() <= now) return 'expired'
  return i.confirmed ? 'active' : 'draft'
}

export const instructionStateSeverity = (state: InstructionState): Severity =>
  state === 'active' ? 'success' : state === 'draft' ? 'info' : 'secondary'

/** The active ones first, then the drafts, then the rest; within each the newest first. */
export function sortInstructions<T extends InstructionItem>(items: readonly T[], now: number): T[] {
  const rank: Record<InstructionState, number> = { active: 0, draft: 1, ended: 2, expired: 2 }
  return [...items].sort(
    (a, b) =>
      rank[instructionState(a, now)] - rank[instructionState(b, now)] ||
      new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
  )
}

/** How far ahead an instruction may end (§10.3). */
export const maxInstructionDays = 30

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** A new instruction as Admin → Cookie → Mute, suppress fills it in. */
export interface InstructionForm {
  kind: InstructionKind
  scope: string
  value: string | null
  label: string
  text: string
  until: Date | null
}

/** The server's checks (Instructions.Validate), by field, as a hint before sending: nothing when it can be created. */
export function instructionProblems(f: InstructionForm, now: number): Record<string, string> {
  const problems: Record<string, string> = {}
  const value = f.value?.trim() ?? ''
  if (f.kind === 'mute') {
    if (!(muteScopes as readonly string[]).includes(f.scope)) problems.scope = 'Host, sensor, location or case.'
    else if (!uuid.test(value)) problems.value = `Choose the ${instructionScopeLabel(f.scope).toLowerCase()}.`
  } else if (f.kind === 'suppress') {
    if (!(suppressScopes as readonly string[]).includes(f.scope))
      problems.scope = 'Channel, sensor type or kind of case.'
    else if (!value || value.length > 100) problems.value = 'Choose what is suppressed.'
    else if (f.scope === 'channel' && !(suppressChannels as readonly string[]).includes(value.toLowerCase()))
      problems.value = 'Pushover, e-mail, tickets, notes on tickets or access notices.'
    else if (f.scope === 'case-kind' && !(suppressCaseKinds as readonly string[]).includes(value.toLowerCase()))
      problems.value = 'Problems or tickets.'
  }
  const text = f.text.trim()
  if (f.kind === 'note' && !text) problems.text = 'Write the instruction.'
  else if (f.text.length > 1000) problems.text = 'At most 1,000 characters.'
  const until = f.until?.getTime()
  if (until === undefined || Number.isNaN(until)) problems.until = 'Choose when it ends.'
  else if (until <= now) problems.until = 'In the future.'
  else if (until > now + maxInstructionDays * 86_400_000 + 60_000)
    problems.until = `At most ${maxInstructionDays} days away.`
  return problems
}

/** The request for a valid form: no scope for a note, the value trimmed. */
export function instructionRequest(f: InstructionForm): S['CookieInstructionRequest'] {
  const note = f.kind === 'note'
  return {
    kind: f.kind,
    scope: note ? '' : f.scope,
    value: note ? null : f.value?.trim() || null,
    label: note ? null : f.label.trim() || null,
    text: f.text.trim(),
    until: (f.until ?? new Date()).toISOString(),
  }
}

/** How long a new one lasts: a choice of spans, or a moment chosen ("custom"). */
export const instructionSpans = [
  { label: '1 hour', value: 'PT1H', ms: 3_600_000 },
  { label: '1 day', value: 'P1D', ms: 86_400_000 },
  { label: '3 days', value: 'P3D', ms: 3 * 86_400_000 },
  { label: '7 days', value: 'P7D', ms: 7 * 86_400_000 },
  { label: '30 days', value: 'P30D', ms: 30 * 86_400_000 },
  { label: 'Until…', value: 'custom', ms: 0 },
] as const

export type InstructionSpan = (typeof instructionSpans)[number]['value']

/** The end of a span from now; null for a chosen moment. */
export function untilAfter(span: InstructionSpan, now: number): Date | null {
  const ms = instructionSpans.find((s) => s.value === span)?.ms ?? 0
  return ms ? new Date(now + ms) : null
}

// --- lessons (§11) --------------------------------------------------------------------------------------------------

export const maxActiveLessons = 30

export type Lesson = S['Lesson']

/** The server's checks on the lessons (LessonSettings.Validate), by lesson id and for the whole list. */
export function lessonProblems(lessons: readonly Lesson[]): Record<string, string> {
  const problems: Record<string, string> = {}
  const active = lessons.filter((l) => l.active).length
  if (lessons.length > 100) problems.lessons = 'At most 100 lessons.'
  else if (active > maxActiveLessons) problems.lessons = `At most ${maxActiveLessons} active; turn some off.`
  for (const l of lessons) {
    const text = l.text.trim()
    if (!text) problems[l.id] = 'Write the lesson.'
    else if (text.length > 300) problems[l.id] = 'At most 300 characters.'
    else if (l.suppressScope && !l.suppressValue?.trim()) problems[l.id] = 'Choose what it suppresses.'
  }
  return problems
}
