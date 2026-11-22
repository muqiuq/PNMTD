import type { KbRecentChange } from '@/api/kb'

/**
 * How a recent change reads (docs/knowledge-base-extensions-plan.md §6): what happened and who did it. The time, the lines
 * and the note are shown beside it.
 */
export function changeLine(c: Pick<KbRecentChange, 'kind' | 'detail' | 'byName'>): string {
  switch (c.kind) {
    case 'created':
      return `created by ${c.byName}`
    case 'restored':
      return `restored ${c.detail ?? ''} by ${c.byName}`.replace(/\s+/g, ' ')
    case 'status':
      return `status ${c.detail ?? 'changed'} by ${c.byName}`
    default:
      return c.detail ? `edited by ${c.byName}, status ${c.detail}` : `edited by ${c.byName}`
  }
}

/** "4 changes by 2 people": only when the page changed more than once in the period. */
export function moreLine(c: Pick<KbRecentChange, 'changes' | 'people'>): string | null {
  if (c.changes <= 1) return null
  return `${c.changes} changes by ${c.people} ${c.people === 1 ? 'person' : 'people'}`
}
