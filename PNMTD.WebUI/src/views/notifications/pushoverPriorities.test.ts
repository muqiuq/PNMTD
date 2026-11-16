import { describe, expect, it } from 'vitest'
import { draftOf, pushoverErrors, pushoverPriorities, requestOf, usesEmergency } from './pushoverPriorities'

const defaults = draftOf({
  pushoverLow: -1,
  pushoverNormal: 0,
  pushoverHigh: 1,
  emergencyRetrySeconds: 60,
  emergencyExpireSeconds: 3600,
  changedAt: null,
  changedBy: null,
})

describe('the Pushover priorities tab', () => {
  it('offers all five of Pushover', () => {
    expect(pushoverPriorities.map((p) => p.value)).toEqual([-2, -1, 0, 1, 2])
  })

  it('needs the repeat only when a row is an emergency', () => {
    expect(usesEmergency(defaults)).toBe(false)
    expect(usesEmergency({ ...defaults, pushoverHigh: 2 })).toBe(true)
    expect(pushoverErrors({ ...defaults, emergencyRetrySeconds: 1 })).toEqual({})
    expect(requestOf({ ...defaults, emergencyRetrySeconds: null }).emergencyRetrySeconds).toBe(60)
  })

  it("checks the repeat as the server does, with Pushover's limits", () => {
    const loud = { ...defaults, pushoverHigh: 2 }
    expect(pushoverErrors(loud)).toEqual({})
    expect(pushoverErrors({ ...loud, emergencyRetrySeconds: 10 })).toHaveProperty('emergencyRetrySeconds')
    expect(pushoverErrors({ ...loud, emergencyRetrySeconds: null })).toHaveProperty('emergencyRetrySeconds')
    expect(pushoverErrors({ ...loud, emergencyRetrySeconds: 600, emergencyExpireSeconds: 300 })).toHaveProperty(
      'emergencyExpireSeconds',
    )
    expect(pushoverErrors({ ...loud, emergencyExpireSeconds: 20000 })).toHaveProperty('emergencyExpireSeconds')
  })
})
