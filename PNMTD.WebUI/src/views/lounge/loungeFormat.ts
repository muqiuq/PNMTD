import type { S } from '@/api/resources'

/** The lounge's words and shapes (docs/lounge-plan.md §15.2): who said it, links in the text, comments under their message. */

export type LoungeMessage = S['LoungeMessageItem']

// The chips live in src/shared (docs/boards-plan.md §4.3): the board app uses them too.
export { segments, type Segment } from '@/shared/loungeText'

/** One line of the room with the comments on it underneath (§9.3); a comment on no message stands alone in the flow. */
export interface Entry {
  message: LoungeMessage
  comments: LoungeMessage[]
  /** The first line of a new day: its date above it. */
  day: string | null
}

const dayFormat = new Intl.DateTimeFormat(undefined, { weekday: 'long', day: 'numeric', month: 'long' })
const timeFormat = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' })

export const dayOf = (at: string) => dayFormat.format(new Date(at))
export const timeOf = (at: string | null | undefined) => (at ? timeFormat.format(new Date(at)) : '')

/** The flow (§15.2): oldest first, comments under the message they are on, a date at each new day. */
export function flow(messages: readonly LoungeMessage[]): Entry[] {
  const sorted = [...messages].sort((a, b) => a.id - b.id)
  const ids = new Set(sorted.map((m) => m.id))
  const under = new Map<number, LoungeMessage[]>()
  for (const m of sorted) {
    if (m.kind === 'comment' && m.onId != null && ids.has(m.onId)) {
      under.set(m.onId, [...(under.get(m.onId) ?? []), m])
    }
  }
  const entries: Entry[] = []
  let lastDay: string | null = null
  for (const m of sorted) {
    if (m.kind === 'comment' && m.onId != null && ids.has(m.onId)) continue
    const day = dayOf(m.at)
    entries.push({ message: m, comments: under.get(m.id) ?? [], day: day === lastDay ? null : day })
    lastDay = day
  }
  return entries
}

/** Merges what a poll brought into what is shown, by id: a removed or taken-up line changes in place. */
export function merge(current: readonly LoungeMessage[], incoming: readonly LoungeMessage[]): LoungeMessage[] {
  const byId = new Map(current.map((m) => [m.id, m]))
  for (const m of incoming) byId.set(m.id, m)
  return [...byId.values()].sort((a, b) => a.id - b.id)
}

export const isVoice = (m: Pick<LoungeMessage, 'from'>) => m.from === 'cookie' || m.from === 'seraph'

/** Who wrote it, as the room shows it: Cookie by its name, Seraph, a person by theirs. */
export function author(m: LoungeMessage, cookieName: string): string {
  if (m.from === 'cookie') return cookieName
  if (m.from === 'seraph') return 'Seraph'
  if (m.from === 'system') return ''
  return m.author ?? 'Someone'
}

/** A message can be commented on (§9.3): not a comment, not removed, not the room's own line. */
export const commentable = (m: LoungeMessage) => m.kind !== 'comment' && !m.removed && m.from !== 'system'

/** A person's short code, or their initials. */
export function initials(m: LoungeMessage): string {
  if (m.authorCode) return m.authorCode
  const name = m.author ?? '?'
  return (
    name
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((w) => w[0]!.toUpperCase())
      .join('') || '?'
  )
}

/** What came of a lounge e-mail (§15.2), in the room's words. */
export function emailLine(e: S['LoungeEmailItem']): string {
  const how =
    e.outcome === 'done'
      ? 'sent'
      : e.outcome === 'proposed' || e.outcome === 'running'
        ? 'waiting for a person'
        : e.outcome === 'held-back'
          ? 'held back by a limit'
          : e.outcome === 'approved'
            ? 'approved'
            : `not sent: ${e.outcome}`
  return `E-mailed ${e.to} (${how})`
}

/** Where a position stands on the axis (§6.1): −1 at the left, +1 at the right, in percent. */
export const axisPercent = (position: number) => Math.round(((Math.max(-1, Math.min(1, position)) + 1) / 2) * 1000) / 10

/** A position in words (§6.1): certain, fairly sure, leaning or torn. */
export function strength(position: number): string {
  const a = Math.abs(position)
  return a >= 0.8 ? 'certain' : a >= 0.5 ? 'fairly sure' : a >= 0.2 ? 'leaning' : 'torn'
}

/** The state line in the header (§3.3, §15.2). */
export function stateLine(state: S['LoungeStateItem'], speaker: string | null): string {
  switch (state.state) {
    case 'Stopped':
      return 'closed'
    case 'Paused':
      return 'paused'
    case 'Asleep':
      return state.nextAt ? `asleep until ${timeOf(state.nextAt)}` : 'asleep'
    case 'Speaking':
      return speaker ? `${speaker} is typing…` : 'awake'
    default:
      return state.awakeUntil ? `awake until ${timeOf(state.awakeUntil)}` : 'awake'
  }
}
