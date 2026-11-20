import type { S } from '@/api/resources'
import { minutesOf, periodProblems } from '@/views/admin/availability/hours'

/**
 * Admin → Cookie → Lounge's checks and words (docs/lounge-plan.md §12, §15.3), the same as the server's
 * (PNMTD/Cookie/Lounge/LoungeSettings.cs Validate, LoungePacing.Describe), so the tab marks a field before Save does.
 */
export type LoungeValues = Required<S['LoungeSettings']>

/** The ceiling of messages an hour (D19): a message about every 15 seconds. */
export const maxMessagesPerHour = 240

/** The rhythm aims a little below the setting, a ceiling and not a promise (LoungePacing.Aim). */
export const aim = 0.95

/** From this many an hour the scenes run into one continuous conversation (§5.2). */
export const continuousFrom = 45

/** The owner of a conviction starts at least this far on one side (D9). */
export const ownerFloor = 0.5

export const efforts = ['minimal', 'low', 'medium', 'high'] as const

const keyPattern = /^[a-z0-9-]{1,32}$/

const between = (v: number | null | undefined, min: number, max: number) =>
  typeof v === 'number' && v >= min && v <= max

/** The problems by field, as the server names them; none when the section can be saved. */
export function loungeProblems(v: LoungeValues): Record<string, string> {
  const p: Record<string, string> = {}
  const check = (ok: boolean, field: string, message: string) => {
    if (!ok && !p[field]) p[field] = message
  }
  const days = v.awake ?? []
  check(new Set(days.map((d) => d.day)).size === days.length, 'awake', 'Each day once.')
  check(
    days.every((d) => periodProblems(d.periods).length === 0),
    'awake',
    'Each period from HH:mm to a later HH:mm.',
  )
  check(
    days.some((d) => d.periods.length > 0),
    'awake',
    'At least one awake period: to keep them quiet, stop the lounge.',
  )
  check(between(v.messagesPerHour, 0, maxMessagesPerHour), 'messagesPerHour', `0 to ${maxMessagesPerHour}.`)
  check(between(v.longestScene, 2, 10), 'longestScene', '2 to 10.')
  check(between(v.disagreeShare, 0, 1), 'disagreeShare', '0 to 1.')
  check(between(v.repliesPerHour, 0, 60), 'repliesPerHour', '0 to 60.')
  check(between(v.importantPerDay, 0, 10), 'importantPerDay', '0 to 10.')
  check((efforts as readonly string[]).includes(v.effort), 'effort', 'minimal, low, medium or high.')
  check(between(v.toolCallsPerMessage, 0, 5), 'toolCallsPerMessage', '0 to 5.')
  check(between(v.casesPerDay, 0, 5), 'casesPerDay', '0 to 5.')
  check(between(v.emailsPerDay, 0, 10), 'emailsPerDay', '0 to 10.')
  check(between(v.emailsPerPersonWeek, 0, 3), 'emailsPerPersonWeek', '0 to 3.')
  check(between(v.halfLifeDays, 1, 60), 'halfLifeDays', '1 to 60 days.')
  check(between(v.memoriesInPrompt, 5, 30), 'memoriesInPrompt', '5 to 30.')
  check(between(v.maxDriftPerDay, 0, 0.2), 'maxDriftPerDay', '0 to 0.2.')
  check(between(v.budgetUsd, 0, 1000), 'budgetUsd', '0 to 1,000 USD.')
  const convictions = v.convictions ?? []
  check(
    convictions.length === 2 &&
      new Set(convictions.map((c) => c.owner)).size === 2 &&
      convictions.every((c) => c.owner === 'cookie' || c.owner === 'seraph'),
    'convictions',
    'Two convictions, one for Cookie and one for Seraph.',
  )
  check(new Set(convictions.map((c) => c.key)).size === convictions.length, 'convictions', 'Each key once.')
  convictions.forEach((c, i) => {
    const field = `convictions.${i}`
    check(keyPattern.test(c.key), field, 'A key of 1 to 32 lower-case letters, digits and dashes.')
    check(c.question.trim().length > 0 && c.question.trim().length <= 200, field, 'A question of 1 to 200 characters.')
    check(
      c.minus.trim().length > 0 &&
        c.minus.trim().length <= 200 &&
        c.plus.trim().length > 0 &&
        c.plus.trim().length <= 200,
      field,
      'Both poles, 1 to 200 characters each.',
    )
    check(between(c.cookie, -1, 1) && between(c.seraph, -1, 1), field, 'Positions from -1 to +1.')
    const own = c.owner === 'seraph' ? c.seraph : c.cookie
    check(Math.abs(own) >= ownerFloor, field, `The owner starts at least ${ownerFloor} on one side.`)
  })
  return p
}

/** The average awake hours of a day that has any (§15.3). */
export function awakeHours(awake: readonly S['DayHours'][]): number {
  const days = awake.filter((d) => d.periods.length > 0)
  if (!days.length) return 0
  const hours = days.map((d) =>
    d.periods.reduce((sum, p) => {
      const from = minutesOf(p.from)
      const to = minutesOf(p.to, true)
      return from !== null && to !== null && to > from ? sum + (to - from) / 60 : sum
    }, 0),
  )
  return hours.reduce((a, b) => a + b, 0) / days.length
}

/** A scene's mean planned length (§5.2): 2 to the longest, weighted 1/(n−1), a disagreement 2 more. */
export function meanSceneLength(longest: number, disagreeShare: number): number {
  const top = Math.max(2, Math.min(10, longest))
  let total = 0
  let weighted = 0
  for (let n = 2; n <= top; n++) {
    total += 1 / (n - 1)
    weighted += n / (n - 1)
  }
  return weighted / total + 2 * Math.max(0, Math.min(1, disagreeShare))
}

/** What the rhythm means (§15.3), as the loop paces it: messages a day, and a scene's spacing or one conversation. */
export function describeRhythm(
  v: Pick<LoungeValues, 'messagesPerHour' | 'awake' | 'longestScene' | 'disagreeShare'>,
): string {
  if (!v.messagesPerHour || v.messagesPerHour <= 0) return 'They only answer people: no conversation of their own.'
  const perDay = Math.round((v.messagesPerHour * aim * awakeHours(v.awake ?? [])) / 10) * 10
  const seconds = 3600 / (v.messagesPerHour * aim)
  if (v.messagesPerHour >= continuousFrom) {
    const every =
      seconds < 50
        ? `${Math.round(seconds / 5) * 5} seconds`
        : seconds / 60 < 1.5
          ? 'minute'
          : `${Math.round(seconds / 60)} minutes`
    return `A message about every ${every}: one continuous conversation, about ${perDay.toLocaleString('en-US')} messages a day.`
  }
  const cycle = (seconds * meanSceneLength(v.longestScene, v.disagreeShare)) / 60
  return `About ${perDay.toLocaleString('en-US')} messages a day, a scene about every ${Math.round(cycle)} minutes.`
}

/** The two voices, as the settings and the tables name them. */
export const voices = [
  { label: 'Cookie', value: 'cookie' },
  { label: 'Seraph', value: 'seraph' },
] as const

export const emailLevels: { label: string; value: S['ActionLevel'] }[] = [
  { label: 'Off', value: 'Off' },
  { label: 'Propose', value: 'Propose' },
  { label: 'Auto', value: 'Auto' },
]

/** The lounge's state as a tag's tone. */
export const loungeStateSeverity = (s: S['LoungeState']) =>
  s === 'Awake' || s === 'Speaking' ? 'success' : s === 'Asleep' ? 'info' : s === 'Paused' ? 'warn' : 'secondary'
