import type { S } from '@/api/resources'

/**
 * Working hours and holidays (docs/cookie-plan.md §7.3) as the availability plan's settings edit them (revision 7; Admin →
 * Cookie before), with the server's checks (PNMTD/Availability/AvailabilitySettings.cs, Validate) mirrored so a mistake
 * shows while typing. The server decides.
 */

export const weekdays: S['DayOfWeek'][] = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']

export const maxPeriods = 6

const hhmm = /^([01]\d|2[0-3]):[0-5]\d$/

/** "08:30" as minutes of the day; "24:00" only as the end of a period; null when it isn't HH:mm. */
export function minutesOf(value: string, end = false): number | null {
  const v = value.trim()
  if (end && v === '24:00') return 24 * 60
  if (!hhmm.test(v)) return null
  const [h, m] = v.split(':').map(Number)
  return h * 60 + m
}

/** What is wrong with a day's periods, in the server's words; none when they can be saved. */
export function periodProblems(periods: readonly S['HoursPeriod'][]): string[] {
  const problems: string[] = []
  if (periods.length > maxPeriods) problems.push(`At most ${maxPeriods} periods a day.`)
  const parsed: { from: number; to: number }[] = []
  for (const p of periods) {
    const from = minutesOf(p.from)
    const to = minutesOf(p.to, true)
    if (from === null || to === null || from >= to) {
      problems.push(`${p.from || '?'}–${p.to || '?'}: from and to as HH:mm, from before to.`)
      continue
    }
    parsed.push({ from, to })
  }
  parsed.sort((a, b) => a.from - b.from)
  if (parsed.some((p, i) => i > 0 && parsed[i - 1].to > p.from)) problems.push('Periods may not overlap.')
  return problems
}

/** What is wrong with a holiday besides its periods: its date, a date twice, a long name. */
export function holidayProblems(holiday: S['Holiday'], all: readonly S['Holiday'][]): string[] {
  const problems: string[] = []
  if (!/^\d{4}-\d{2}-\d{2}$/.test(holiday.date)) problems.push('Choose a date.')
  else if (all.filter((h) => h.date === holiday.date).length > 1) problems.push('Each date once.')
  if (holiday.name.length > 100) problems.push('At most 100 characters.')
  return [...problems, ...periodProblems(holiday.periods)]
}

/** The week as the tab shows it: every day once, Monday first; a day the settings leave out is closed. */
export function normalizeWeek(week: readonly S['DayHours'][]): S['DayHours'][] {
  return weekdays.map((day) => ({ day, periods: [...(week.find((d) => d.day === day)?.periods ?? [])] }))
}

/** The periods as sent: trimmed, in order of their start. */
export function tidyPeriods(periods: readonly S['HoursPeriod'][]): S['HoursPeriod'][] {
  return periods
    .map((p) => ({ from: p.from.trim(), to: p.to.trim() }))
    .sort((a, b) => (minutesOf(a.from) ?? 0) - (minutesOf(b.from) ?? 0))
}

/** The next period after the last: an hour after it ends, or 08:00–12:00 on a closed day. */
export function nextPeriod(periods: readonly S['HoursPeriod'][]): S['HoursPeriod'] {
  const ends = periods.map((p) => minutesOf(p.to, true)).filter((m): m is number => m !== null)
  const last = ends.length ? Math.max(...ends) : null
  if (last === null || last + 60 >= 24 * 60) return { from: '08:00', to: '12:00' }
  const at = (m: number) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`
  return { from: at(last + 60), to: at(Math.min(24 * 60 - 1, last + 240)) }
}
