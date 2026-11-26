import { describe, expect, it } from 'vitest'
import type { ResponsibilityItem, ResponsibilityPerson } from '@/api/responsibilities'
import { formatSort, parseSort, sortItems } from './listSort'

const person = (name: string, active = true): ResponsibilityPerson => ({ id: name, name, active })

function item(name: string, more: Partial<ResponsibilityItem> = {}): ResponsibilityItem {
  return {
    id: name,
    name,
    summary: null,
    color: null,
    responsible: null,
    deputy: null,
    globalStandsIn: false,
    attention: false,
    escalation: [],
    links: 0,
    combinations: 0,
    updatedAt: '2026-11-20T10:00:00Z',
    updatedByName: null,
    ...more,
  }
}

const global = { responsible: person('Gabi Global'), deputy: null }
const names = (rows: ResponsibilityItem[]) => rows.map((r) => r.name)

describe('the list sorted by a column (docs/responsibilities-extensions-plan.md §3)', () => {
  it('reads and writes ?sort, and ignores what it does not know', () => {
    expect(parseSort('responsible')).toEqual({ field: 'responsible', order: 1 })
    expect(parseSort('-changed')).toEqual({ field: 'changed', order: -1 })
    expect(parseSort('summary')).toBeNull()
    expect(parseSort('')).toBeNull()
    expect(parseSort(['name'])).toBeNull()
    expect(formatSort({ field: 'links', order: -1 })).toBe('-links')
    expect(formatSort(null)).toBeUndefined()
  })

  it("keeps the server's order without a sort", () => {
    const rows = [item('Zeta'), item('Alpha')]
    expect(names(sortItems(rows, null, global))).toEqual(['Zeta', 'Alpha'])
  })

  it('sorts names as people read them, both ways', () => {
    const rows = [item('backup 10'), item('Backup 2'), item('alpha')]
    expect(names(sortItems(rows, { field: 'name', order: 1 }, global))).toEqual(['alpha', 'Backup 2', 'backup 10'])
    expect(names(sortItems(rows, { field: 'name', order: -1 }, global))).toEqual(['backup 10', 'Backup 2', 'alpha'])
  })

  it('puts nobody last in both directions, and sorts by the global responsible where they stand in', () => {
    const rows = [
      item('Printers', { globalStandsIn: true }),
      item('Network', { responsible: person('Anna') }),
      item('Backup'),
      item('Wi-Fi', { responsible: person('Zoe') }),
    ]
    // Backup names nobody, and isn't marked as standing in here: nobody, last.
    expect(names(sortItems(rows, { field: 'responsible', order: 1 }, global))).toEqual([
      'Network',
      'Printers',
      'Wi-Fi',
      'Backup',
    ])
    expect(names(sortItems(rows, { field: 'responsible', order: -1 }, global))).toEqual([
      'Wi-Fi',
      'Printers',
      'Network',
      'Backup',
    ])
    expect(names(sortItems(rows, { field: 'responsible', order: 1 }, { responsible: null, deputy: null }))).toEqual([
      'Network',
      'Wi-Fi',
      'Backup',
      'Printers',
    ])
  })

  it('sorts the deputy, with nobody last', () => {
    const rows = [item('A', { deputy: person('Ben') }), item('B'), item('C', { deputy: person('Anna') })]
    expect(names(sortItems(rows, { field: 'deputy', order: 1 }, global))).toEqual(['C', 'A', 'B'])
    expect(names(sortItems(rows, { field: 'deputy', order: -1 }, global))).toEqual(['A', 'C', 'B'])
  })

  it('sorts counts and the time of the last change, ties by name', () => {
    const rows = [
      item('Network', { links: 3, combinations: 1, updatedAt: '2026-11-22T10:00:00Z' }),
      item('Backup', { links: 3, combinations: 2, updatedAt: '2026-11-24T10:00:00Z' }),
      item('Printers', { links: 1, combinations: 0, updatedAt: '2026-11-20T10:00:00Z' }),
    ]
    expect(names(sortItems(rows, { field: 'links', order: 1 }, global))).toEqual(['Printers', 'Backup', 'Network'])
    expect(names(sortItems(rows, { field: 'links', order: -1 }, global))).toEqual(['Backup', 'Network', 'Printers'])
    expect(names(sortItems(rows, { field: 'redmine', order: -1 }, global))).toEqual(['Backup', 'Network', 'Printers'])
    expect(names(sortItems(rows, { field: 'changed', order: -1 }, global))).toEqual(['Backup', 'Network', 'Printers'])
  })
})
