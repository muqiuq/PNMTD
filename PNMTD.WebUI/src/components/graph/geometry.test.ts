import { describe, expect, it } from 'vitest'
import { bendOf, boundsOf, edgePath } from './geometry'

describe('drawing a graph', () => {
  it('draws a lone edge straight and bends parallel ones apart, symmetrically', () => {
    expect(edgePath({ x: 0, y: 0 }, { x: 10, y: 0 })).toBe('M 0 0 L 10 0')
    expect(bendOf(0, 1)).toBe(0)
    expect([bendOf(0, 2), bendOf(1, 2)]).toEqual([-9, 9])
    expect([bendOf(0, 3), bendOf(1, 3), bendOf(2, 3)]).toEqual([-18, 0, 18])
    expect(edgePath({ x: 0, y: 0 }, { x: 10, y: 0 }, 5)).toBe('M 0 0 Q 5 5 10 0')
  })

  it('boxes the circles, or falls back when there are none', () => {
    const fallback = { minX: -1, minY: -1, maxX: 1, maxY: 1 }
    expect(boundsOf([], fallback)).toEqual(fallback)
    expect(
      boundsOf(
        [
          { x: 0, y: 0, r: 5 },
          { x: 20, y: -10, r: 2 },
        ],
        fallback,
      ),
    ).toEqual({ minX: -5, minY: -12, maxX: 22, maxY: 5 })
  })
})
