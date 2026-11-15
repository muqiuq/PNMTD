import type { S } from '@/api/resources'

/** Cookie's trace (docs/cookie-plan.md §9.2): its kinds, and the entries by round as the window shows them. */

export type TraceItem = S['CookieTraceItem']

export const traceKinds = [
  'round',
  'triage',
  'screen',
  'reasoning',
  'tool',
  'note',
  'status',
  'summary',
  'reply',
  'access',
  'error',
] as const

const labels: Record<string, string> = {
  round: 'Round',
  triage: 'Triage',
  screen: 'Screening',
  reasoning: 'Thinking',
  tool: 'Tool',
  note: 'Note',
  status: 'Status',
  summary: 'Summary',
  reply: 'Reply',
  access: 'Access',
  error: 'Error',
}

export const traceKindLabel = (kind: string) => labels[kind] ?? kind

export const traceKindSeverity = (kind: string): 'danger' | 'warn' | 'info' | 'success' | 'secondary' =>
  kind === 'error'
    ? 'danger'
    : kind === 'screen'
      ? 'warn'
      : kind === 'note' || kind === 'reply' || kind === 'summary'
        ? 'success'
        : kind === 'status' || kind === 'triage' || kind === 'round'
          ? 'info'
          : 'secondary'

/** Long and for the curious: shown folded until opened. */
export const foldedKind = (kind: string) => kind === 'tool' || kind === 'reasoning'

/** New entries into the list: each id once, oldest first, so a poll's answer (newest first) can simply be added. */
export function mergeTrace(list: readonly TraceItem[], incoming: readonly TraceItem[]): TraceItem[] {
  const byId = new Map(list.map((t) => [t.id, t]))
  for (const t of incoming) byId.set(t.id, t)
  return [...byId.values()].sort((a, b) => a.id - b.id)
}

export interface RoundGroup {
  /** null: entries outside a round, such as a summary written for someone opening the dashboard. */
  roundId: number | null
  items: TraceItem[]
}

/** The entries by round, the newest round first; within a round in the order they happened, as Copilot shows a turn. */
export function byRound(items: readonly TraceItem[]): RoundGroup[] {
  const groups = new Map<number | null, TraceItem[]>()
  for (const t of [...items].sort((a, b) => a.id - b.id)) {
    const list = groups.get(t.roundId) ?? []
    list.push(t)
    groups.set(t.roundId, list)
  }
  return [...groups.entries()]
    .map(([roundId, list]) => ({ roundId, items: list }))
    .sort((a, b) => b.items[b.items.length - 1].id - a.items[a.items.length - 1].id)
}

/** The highest id seen, for the next poll's afterId. */
export const lastId = (items: readonly { id: number }[]) =>
  items.length ? Math.max(...items.map((i) => i.id)) : undefined
