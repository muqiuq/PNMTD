import type { S } from '@/api/resources'
import type { Right } from '@/api/rights'

/** Helpers of Admin → Cookie (docs/cookie-plan.md §9.3) that hold no state. */

export interface Option<T = string> {
  label: string
  value: T
}

/**
 * A new id for a row (a narrower instruction, a count column), as a version-4 UUID. crypto.getRandomValues works on plain
 * HTTP too, where crypto.randomUUID doesn't.
 */
export function newId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

/**
 * The options, plus one for each chosen value they don't list (a deleted user, or a list the reader may not load), so a
 * picker still shows what is chosen.
 */
export function withUnknown<T extends string | number>(
  options: Option<T>[],
  chosen: readonly (T | null | undefined)[],
  label: (value: T) => string,
): Option<T>[] {
  const known = new Set(options.map((o) => o.value))
  const extra = [...new Set(chosen)]
    .filter((v): v is T => v !== null && v !== undefined && !known.has(v))
    .map((value) => ({ label: label(value), value }))
  return [...options, ...extra]
}

/** The lounge's prompts (docs/lounge-plan.md §17): on Admin → Cookie → Lounge, not with Cookie's own on Identity. */
export const isLoungePrompt = (key: string) => key.startsWith('lounge')

/** The placeholders a prompt's text has lost: code fills them in, so they may move but not go (docs/cookie-prompts.md). */
export const missingPlaceholders = (text: string, required: readonly string[]) =>
  required.filter((p) => !text.includes(p))

/**
 * A prompt's or a kind of case's text into the overrides: only what differs from the default is kept, so a text edited
 * back to the default is a default again and follows a new release's.
 */
export function setOverride(overrides: Record<string, string>, key: string, text: string, fallback: string) {
  if (text === fallback) {
    delete overrides[key]
  } else {
    overrides[key] = text
  }
}

/** Cookie's actions with a level (docs/cookie-plan.md §6, §21.1), by the names the settings use. */
export const actionKeys = [
  'pushover',
  'email',
  'ticket',
  'privateNote',
  'publicAnswer',
  'objectNote',
  'mailEvent',
  'accessNotice',
  'awayNotice',
] as const

export type ActionKey = (typeof actionKeys)[number]

/**
 * The defaults of §13.4 and §21.1: what leaves PNMT or reaches people at Propose, a note on a host or sensor and a mail
 * rule's event at Auto; every threshold 0.7. The access notice at Auto with 0.6 (docs/cookie-access-plan.md §6.4): Propose
 * would hold a break-in in the inbox until someone looks.
 */
export const actionDefaults: Record<ActionKey, S['ActionRule']> = {
  pushover: { level: 'Propose', minConfidence: 0.7 },
  email: { level: 'Propose', minConfidence: 0.7 },
  ticket: { level: 'Propose', minConfidence: 0.7 },
  privateNote: { level: 'Propose', minConfidence: 0.7 },
  publicAnswer: { level: 'Propose', minConfidence: 0.7 },
  objectNote: { level: 'Auto', minConfidence: 0.7 },
  mailEvent: { level: 'Auto', minConfidence: 0.7 },
  accessNotice: { level: 'Auto', minConfidence: 0.6 },
  // docs/cookie-redmine-tab-plan.md §6.4: Auto, since a person in the loop holds an important comment until they look.
  awayNotice: { level: 'Auto', minConfidence: 0.7 },
}

/** Every action with a rule, the defaults for any the settings lack. */
export function normalizeLevels(levels: S['ActionLevels'] | undefined): Required<S['ActionLevels']> {
  return Object.fromEntries(
    actionKeys.map((k) => [
      k,
      {
        level: levels?.[k]?.level ?? actionDefaults[k].level,
        minConfidence: levels?.[k]?.minConfidence ?? actionDefaults[k].minConfidence,
      },
    ]),
  ) as Required<S['ActionLevels']>
}

/** A tab of Admin → Cookie and the right it needs (docs/cookie-redmine-tab-plan.md §3.2). */
export interface CookieAdminTab {
  key: string
  label: string
  right: Right
}

const tab = (key: string, label: string, right: Right = ['Cookie', 'Manage']): CookieAdminTab => ({ key, label, right })

