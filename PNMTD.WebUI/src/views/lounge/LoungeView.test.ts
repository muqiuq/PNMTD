import { flushPromises } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import LoungeView from './LoungeView.vue'

/**
 * The lounge (docs/lounge-plan.md §15.2, §16 Web): who is typing, asleep and closed, the composer only with Lounge: Use, a
 * comment under its message and one in the flow, and a message's Comment action opening the composer as a comment on it.
 */
// jsdom has no ResizeObserver, which the auto-resizing Textarea uses when it mounts.
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
)

const session = { isAdmin: false, can: () => true }
vi.mock('@/stores/session', () => ({ useSession: () => session }))

vi.mock('@/api/resources', async (original) => {
  const actual = await original<typeof import('@/api/resources')>()
  return {
    ...actual,
    api: { ...actual.api, lounge: { ...actual.api.lounge, room: vi.fn(), about: vi.fn(), post: vi.fn() } },
  }
})
const room = vi.mocked(api.lounge.room)
const about = vi.mocked(api.lounge.about)
const post = vi.mocked(api.lounge.post)
type Room = Awaited<ReturnType<typeof api.lounge.room>>
type Item = Room['messages'][number]
type State = Room['state']

const line = (id: number, over: Partial<Item> = {}): Item => ({
  id,
  at: '2026-11-20T09:41:00Z',
  from: 'cookie',
  kind: 'message',
  userId: null,
  author: null,
  authorColor: null,
  authorCode: null,
  text: `line ${id}`,
  onId: null,
  takenById: null,
  replyToId: null,
  refused: false,
  removed: false,
  caseId: null,
  caseTitle: null,
  email: null,
  important: false,
  ...over,
})
const awake: State = {
  running: true,
  state: 'Awake',
  reason: null,
  speaking: null,
  nextAt: null,
  canWrite: true,
  waiting: false,
  awakeUntil: '2026-11-20T16:00:00Z',
}
const answer = (messages: Item[], state: Partial<State> = {}): Room => ({
  messages,
  state: { ...awake, ...state },
  cookieName: 'Cookie',
})

async function open() {
  const view = mountWithUi(LoungeView, { global: { stubs: { PageHeader: true } } })
  await flushPromises()
  return view
}

