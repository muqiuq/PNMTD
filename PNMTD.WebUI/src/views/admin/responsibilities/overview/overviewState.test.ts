import { describe, expect, it } from 'vitest'
import { focusFromQuery, layersFromQuery, layersToQuery, linesFromQuery, linesToQuery } from './overviewState'

const id = '0f8fad5b-d9cb-469f-a165-70867728950e'

describe("the overview's state in the URL (docs/responsibilities-extensions-plan.md §6.1)", () => {
  it('starts with people and notes, and writes nothing for them', () => {
    expect([...layersFromQuery({})]).toEqual(['people', 'notes'])
    expect(layersToQuery(new Set(['notes', 'people']))).toBeUndefined()
  })

  it('reads and writes the layers, also none of them', () => {
    expect([...layersFromQuery({ layers: 'hosts,people,sensors' })]).toEqual(['hosts', 'people'])
    expect(layersToQuery(new Set(['pages', 'people']))).toBe('people,pages')
    expect(layersFromQuery({ layers: '' }).size).toBe(0)
    expect(layersToQuery(new Set())).toBe('')
  })

  it('reads a focus on one of its kinds only', () => {
    expect(focusFromQuery({ focus: `responsibility:${id}` })).toBe(`responsibility:${id}`)
    expect(focusFromQuery({ focus: `router:${id}` })).toBeNull()
    expect(focusFromQuery({ focus: 'responsibility:42' })).toBeNull()
    expect(focusFromQuery({})).toBeNull()
  })
})

describe("the overview's lines in the URL (docs/responsibilities-mcp-and-map-plan.md §4.1)", () => {
  it('starts with the first in each escalation order, and writes nothing for it', () => {
    expect(linesFromQuery({})).toBe('responsible')
    expect(linesFromQuery({ lines: 'everyone' })).toBe('responsible')
    expect(linesToQuery('responsible')).toBeUndefined()
  })

  it('reads and writes Deputy and Both', () => {
    expect(linesFromQuery({ lines: 'deputy' })).toBe('deputy')
    expect(linesFromQuery({ lines: ['both'] })).toBe('both')
    expect(linesToQuery('both')).toBe('both')
  })
})
