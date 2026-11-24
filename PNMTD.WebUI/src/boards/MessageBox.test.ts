import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import MessageBox from './MessageBox.vue'
import { countdown, next, open, received, type BoardMessageView } from './messages'
import { boardTime } from './time'

/** docs/board-messages-plan.md §5.3, §5.6, §6.1: the order, the countdown from the server's seconds, the rotation, closing on a screen. */
const time = boardTime('Europe/Zurich')

function message(id: string, kind: BoardMessageView['kind'], sentAt: string, remainingSeconds = 60): BoardMessageView {
  return {
    id,
    kind,
    title: `Title ${id}`,
    text: `Text **${id}**`,
    from: 'Anna Muster',
    sentAt,
    seconds: 60,
    remainingSeconds,
  }
}

describe('board messages', () => {
  it('puts the most important kind first, then the newest, and drops those whose time is up', () => {
    const at = 1_000_000
    const list = received(
      [
        message('info', 'Information', '2026-11-24T10:00:00Z'),
        message('old-alert', 'Alert', '2026-11-24T09:00:00Z'),
        message('new-alert', 'Alert', '2026-11-24T09:30:00Z'),
        message('done', 'Success', '2026-11-24T10:05:00Z', 5),
      ],
      at,
    )
    expect(open(list, at).map((m) => m.id)).toEqual(['new-alert', 'old-alert', 'info', 'done'])
    expect(open(list, at + 6000).map((m) => m.id)).toEqual(['new-alert', 'old-alert', 'info'])
  })

  it('counts down from the seconds the server said were left', () => {
    const [m] = received([message('a', 'Warning', '2026-11-24T10:00:00Z', 42)], 5000)
    expect(m!.endsAt).toBe(47_000)
    expect(countdown(42_000)).toBe('0:42')
    expect(countdown(3_600_000)).toBe('1:00:00')
    expect(countdown(-5)).toBe('0:00')
  })

  it('turns to the next one, and to the first when the current one is gone', () => {
    const list = received([message('a', 'Alert', 'x'), message('b', 'Warning', 'x')], 0)
    expect(next(list, 'a')).toBe('b')
    expect(next(list, 'b')).toBe('a')
    expect(next(list, 'gone')).toBe('a')
    expect(next([], 'a')).toBeNull()
  })
})

describe('the message box', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-11-24T10:00:00Z'))
  })
  afterEach(() => vi.useRealTimers())

  const title = (w: ReturnType<typeof mount>) => w.find('.board-msg-title').text()

  it('shows a new message at once, then rotates every 15 s', async () => {
    const now = Date.now()
    const w = mount(MessageBox, {
      props: { messages: received([message('a', 'Alert', '2026-11-24T09:59:00Z', 600)], now), time },
      attachTo: document.body,
    })
    expect(title(w)).toBe('Title a')
    expect(w.find('.board-msg-left').text()).toBe('closes in 10:00')
    await w.setProps({
      messages: received(
        [message('a', 'Alert', '2026-11-24T09:59:00Z', 600), message('b', 'Information', '2026-11-24T10:00:00Z', 600)],
        now,
      ),
    })
    expect(title(w)).toBe('Title b')
    expect(w.find('.board-msg-count').text()).toContain('2 / 2')
    vi.advanceTimersByTime(15_000)
    await nextTick()
    expect(title(w)).toBe('Title a')
    w.unmount()
  })

  it('closes on this screen with Esc until a new one arrives', async () => {
    const now = Date.now()
    const w = mount(MessageBox, {
      props: { messages: received([message('a', 'Warning', '2026-11-24T09:59:00Z', 600)], now), time },
      attachTo: document.body,
    })
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await nextTick()
    expect(w.find('[data-test="board-message"]').exists()).toBe(false)
    await w.setProps({
      messages: received(
        [message('a', 'Warning', '2026-11-24T09:59:00Z', 600), message('c', 'Success', '2026-11-24T10:00:00Z', 600)],
        now,
      ),
    })
    expect(title(w)).toBe('Title c')
    w.unmount()
  })

  it('stays in a preview, whatever Esc does on the page', async () => {
    const w = mount(MessageBox, {
      props: {
        messages: received([message('p', 'Information', '2026-11-24T09:59:00Z', 60)], Date.now()),
        time,
        still: true,
      },
      attachTo: document.body,
    })
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await nextTick()
    expect(title(w)).toBe('Title p')
    expect(w.find('.board-msg-left').exists()).toBe(false)
    w.unmount()
  })

  it('goes when the last one is over, and shows links as text', async () => {
    const w = mount(MessageBox, {
      props: {
        messages: received(
          [{ ...message('a', 'Maintenance', 'x', 2), text: 'See [the plan](https://example.com)' }],
          Date.now(),
        ),
        time,
      },
      attachTo: document.body,
    })
    expect(w.find('.board-msg-text a').exists()).toBe(false)
    expect(w.find('.board-msg-text').text()).toBe('See the plan')
    vi.advanceTimersByTime(3000)
    await nextTick()
    expect(w.find('[data-test="board-message"]').exists()).toBe(false)
    w.unmount()
  })
})
