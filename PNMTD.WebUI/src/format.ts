/**
 * Formatting shared by the views. v1 timestamps are UTC instants (§3.7); they are shown in the
 * browser's zone, as relative times with the absolute one in a tooltip (§5.3).
 */

const absolute = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
})

const dateOnly = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric' })

export function toDate(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') {
    return null
  }
  const date = value instanceof Date ? value : new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

export function formatAbsolute(value: string | number | Date | null | undefined): string {
  const date = toDate(value)
  return date ? absolute.format(date) : '–'
}

export function formatDay(value: string | Date): string {
  const date = typeof value === 'string' ? new Date(`${value.slice(0, 10)}T12:00:00`) : value
  return dateOnly.format(date)
}

/** "3 d 4 h", "2 h 14 min", "5 min", "40 s": the two largest units. */
export function formatDuration(ms: number): string {
  const seconds = Math.max(0, Math.round(ms / 1000))
  const units: [number, string][] = [
    [86_400, 'd'],
    [3_600, 'h'],
    [60, 'min'],
    [1, 's'],
  ]
  const parts: string[] = []
  let rest = seconds
  for (const [size, unit] of units) {
    if (parts.length === 2) {
      break
    }
    const count = Math.floor(rest / size)
    if (count > 0 || (unit === 's' && parts.length === 0)) {
      parts.push(`${count} ${unit}`)
      rest -= count * size
    } else if (parts.length > 0) {
      break
    }
  }
  return parts.join(' ')
}

/** "just now", "5 min ago", "in 3 h", "2 d ago"; beyond 30 days the date. */
export function formatRelative(value: string | number | Date | null | undefined, now: number): string {
  const date = toDate(value)
  if (!date) {
    return 'never'
  }
  const diff = now - date.getTime()
  const abs = Math.abs(diff)
  if (abs < 45_000) {
    return 'just now'
  }
  if (abs > 30 * 86_400_000) {
    return dateOnly.format(date)
  }
  const amount =
    abs < 3_600_000
      ? `${Math.round(abs / 60_000)} min`
      : abs < 86_400_000
        ? `${Math.round(abs / 3_600_000)} h`
        : `${Math.round(abs / 86_400_000)} d`
  return diff >= 0 ? `${amount} ago` : `in ${amount}`
}

/** A fraction from 0 to 1 as "99.9 %" (100 % only when it is 1). */
export function formatPercent(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined) {
    return '–'
  }
  const percent = value * 100
  const shown = percent >= 100 ? '100' : Math.min(percent, 100 - 10 ** -digits).toFixed(digits)
  return `${shown} %`
}
