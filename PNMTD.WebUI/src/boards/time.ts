import { ref } from 'vue'
import { formatDuration } from '@/format'

/**
 * Times on a board (docs/boards-plan.md §6.4): in PNMT's time zone, which the view names, not the browser's (a wall's browser often
 * runs in UTC). A zone the browser doesn't know falls back to its own.
 */
export interface BoardTime {
  time: (at: string | null | undefined, seconds?: boolean) => string
  date: (at: string | Date, weekday?: boolean) => string
  /** "13:51", or "Mon 13:51" when not today. */
  when: (at: string | null | undefined) => string
  zone: string
}

const formats = new Map<string, Intl.DateTimeFormat>()

function format(zone: string, options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  const key = zone + JSON.stringify(options)
  let f = formats.get(key)
  if (!f) {
    try {
      f = new Intl.DateTimeFormat(undefined, { ...options, timeZone: zone })
    } catch {
      f = new Intl.DateTimeFormat(undefined, options)
    }
    formats.set(key, f)
  }
  return f
}

export function boardTime(zone: string): BoardTime {
  const day = (d: Date) => format(zone, { year: 'numeric', month: '2-digit', day: '2-digit' }).format(d)
  return {
    zone,
    time: (at, seconds = false) =>
      at
        ? format(zone, { hour: '2-digit', minute: '2-digit', ...(seconds ? { second: '2-digit' } : {}) }).format(
            new Date(at),
          )
        : '',
    date: (at, weekday = true) =>
      format(zone, { ...(weekday ? { weekday: 'long' } : {}), day: 'numeric', month: 'long' }).format(
        typeof at === 'string' ? new Date(at) : at,
      ),
    when: (at) => {
      if (!at) return ''
      const d = new Date(at)
      const time = format(zone, { hour: '2-digit', minute: '2-digit' }).format(d)
      return day(d) === day(new Date()) ? time : `${format(zone, { weekday: 'short' }).format(d)} ${time}`
    },
  }
}

/** "12 min", "3 h 5 min": how long since, or between two times. */
export function since(at: string | null | undefined, now: number): string {
  return at ? formatDuration(now - new Date(at).getTime()) : ''
}

export function between(from: string, to: string): string {
  return formatDuration(new Date(to).getTime() - new Date(from).getTime())
}

/** Now, every ten seconds, for the relative times of every element on a board. */
export const boardNow = ref(Date.now())
if (typeof window !== 'undefined') {
  setInterval(() => (boardNow.value = Date.now()), 10_000)
}
