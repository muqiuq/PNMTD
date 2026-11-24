import { describe, expect, it } from 'vitest'
import { fitText } from './fitText'

/**
 * docs/board-messages-plan.md §5.2, §9.3: jsdom lays nothing out, so the text's box is a model: a character is 0.6 em wide, a line
 * 1.2 em high, words wrap at spaces, and a word longer than the line sticks out sideways.
 */
function model(text: string, width: number, height: number) {
  const box = document.createElement('div')
  const el = document.createElement('div')
  box.append(el)
  const size = () => parseFloat(el.style.fontSize) * 16
  const words = text.split(' ')
  const lines = () => {
    const perLine = Math.max(1, Math.floor(width / (0.6 * size())))
    let count = 1
    let used = 0
    for (const w of words) {
      const need = used === 0 ? w.length : used + 1 + w.length
      if (need > perLine && used > 0) {
        count++
        used = w.length
      } else used = need
    }
    return count
  }
  const widest = () => Math.max(...words.map((w) => w.length)) * 0.6 * size()
  Object.defineProperty(box, 'clientHeight', { get: () => height })
  Object.defineProperty(el, 'clientWidth', { get: () => width })
  Object.defineProperty(el, 'scrollWidth', { get: () => Math.max(width, widest()) })
  Object.defineProperty(el, 'offsetHeight', { get: () => lines() * 1.2 * size() })
  return { box, el }
}

describe('fitText', () => {
  it('grows a short value until its box is full', () => {
    const { box, el } = model('41.5 °C', 600, 200)
    const { size, overflows } = fitText(box, el, 0.8, 20)
    expect(overflows).toBe(false)
    // As wide as the box: 4 characters ("41.5") at 0.6 em in 600 px, or as high as one line in 200 px.
    expect(size * 16 * 1.2).toBeLessThanOrEqual(200 + 1)
    expect(size).toBeGreaterThan(8)
    expect(el.style.fontSize).toBe(`${size}em`)
  })

  it('shrinks a long text to its minimum, then says it overflows', () => {
    const long = Array.from({ length: 400 }, () => 'word').join(' ')
    const { box, el } = model(long, 400, 100)
    expect(fitText(box, el, 0.8, 20)).toEqual({ size: 0.8, overflows: true })
  })

  it('fills only the width where the height follows the text, up to its maximum', () => {
    const { box, el } = model('OK', 1000, 10)
    expect(fitText(box, el, 0.8, 6, true)).toEqual({ size: 6, overflows: false })
    const wide = model('a-very-long-hostname', 300, 10)
    const { size } = fitText(wide.box, wide.el, 0.8, 6, true)
    expect(20 * 0.6 * size * 16).toBeLessThanOrEqual(301)
  })
})
