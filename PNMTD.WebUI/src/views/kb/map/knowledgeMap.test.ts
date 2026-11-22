import { describe, expect, it } from 'vitest'
import { edgeKey, mapFromQuery, mapToApi, mapToQuery, nodeLabel } from './knowledgeMap'

describe('the knowledge map', () => {
  const id = '0193f3c2-7d1e-7c55-9a35-8e2f1a6b4c90'

  it('reads its defaults and round-trips a shared view', () => {
    expect(mapFromQuery({})).toEqual({
      focus: null,
      depth: 2,
      tags: [],
      location: null,
      status: null,
      ai: null,
      archived: false,
      unlinked: false,
      circles: false,
    })
    const whole = { tags: 'backup,zrh', location: id, status: 'Draft', ai: 'true', unlinked: 'true', circles: 'true' }
    expect(mapToQuery(mapFromQuery(whole))).toEqual(whole)
    const around = { focus: 'page:42', depth: '3', archived: 'true' }
    expect(mapToQuery(mapFromQuery(around))).toEqual(around)
  })

  it('sends the filters only without a focus, and drops what it does not know', () => {
    expect(mapToApi(mapFromQuery({ focus: `host:${id}`, tags: 'x' }))).toEqual({
      focus: `host:${id}`,
      depth: 2,
      archived: undefined,
    })
    expect(mapToApi(mapFromQuery({ tags: '#Backup' }))).toMatchObject({ tags: 'backup' })
    expect(mapFromQuery({ focus: 'page:abc', depth: '9', status: 'Gone' })).toMatchObject({
      focus: null,
      depth: 2,
      status: null,
    })
  })

  it('labels pages with their number and cuts long names', () => {
    expect(nodeLabel({ kind: 'Page', name: 'Printer setup', number: 42 })).toBe('KB-42 Printer setup')
    expect(nodeLabel({ kind: 'Host', name: 'a'.repeat(40), number: null })).toHaveLength(32)
    expect(edgeKey({ kind: 'PageLink', source: 'page:a', target: 'page:b' })).toBe('PageLink|page:a|page:b')
  })
})
