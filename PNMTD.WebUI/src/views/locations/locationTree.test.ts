import { describe, expect, it } from 'vitest'
import {
  filledText,
  matching,
  pickerOptions,
  relativePath,
  subtreeIds,
  toTree,
  type LocationItem,
} from './locationTree'

const item = (id: string, parentId: string | null, path: string, patch: Partial<LocationItem> = {}): LocationItem => ({
  id,
  parentId,
  name: path.split(' › ').at(-1)!,
  description: null,
  path,
  depth: path.split(' › ').length,
  state: 'Ok',
  hosts: { total: 0, failing: 0, own: 0 },
  subLocations: 0,
  fieldsFilled: 0,
  inheritedFields: [],
  updatedAt: '2026-11-14T09:00:00Z',
  updatedByName: null,
  ...patch,
})

// As GET /locations answers: depth-first.
const items = [
  item('zrh', null, 'Zurich DC', { description: 'The data centre' }),
  item('cage', 'zrh', 'Zurich DC › Cage B'),
  item('rack', 'cage', 'Zurich DC › Cage B › Rack 4'),
  item('office', 'zrh', 'Zurich DC › Office'),
  item('bern', null, 'Bern'),
]

describe('the locations tree', () => {
  it('nests the flat list in its order', () => {
    const tree = toTree(items)
    expect(tree.map((n) => n.key)).toEqual(['zrh', 'bern'])
    expect(tree[0]!.children.map((n) => n.key)).toEqual(['cage', 'office'])
    expect(tree[0]!.children[0]!.children[0]!.data.name).toBe('Rack 4')
  })

  it('puts a match whose parent is missing at the top', () => {
    expect(toTree([items[2]!]).map((n) => n.key)).toEqual(['rack'])
  })

  it('finds a subtree', () => {
    expect([...subtreeIds(items, 'zrh')].sort()).toEqual(['cage', 'office', 'rack', 'zrh'])
    expect([...subtreeIds(items, 'cage')].sort()).toEqual(['cage', 'rack'])
    expect(subtreeIds(items, 'nowhere').size).toBe(0)
  })

  it('names a sub-location relative to its ancestor', () => {
    expect(relativePath('Zurich DC › Cage B › Rack 4', 'Zurich DC')).toBe('Cage B › Rack 4')
    expect(relativePath('Zurich DC', 'Zurich DC')).toBe('')
    expect(relativePath('Zurich DCX › A', 'Zurich DC')).toBe('Zurich DCX › A')
  })

  it('leaves a location and its sub-locations out of its own move', () => {
    expect(pickerOptions(items, 'cage').map((o) => o.value)).toEqual(['zrh', 'office', 'bern'])
    expect(pickerOptions(items).map((o) => o.label)).toContain('Zurich DC › Cage B › Rack 4')
  })

  it('searches names and descriptions, flat', () => {
    expect(matching(items, 'centre').map((i) => i.id)).toEqual(['zrh'])
    expect(matching(items, 'b').map((i) => i.id)).toEqual(['cage', 'bern'])
    expect(matching(items, '  ').length).toBe(5)
  })

  it('counts the fields filled', () => {
    expect(filledText({ fieldsFilled: 6 }, 9)).toBe('6 of 9')
  })
})
