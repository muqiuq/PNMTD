import { describe, expect, it } from 'vitest'
import { fromQuery, toApi, toggleTag, toQuery } from './listState'

describe('the knowledge base list in the URL', () => {
  it('reads the defaults from an empty query', () => {
    expect(fromQuery({})).toEqual({
      q: '',
      tags: [],
      status: null,
      location: null,
      host: null,
      sensor: null,
      archived: false,
      sensitive: null,
      ai: null,
      sort: null,
      period: '30d',
      page: 1,
    })
    expect(toQuery(fromQuery({}))).toEqual({})
  })

  it('round-trips a shared list', () => {
    const query = {
      tags: 'backup,zrh',
      sort: 'views',
      period: 'all',
      status: 'Draft',
      archived: 'true',
      sensitive: 'false',
      ai: 'true',
      page: '3',
      q: 'ups',
    }
    expect(toQuery(fromQuery(query))).toEqual(query)
    expect(toApi(fromQuery(query))).toMatchObject({ sensitive: false, ai: true })
    expect(fromQuery({ sensitive: 'maybe', ai: '' })).toMatchObject({ sensitive: null, ai: null })
  })

  it('normalises tags and drops what it does not know', () => {
    const state = fromQuery({
      tags: ' #Backup,,zrh,backup ',
      sort: 'random',
      status: 'Gone',
      period: 'week',
      page: '-2',
    })
    expect(state.tags).toEqual(['backup', 'zrh'])
    expect(state.sort).toBeNull()
    expect(state.status).toBeNull()
    expect(state.period).toBe('30d')
    expect(state.page).toBe(1)
  })

  it('takes the first of repeated parameters', () => {
    expect(fromQuery({ q: ['one', 'two'] }).q).toBe('one')
  })

  it('writes the period only for the orders that have one', () => {
    expect(toQuery({ ...fromQuery({}), sort: 'title', period: 'all' })).toEqual({ sort: 'title' })
    expect(toApi({ ...fromQuery({}), sort: 'title', period: 'all' }).period).toBeUndefined()
    expect(toApi({ ...fromQuery({}), sort: 'edits' }).period).toBe('30d')
  })

  it('asks for relevance only while searching', () => {
    expect(toApi({ ...fromQuery({}), sort: 'relevance' }).sort).toBeUndefined()
    expect(toApi({ ...fromQuery({ q: ' printer ' }), sort: 'relevance' })).toMatchObject({
      q: 'printer',
      sort: 'relevance',
    })
  })

  it('toggles a tag from the tag browser, back to the first page', () => {
    const state = { ...fromQuery({ tags: 'backup' }), page: 4 }
    expect(toggleTag(state, 'ZRH')).toMatchObject({ tags: ['backup', 'zrh'], page: 1 })
    expect(toggleTag(state, 'backup').tags).toEqual([])
  })
})
