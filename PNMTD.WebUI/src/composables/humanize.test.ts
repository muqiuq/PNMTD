import { describe, expect, it } from 'vitest'
import { humanize } from './useLookups'

describe('humanize', () => {
  it('turns a status code name into words people read', () => {
    expect(humanize('HEARTBEAT_MISSING')).toBe('Heartbeat missing')
    expect(humanize('SSL_ABOUT_TO_EXPIRE')).toBe('SSL about to expire')
    expect(humanize('PING_SUCCESSFULL')).toBe('Ping successful')
  })

  it('says licence and end of life (docs/licences-plan.md §Words)', () => {
    expect(humanize('LICENSE_ABOUT_TO_EXPIRE')).toBe('Licence about to expire')
    expect(humanize('LICENSE_EOL_SOON')).toBe('Licence end of life soon')
    expect(humanize('LICENSE_OVER_ASSIGNED')).toBe('Licence over assigned')
  })
})
