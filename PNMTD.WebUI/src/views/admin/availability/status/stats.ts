import type { S } from '@/api/resources'
import { addDays, flags, formatPlanDate, type Flag } from '../plan'

/**
 * The Status tab's arithmetic (docs/availability-extensions-plan.md §9, §10), holding no state: the server counts open days
 * per person, month and type, done and planned (§11); this sums them into a year or a month, into the types' meanings,
 * into weekday profiles and a year's calendar, and writes CSV. The server decides what counts.
 */

export type Stats = S['AvailabilityStatsItem']
export type StatsMonth = S['AvailabilityStatsMonth']
export type StatsType = S['AvailabilityTypeItem']
export type Tally = S['AvailabilityTally']

/** Until today (what was done), or the whole year (with what is planned). */
export type Scope = 'done' | 'all'

export const take = (t: Tally | { done: number; planned: number }, scope: Scope) =>
  scope === 'done' ? t.done : t.done + t.planned

/** What a table counts: a type, a meaning of the types' checkboxes, working days, days as responsible or fallback, empty days. */
export type Measure =
  | { kind: 'type'; typeId: string }
  | { kind: 'flag'; flag: Flag }
  | { kind: 'works' }
  | { kind: 'responsible' }
  | { kind: 'fallback' }
  | { kind: 'empty' }

/** A person's days of a measure in one month. */
export function countIn(
  month: StatsMonth,
  measure: Measure,
  types: ReadonlyMap<string, StatsType>,
  scope: Scope,
): number {
  switch (measure.kind) {
    case 'type':
      return month.types.filter((t) => t.typeId === measure.typeId).reduce((n, t) => n + take(t, scope), 0)
    case 'flag':
      return month.types
        .filter((t) => types.get(t.typeId)?.flags.includes(measure.flag))
        .reduce((n, t) => n + take(t, scope), 0)
    case 'works':
      return month.types.filter((t) => types.get(t.typeId)?.works).reduce((n, t) => n + take(t, scope), 0)
    case 'responsible':
      return take(month.responsible, scope)
    case 'fallback':
      return take(month.fallback, scope)
    case 'empty':
      return take(month.empty, scope)
  }
}

export const typesOf = (stats: Stats) => new Map(stats.types.map((t) => [t.id, t]))

/** A person's days of a measure over some months (1 to 12), or the whole year. */
export function sum(
  stats: Stats,
  userId: string,
  measure: Measure,
  scope: Scope,
  months: readonly number[] | 'year' = 'year',
  types = typesOf(stats),
): number {
  return stats.months
    .filter((m) => m.userId === userId && (months === 'year' || months.includes(m.month)))
    .reduce((n, m) => n + countIn(m, measure, types, scope), 0)
}

/** A choice of the By month table: its key for the select, its label, and what it counts. */
export interface MeasureChoice {
  key: string
  label: string
  measure: Measure
}

/** The measures to choose from: each type in use, the meanings, working days, the responsible as the settings call it, the fallback, empty. */
export function measureChoices(stats: Stats): MeasureChoice[] {
  const used = new Set(stats.months.flatMap((m) => m.types.map((t) => t.typeId)))
  const types = stats.types
    .filter((t) => !t.retired || used.has(t.id))
    .sort((a, b) => a.order - b.order)
    .map((t) => ({
      key: `type:${t.id}`,
      label: `${t.code} · ${t.name}`,
      measure: { kind: 'type', typeId: t.id } as Measure,
    }))
  return [
    ...types,
    ...flags.map((f) => ({
      key: `flag:${f.value}`,
      label: f.label,
      measure: { kind: 'flag', flag: f.value } as Measure,
    })),
    { key: 'works', label: 'Works', measure: { kind: 'works' } },
    { key: 'responsible', label: `As ${stats.responsibleLabel}`, measure: { kind: 'responsible' } },
    { key: 'fallback', label: 'As fallback', measure: { kind: 'fallback' } },
    { key: 'empty', label: 'Empty', measure: { kind: 'empty' } },
  ]
}

/** The types that have days in the year, in their order: the columns of the tables and the segments of the charts. */
export function typesInUse(stats: Stats, scope: Scope): StatsType[] {
  const days = new Map<string, number>()
  for (const m of stats.months) for (const t of m.types) days.set(t.typeId, (days.get(t.typeId) ?? 0) + take(t, scope))
  return stats.types.filter((t) => (days.get(t.id) ?? 0) > 0).sort((a, b) => a.order - b.order)
}

/** For each month, each type's person-days: everyone's, or one person's. */
export function byMonth(
  stats: Stats,
  scope: Scope,
  userId: string | null = null,
): { month: number; parts: { typeId: string; days: number }[] }[] {
  return Array.from({ length: 12 }, (_, i) => {
    const days = new Map<string, number>()
    for (const m of stats.months)
      if (m.month === i + 1 && (userId === null || m.userId === userId))
        for (const t of m.types) days.set(t.typeId, (days.get(t.typeId) ?? 0) + take(t, scope))
    return { month: i + 1, parts: [...days].map(([typeId, d]) => ({ typeId, days: d })).filter((p) => p.days > 0) }
  })
}

/** The share window the page shows: the year (until today, or whole), or the last 365 or 30 days. */
export type WindowKey = 'year' | 'last365' | 'last30'

export function windowOf(stats: Stats, key: WindowKey, scope: Scope): S['AvailabilityShareWindow'] | undefined {
  const name = key === 'year' ? (scope === 'done' ? 'yearToDate' : 'year') : key
  return stats.windows.find((w) => w.window === name)
}

