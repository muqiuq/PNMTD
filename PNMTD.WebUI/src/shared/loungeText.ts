/**
 * The lounge's text (docs/lounge-plan.md §15.2), shared by the admin UI and the board app (docs/boards-plan.md §7.6): references to a
 * case, host, sensor or ticket become chips, which never name the object.
 */

/** A part of a message: plain text, or a reference to a case, host, sensor or ticket shown as a chip. */
export type Segment =
  | { kind: 'text'; text: string }
  | { kind: 'case' | 'host' | 'sensor'; id: string; text: string }
  | { kind: 'ticket'; id: number; text: string }

const reference =
  /\[(case|host|sensor):([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})[^\]]*\]|\[(?:ticket|issue):#?(\d{1,9})\]|(?<![\w#])#(\d{2,9})\b/gi

const chipLabel: Record<'case' | 'host' | 'sensor', string> = { case: 'case', host: 'host', sensor: 'sensor' }

/** "[host:3f2c…]" and "#4821" as chips, the rest as text. */
export function segments(text: string): Segment[] {
  const parts: Segment[] = []
  let last = 0
  for (const m of text.matchAll(reference)) {
    const at = m.index ?? 0
    if (at > last) parts.push({ kind: 'text', text: text.slice(last, at) })
    if (m[1] && m[2]) {
      const kind = m[1].toLowerCase() as 'case' | 'host' | 'sensor'
      parts.push({ kind, id: m[2].toLowerCase(), text: chipLabel[kind] })
    } else {
      const n = Number(m[3] ?? m[4])
      parts.push({ kind: 'ticket', id: n, text: `#${n}` })
    }
    last = at + m[0].length
  }
  if (last < text.length) parts.push({ kind: 'text', text: text.slice(last) })
  return parts
}
