import { describe, expect, it } from 'vitest'
import { byRound, lastId, mergeTrace, type TraceItem } from './trace'

const t = (id: number, roundId: number | null, kind = 'note'): TraceItem => ({
  id,
  roundId,
  caseId: null,
  caseTitle: null,
  at: '2026-11-14T10:00:00Z',
  kind,
  title: `#${id}`,
  body: '',
  areas: [],
})

describe("Cookie's trace (docs/cookie-plan.md §9.2)", () => {
  it('adds what a poll brings (newest first) once each, oldest first', () => {
    const list = mergeTrace([], [t(3, 1), t(2, 1), t(1, 1)])
    expect(list.map((i) => i.id)).toEqual([1, 2, 3])
    const more = mergeTrace(list, [t(5, 2), t(4, 2), t(3, 1)])
    expect(more.map((i) => i.id)).toEqual([1, 2, 3, 4, 5])
    expect(lastId(more)).toBe(5)
    expect(lastId([])).toBeUndefined()
  })

  it('groups by round, the newest round first, each round in its order', () => {
    const groups = byRound([t(1, 7), t(2, 7), t(3, null, 'summary'), t(4, 8), t(5, 8)])
    expect(groups.map((g) => g.roundId)).toEqual([8, null, 7])
    expect(groups[0].items.map((i) => i.id)).toEqual([4, 5])
    expect(groups[2].items.map((i) => i.id)).toEqual([1, 2])
  })
})
