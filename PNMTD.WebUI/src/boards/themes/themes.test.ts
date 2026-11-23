import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { statusColours, themes } from './registry'

/** docs/boards-plan.md §9.4: a theme never changes what a status means, and its status text stays readable. */

function rgb(hex: string): [number, number, number] {
  const n = parseInt(hex.slice(1), 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}

function hue(hex: string): number {
  const [r, g, b] = rgb(hex).map((v) => v / 255) as [number, number, number]
  const max = Math.max(r, g, b)
  const min = Math.min(r, g, b)
  if (max === min) return 0
  const d = max - min
  const h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4
  return (h * 60 + 360) % 360
}

function luminance(hex: string): number {
  const [r, g, b] = rgb(hex).map((v) => {
    const c = v / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  }) as [number, number, number]
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number]
  return (hi + 0.05) / (lo + 0.05)
}

describe('board themes', () => {
  it('are the fifteen of panag, in its order, each with its block in themes.css', () => {
    expect(themes.map((t) => t.id)).toEqual([
      'default',
      'dark',
      'high-contrast',
      'high-contrast-dark',
      'slay',
      'switzerland',
      'terminal',
      'brasil',
      'atomic-age',
      'wtf',
      'suse',
      'win95',
      'winxp',
      'ubuntu',
      'amiga',
    ])
    const css = readFileSync(join(process.cwd(), 'src/boards/themes/themes.css'), 'utf8')
    for (const t of themes.filter((t) => t.id !== 'default'))
      expect(css, t.id).toContain(`.board[data-board-theme='${t.id}'] {`)
    expect(Object.keys(statusColours).sort()).toEqual(themes.map((t) => t.id).sort())
  })

  it.each(themes.map((t) => t.id))('%s keeps OK green, warning yellow, failure red', (id) => {
    const c = statusColours[id]!
    expect(hue(c.ok[0])).toBeGreaterThanOrEqual(90)
    expect(hue(c.ok[0])).toBeLessThanOrEqual(160)
    expect(hue(c.warn[0])).toBeGreaterThanOrEqual(35)
    expect(hue(c.warn[0])).toBeLessThanOrEqual(65)
    // Amiga's Guru Meditation is red too: its text and frame (registry.ts).
    expect(hue(c.fail[0]) >= 340 || hue(c.fail[0]) <= 20, `${id} fail ${c.fail[0]}`).toBe(true)
  })

  it.each(themes.map((t) => t.id))('%s has readable status text: AA, AAA in the high-contrast themes', (id) => {
    const c = statusColours[id]!
    const min = id.startsWith('high-contrast') ? 7 : 4.5
    for (const [fill, on] of [c.ok, c.warn, c.fail])
      expect(contrast(fill, on), `${id} ${fill} on ${on}`).toBeGreaterThanOrEqual(min)
  })
})
