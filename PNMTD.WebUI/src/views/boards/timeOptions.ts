import { boardTime, type TimeOptions } from '@/boards/time'
import type { BoardDateFormat, BoardDateLanguage, BoardTimeFormat } from '@/boards/types'

/** The editor's choices for times and dates (docs/board-messages-plan.md §9.1, §9.2), with examples drawn from now. */
export const timeFormats: { value: BoardTimeFormat; label: string }[] = [
  { value: 'Hours24', label: '24-hour' },
  { value: 'Hours12', label: '12-hour' },
  { value: 'Browser', label: "As the screen's browser" },
]

/** A clock's own choice: '' stands for "as the board" (PrimeVue's Select shows nothing for null). */
export const clockTimeFormats: { value: BoardTimeFormat | ''; label: string }[] = [
  { value: '', label: 'As the board' },
  { value: 'Hours24', label: '24-hour' },
  { value: 'Hours12', label: '12-hour' },
]

export const dateFormatValues: BoardDateFormat[] = [
  'WeekdayDayMonth',
  'Short',
  'DayMonthYear',
  'WeekdayDayMonthYear',
  'Dotted',
  'WeekdayDotted',
  'Iso',
  'Us',
]

export const dateLanguages: { value: BoardDateLanguage; label: string }[] = [
  { value: 'English', label: 'English' },
  { value: 'German', label: 'Deutsch' },
  { value: 'French', label: 'Français' },
  { value: 'Italian', label: 'Italiano' },
  { value: 'Browser', label: "As the screen's browser" },
]

/** Each format with today's date in it, in the board's language: "Monday, 24 November". */
export function dateFormats(zone: string, options: Partial<TimeOptions>): { value: BoardDateFormat; label: string }[] {
  const now = new Date()
  return dateFormatValues.map((value) => ({
    value,
    label: boardTime(zone, { ...options, dateFormat: value }).date(now),
  }))
}
