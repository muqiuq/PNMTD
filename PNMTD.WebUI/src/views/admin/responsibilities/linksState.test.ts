import { describe, expect, it } from 'vitest'
import { linksFromQuery, linksToQuery, type LinksState } from './linksState'

const empty: LinksState = {
  show: 'links',
  responsibilities: [],
  person: null,
  kinds: [],
  location: null,
  below: true,
  covered: false,
}

describe("the Links tab's state in the URL (docs/responsibilities-extensions-plan.md §5.1)", () => {
  it('reads the defaults from an empty URL and writes nothing for them', () => {
    expect(linksFromQuery({})).toEqual(empty)
    expect(Object.values(linksToQuery(empty)).filter((v) => v !== undefined)).toEqual([])
  })

  it('reads and writes every filter', () => {
    const state: LinksState = {
      show: 'links',
      responsibilities: ['a', 'b'],
      person: 'p',
      kinds: ['Host', 'Page'],
      location: 'l',
      below: false,
      covered: false,
    }
    const query = linksToQuery(state)
    expect(query).toMatchObject({ r: 'a,b', person: 'p', kinds: 'Host,Page', location: 'l', below: '0' })
    expect(linksFromQuery(query as Record<string, string>)).toEqual(state)
  })

  it('keeps only what the view uses', () => {
    const hosts: LinksState = { ...empty, show: 'hosts', responsibilities: ['a'], kinds: ['Page'], covered: true }
    expect(linksToQuery(hosts)).toMatchObject({ show: 'hosts', r: undefined, kinds: undefined, covered: '1' })
    expect(linksToQuery({ ...empty, covered: true }).covered).toBeUndefined()
    expect(linksToQuery({ ...empty, below: false }).below).toBeUndefined()
  })

  it('ignores what it does not know', () => {
    expect(linksFromQuery({ show: 'sensors', kinds: 'Sensor,Host', below: 'x' })).toEqual({
      ...empty,
      kinds: ['Host'],
    })
  })
})
