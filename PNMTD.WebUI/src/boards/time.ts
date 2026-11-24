import { ref } from 'vue'
import { formatDuration } from '@/format'
import type { BoardDateFormat, BoardDateLanguage, BoardTimeFormat } from './types'

/**
 * Times on a board (docs/boards-plan.md §6.4): in PNMT's time zone, which the view names, not the browser's (a wall's browser often
 * runs in UTC). A zone the browser doesn't know falls back to its own. The hour cycle, the date's format, the week number and the
 * language of day and month names are the board's (docs/board-messages-plan.md §9.1, §9.2); a clock may override the first two.
 */
export interface TimeOptions {
  timeFormat: BoardTimeFormat
  dateFormat: BoardDateFormat
  dateLanguage: BoardDateLanguage
  weekNumber: boolean
}

export interface BoardTime {
  /** "14:35", "2:35 PM", with seconds where asked. */
  time: (at: string | Date | null | undefined, seconds?: boolean) => string
  /** The time and its "AM"/"PM" apart, for a clock that draws the latter smaller. */
  timeParts: (at: string | Date, seconds?: boolean) => { time: string; period: string }
  /**
   * The date in the board's format. In the header line the default format leaves the weekday out, as 1.23 did ("24 November"); every
   * other format shows as chosen.
   */
  date: (at: string | Date, header?: boolean) => string
  /** The day's name in the board's language: "Monday", "Montag". */
  weekday: (at: string | Date) => string
  /** The ISO 8601 week, in PNMT's zone. */
  week: (at: string | Date) => number
  /** "13:51" today, "Mon 13:51" in the last six days, else "24 Nov 13:51" (or the format's short form). */
  when: (at: string | null | undefined) => string
  /** The same zone with some options changed: a clock's own time or date format, another zone's clock with the board's options. */
  with: (changes: Partial<TimeOptions> & { zone?: string }) => BoardTime
  zone: string
  options: TimeOptions
}

/** 1.23's look but 24-hour (decision 6): what a layout without these settings shows. */
export const defaultTimeOptions: TimeOptions = {
  timeFormat: 'Hours24',
  dateFormat: 'WeekdayDayMonth',
  dateLanguage: 'Browser',
  weekNumber: false,
}

/** English in day-month order ("Monday, 24 November"); the US format is a format of its own. */
const locales: Record<BoardDateLanguage, string | undefined> = {
  English: 'en-GB',
  German: 'de',
  French: 'fr',
  Italian: 'it',
  Browser: undefined,
}

const formats = new Map<string, Intl.DateTimeFormat>()

function format(zone: string, locale: string | undefined, options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  const key = `${zone}|${locale ?? ''}|${JSON.stringify(options)}`
  let f = formats.get(key)
  if (!f) {
    try {
      f = new Intl.DateTimeFormat(locale, { ...options, timeZone: zone })
    } catch {
      f = new Intl.DateTimeFormat(locale, options)
    }
    formats.set(key, f)
  }
  return f
}

const toDate = (at: string | Date) => (typeof at === 'string' ? new Date(at) : at)

/** The day in PNMT's zone as numbers, whatever the browser's digits. */
function ymd(zone: string, d: Date): { y: number; m: number; d: number } {
  const parts = format(zone, 'en-US', { year: 'numeric', month: 'numeric', day: 'numeric' }).formatToParts(d)
  const n = (type: string) => Number(parts.find((p) => p.type === type)?.value ?? 0)
  return { y: n('year'), m: n('month'), d: n('day') }
}

/** ISO 8601: weeks start on Monday, and week 1 holds the year's first Thursday. */
export function isoWeek(y: number, m: number, d: number): number {
  const date = new Date(Date.UTC(y, m - 1, d))
  const weekday = date.getUTCDay() || 7
  date.setUTCDate(date.getUTCDate() + 4 - weekday)
  const start = Date.UTC(date.getUTCFullYear(), 0, 1)
  return Math.ceil(((date.getTime() - start) / 86_400_000 + 1) / 7)
}

const pad = (n: number) => String(n).padStart(2, '0')