describe('the lounge', () => {
  beforeEach(() => {
    room.mockReset()
    about.mockReset()
    post.mockReset()
    about.mockResolvedValue({ cookie: [], seraph: [], convictions: [], cookieName: 'Cookie' })
  })

  it('shows who is typing', async () => {
    room.mockResolvedValue(answer([line(1)], { state: 'Speaking', speaking: 'seraph' }))
    const view = await open()
    expect(view.find('.typing').text()).toContain('Seraph is typing…')
  })

  it('marks a message a voice thought really matters', async () => {
    room.mockResolvedValue(
      answer([line(1), line(2, { important: true, text: 'Somebody should renew the certificate today.' })]),
    )
    const view = await open()
    expect(view.find('[data-id="1"]').classes()).not.toContain('important')
    expect(view.find('[data-id="2"]').classes()).toContain('important')
    expect(view.find('[data-id="2"]').text()).toContain('important')
  })

  it('says when they will read a message while they are asleep', async () => {
    room.mockResolvedValue(answer([line(1)], { state: 'Asleep', nextAt: '2026-11-21T07:00:00Z' }))
    const view = await open()
    expect(view.find('form').exists()).toBe(true)
    expect(view.text()).toContain("They're asleep. They'll read it at")
  })

  it('is closed while stopped: readable, without a composer', async () => {
    room.mockResolvedValue(
      answer([line(1, { text: 'Beep, an old line.' })], { running: false, state: 'Stopped', canWrite: false }),
    )
    const view = await open()
    expect(view.text()).toContain('Beep, an old line.')
    expect(view.text()).toContain('The lounge is closed')
    expect(view.find('form').exists()).toBe(false)
  })

  it('has no composer without Lounge: Use', async () => {
    room.mockResolvedValue(answer([line(1)], { canWrite: false }))
    const view = await open()
    expect(view.find('form').exists()).toBe(false)
    expect(view.find('.comment-action').exists()).toBe(false)
  })

  it('puts a comment under its message, and one on none in the flow, marked until taken up', async () => {
    room.mockResolvedValue(
      answer([
        line(1, { from: 'seraph', text: 'It was successful until 01:10.' }),
        line(2, { from: 'person', kind: 'comment', author: 'Ben', onId: 1, text: 'I cleaned it up yesterday.' }),
        line(3, { from: 'person', kind: 'comment', author: 'Anna', text: 'Coffee is ready.', takenById: 4 }),
        line(4, { text: 'Coffee! Beep.' }),
      ]),
    )
    const view = await open()
    const seraph = view.find('[data-id="1"]')
    expect(seraph.find('.comments [data-id="2"]').exists()).toBe(true)
    expect(seraph.find('.comments [data-id="2"]').text()).toContain('not yet read')
    const flowComment = view.find('.thread > [data-id="3"]')
    expect(flowComment.exists()).toBe(true)
    expect(flowComment.text()).toContain('Anna')
    expect(flowComment.text()).not.toContain('not yet read')
  })

  it("opens the composer as a comment on a message from its Comment action, and sends it with the message's id", async () => {
    room.mockResolvedValue(answer([line(1, { from: 'seraph', text: 'Funerals are patient.' })]))
    post.mockResolvedValue(line(2, { from: 'person', kind: 'comment', onId: 1, author: 'Ben', text: 'Ha.' }))
    const view = await open()
    await view.find('.comment-action').trigger('click')
    await flushPromises()
    expect(view.find('.on').text()).toContain("Seraph's message")
    await view.find('textarea').setValue('Ha.')
    await view.find('form').trigger('submit')
    await flushPromises()
    expect(post).toHaveBeenCalledWith({ text: 'Ha.', kind: 'comment', onId: 1 })
  })

  it('polls with afterId and replaces a changed line by id: a comment taken up, a line removed', async () => {
    room.mockResolvedValueOnce(
      answer([
        line(1, { from: 'seraph', text: 'Funerals are patient.' }),
        line(2, { from: 'person', kind: 'comment', author: 'Ben', onId: 1, text: 'Ha.' }),
        line(3, { from: 'person', author: 'Anna', text: 'Rude words.' }),
      ]),
    )
    room.mockResolvedValue(
      answer([
        line(2, { from: 'person', kind: 'comment', author: 'Ben', onId: 1, text: 'Ha.', takenById: 4 }),
        line(3, { from: 'person', author: 'Anna', text: '', removed: true }),
        line(4, { text: 'Ben laughs. Beep.' }),
      ]),
    )
    vi.useFakeTimers()
    try {
      const view = await open()
      expect(view.find('[data-id="2"]').text()).toContain('not yet read')
      await vi.advanceTimersByTimeAsync(10_000)
      await flushPromises()
      expect(room).toHaveBeenLastCalledWith(3)
      expect(view.find('[data-id="2"]').text()).not.toContain('not yet read')
      expect(view.find('[data-id="3"]').text()).toContain('Removed by an admin')
      expect(view.text()).toContain('Ben laughs. Beep.')
    } finally {
      vi.useRealTimers()
    }
  })

  it('says why a message was refused at the limit', async () => {
    const { ApiError } = await import('@/api/client')
    room.mockResolvedValue(answer([line(1)]))
    post.mockRejectedValue(new ApiError(429, { code: 'lounge_too_many_messages', title: 'At most 20' } as never))
    const view = await open()
    await view.find('textarea').setValue('Hello')
    await view.find('form').trigger('submit')
    await flushPromises()
    expect(view.find('[role="alert"]').text()).toContain('20 messages and comments in an hour')
  })
})
