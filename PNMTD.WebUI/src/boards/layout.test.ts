import { describe, expect, it } from 'vitest'
import { add, canAdd, elements, find, fontSize, move, newElement, newId, remove, setColumns, step } from './layout'
import type { BoardLayout } from './types'

/** docs/boards-plan.md §6.2, §6.3, §10.3: the editor's layout helpers. */
function layout(...columns: string[][]): BoardLayout {
  return {
    schemaVersion: 1,
    refreshSeconds: 30,
    look: {
      theme: 'default',
      fit: true,
      textScale: 100,
      motion: true,
      people: 'Names',
      weekNumber: false,
      messages: true,
    },
    header: { showTitle: true, title: null, clock: true, date: false, seconds: true },
    bar: { show: true, position: 'Top', size: 'Medium', greenText: null, flash: true, rules: [] },
    columns: columns.map((ids) => ({ width: 1, elements: ids.map((id) => ({ ...newElement('Clock'), id })) })),
  }
}
const ids = (l: BoardLayout) => l.columns.map((c) => c.elements.map((e) => e.id))

describe('board layout', () => {
  it('makes ids the server takes', () => {
    expect(newId()).toMatch(/^[a-z0-9]{8}$/)
    expect(new Set(Array.from({ length: 50 }, newId)).size).toBe(50)
  })

  it('moves the elements of columns taken away to the last one that stays, and adds empty ones', () => {
    const l = layout(['a'], ['b', 'c'], ['d'])
    expect(ids(setColumns(l, 1))).toEqual([['a', 'b', 'c', 'd']])
    expect(ids(setColumns(l, 2))).toEqual([['a'], ['b', 'c', 'd']])
    expect(ids(setColumns(l, 4))).toEqual([['a'], ['b', 'c'], ['d'], []])
    expect(elements(setColumns(l, 1))).toHaveLength(4)
  })

  it('moves an element within and between columns, and one step up or down', () => {
    const l = layout(['a', 'b', 'c'], ['d'])
    expect(ids(move(l, 'a', 1, 0))).toEqual([
      ['b', 'c'],
      ['a', 'd'],
    ])
    expect(ids(move(l, 'a', 0, 99))).toEqual([['b', 'c', 'a'], ['d']])
    expect(ids(step(l, 'b', -1))).toEqual([['b', 'a', 'c'], ['d']])
    expect(ids(step(l, 'c', 1))).toEqual([['a', 'b', 'c'], ['d']])
    expect(find(l, 'd')).toMatchObject({ column: 1, index: 0 })
    expect(ids(remove(l, 'b'))).toEqual([['a', 'c'], ['d']])
  })

  it('keeps a column to 12 elements and a board to 30', () => {
    let l = layout([])
    for (let i = 0; i < 12; i++) l = add(l, 0, newElement('Note'))
    expect(canAdd(l, 0)).toBe(false)
    l = setColumns(l, 3)
    for (let i = 0; i < 12; i++) l = add(l, 1, newElement('Note'))
    for (let i = 0; i < 6; i++) l = add(l, 2, newElement('Note'))
    expect(canAdd(l, 2)).toBe(false)
  })

  it('scales the text with the screen when the board fits the window', () => {
    expect(fontSize(true, 1080, 100)).toBe(16)
    expect(fontSize(true, 2160, 100)).toBe(32)
    expect(fontSize(true, 1080, 150)).toBe(24)
    expect(fontSize(false, 2160, 100)).toBe(16)
  })
})