/** Every tab, in order: the Redmine tab is Redmine: Manage's, every other Cookie: Manage's. */
export const cookieAdminTabs: readonly CookieAdminTab[] = [
  tab('overview', 'Overview'),
  tab('identity', 'Identity'),
  tab('cases', 'Cases'),
  tab('redmine', 'Redmine', ['Redmine', 'Manage']),
  tab('actions', 'Actions'),
  tab('access', 'Access'),
  tab('lounge', 'Lounge'),
  tab('instructions', 'Mute, suppress'),
  tab('lessons', 'Lessons'),
  tab('models', 'Models'),
  tab('rights', 'Rights'),
]

/** The tab shown for the one asked for: it, if the reader may see it; else the first they may see. */
export function shownTab(asked: string | null, allowed: readonly CookieAdminTab[]): string {
  return allowed.some((t) => t.key === asked) ? asked! : (allowed[0]?.key ?? 'overview')
}

/** A Redmine link's state (docs/cookie-redmine-tab-plan.md §4.3), as the links panel names it. */
export type RedmineLinkState = S['RedmineLinkState']

export const linkStateLabel: Record<RedmineLinkState, string> = {
  Linked: 'linked',
  LinkedUnknown: 'linked, unknown to Redmine',
  Matches: 'matches by name',
  Several: 'several match',
  NoMatch: 'no match',
  NotSet: 'not set',
}

/** What each state means for the person's reminders. */
export const linkStateMeaning: Record<RedmineLinkState, string> = {
  Linked: 'Found by the Redmine user’s id, whatever Redmine calls them.',
  LinkedUnknown:
    'No member or watched ticket names this id (left the projects, or deleted): the name is tried instead.',
  Matches: 'Found by the name. Link pins it by id, so a rename in Redmine keeps it.',
  Several: 'Two or more Redmine users have this name: the reminders reach this person only once one is picked.',
  NoMatch: 'No Redmine user has this name: the reminders go to the escalation group or whoever is responsible.',
  NotSet: 'No Redmine user: the reminders for their tickets go to the escalation group or whoever is responsible.',
}

export const linkStateSeverity = (state: RedmineLinkState): 'success' | 'info' | 'warn' | 'danger' | 'secondary' =>
  state === 'Linked'
    ? 'success'
    : state === 'Matches'
      ? 'info'
      : state === 'NoMatch'
        ? 'danger'
        : state === 'NotSet'
          ? 'secondary'
          : 'warn'

/** A row the panel's "Problems only" keeps: anything but a link Redmine knows, and two people with one Redmine user. */
export const isLinkProblem = (row: Pick<S['RedmineLinkRow'], 'state' | 'shared'>): boolean =>
  row.state !== 'Linked' || row.shared

/** The Redmine user a row's picker starts with: the linked one, or the one match. */
export const pickedFor = (row: Pick<S['RedmineLinkRow'], 'redmineUserId' | 'candidates'>): number | null =>
  row.redmineUserId ?? (row.candidates.length === 1 ? row.candidates[0]!.id : null)

/** What came of a comment while away (docs/cookie-redmine-tab-plan.md §6), as the list names it. */
export const awayStateLabel: Record<S['AwayCommentState'], string> = {
  Pending: 'waits for Cookie',
  Withheld: 'withheld',
  Waiting: 'to be told',
  Notified: 'told',
  Joined: 'told with another',
  NotImportant: 'not important',
  Recorded: 'recorded',
  Nobody: 'nobody to tell',
  Back: 'back, not sent',
  Expired: 'expired',
  Off: 'off',
}

export const awayStateSeverity = (
  state: S['AwayCommentState'],
): 'success' | 'info' | 'warn' | 'danger' | 'secondary' =>
  state === 'Notified' || state === 'Joined'
    ? 'success'
    : state === 'Pending' || state === 'Waiting'
      ? 'info'
      : state === 'Withheld' || state === 'Nobody' || state === 'Expired'
        ? 'warn'
        : 'secondary'

/** Cookie's verdict in a few words: the importance and how sure, or why there is none. */
export const awayVerdict = (row: Pick<S['CookieAwayCommentItem'], 'importance' | 'confidence' | 'judgedBy'>): string =>
  row.importance
    ? `${row.importance}${row.confidence != null ? `, ${Math.round(row.confidence * 100)} % sure` : ''}`
    : row.judgedBy === 'code'
      ? 'not judged'
      : '–'
