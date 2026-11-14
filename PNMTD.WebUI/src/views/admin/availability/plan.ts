import type { S } from '@/api/resources'
import { weekdays } from './hours'

/**
 * The availability plan's arithmetic (docs/cookie-plan.md §8.2, §9.4) that holds no state: dates as yyyy-MM-dd, the days
 * loaded in chunks, a selection of cells, a code typed into it, the colours, and the warnings. The server decides; this
 * only shows.
 */

export type PlanDay = S['AvailabilityDayItem']
export type PlanType = S['AvailabilityTypeItem']
export type Flag = S['AvailabilityFlag']

/** How many days the plan loads at a time when it scrolls into the past or the future. */
export const chunkDays = 60

/** The plan is filled this many days ahead, from today on: an empty cell there is a warning. */
export const fillDays = 365

const day = 86_400_000

const toTime = (iso: string) => Date.parse(`${iso}T00:00:00Z`)

/** A date (yyyy-MM-dd) n days later (or earlier); in UTC, so a change to summer time never skips a day. */
export function addDays(iso: string, n: number): string {
  return new Date(toTime(iso) + n * day).toISOString().slice(0, 10)
}

/** The days from one date to another: 1 from Monday to Tuesday. */
export function daysBetween(from: string, to: string): number {
  return Math.round((toTime(to) - toTime(from)) / day)
}

/** Every date from one to another, both included. */
export function datesBetween(from: string, to: string): string[] {
  const n = daysBetween(from, to)
  return n < 0 ? [] : Array.from({ length: n + 1 }, (_, i) => addDays(from, i))
}

/** 17.11.2026, as the team's Excel plan writes it. */
export function formatPlanDate(iso: string): string {
  const [y, m, d] = iso.split('-')
  return `${d}.${m}.${y}`
}

/** The chunk before the first day loaded, and the one after the last. */
export const before = (from: string, n = chunkDays) => ({ from: addDays(from, -n), to: addDays(from, -1) })
export const after = (to: string, n = chunkDays) => ({ from: addDays(to, 1), to: addDays(to, n) })

/** The days of every chunk loaded, each date once (the later chunk's when two overlap), in order. */
export function mergeDays(pages: readonly { days: readonly PlanDay[] }[]): PlanDay[] {
  const byDate = new Map<string, PlanDay>()
  for (const page of pages) for (const d of page.days) byDate.set(d.date, d)
  return [...byDate.values()].sort((a, b) => a.date.localeCompare(b.date))
}

// --- colours --------------------------------------------------------------------------------------------------------

export const isColour = (value: string | null | undefined): value is string => !!value && /^#[0-9a-f]{6}$/i.test(value)

/** WCAG's relative luminance of #rrggbb. */
function luminance(hex: string): number {
  const channel = (i: number) => {
    const c = Number.parseInt(hex.slice(i, i + 2), 16) / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  }
  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5)
}

/** Black or white, whichever reads better on the colour (the higher contrast); black when it isn't a colour. */
export function textOn(background: string | null | undefined): '#000000' | '#ffffff' {
  if (!isColour(background)) return '#000000'
  const l = luminance(background)
  return (l + 0.05) / 0.05 >= 1.05 / (l + 0.05) ? '#000000' : '#ffffff'
}

/** A code shown on its colour (a type's, a person's): the background and a text colour that reads on it. */
export function chipStyle(colour: string | null | undefined): Record<string, string> {
  return isColour(colour) ? { background: colour, color: textOn(colour) } : {}
}

// --- what a type means ----------------------------------------------------------------------------------------------

export const flags: { value: Flag; label: string }[] = [
  { value: 'Vacation', label: 'Vacation' },
  { value: 'Holiday', label: 'Holiday' },
  { value: 'OnSite', label: 'On site' },
  { value: 'WorkTravel', label: 'Work travel' },
  { value: 'HomeOffice', label: 'Home office' },
  { value: 'Compensation', label: 'Compensation' },
]

export const flagLabel = (f: Flag) => flags.find((x) => x.value === f)?.label ?? f

/** Whether a person works on a day of a type with these checkboxes (§8.2), as the server reads them. */
export function works(checked: readonly Flag[]): boolean {
  const has = (f: Flag) => checked.includes(f)
  return (
    (has('OnSite') || has('WorkTravel') || has('HomeOffice')) &&
    !(has('Vacation') || has('Holiday') || has('Compensation'))
  )
}

// --- the warnings of §8.2 -------------------------------------------------------------------------------------------

/** Why a day's responsible is red: nobody on a working day, or someone who doesn't work that day. Closed days need nothing. */
export function responsibleWarning(d: PlanDay): string | null {
  if (d.closed) return null
  if (!d.responsibleId) return 'Nobody is responsible on a working day.'
  return d.responsibleWorks === false ? "The responsible doesn't work that day." : null
}

export function fallbackWarning(d: PlanDay): string | null {
  return !d.closed && d.fallbackId && d.fallbackWorks === false ? "The fallback doesn't work that day." : null
}

/** A count below its column's minimum, on a working day. */
export function belowMinimum(d: PlanDay, index: number, columns: readonly S['AvailabilityColumnItem'][]): boolean {
  const minimum = columns[index]?.minimum
  return !d.closed && minimum !== null && minimum !== undefined && (d.counts[index] ?? 0) < minimum
}

/** An empty cell that should be set: a working day from today on, within the 365 days the plan is filled. */
export function shouldBeSet(d: PlanDay, today: string): boolean {
  return !d.closed && d.date >= today && d.date < addDays(today, fillDays)
}

