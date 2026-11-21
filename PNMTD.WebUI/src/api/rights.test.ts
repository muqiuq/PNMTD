import { describe, expect, it } from 'vitest'
import { holds } from './rights'

describe('holds', () => {
  it('lets Servers: Manage include Run, and Run not Manage (docs/linux-os-updates-plan.md §8.2, D2)', () => {
    expect(holds(false, { Servers: 'Manage' }, 'Servers', 'Run')).toBe(true)
    expect(holds(false, { Servers: 'Manage' }, 'Servers', 'Manage')).toBe(true)
    expect(holds(false, { Servers: 'Run' }, 'Servers', 'Run')).toBe(true)
    expect(holds(false, { Servers: 'Run' }, 'Servers', 'Manage')).toBe(false)
    expect(holds(false, {}, 'Servers', 'Run')).toBe(false)
  })

  it("never grants a level the area doesn't have, and the admin flag grants everything", () => {
    expect(holds(false, { Servers: 'Manage' }, 'Servers', 'View')).toBe(false)
    expect(holds(true, {}, 'Servers', 'Run')).toBe(true)
  })
})
