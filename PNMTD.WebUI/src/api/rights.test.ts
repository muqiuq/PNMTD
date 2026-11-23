import { describe, expect, it } from 'vitest'
import { holds, meets, type Area, type Level, type Right } from './rights'

describe('holds', () => {
  it('lets Servers: Manage include Run, and Run not Manage (docs/linux-os-updates-plan.md §8.2, D2)', () => {
    expect(holds(false, { Servers: 'Manage' }, 'Servers', 'Run')).toBe(true)
    expect(holds(false, { Servers: 'Manage' }, 'Servers', 'Manage')).toBe(true)
    expect(holds(false, { Servers: 'Run' }, 'Servers', 'Run')).toBe(true)
    expect(holds(false, { Servers: 'Run' }, 'Servers', 'Manage')).toBe(false)
    expect(holds(false, {}, 'Servers', 'Run')).toBe(false)
  })

  it('ranks Redmine: Manage above Link (docs/cookie-redmine-tab-plan.md §3.1)', () => {
    expect(holds(false, { Redmine: 'Manage' }, 'Redmine', 'Link')).toBe(true)
    expect(holds(false, { Redmine: 'Link' }, 'Redmine', 'Manage')).toBe(false)
  })

  it("never grants a level the area doesn't have, and the admin flag grants everything", () => {
    expect(holds(false, { Servers: 'Manage' }, 'Servers', 'View')).toBe(false)
    expect(holds(true, {}, 'Servers', 'Run')).toBe(true)
  })
})

describe('meets', () => {
  const cookieAdmin: Right = {
    anyOf: [
      ['Cookie', 'Manage'],
      ['Redmine', 'Manage'],
    ],
  }
  const of = (rights: Partial<Record<Area, Level>>) => (area: Area, level: Level) => holds(false, rights, area, level)

  it('opens Admin → Cookie for either right, and for neither not (docs/cookie-redmine-tab-plan.md §3.2)', () => {
    expect(meets(cookieAdmin, false, of({ Cookie: 'Manage' }))).toBe(true)
    expect(meets(cookieAdmin, false, of({ Redmine: 'Manage' }))).toBe(true)
    expect(meets(cookieAdmin, false, of({ Cookie: 'Instruct', Redmine: 'Link' }))).toBe(false)
  })

  it('keeps the single level and the admin flag as they were', () => {
    expect(meets(['Redmine', 'Link'], false, of({ Redmine: 'Manage' }))).toBe(true)
    expect(meets('admin', false, of({ Redmine: 'Manage' }))).toBe(false)
    expect(meets('admin', true, of({}))).toBe(true)
  })
})
