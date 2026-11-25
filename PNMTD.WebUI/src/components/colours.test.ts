import { describe, expect, it } from 'vitest'
import { colourName, contrast, firstUnused, isColour, palette, textOn } from './colours'

describe('colours (docs/responsibilities-extensions-plan.md §4.3)', () => {
  it('takes #rrggbb only', () => {
    expect(isColour('#2f8fd8')).toBe(true)
    expect(isColour('#2F8FD8')).toBe(true)
    for (const wrong of ['', null, undefined, 'blue', '#12345', '#1234567', '2f8fd8', '#gggggg']) {
      expect(isColour(wrong)).toBe(false)
    }
  })

  it('has twelve distinct swatches, each read by black or white text at 4.5:1 at least', () => {
    expect(palette).toHaveLength(12)
    expect(new Set(palette.map((p) => p.hex)).size).toBe(12)
    for (const p of palette) {
      expect(isColour(p.hex)).toBe(true)
      expect(contrast(p.hex, textOn(p.hex))).toBeGreaterThanOrEqual(4.5)
    }
  })

  it('measures contrast as WCAG does', () => {
    expect(contrast('#000000', '#ffffff')).toBeCloseTo(21, 5)
    expect(contrast('#ffffff', '#000000')).toBeCloseTo(21, 5)
    expect(contrast('#777777', '#777777')).toBe(1)
  })

  it('names a swatch, or shows a custom colour as it is', () => {
    expect(colourName('#2F8FD8')).toBe('Blue')
    expect(colourName('#123456')).toBe('#123456')
    expect(colourName(null)).toBe('No colour')
  })

  it('suggests the first swatch nobody uses, and the first when all are taken', () => {
    expect(firstUnused([])).toBe(palette[0]!.hex)
    expect(firstUnused([palette[0]!.hex.toUpperCase(), null, '#123456'])).toBe(palette[1]!.hex)
    expect(firstUnused(palette.map((p) => p.hex))).toBe(palette[0]!.hex)
  })
})
