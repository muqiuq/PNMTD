import { describe, expect, it } from 'vitest'
import { holidayProblems, minutesOf, nextPeriod, normalizeWeek, periodProblems } from './hours'

const p = (from: string, to: string) => ({ from, to })

describe('working hours (docs/cookie-plan.md §7.3), as the server checks them', () => {
  it('takes HH:mm, and 24:00 only as an end', () => {
    expect(minutesOf('08:30')).toBe(510)
    expect(minutesOf('8:30')).toBeNull()
    expect(minutesOf('24:00')).toBeNull()
    expect(minutesOf('24:00', true)).toBe(1440)
    expect(minutesOf('23:60')).toBeNull()
  })

  it('wants from before to, no overlaps, and at most 6 a day; touching periods are fine', () => {
    expect(periodProblems([p('08:00', '12:00'), p('12:00', '17:00')])).toEqual([])
    expect(periodProblems([p('13:00', '24:00'), p('08:00', '12:00')])).toEqual([])
    expect(periodProblems([p('12:00', '08:00')])).toEqual(['12:00–08:00: from and to as HH:mm, from before to.'])
    expect(periodProblems([p('08:00', '13:00'), p('12:00', '17:00')])).toEqual(['Periods may not overlap.'])
    const seven = Array.from({ length: 7 }, (_, i) => p(`0${i}:00`, `0${i}:30`))
    expect(periodProblems(seven)).toEqual(['At most 6 periods a day.'])
    expect(periodProblems([])).toEqual([])
  })

  it('wants each holiday on a date of its own', () => {
    const a = { date: '2026-12-24', name: 'Christmas Eve', periods: [p('08:00', '12:00')] }
    const b = { date: '2026-12-24', name: 'Again', periods: [] }
    expect(holidayProblems(a, [a])).toEqual([])
    expect(holidayProblems(a, [a, b])).toEqual(['Each date once.'])
    expect(holidayProblems({ date: '', name: '', periods: [] }, [])).toEqual(['Choose a date.'])
  })

  it('shows every weekday once, Monday first, a missing day closed', () => {
    const week = normalizeWeek([{ day: 'Sunday', periods: [p('10:00', '14:00')] }])
    expect(week.map((d) => d.day)).toEqual([
      'Monday',
      'Tuesday',
      'Wednesday',
      'Thursday',
      'Friday',
      'Saturday',
      'Sunday',
    ])
    expect(week[0].periods).toEqual([])
    expect(week[6].periods).toEqual([p('10:00', '14:00')])
  })

  it('suggests a period that fits after the last', () => {
    expect(nextPeriod([])).toEqual(p('08:00', '12:00'))
    expect(nextPeriod([p('08:00', '12:00')])).toEqual(p('13:00', '16:00'))
    expect(periodProblems([p('20:00', '22:30'), nextPeriod([p('20:00', '22:30')])])).toEqual([])
  })
})
