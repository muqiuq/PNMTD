import { describe, expect, it } from 'vitest'
import { periodProblems } from './hours'
import { currentErrors, partOf, sentParts, type SettingsParts } from './serverErrors'

type Period = { from: string; to: string }
const p = (from: string, to: string): Period => ({ from, to })

function parts(
  monday: Period[] = [p('08:00', '12:00'), p('11:30', '17:00')],
  tuesday: Period[] = [p('08:00', '17:00')],
) {
  const week = [
    { day: 'Monday', periods: monday },
    { day: 'Tuesday', periods: tuesday },
  ]
  const all: SettingsParts = {
    types: [{ code: 'ANWE' }, { code: 'HOME' }],
    countColumns: [{ id: 'c1' }],
    week,
    holidays: [{ date: '2026-12-24' }],
    globalFallback: { mode: 'none' },
    responsibleLabel: 'Responsible',
  }
  return { all, week }
}

describe("the server's errors on the plan's settings (docs/availability-extensions-plan.md §3.2)", () => {
  it('the reported periods are fine for the browser as for the server', () => {
    expect(periodProblems([p('08:00', '11:30'), p('13:00', '17:00')])).toEqual([])
    expect(periodProblems([p('13:00', '17:00'), p('08:00', '11:30')])).toEqual([])
  })

  it("hides an overlap message once the day's periods are fixed (before 1.28 it stayed until the next Save)", () => {
    const errors = { 'week.Monday': ['Periods may not overlap.'] }
    const sent = sentParts(errors, parts().all)
    expect(currentErrors(errors, sent, parts().all)).toEqual(errors)
    const fixed = parts([p('08:00', '11:30'), p('13:00', '17:00')])
    expect(currentErrors(errors, sent, fixed.all)).toEqual({})
    expect(periodProblems(fixed.week[0].periods)).toEqual([])
  })

  it("keeps a day's message while only another day changes", () => {
    const errors = { 'week.Monday': ['Periods may not overlap.'] }
    const sent = sentParts(errors, parts().all)
    expect(currentErrors(errors, sent, parts(undefined, [p('09:00', '17:00')]).all)).toEqual(errors)
  })

  it('names the part of each field', () => {
    const { all } = parts()
    expect(partOf(all, 'types.1')).toBe(JSON.stringify({ code: 'HOME' }))
    expect(partOf(all, 'countColumns.c1')).toBe(JSON.stringify({ id: 'c1' }))
    expect(partOf(all, 'holidays.2026-12-24')).toBe(JSON.stringify([{ date: '2026-12-24' }]))
    expect(partOf(all, 'responsibleLabel')).toBe('"Responsible"')
    expect(partOf(all, 'types')).toBe(JSON.stringify(all.types))
    expect(partOf(all, 'unknown')).toBe(JSON.stringify(all))
  })
})
