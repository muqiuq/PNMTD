import { describe, expect, it } from 'vitest'
import {
  awayList,
  byMonth,
  deviationScale,
  heatStep,
  measureChoices,
  responsibleByWeekday,
  signedDays,
  sum,
  toCsv,
  typesInUse,
  weekdayAverages,
  windowOf,
  yearCalendar,
  type Stats,
} from './stats'

const type = (id: string, code: string, flags: string[], works: boolean, order: number) => ({
  id,
  name: code,
  code,
  color: '#d9f2d9',
  flags: flags as never,
  isDefault: false,
  isDayOff: false,
  order,
  retired: false,
  works,
})
const tally = (done: number, planned = 0) => ({ done, planned })
const month = (
  userId: string,
  m: number,
  types: { typeId: string; done: number; planned: number }[],
  responsible = tally(0),
) => ({
  userId,
  month: m,
  types,
  empty: tally(0),
  responsible,
  fallback: tally(0),
})

const stats: Stats = {
  year: 2026,
  firstYear: 2025,
  today: '2026-11-26',
  timeZone: 'Europe/Zurich',
  responsibleLabel: 'Hero',
  types: [
    type('anwe', 'ANWE', ['OnSite'], true, 0),
    type('home', 'HOME', ['HomeOffice'], true, 1),
    type('vaca', 'VACA', ['Vacation'], false, 2),
    type('both', 'FERI', ['Vacation', 'Holiday'], false, 3),
  ],
  columns: [{ id: 'c1', name: 'On site', minimum: 2 }],
  people: [
    { id: 'a', name: 'Anna', code: 'ANN', color: null, share: 100, target: 0.6667 },
    { id: 'b', name: 'Ben', code: 'BEN', color: null, share: 50, target: 0.3333 },
  ],
  months: [
    month(
      'a',
      11,
      [
        { typeId: 'anwe', done: 10, planned: 2 },
        { typeId: 'vaca', done: 0, planned: 3 },
        { typeId: 'both', done: 1, planned: 0 },
      ],
      tally(4, 1),
    ),
    month('a', 12, [{ typeId: 'home', done: 0, planned: 5 }]),
    month('b', 11, [{ typeId: 'home', done: 7, planned: 1 }], tally(2)),
  ],
  windows: [
    {
      window: 'yearToDate',
      from: '2026-01-01',
      to: '2026-11-26',
      openDays: 230,
      total: 6,
      others: 0,
      withoutResponsible: 3,
      people: [],
    },
    {
      window: 'year',
      from: '2026-01-01',
      to: '2026-12-31',
      openDays: 255,
      total: 7,
      others: 1,
      withoutResponsible: 3,
      people: [],
    },
  ],
  days: [
    { date: '2026-11-23', counts: [1], responsibleId: 'a' }, // a Monday
    { date: '2026-11-24', counts: [3], responsibleId: 'a' },
    { date: '2026-11-30', counts: [5], responsibleId: 'b' }, // the next Monday, still to come
    { date: '2026-01-01', counts: [0], responsibleId: null }, // a Thursday
  ],
  ahead: {
    from: '2026-11-26',
    to: '2026-12-25',
    withoutResponsible: 1,
    firstWithoutResponsible: '2026-12-01',
    belowMinimum: 0,
    firstBelowMinimum: null,
    emptyCells: 0,
    firstEmpty: null,
    away: [
      { userId: 'b', date: '2026-12-02', typeId: 'vaca' },
      { userId: 'a', date: '2026-11-30', typeId: 'vaca' },
      { userId: 'b', date: '2026-12-01', typeId: 'vaca' },
    ],
  },
}

