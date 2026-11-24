import type { components } from '@/api/schema'

/**
 * Board messages on a screen (docs/board-messages-plan.md §5, §6): each counts down from the seconds the server said were left when its
 * answer arrived, not from the screen's own clock; the most important kind first, then the newest; a new one shown at once for one turn.
 */
export type BoardMessageView = components['schemas']['BoardMessageView']
export type BoardMessageKind = components['schemas']['BoardMessageKind']

/** A message as this screen shows it: when it ends, in this screen's milliseconds. */
export interface ShownMessage extends BoardMessageView {
  endsAt: number
}

export const kindOrder: BoardMessageKind[] = ['Alert', 'Warning', 'Maintenance', 'Information', 'Success']

export const kindNames: Record<BoardMessageKind, string> = {
  Alert: 'Alert',
  Warning: 'Warning',
  Maintenance: 'Maintenance',
  Information: 'Information',
  Success: 'All clear',
}

/** An answer's messages, timed from when it arrived. */
export function received(messages: BoardMessageView[], at: number): ShownMessage[] {
  return messages.map((m) => ({ ...m, endsAt: at + m.remainingSeconds * 1000 }))
}

/** Those whose time isn't up, the most important kind first, then the newest. */
export function open(messages: ShownMessage[], now: number): ShownMessage[] {
  return messages
    .filter((m) => m.endsAt > now)
    .sort((a, b) => kindOrder.indexOf(a.kind) - kindOrder.indexOf(b.kind) || b.sentAt.localeCompare(a.sentAt))
}

/** The one after <paramref name="current"/> in the order; the first when it is gone. */
export function next(order: ShownMessage[], current: string | null): string | null {
  if (!order.length) return null
  const at = order.findIndex((m) => m.id === current)
  return order[(at + 1) % order.length]!.id
}

/** "0:42", "12:05", "1:00:00". */
export function countdown(ms: number): string {
  const total = Math.max(0, Math.ceil(ms / 1000))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  const ss = String(s).padStart(2, '0')
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${m}:${ss}`
}

/** Whether a kind puts "⚠" and its title before the tab's name. */
export const urgent = (kind: BoardMessageKind) => kind === 'Alert' || kind === 'Warning'
