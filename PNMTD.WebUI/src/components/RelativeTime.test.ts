import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { mountWithUi } from '@/test/mount'
import RelativeTime from './RelativeTime.vue'

describe('RelativeTime', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-11-11T12:00:00Z'))
  })
  afterEach(() => vi.useRealTimers())

  it('updates itself and keeps the absolute time as a tooltip', async () => {
    const time = mountWithUi(RelativeTime, { props: { value: '2026-11-11T11:55:00Z' } })
    expect(time.text()).toBe('5 min ago')
    expect(time.attributes('datetime')).toBe('2026-11-11T11:55:00.000Z')
    expect(time.attributes('title')).toMatch(/2026/)

    vi.advanceTimersByTime(10 * 60_000)
    await nextTick()
    expect(time.text()).toBe('15 min ago')
    time.unmount()
  })

  it('says never, or what it is told, without a time', () => {
    expect(mountWithUi(RelativeTime, { props: { value: null } }).text()).toBe('never')
    expect(mountWithUi(RelativeTime, { props: { value: null, empty: 'no events' } }).text()).toBe('no events')
  })
})