describe('the Status tab (docs/availability-extensions-plan.md §9, §10)', () => {
  it('sums a person’s days by type, by meaning and as responsible, until today or with what is planned', () => {
    expect(sum(stats, 'a', { kind: 'type', typeId: 'anwe' }, 'done')).toBe(10)
    expect(sum(stats, 'a', { kind: 'type', typeId: 'anwe' }, 'all')).toBe(12)
    // A type with two checkboxes counts under each meaning.
    expect(sum(stats, 'a', { kind: 'flag', flag: 'Vacation' }, 'all')).toBe(4)
    expect(sum(stats, 'a', { kind: 'flag', flag: 'Holiday' }, 'all')).toBe(1)
    expect(sum(stats, 'a', { kind: 'works' }, 'all')).toBe(17)
    expect(sum(stats, 'a', { kind: 'works' }, 'all', [12])).toBe(5)
    expect(sum(stats, 'a', { kind: 'responsible' }, 'done')).toBe(4)
    expect(sum(stats, 'a', { kind: 'responsible' }, 'all')).toBe(5)
  })

  it('offers the types, the meanings and the responsible as the settings call it', () => {
    const labels = measureChoices(stats).map((c) => c.label)
    expect(labels.slice(0, 4)).toEqual(['ANWE · ANWE', 'HOME · HOME', 'VACA · VACA', 'FERI · FERI'])
    expect(labels).toContain('Vacation')
    expect(labels).toContain('As Hero')
    expect(typesInUse(stats, 'done').map((t) => t.code)).toEqual(['ANWE', 'HOME', 'FERI'])
  })

  it('stacks the team’s months by type, or one person’s', () => {
    const all = byMonth(stats, 'all')
    expect(all).toHaveLength(12)
    expect(all[10].parts).toEqual([
      { typeId: 'anwe', days: 12 },
      { typeId: 'vaca', days: 3 },
      { typeId: 'both', days: 1 },
      { typeId: 'home', days: 8 },
    ])
    expect(byMonth(stats, 'done', 'b')[10].parts).toEqual([{ typeId: 'home', days: 7 }])
  })

  it('picks the year until today or whole, and scales the deviations', () => {
    expect(windowOf(stats, 'year', 'done')?.window).toBe('yearToDate')
    expect(windowOf(stats, 'year', 'all')?.window).toBe('year')
    expect(deviationScale([{ deviation: -2.5 }, { deviation: 1 }])).toBe(2.5)
    expect(deviationScale([])).toBe(1)
    expect(signedDays(1.64)).toBe('+1.6 d')
    expect(signedDays(-0.44)).toBe('−0.4 d')
    expect(signedDays(0.02)).toBe('±0 d')
  })

  it('averages a count column per weekday, and counts the responsible per weekday', () => {
    const until = weekdayAverages(stats, 0, 'done')
    expect(until.map((w) => w.label)).toEqual(['Mo', 'Tu', 'Th'])
    expect(until[0]).toMatchObject({ label: 'Mo', average: 1, days: 1 })
    expect(weekdayAverages(stats, 0, 'all')[0]).toMatchObject({ average: 3, days: 2 })
    const byDay = responsibleByWeekday(stats, 'all')
    expect(byDay.get('a')?.get(1)).toBe(1)
    expect(byDay.get('a')?.get(2)).toBe(1)
    expect(byDay.get('b')?.get(1)).toBe(1)
    expect(responsibleByWeekday(stats, 'done').get('b')).toBeUndefined()
  })

  it('lays the year out in weeks of its open weekdays, a closed day empty and a low count marked', () => {
    const { weekdays, weeks } = yearCalendar(stats, 0)
    expect(weekdays.map((w) => w.label)).toEqual(['Mo', 'Tu', 'Th'])
    expect(weeks).toHaveLength(53)
    // 1 January 2026 is a Thursday: the first week's Monday and Tuesday are in 2025.
    expect(weeks[0][0]).toBeNull()
    expect(weeks[0][2]).toMatchObject({ date: '2026-01-01', value: 0, below: true })
    const monday = weeks.flat().find((d) => d?.date === '2026-11-23')
    expect(monday).toMatchObject({ value: 1, below: true, future: false })
    expect(weeks.flat().find((d) => d?.date === '2026-11-25')).toBeUndefined()
    expect(heatStep(0, 5)).toBe(0)
    expect(heatStep(5, 5)).toBe(7)
    expect(heatStep(1, 5)).toBe(2)
  })

  it('lists who is away, in order of their first day', () => {
    expect(awayList(stats)).toEqual([
      { userId: 'a', days: [{ date: '2026-11-30', typeId: 'vaca' }] },
      {
        userId: 'b',
        days: [
          { date: '2026-12-01', typeId: 'vaca' },
          { date: '2026-12-02', typeId: 'vaca' },
        ],
      },
    ])
  })

  it('writes CSV for Excel', () => {
    expect(
      toCsv([
        ['Person', 'ANWE'],
        ['Anna; "A"', 12],
      ]),
    ).toBe('\uFEFFPerson;ANWE\r\n"Anna; ""A""";12\r\n')
  })
})