export function boardTime(zone: string, options: Partial<TimeOptions> = {}): BoardTime {
  const o: TimeOptions = { ...defaultTimeOptions, ...options }
  const locale = locales[o.dateLanguage]
  const cycle: Intl.DateTimeFormatOptions =
    o.timeFormat === 'Hours24'
      ? { hourCycle: 'h23', hour: '2-digit' }
      : o.timeFormat === 'Hours12'
        ? { hourCycle: 'h12', hour: 'numeric' }
        : { hour: '2-digit' }
  const timeFormat = (seconds: boolean) =>
    format(zone, undefined, { ...cycle, minute: '2-digit', ...(seconds ? { second: '2-digit' } : {}) })
  const names = (options: Intl.DateTimeFormatOptions, d: Date) => format(zone, locale, options).format(d)
  const numeric = (d: Date) => ymd(zone, d)

  function date(at: string | Date, header = false): string {
    const d = toDate(at)
    switch (o.dateFormat) {
      case 'Short':
        return names({ weekday: 'short', day: 'numeric', month: 'short' }, d)
      case 'DayMonthYear':
        return names({ day: 'numeric', month: 'long', year: 'numeric' }, d)
      case 'WeekdayDayMonthYear':
        return names({ weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }, d)
      case 'Dotted': {
        const n = numeric(d)
        return `${pad(n.d)}.${pad(n.m)}.${n.y}`
      }
      case 'WeekdayDotted': {
        const n = numeric(d)
        return `${names({ weekday: 'short' }, d)} ${pad(n.d)}.${pad(n.m)}.${n.y}`
      }
      case 'Iso': {
        const n = numeric(d)
        return `${n.y}-${pad(n.m)}-${pad(n.d)}`
      }
      case 'Us': {
        const n = numeric(d)
        return `${pad(n.m)}/${pad(n.d)}/${n.y}`
      }
      default:
        return header
          ? names({ day: 'numeric', month: 'long' }, d)
          : names({ weekday: 'long', day: 'numeric', month: 'long' }, d)
    }
  }

  /** A list's date for something older than six days: short, in the format's style. */
  function shortDate(d: Date): string {
    const n = numeric(d)
    switch (o.dateFormat) {
      case 'Dotted':
      case 'WeekdayDotted':
        return `${pad(n.d)}.${pad(n.m)}.`
      case 'Iso':
        return `${n.y}-${pad(n.m)}-${pad(n.d)}`
      case 'Us':
        return `${pad(n.m)}/${pad(n.d)}`
      default:
        return names({ day: 'numeric', month: 'short' }, d)
    }
  }

  const time = (at: string | Date | null | undefined, seconds = false) =>
    at ? timeFormat(seconds).format(toDate(at)) : ''

  return {
    zone,
    options: o,
    time,
    timeParts: (at, seconds = false) => {
      const parts = timeFormat(seconds).formatToParts(toDate(at))
      const period = parts.find((p) => p.type === 'dayPeriod')?.value ?? ''
      if (!period) return { time: parts.map((p) => p.value).join(''), period: '' }
      return {
        time: parts
          .filter((p) => p.type !== 'dayPeriod')
          .map((p) => p.value)
          .join('')
          .trim(),
        period,
      }
    },
    date,
    weekday: (at) => names({ weekday: 'long' }, toDate(at)),
    week: (at) => {
      const n = numeric(toDate(at))
      return isoWeek(n.y, n.m, n.d)
    },
    when: (at) => {
      if (!at) return ''
      const d = new Date(at)
      if (Number.isNaN(d.getTime())) return ''
      const t = time(d)
      const day = (x: Date) => {
        const n = numeric(x)
        return Date.UTC(n.y, n.m - 1, n.d)
      }
      const days = Math.round((day(new Date()) - day(d)) / 86_400_000)
      if (days === 0) return t
      if (days > 0 && days < 7) return `${names({ weekday: 'short' }, d)} ${t}`
      return `${shortDate(d)} ${t}`
    },
    with: ({ zone: other, ...changes }) => boardTime(other ?? zone, { ...o, ...changes }),
  }
}

/** The board's options from its layout: what a 1.23 layout lacks reads as {@link defaultTimeOptions}. */
export function lookTime(look: {
  timeFormat?: BoardTimeFormat
  dateFormat?: BoardDateFormat
  dateLanguage?: BoardDateLanguage
  weekNumber?: boolean
}): TimeOptions {
  return {
    timeFormat: look.timeFormat ?? defaultTimeOptions.timeFormat,
    dateFormat: look.dateFormat ?? defaultTimeOptions.dateFormat,
    dateLanguage: look.dateLanguage ?? defaultTimeOptions.dateLanguage,
    weekNumber: look.weekNumber ?? defaultTimeOptions.weekNumber,
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
