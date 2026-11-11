/** The colour of an uptime value (§5.7): ≥ 0.99 green, ≥ 0.9 amber, below red, no data grey. */
export type UptimeTone = 'ok' | 'warn' | 'fail' | 'none'

export function uptimeTone(value: number | null | undefined): UptimeTone {
  if (value === null || value === undefined) {
    return 'none'
  }
  return value >= 0.99 ? 'ok' : value >= 0.9 ? 'warn' : 'fail'
}

/**
 * How many of the newest values fit at `minWidth` px each (§5.5): at least `minimum`, at most all.
 * The newest is always included: the bar ends today (or this hour).
 */
export function visibleCount(width: number, total: number, minWidth = 3, minimum = 30): number {
  if (total <= 0) {
    return 0
  }
  const fits = width > 0 ? Math.floor(width / minWidth) : total
  return Math.min(total, Math.max(Math.min(minimum, total), fits))
}

/** The date of the i-th value of a series that starts at `start` (yyyy-mm-dd), as yyyy-mm-dd. */
export function dayOf(start: string, index: number): string {
  const date = new Date(`${start.slice(0, 10)}T00:00:00Z`)
  date.setUTCDate(date.getUTCDate() + index)
  return date.toISOString().slice(0, 10)
}
