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

const timeOnly = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })

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

/** The time of day with seconds, for logs whose date is shown once above them. */
export function formatTime(value: string | number | Date | null | undefined): string {
  const date = toDate(value)
  return date ? timeOnly.format(date) : '–'
}

export function formatDay(value: string | Date): string {
  const date = typeof value === 'string' ? new Date(`${value.slice(0, 10)}T12:00:00`) : value
  return dateOnly.format(date)
}

/** The day (local midnight) if it exists: not 31.11. or 29.2.2027. */
function existingDay(year: number, month: number, day: number): Date | null {
  const date = new Date(year, month - 1, day)
  return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day ? date : null
}

/**
 * A day typed by hand (docs/fill-again-typed-days-plan.md §3.2): 3.11.2027 or 03.11.2027; 3.11.27 for 2027; 3.11. or
 * 3.11 for the next 3 November on or after today; 2027-11-03. Null when the text isn't a day that exists.
 */
export function parseDay(text: string, today: Date): Date | null {
  const typed = text.trim()
  const iso = /^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(typed)
  if (iso) {
    return existingDay(Number(iso[1]), Number(iso[2]), Number(iso[3]))
  }
  const dotted = /^(\d{1,2})\.(\d{1,2})(?:\.(\d{4}|\d{2})?)?$/.exec(typed)
  if (!dotted) {
    return null
  }
  const day = Number(dotted[1])
  const month = Number(dotted[2])
  if (dotted[3]) {
    return existingDay(dotted[3].length === 2 ? 2000 + Number(dotted[3]) : Number(dotted[3]), month, day)
  }
  const start = new Date(today.getFullYear(), today.getMonth(), today.getDate())
  // This year's, or a later one's: 29.2. may be up to four years on.
  for (let year = start.getFullYear(); year <= start.getFullYear() + 4; year++) {
    const date = existingDay(year, month, day)
    if (date && date >= start) {
      return date
    }
  }
  return null
}

/** "3 d 4 h", "2 h 14 min", "5 min", "40 s": the two largest units, seconds only below a minute. */
export function formatDuration(ms: number): string {
  const seconds = Math.max(0, Math.round(ms / 1000))
  if (seconds < 60) {
    return `${seconds} s`
  }
  const units: [number, string][] = [
    [86_400, 'd'],
    [3_600, 'h'],
    [60, 'min'],
  ]
  const parts: string[] = []
  let rest = seconds
  for (const [size, unit] of units) {
    if (parts.length === 2) {
      break
    }
    const count = Math.floor(rest / size)
    if (count > 0) {
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
