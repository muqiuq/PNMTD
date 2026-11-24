import { describe, expect, it, vi } from 'vitest'
import { boardTime, isoWeek, lookTime } from './time'
import type { BoardDateFormat } from './types'

/** docs/board-messages-plan.md §9.1, §9.2: the hour cycle, the date formats in four languages, and the ISO week. */
const zone = 'Europe/Zurich'
// Tuesday, 24 November 2026, 14:35:07 in Zurich (UTC+1).
const at = '2026-11-24T13:35:07Z'

describe('board time', () => {
  it('is 24-hour by default, also for a layout from 1.23', () => {
    expect(boardTime(zone).time(at)).toBe('14:35')
    expect(boardTime(zone, lookTime({})).time(at, true)).toBe('14:35:07')
    expect(boardTime(zone).time('2026-11-24T23:05:00Z')).toBe('00:05')
  })

  it('is 12-hour where asked, with the period apart for a clock', () => {
    const t = boardTime(zone, { timeFormat: 'Hours12' })
    expect(t.time(at)).toMatch(/^2:35\s?PM$/)
    expect(t.timeParts(at)).toEqual({ time: '2:35', period: 'PM' })
    expect(t.timeParts('2026-11-23T23:05:00Z').period).toBe('AM')
    expect(boardTime(zone).timeParts(at)).toEqual({ time: '14:35', period: '' })
  })

  it('shows the date in every format', () => {
    const date = (dateFormat: BoardDateFormat) => boardTime(zone, { dateFormat, dateLanguage: 'English' }).date(at)
    expect(date('WeekdayDayMonth')).toMatch(/^Tuesday,? 24 November$/)
    expect(date('Short')).toMatch(/^Tue,? 24 Nov$/)
    expect(date('DayMonthYear')).toBe('24 November 2026')
    expect(date('WeekdayDayMonthYear')).toMatch(/^Tuesday,? 24 November 2026$/)
    expect(date('Dotted')).toBe('24.11.2026')
    expect(date('WeekdayDotted')).toBe('Tue 24.11.2026')
    expect(date('Iso')).toBe('2026-11-24')
    expect(date('Us')).toBe('11/24/2026')
  })

  it('leaves the weekday out of the header for the default format only, as 1.23 did', () => {
    expect(boardTime(zone, { dateLanguage: 'English' }).date(at, true)).toBe('24 November')
    expect(boardTime(zone, { dateFormat: 'Dotted' }).date(at, true)).toBe('24.11.2026')
  })

  it('names days and months in four languages', () => {
    const date = (dateLanguage: 'German' | 'French' | 'Italian') =>
      boardTime(zone, { dateLanguage }).date(at).toLowerCase()
    expect(date('German')).toBe('dienstag, 24. november')
    expect(date('French')).toBe('mardi 24 novembre')
    expect(date('Italian')).toBe('martedì 24 novembre')
    expect(boardTime(zone, { dateLanguage: 'German', dateFormat: 'WeekdayDotted' }).date(at)).toMatch(
      /^Di\.? 24\.11\.2026$/,
    )
  })

  it('shows the day in PNMT zone, not the browser', () => {
    // 23:30 UTC is already the next day in Zurich.
    expect(boardTime(zone, { dateFormat: 'Iso' }).date('2026-11-24T23:30:00Z')).toBe('2026-11-25')
    expect(boardTime('UTC', { dateFormat: 'Iso' }).date('2026-11-24T23:30:00Z')).toBe('2026-11-24')
  })

  it('counts ISO weeks, with 53-week years and 1 January in the last year', () => {
    expect(isoWeek(2026, 11, 24)).toBe(48)
    expect(isoWeek(2026, 1, 1)).toBe(1)
    expect(isoWeek(2021, 1, 1)).toBe(53)
    expect(isoWeek(2023, 1, 1)).toBe(52)
    expect(isoWeek(2020, 12, 31)).toBe(53)
    expect(isoWeek(2024, 12, 30)).toBe(1)
    expect(boardTime(zone).week(at)).toBe(48)
  })

  it('says when: the time today, the weekday this week, else the short date', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date(at))
    try {
      const t = boardTime(zone, { dateLanguage: 'English' })
      expect(t.when('2026-11-24T08:00:00Z')).toBe('09:00')
      expect(t.when('2026-11-21T08:00:00Z')).toMatch(/^Sat 09:00$/)
      expect(t.when('2026-11-10T08:00:00Z')).toBe('10 Nov 09:00')
      expect(boardTime(zone, { dateFormat: 'Dotted' }).when('2026-11-10T08:00:00Z')).toBe('10.11. 09:00')
      expect(boardTime(zone, { dateFormat: 'Iso' }).when('2026-11-10T08:00:00Z')).toBe('2026-11-10 09:00')
    } finally {
      vi.useRealTimers()
    }
  })

  it('changes options and zones while keeping the rest', () => {
    const t = boardTime(zone, { timeFormat: 'Hours12', dateFormat: 'Iso' })
    expect(t.with({ timeFormat: 'Hours24' }).time(at)).toBe('14:35')
    const ny = t.with({ zone: 'America/New_York' })
    expect(ny.zone).toBe('America/New_York')
    expect(ny.timeParts(at)).toEqual({ time: '8:35', period: 'AM' })
    expect(ny.options.dateFormat).toBe('Iso')
  })
})
