import { afterEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { mountWithUi } from '@/test/mount'
import UptimeBar from './UptimeBar.vue'
import { dayOf, uptimeTone, visibleCount } from './uptime'

describe('uptime', () => {
  it('colours by the thresholds: 0.99 green, 0.9 amber, below red, no data grey', () => {
    expect([1, 0.99, 0.989, 0.9, 0.899, 0, null].map(uptimeTone)).toEqual([
      'ok',
      'ok',
      'warn',
      'warn',
      'fail',
      'fail',
      'none',
    ])
  })

  it('shows as many days as fit at 3 px, never fewer than 30, never more than there are', () => {
    expect(visibleCount(270, 90)).toBe(90)
    expect(visibleCount(150, 90)).toBe(50)
    expect(visibleCount(30, 90)).toBe(30)
    expect(visibleCount(1000, 90)).toBe(90)
    expect(visibleCount(30, 7)).toBe(7)
    expect(visibleCount(0, 90)).toBe(90)
  })

  it('counts days from the series start', () => {
    expect(dayOf('2026-10-31', 0)).toBe('2026-10-31')
    expect(dayOf('2026-10-31', 1)).toBe('2026-11-01')
  })
})

describe('UptimeBar', () => {
  afterEach(() => vi.restoreAllMocks())

  const days = Array.from({ length: 90 }, (_, i) => (i === 89 ? 0.5 : i === 88 ? null : 1))

  it('ends with the newest day and drops the oldest when narrow', async () => {
    vi.spyOn(Element.prototype, 'clientWidth', 'get').mockReturnValue(150)
    const bar = mountWithUi(UptimeBar, { props: { values: days, start: '2026-08-14' } })
    await nextTick()

    const rects = bar.findAll('rect')
    expect(rects).toHaveLength(50)
    expect(rects.at(-1)!.attributes('data-tone')).toBe('fail')
    expect(rects.at(-2)!.attributes('data-tone')).toBe('none')
    expect(rects.at(-1)!.find('title').text()).toMatch(/50\.00 %$/)
    expect(rects.at(-2)!.find('title').text()).toMatch(/no data$/)
    expect(bar.find('svg').attributes('aria-label')).toBe('Uptime of the last 50 days')
  })

  it('labels hours when the series has no start day', async () => {
    vi.spyOn(Element.prototype, 'clientWidth', 'get').mockReturnValue(200)
    const bar = mountWithUi(UptimeBar, { props: { values: Array(24).fill(1), minimum: 24 } })
    await nextTick()
    const titles = bar.findAll('rect title').map((t) => t.text())
    expect(titles).toHaveLength(24)
    expect(titles.at(-1)).toBe('this hour: 100 %')
    expect(titles[0]).toBe('23 h ago: 100 %')
  })
})
