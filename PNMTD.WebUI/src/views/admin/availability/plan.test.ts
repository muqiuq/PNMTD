import { describe, expect, it } from 'vitest'
import {
  addDays,
  after,
  barShare,
  before,
  belowMinimum,
  clockLine,
  datesBetween,
  daysBetween,
  exactCode,
  fallbackWarning,
  fillDaysErrors,
  formatPlanDate,
  fullWeek,
  inSelection,
  inZone,
  mergeDays,
  monthLabel,
  movePoint,
  noteSummary,
  noteTime,
  planRows,
  responsibleWarning,
  selectionRect,
  shouldBeSet,
  textOn,
  typeAhead,
  works,
  type GridColumn,
  type PlanDay,
} from './plan'

const day = (date: string, patch: Partial<PlanDay> = {}): PlanDay => ({
  date,
  weekday: 'Mo',
  closed: false,
  holiday: null,
  hours: '08:00–17:00',
  responsibleId: null,
  fallbackId: null,
  comment: null,
  responsibleWorks: null,
  fallbackWorks: null,
  cells: [],
  counts: [],
  missing: 0,
  notes: [],
  ...patch,
})

describe('the availability plan (docs/cookie-plan.md §8.2, §9.4)', () => {
  it('counts dates in days, across summer time and the end of a year', () => {
    expect(addDays('2026-03-28', 1)).toBe('2026-03-29')
    expect(addDays('2026-03-29', 1)).toBe('2026-03-30')
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01')
    expect(addDays('2026-01-01', -1)).toBe('2025-12-31')
    expect(daysBetween('2026-10-24', '2026-10-26')).toBe(2)
    expect(daysBetween('2026-11-18', '2026-11-17')).toBe(-1)
    expect(datesBetween('2026-11-30', '2026-12-02')).toEqual(['2026-11-30', '2026-12-01', '2026-12-02'])
    expect(datesBetween('2026-12-02', '2026-11-30')).toEqual([])
    expect(formatPlanDate('2026-11-17')).toBe('17.11.2026')
  })

  it('loads the chunk before the first day and after the last, and merges the chunks into one list', () => {
    expect(before('2026-11-08', 60)).toEqual({ from: '2026-09-09', to: '2026-11-07' })
    expect(after('2027-01-07', 60)).toEqual({ from: '2027-01-08', to: '2027-03-08' })
    const merged = mergeDays([
      { days: [day('2026-11-03'), day('2026-11-04', { comment: 'old' })] },
      { days: [day('2026-11-01'), day('2026-11-02')] },
      { days: [day('2026-11-04', { comment: 'new' }), day('2026-11-05')] },
    ])
    expect(merged.map((d) => d.date)).toEqual(['2026-11-01', '2026-11-02', '2026-11-03', '2026-11-04', '2026-11-05'])
    expect(merged[3].comment).toBe('new')
  })

  it("puts a row with the month before each 1st, a year's row before 1 January, and none inside a month", () => {
    const rows = planRows(datesBetween('2026-12-30', '2027-02-02').map((d) => day(d)))
    const marks = rows.flatMap((r, i) => (r.kind === 'day' ? [] : [{ i, kind: r.kind, label: r.label }]))
    expect(marks).toEqual([
      { i: 2, kind: 'year', label: '2027' },
      { i: 3, kind: 'month', label: 'January 2027' },
      { i: 35, kind: 'month', label: 'February 2027' },
    ])
    expect(rows[4]).toMatchObject({ kind: 'day', key: '2027-01-01', date: '2027-01-01' })
    expect(rows.filter((r) => r.kind === 'day')).toHaveLength(35)
    expect(planRows([day('2026-11-15'), day('2026-11-16')]).map((r) => r.kind)).toEqual(['day', 'day'])
    // Loaded from inside January: neither the year's row nor the month's comes.
    expect(planRows([day('2027-01-15'), day('2027-01-16')]).map((r) => r.kind)).toEqual(['day', 'day'])
    expect(new Set(rows.map((r) => r.key)).size).toBe(rows.length)
    expect(monthLabel('2026-12-24')).toBe('December 2026')
  })

  it('names the responsible as the settings do, never in a plural (docs/availability-extensions-plan.md §7)', () => {
    expect(responsibleWarning(day('2026-11-17'), 'Hero')).toBe('Hero: nobody on a working day.')
    expect(responsibleWarning(day('2026-11-17', { responsibleId: 'a', responsibleWorks: false }), 'Hero')).toBe(
      "Hero: doesn't work that day.",
    )
  })

  it("shows a day's newest note and how many more, with times in PNMT's zone (§6)", () => {
    const note = (id: string, createdAt: string) => ({
      id,
      date: '2026-11-26',
      userId: 'u',
      author: 'Ben Keller',
      text: id,
      createdAt,
      changedAt: createdAt,
      mine: false,
    })
    expect(noteSummary([])).toBeNull()
    const summary = noteSummary([
      note('a', '2026-11-26T08:00:00Z'),
      note('b', '2026-11-26T09:30:00Z'),
      note('c', '2026-11-26T07:00:00Z'),
    ])
    expect(summary?.newest.id).toBe('b')
    expect(summary?.more).toBe(2)
    expect(noteTime('2026-11-26T13:32:00Z', 'Europe/Zurich')).toBe('26.11. 14:32')
    expect(noteTime('2026-07-01T13:32:00Z', 'Europe/Zurich')).toBe('1.7. 15:32')
  })

  it("tells the time in PNMT's zone, not the browser's (§3.3)", () => {
    const at = new Date('2026-11-26T13:32:10Z')
    expect(clockLine(at, 'Europe/Zurich')).toBe('Europe/Zurich · Thu 26.11.2026 · 14:32')
    expect(clockLine(at, 'UTC')).toBe('UTC · Thu 26.11.2026 · 13:32')
    expect(inZone(at, 'Europe/Zurich').offset).toBe('UTC+1')
    expect(inZone(at, 'UTC').offset).toBe('UTC')
    // Late in the evening in New York it is already the next day in Zurich.
    const late = new Date('2026-11-27T03:30:00Z')
    expect(inZone(late, 'America/New_York').date).toBe('2026-11-26')
    expect(inZone(late, 'Europe/Zurich').date).toBe('2026-11-27')
  })

  it('writes black on light colours and white on dark ones', () => {
    expect(textOn('#ffffff')).toBe('#000000')
    expect(textOn('#fde68a')).toBe('#000000')
    expect(textOn('#d9f2d9')).toBe('#000000')
    expect(textOn('#000000')).toBe('#ffffff')
    expect(textOn('#1e3a8a')).toBe('#ffffff')
    expect(textOn('#dc2626')).toBe('#ffffff')
    expect(textOn(null)).toBe('#000000')
    expect(textOn('red')).toBe('#000000')
  })

  it('reads the checkboxes as the server does: on site, travelling or at home, and not off', () => {
    expect(works(['OnSite'])).toBe(true)
    expect(works(['HomeOffice'])).toBe(true)
    expect(works(['WorkTravel'])).toBe(true)
    expect(works(['OnSite', 'Vacation'])).toBe(false)
    expect(works(['HomeOffice', 'Compensation'])).toBe(false)
    expect(works(['Vacation', 'Holiday'])).toBe(false)
    expect(works([])).toBe(false)
  })

  it('warns on a working day without a responsible, or with one who is off; closed days need nothing', () => {
    expect(responsibleWarning(day('2026-11-17'))).toBe('Responsible: nobody on a working day.')
    expect(responsibleWarning(day('2026-11-17', { responsibleId: 'a', responsibleWorks: true }))).toBeNull()
    expect(responsibleWarning(day('2026-11-17', { responsibleId: 'a', responsibleWorks: false }))).toMatch(
      /doesn't work/,
    )
    expect(responsibleWarning(day('2026-11-21', { closed: true }))).toBeNull()
    expect(fallbackWarning(day('2026-11-17'))).toBeNull()
    expect(fallbackWarning(day('2026-11-17', { fallbackId: 'b', fallbackWorks: false }))).toMatch(/doesn't work/)
    expect(fallbackWarning(day('2026-11-21', { closed: true, fallbackId: 'b', fallbackWorks: false }))).toBeNull()
  })

  it('flags a count below its minimum on working days only, and draws its bar against everyone', () => {
    const columns = [
      { id: 'c1', name: 'On site', minimum: 2 },
      { id: 'c2', name: 'Remote', minimum: null },
    ]
    expect(belowMinimum(day('2026-11-18', { counts: [1, 0] }), 0, columns)).toBe(true)
    expect(belowMinimum(day('2026-11-18', { counts: [2, 0] }), 0, columns)).toBe(false)
    expect(belowMinimum(day('2026-11-18', { counts: [1, 0] }), 1, columns)).toBe(false)
    expect(belowMinimum(day('2026-11-21', { closed: true, counts: [0, 0] }), 0, columns)).toBe(false)
    expect(barShare(3, 4)).toBe(75)
    expect(barShare(5, 4)).toBe(100)
    expect(barShare(1, 0)).toBe(0)
  })

  it('wants the cells of working days set from today on, for 365 days', () => {
    expect(shouldBeSet(day('2026-11-17'), '2026-11-17')).toBe(true)
    expect(shouldBeSet(day('2026-11-16'), '2026-11-17')).toBe(false)
    expect(shouldBeSet(day('2027-11-16'), '2026-11-17')).toBe(true)
    expect(shouldBeSet(day('2027-11-17'), '2026-11-17')).toBe(false)
    expect(shouldBeSet(day('2026-11-21', { closed: true }), '2026-11-17')).toBe(false)
  })
})

describe('selecting cells of the plan', () => {
  const columns: GridColumn[] = [
    { key: 'responsible', kind: 'staff' },
    { key: 'fallback', kind: 'staff' },
    { key: 'ann', kind: 'person' },
    { key: 'bob', kind: 'person' },
    { key: 'cid', kind: 'person' },
  ]

  it('covers the dates and columns between where it started and where it is, either way round', () => {
    const rect = selectionRect(
      { anchor: { date: '2026-11-20', col: 'cid' }, focus: { date: '2026-11-18', col: 'bob' } },
      columns,
    )
    expect(rect).toEqual({ from: '2026-11-18', to: '2026-11-20', cols: ['bob', 'cid'] })
    const one = selectionRect(
      { anchor: { date: '2026-11-18', col: 'ann' }, focus: { date: '2026-11-18', col: 'ann' } },
      columns,
    )
    expect(one).toEqual({ from: '2026-11-18', to: '2026-11-18', cols: ['ann'] })
  })

  it('stays within the kind of column it started in', () => {
    const rect = selectionRect(
      { anchor: { date: '2026-11-18', col: 'fallback' }, focus: { date: '2026-11-19', col: 'bob' } },
      columns,
    )
    expect(rect.cols).toEqual(['fallback'])
    const people = selectionRect(
      { anchor: { date: '2026-11-18', col: 'ann' }, focus: { date: '2026-11-19', col: 'responsible' } },
      columns,
    )
    expect(people.cols).toEqual(['ann'])
    const selection = { anchor: { date: '2026-11-18', col: 'ann' }, focus: { date: '2026-11-19', col: 'bob' } }
    expect(inSelection(selection, columns, '2026-11-19', 'bob')).toBe(true)
    expect(inSelection(selection, columns, '2026-11-20', 'bob')).toBe(false)
    expect(inSelection(selection, columns, '2026-11-18', 'cid')).toBe(false)
    expect(inSelection(null, columns, '2026-11-18', 'ann')).toBe(false)
  })

  it('moves with the arrow keys within the days loaded and the columns', () => {
    const p = { date: '2026-11-18', col: 'ann' }
    expect(movePoint(p, 1, 0, columns, '2026-11-01', '2026-11-30')).toEqual({ date: '2026-11-19', col: 'ann' })
    expect(movePoint(p, 0, 1, columns, '2026-11-01', '2026-11-30')).toEqual({ date: '2026-11-18', col: 'bob' })
    expect(movePoint(p, 0, 9, columns, '2026-11-01', '2026-11-30')).toEqual({ date: '2026-11-18', col: 'cid' })
    expect(movePoint(p, -30, 0, columns, '2026-11-01', '2026-11-30')).toEqual({ date: '2026-11-01', col: 'ann' })
  })
})

describe('typing a code into the plan', () => {
  const types = [{ code: 'ANWE' }, { code: 'HOME' }, { code: 'HO' }, { code: 'VACA' }, { code: 'UNAV' }]

  it('sets the type once its code is whole and no longer code goes on from it', () => {
    let state = typeAhead('', 'a', types)
    expect(state).toEqual({ typed: 'A', chosen: null })
    state = typeAhead(state.typed, 'n', types)
    state = typeAhead(state.typed, 'w', types)
    state = typeAhead(state.typed, 'e', types)
    expect(state).toEqual({ typed: '', chosen: { code: 'ANWE' } })
  })

  it('waits when a longer code goes on, and Enter takes the exact one', () => {
    let state = typeAhead('', 'h', types)
    state = typeAhead(state.typed, 'o', types)
    expect(state).toEqual({ typed: 'HO', chosen: null })
    expect(exactCode(state.typed, types)).toEqual({ code: 'HO' })
    state = typeAhead(state.typed, 'm', types)
    state = typeAhead(state.typed, 'e', types)
    expect(state.chosen).toEqual({ code: 'HOME' })
  })

  it('starts again with a key no code continues with, and forgets a key no code starts with', () => {
    expect(typeAhead('AN', 'v', types)).toEqual({ typed: 'V', chosen: null })
    expect(typeAhead('AN', 'x', types)).toEqual({ typed: '', chosen: null })
    expect(exactCode('', types)).toBeNull()
  })
})

describe("a person's default week (docs/cookie-plan.md §8.1)", () => {
  it('shows every weekday once, Monday first; a missing day works with the default', () => {
    const week = fullWeek([
      { day: 'Friday', works: true, typeId: 'home' },
      { day: 'Sunday', works: false },
    ])
    expect(week.map((d) => d.day)).toEqual([
      'Monday',
      'Tuesday',
      'Wednesday',
      'Thursday',
      'Friday',
      'Saturday',
      'Sunday',
    ])
    expect(week[0]).toEqual({ day: 'Monday', works: true, typeId: null })
    expect(week[4]).toEqual({ day: 'Friday', works: true, typeId: 'home' })
    expect(week[6]).toEqual({ day: 'Sunday', works: false, typeId: null })
  })
})

describe('the days of Fill again (docs/fill-again-typed-days-plan.md §3.4)', () => {
  const today = '2026-11-23'
  const at = (day: string | null, unread: string | null = null) => ({ day, unread })

  it('takes today to the 365th day, and one day as both', () => {
    expect(fillDaysErrors(at('2026-11-23'), at('2027-11-22'), today)).toEqual({})
    expect(fillDaysErrors(at('2027-03-01'), at('2027-03-01'), today)).toEqual({})
  })

  it('says why a day is refused, in the words of the server', () => {
    expect(fillDaysErrors(at(null, '3.13.2027'), at('2027-11-22'), today)).toEqual({
      from: `"3.13.2027" isn't a day: type 3.11.2027, 3.11.27 or 3.11.`,
    })
    expect(fillDaysErrors(at('2026-11-23'), at(null), today)).toEqual({ to: 'A day, please.' })
    expect(fillDaysErrors(at('2026-11-22'), at('2026-11-30'), today)).toEqual({ from: 'From today on.' })
    expect(fillDaysErrors(at('2026-11-23'), at('2027-11-23'), today)).toEqual({
      to: 'At most 365 days ahead: until 22.11.2027.',
    })
    expect(fillDaysErrors(at('2027-03-02'), at('2027-03-01'), today)).toEqual({ to: 'Not before From.' })
  })

  it('names each field on its own, and the order only when both are days', () => {
    expect(fillDaysErrors(at('2026-11-01'), at('2028-01-01'), today)).toEqual({
      from: 'From today on.',
      to: 'At most 365 days ahead: until 22.11.2027.',
    })
    expect(fillDaysErrors(at(null), at('2027-01-01'), today)).toEqual({ from: 'A day, please.' })
  })
})
