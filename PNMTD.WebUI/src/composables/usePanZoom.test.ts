import { describe, expect, it } from 'vitest'
import { centreOn, fitTo, MAX_ZOOM, MIN_ZOOM, toWorld, zoomAround } from './usePanZoom'

describe('usePanZoom (docs/network-map-plan.md §8.2)', () => {
  it('zooms around the pointer: the point under it stays', () => {
    const view = { x: 100, y: 50, k: 1 }
    const at = { x: 300, y: 200 }
    const before = toWorld(view, at)
    const zoomed = zoomAround(view, at, 2)
    expect(zoomed.k).toBe(2)
    expect(toWorld(zoomed, at)).toEqual(before)
  })

  it('stays within its limits', () => {
    expect(zoomAround({ x: 0, y: 0, k: 1 }, { x: 0, y: 0 }, 100).k).toBe(MAX_ZOOM)
    expect(zoomAround({ x: 0, y: 0, k: 1 }, { x: 0, y: 0 }, 0.0001).k).toBe(MIN_ZOOM)
  })

  it('fits a box into the viewport, centred', () => {
    const view = fitTo({ minX: -500, minY: -100, maxX: 500, maxY: 100 }, 1064, 600)
    expect(view.k).toBeCloseTo(1)
    expect(toWorld(view, { x: 532, y: 300 })).toEqual({ x: 0, y: 0 })
    expect(fitTo({ minX: 0, minY: 0, maxX: 10, maxY: 10 }, 800, 600).k).toBe(1.5)
  })

  it('centres a point', () => {
    const view = centreOn({ x: 0, y: 0, k: 2 }, { x: 40, y: -20 }, 800, 600)
    expect(toWorld(view, { x: 400, y: 300 })).toEqual({ x: 40, y: -20 })
  })
})