/** The bars of the share chart: each deviation as a share of the largest one, -1 to 1, so the widest bar fills its half. */
export function deviationScale(rows: readonly { deviation: number }[]): number {
  return Math.max(1, ...rows.map((r) => Math.abs(r.deviation)))
}

/** "+1.6 d", "−0.4 d", "±0 d". */
export function signedDays(value: number): string {
  const r = Math.round(value * 10) / 10
  if (r === 0) return '±0 d'
  return `${r > 0 ? '+' : '−'}${Math.abs(r).toLocaleString('en-GB', { maximumFractionDigits: 1 })} d`
}

export const percent = (share: number) => `${Math.round(share * 100)} %`

const isoWeekday = (date: string) => ((new Date(`${date}T12:00:00Z`).getUTCDay() + 6) % 7) + 1

const weekdayNames = ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su']

/** The weekdays with open days in the year, Monday first (usually Monday to Friday). */
export function openWeekdays(stats: Stats): { day: number; label: string }[] {
  const present = new Set(stats.days.map((d) => isoWeekday(d.date)))
  return [1, 2, 3, 4, 5, 6, 7].filter((d) => present.has(d)).map((d) => ({ day: d, label: weekdayNames[d - 1] }))
}

/** A count column's average per weekday over the open days of the year: until today, or all of them. */
export function weekdayAverages(
  stats: Stats,
  column: number,
  scope: Scope,
): { day: number; label: string; average: number; days: number }[] {
  return openWeekdays(stats).map(({ day, label }) => {
    const days = stats.days.filter((d) => isoWeekday(d.date) === day && (scope === 'all' || d.date <= stats.today))
    const total = days.reduce((n, d) => n + (d.counts[column] ?? 0), 0)
    return { day, label, average: days.length ? total / days.length : 0, days: days.length }
  })
}

/** Each person's days as responsible per weekday, from the days' planned responsible. */
export function responsibleByWeekday(stats: Stats, scope: Scope): Map<string, Map<number, number>> {
  const out = new Map<string, Map<number, number>>()
  for (const d of stats.days) {
    if (!d.responsibleId || (scope === 'done' && d.date > stats.today)) continue
    const row = out.get(d.responsibleId) ?? new Map<number, number>()
    const day = isoWeekday(d.date)
    row.set(day, (row.get(day) ?? 0) + 1)
    out.set(d.responsibleId, row)
  }
  return out
}

/** A heatmap's step for a value, 1 to 7 of the sequential ramp; 0 for nothing. */
export function heatStep(value: number, max: number): number {
  if (value <= 0 || max <= 0) return 0
  return Math.max(1, Math.min(7, Math.ceil((value / max) * 7)))
}

/** A day of the year's calendar: its date, and the count of the column chosen, null when the day is closed. */
export interface CalendarDay {
  date: string
  value: number | null
  below: boolean
  future: boolean
}

/**
 * The year as weeks (columns) of the weekdays with open days (rows), Monday first. The first week starts on the Monday on or
 * before 1 January; days of the other years are left out (null).
 */
export function yearCalendar(
  stats: Stats,
  column: number,
): { weekdays: { day: number; label: string }[]; weeks: (CalendarDay | null)[][] } {
  const weekdays = openWeekdays(stats)
  const minimum = stats.columns[column]?.minimum
  const byDate = new Map(stats.days.map((d) => [d.date, d]))
  const first = `${stats.year}-01-01`
  const last = `${stats.year}-12-31`
  let monday = addDays(first, -(isoWeekday(first) - 1))
  const weeks: (CalendarDay | null)[][] = []
  while (monday <= last) {
    weeks.push(
      weekdays.map(({ day }) => {
        const date = addDays(monday, day - 1)
        if (date < first || date > last) return null
        const d = byDate.get(date)
        const value = d ? (d.counts[column] ?? 0) : null
        return {
          date,
          value,
          below: value !== null && minimum !== null && minimum !== undefined && value < minimum,
          future: date > stats.today,
        }
      }),
    )
    monday = addDays(monday, 7)
  }
  return { weekdays, weeks }
}

/** Who is away in the next 14 days, each with their days in order: "Mo 30.11. – We 2.12." runs of days next to each other. */
export function awayList(stats: Stats): { userId: string; days: { date: string; typeId: string }[] }[] {
  const byUser = new Map<string, { date: string; typeId: string }[]>()
  for (const a of stats.ahead?.away ?? [])
    byUser.set(a.userId, [...(byUser.get(a.userId) ?? []), { date: a.date, typeId: a.typeId }])
  const order = new Map(stats.people.map((p, i) => [p.id, i]))
  return [...byUser]
    .map(([userId, days]) => ({ userId, days: days.sort((a, b) => a.date.localeCompare(b.date)) }))
    .sort(
      (a, b) => a.days[0].date.localeCompare(b.days[0].date) || (order.get(a.userId) ?? 0) - (order.get(b.userId) ?? 0),
    )
}

/** A short day for the lists: "30.11.". */
export const shortDay = (date: string) => formatPlanDate(date).slice(0, 6)

/** CSV for Excel: semicolons, quotes where needed, CRLF, and a BOM so that umlauts survive. */
export function toCsv(rows: readonly (readonly (string | number)[])[]): string {
  const cell = (v: string | number) => {
    const text = String(v)
    return /[;"\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
  }
  return `\uFEFF${rows.map((r) => r.map(cell).join(';')).join('\r\n')}\r\n`
}

export const monthShort = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
