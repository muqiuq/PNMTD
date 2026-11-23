import { describe, expect, it } from 'vitest'
import { combinationLabel, personLabel, standsInLabel, type ResponsibilityGlobal } from './responsibilities'

const anna = { id: 'a', name: 'Anna Muster', active: true }
const ben = { id: 'b', name: 'Ben Keller', active: true }
const global = (responsible: typeof anna | null, deputy: typeof anna | null): ResponsibilityGlobal => ({
  responsible,
  deputy,
  updatedAt: '2026-11-23T10:00:00Z',
  updatedByName: null,
})

describe('personLabel', () => {
  it('names a person, marks a deactivated one, and says nobody without one (docs/responsibilities-plan.md §4.2)', () => {
    expect(personLabel(anna)).toBe('Anna Muster')
    expect(personLabel({ ...anna, active: false })).toBe('Anna Muster (inactive)')
    expect(personLabel(null)).toBe('Nobody')
  })
})

describe('standsInLabel', () => {
  it('says who of the global people stands in, never "the responsible" alone (§2.2, §5)', () => {
    expect(standsInLabel(global(anna, ben))).toBe('Global responsible stands in: Anna Muster, deputy Ben Keller')
    expect(standsInLabel(global(anna, null))).toBe('Global responsible stands in: Anna Muster')
    expect(standsInLabel(global(null, ben))).toBe('Global deputy stands in: Ben Keller')
    expect(standsInLabel(global(null, null))).toBe('Nobody: no global responsible is set')
  })
})

describe('combinationLabel', () => {
  it('names a project with its tracker or every tracker (§6.1)', () => {
    expect(combinationLabel({ projectPath: 'IT Support › Printers', trackerId: null })).toBe(
      'IT Support › Printers · every tracker',
    )
    expect(combinationLabel({ projectPath: 'Helpdesk', trackerId: 1, trackerName: 'Hardware' })).toBe(
      'Helpdesk · Hardware',
    )
    expect(combinationLabel({ projectPath: 'Helpdesk', trackerId: 7, trackerName: null })).toBe('Helpdesk · #7')
  })
})
