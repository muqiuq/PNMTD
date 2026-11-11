import { describe, expect, it } from 'vitest'
import { formatDuration, formatPercent, formatRelative } from './format'

describe('formatting', () => {
  it('names durations by their two largest units', () => {
    expect(formatDuration(40_000)).toBe('40 s')
    expect(formatDuration(5 * 60_000 + 20_000)).toBe('5 min 20 s')
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
