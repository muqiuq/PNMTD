import { describe, expect, it } from 'vitest'
import { parseFocus } from './focus'

describe('a focus in the URL', () => {
  const id = '0193f3c2-7d1e-7c55-9a35-8e2f1a6b4c90'
  it("takes the map's kinds, a UUID, and a number where the kind is numbered", () => {
    expect(parseFocus(`HOST:${id.toUpperCase()}`, ['router', 'host'])).toEqual({ kind: 'host', id })
    expect(parseFocus('page:42', ['page', 'host'], ['page'])).toEqual({ kind: 'page', id: '42' })
  })

  it('refuses anything else', () => {
    expect(parseFocus('page:42', ['page', 'host'])).toBeNull()
    expect(parseFocus(`page:${id}`, ['page'], ['page'])).toBeNull()
    expect(parseFocus(`sensor:${id}`, ['router', 'host'])).toBeNull()
    expect(parseFocus(42, ['page'], ['page'])).toBeNull()
    expect(parseFocus('host:', ['host'])).toBeNull()
  })
})
