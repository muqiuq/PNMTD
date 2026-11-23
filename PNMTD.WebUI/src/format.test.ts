import { describe, expect, it } from 'vitest'
import { formatDuration, formatPercent, formatRelative, parseDay } from './format'

describe('formatting', () => {
  it('names durations by their two largest units', () => {
    expect(formatDuration(40_000)).toBe('40 s')
    expect(formatDuration(5 * 60_000 + 20_000)).toBe('5 min')
    expect(formatDuration((2 * 60 + 14) * 60_000)).toBe('2 h 14 min')
    expect(formatDuration((27 * 60 + 5) * 60_000)).toBe('1 d 3 h')
    expect(formatDuration(3 * 3_600_000)).toBe('3 h')
    expect(formatDuration(0)).toBe('0 s')
  })

  it('says how long ago, and the date beyond a month', () => {
    const now = Date.parse('2026-11-11T12:00:00Z')
    expect(formatRelative('2026-11-11T11:59:40Z', now)).toBe('just now')
    expect(formatRelative('2026-11-11T11:55:00Z', now)).toBe('5 min ago')
    expect(formatRelative('2026-11-11T09:00:00Z', now)).toBe('3 h ago')
    expect(formatRelative('2026-11-09T12:00:00Z', now)).toBe('2 d ago')
    expect(formatRelative('2026-11-11T12:10:00Z', now)).toBe('in 10 min')
    expect(formatRelative(null, now)).toBe('never')
    expect(formatRelative('2026-08-01T12:00:00Z', now)).not.toContain('ago')
  })

  it('shows 100 % only for full uptime', () => {
    expect(formatPercent(1)).toBe('100 %')
    expect(formatPercent(0.99999)).toBe('99.9 %')
    expect(formatPercent(0.9712)).toBe('97.1 %')
    expect(formatPercent(null)).toBe('–')
  })
})

describe('reading a typed day (docs/fill-again-typed-days-plan.md §3.2)', () => {
  const today = new Date(2026, 10, 23, 15, 30)
  const read = (text: string) => {
    const d = parseDay(text, today)
    return d && `${d.getFullYear()}-${d.getMonth() + 1}-${d.getDate()}`
  }

  it('reads the day, the month and the year with dots, and ISO', () => {
    expect(read('3.11.2027')).toBe('2027-11-3')
    expect(read('03.11.2027')).toBe('2027-11-3')
    expect(read('3.11.27')).toBe('2027-11-3')
    expect(read('2027-11-03')).toBe('2027-11-3')
    expect(read('  4.1.2028 ')).toBe('2028-1-4')
  })

  it('reads a day without a year as the next one on or after today', () => {
    expect(read('23.11.')).toBe('2026-11-23')
    expect(read('25.11')).toBe('2026-11-25')
    expect(read('22.11.')).toBe('2027-11-22')
    expect(read('3.1.')).toBe('2027-1-3')
    expect(read('29.2.')).toBe('2028-2-29')
  })

  it('gives local midnight', () => {
    const d = parseDay('3.11.2027', today)!
    expect([d.getHours(), d.getMinutes(), d.getSeconds()]).toEqual([0, 0, 0])
  })

  it('reads nothing that is not a day that exists', () => {
    for (const text of [
      '',
      '   ',
      '31.11.2027',
      '29.2.2027',
      '3.13.2027',
      '0.11.2027',
      '3.11.202',
      '3/11/2027',
      '3.11.2027 - 4.11.2027',
      'today',
      '2027-02-30',
    ]) {
      expect(parseDay(text, today), text).toBeNull()
    }
  })
})