/** A count's bar, as a share of everyone in the plan (like Excel's data bars), 0 to 100. */
export function barShare(count: number, people: number): number {
  return people > 0 ? Math.max(0, Math.min(100, Math.round((count / people) * 100))) : 0
}

// --- selecting cells ------------------------------------------------------------------------------------------------

/** A column one can select in: the responsible and the fallback hold a person, the people's columns a type. */
export interface GridColumn {
  key: string
  kind: 'staff' | 'person'
}

/** A cell, by its date and its column's key, so that loading earlier days doesn't move it. */
export interface GridPoint {
  date: string
  col: string
}

/** From where the selection started to where it is now: a range in a column, or a block of columns of one kind. */
export interface GridSelection {
  anchor: GridPoint
  focus: GridPoint
}

/** The dates and columns a selection covers: only the columns of the kind it started in. */
export function selectionRect(
  selection: GridSelection,
  columns: readonly GridColumn[],
): { from: string; to: string; cols: string[] } {
  const { anchor, focus } = selection
  const from = anchor.date <= focus.date ? anchor.date : focus.date
  const to = anchor.date <= focus.date ? focus.date : anchor.date
  const a = columns.findIndex((c) => c.key === anchor.col)
  if (a < 0) return { from, to, cols: [] }
  const f = columns.findIndex((c) => c.key === focus.col)
  const [left, right] = f < 0 ? [a, a] : [Math.min(a, f), Math.max(a, f)]
  const kind = columns[a].kind
  return {
    from,
    to,
    cols: columns
      .slice(left, right + 1)
      .filter((c) => c.kind === kind)
      .map((c) => c.key),
  }
}

export function inSelection(
  selection: GridSelection | null,
  columns: readonly GridColumn[],
  date: string,
  col: string,
): boolean {
  if (!selection) return false
  const rect = selectionRect(selection, columns)
  return date >= rect.from && date <= rect.to && rect.cols.includes(col)
}

/** A point moved by arrow keys: a row up or down within the days loaded, a column left or right within the grid. */
export function movePoint(
  point: GridPoint,
  rows: number,
  cols: number,
  columns: readonly GridColumn[],
  first: string,
  last: string,
): GridPoint {
  const date = addDays(point.date, rows)
  const index = columns.findIndex((c) => c.key === point.col)
  const col = columns[Math.max(0, Math.min(columns.length - 1, index + cols))]?.key ?? point.col
  return { date: date < first ? first : date > last ? last : date, col }
}

// --- typing a code --------------------------------------------------------------------------------------------------

/**
 * A key typed into a selection: the code so far, and the item to set once the code is whole (an exact match no other
 * code goes on from). A key no code continues with starts a new code; a key no code starts with clears it.
 */
export function typeAhead<T extends { code: string }>(
  typed: string,
  key: string,
  items: readonly T[],
): { typed: string; chosen: T | null } {
  const starts = (prefix: string) => items.some((i) => i.code.toUpperCase().startsWith(prefix))
  let next = (typed + key).toUpperCase()
  if (!starts(next)) {
    next = key.toUpperCase()
    if (!starts(next)) return { typed: '', chosen: null }
  }
  const exact = items.find((i) => i.code.toUpperCase() === next) ?? null
  const longer = items.some((i) => i.code.toUpperCase() !== next && i.code.toUpperCase().startsWith(next))
  return exact && !longer ? { typed: '', chosen: exact } : { typed: next, chosen: null }
}

/** The item whose code is exactly what was typed (Enter takes it, even when a longer code goes on from it). */
export const exactCode = <T extends { code: string }>(typed: string, items: readonly T[]): T | null =>
  typed ? (items.find((i) => i.code.toUpperCase() === typed.toUpperCase()) ?? null) : null

// --- the default week -----------------------------------------------------------------------------------------------

export const weekdayShort = (d: S['DayOfWeek']) => d.slice(0, 2)

/** A default week with every weekday once, Monday first; a weekday it lacks works with the default type. */
export function fullWeek(week: readonly S['DefaultDay'][] | null | undefined): S['DefaultDay'][] {
  return weekdays.map((d) => {
    const found = week?.find((w) => w.day === d)
    return { day: d, works: found?.works ?? true, typeId: found?.typeId ?? null }
  })
}

/** A person's place in the plan as a form edits it (§8.1): the People tab and Admin → Users. */
export interface PlanDraft {
  inPlan: boolean
  shortCode: string
  color: string | null
  defaultWeek: S['DefaultDay'][]
  planOrder: number
}

export function planDraftOf(p: {
  inPlan: boolean
  shortCode: string | null
  color: string | null
  defaultWeek: readonly S['DefaultDay'][]
  planOrder: number
}): PlanDraft {
  return {
    inPlan: p.inPlan,
    shortCode: p.shortCode ?? '',
    color: p.color,
    defaultWeek: fullWeek(p.defaultWeek),
    planOrder: p.planOrder,
  }
}

/** As sent: the code trimmed and in capitals, none as null; a day that isn't worked has no type. */
export function planRequest(d: PlanDraft): S['AvailabilityPersonRequest'] {
  return {
    inPlan: d.inPlan,
    shortCode: d.shortCode.trim() ? d.shortCode.trim().toUpperCase() : null,
    color: d.color?.trim() ? d.color.trim().toLowerCase() : null,
    defaultWeek: d.defaultWeek.map((w) => ({
      day: w.day,
      works: w.works,
      typeId: w.works ? (w.typeId ?? null) : null,
    })),
    planOrder: d.planOrder ?? 0,
  }
}
