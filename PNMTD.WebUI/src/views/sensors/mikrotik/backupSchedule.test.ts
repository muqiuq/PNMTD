import { describe, expect, it } from 'vitest'
import { nextBackupAt, scheduleError, scheduleLabel } from './backupSchedule'

describe('backup schedule', () => {
  it('names off, the presets and other intervals', () => {
    expect(scheduleLabel(null)).toBe('Off')
    expect(scheduleLabel(24)).toBe('Daily')
    expect(scheduleLabel(720)).toBe('Monthly')
    expect(scheduleLabel(72)).toBe('Every 3 days')
    expect(scheduleLabel(12)).toBe('Every 12 hours')
  })

  it('takes off, or 1 hour to a year', () => {
    expect(scheduleError(null)).toBeUndefined()
    expect(scheduleError(8760)).toBeUndefined()
    expect(scheduleError(8761)).toBeTruthy()
    expect(scheduleError(0)).toBeTruthy()
    expect(scheduleError(undefined)).toBeTruthy()
  })

  it('is due an interval after the last export, never when off', () => {
    expect(nextBackupAt('2026-11-01T00:00:00Z', 720)?.toISOString()).toBe('2026-12-01T00:00:00.000Z')
    expect(nextBackupAt('2026-11-01T00:00:00Z', null)).toBeNull()
  })
})
