import { flushPromises } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import ChatTab from './ChatTab.vue'

/**
 * Cookie's powers for one message (docs/cookie-powers-plan.md §3, §9): the checkbox is there for an admin only, asks every
 * time, sends that one message through the admin endpoint, and the thread shows the mark and what came of the order.
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

type Dialog = { header: string; message: string; accept: () => void; reject: () => void }
const dialog: { last: Dialog | null } = { last: null }
vi.mock('primevue/useconfirm', () => ({ useConfirm: () => ({ require: (o: Dialog) => (dialog.last = o) }) }))

vi.mock('@/api/resources', async (original) => {
  const actual = await original<typeof import('@/api/resources')>()
  return {
    ...actual,
    api: { ...actual.api, cookie: { ...actual.api.cookie, messages: vi.fn(), post: vi.fn(), postEmpowered: vi.fn() } },
  }
})
const messages = vi.mocked(api.cookie.messages)
const post = vi.mocked(api.cookie.post)
const postEmpowered = vi.mocked(api.cookie.postEmpowered)
type Thread = Awaited<ReturnType<typeof api.cookie.messages>>
type Item = Thread['messages'][number]

const person = (id: number, text: string, empowered = false): Item => ({
  id,
  at: '2026-11-16T10:00:00Z',
  from: 'person',
  text,
  answered: true,
  to: 'cookie',
  instruction: null,
  source: 'ui',
  readOnly: false,
  refusedWhy: null,
  empowered,
  actions: null,
})
const thread = (list: Item[] = []): Thread => ({ messages: list, waiting: false, state: 'Sleeping' })

async function open() {
  const tab = mountWithUi(ChatTab)
  await flushPromises()
  return tab
}

describe('the chat with powers', () => {
  beforeEach(() => {
    messages.mockReset()
    post.mockReset()
    postEmpowered.mockReset()
    messages.mockResolvedValue(thread())
    session.isAdmin = false
    dialog.last = null
  })

  it('has no checkbox for anyone but an admin', async () => {
    const tab = await open()
    expect(tab.find('#chat-powers').exists()).toBe(false)
    expect(tab.text()).not.toContain('With powers')
    expect(tab.text()).not.toContain('give Cookie its powers')
  })

  it('asks the admin every time, sends that one message with powers, and unticks itself', async () => {
    session.isAdmin = true
    postEmpowered.mockResolvedValue({ ...person(7, 'Send Ben an e-mail', true), answered: false })
    post.mockResolvedValue(person(8, 'Is the NAS fine?'))
    const tab = await open()
    const box = tab.find('#chat-powers')
    expect(box.exists()).toBe(true)
    expect(tab.text()).toContain('give Cookie its powers')

    // Cancel leaves it unticked.
    await box.trigger('change')
    expect(dialog.last?.header).toBe('Give Cookie its powers for this message?')
    expect(dialog.last?.message).toContain('Seraph still reads every order')
    dialog.last!.reject()
    await flushPromises()
    expect(tab.find('button[type="submit"]').text()).toBe('Send')
    expect(tab.find('.state').exists()).toBe(false)

    // I understand: the compose says so, and the message goes through the admin endpoint.
    await box.trigger('change')
    dialog.last!.accept()
    await flushPromises()
    expect(tab.find('button[type="submit"]').text()).toBe('Send with powers')
    expect(tab.find('.state').text()).toContain('Cookie will act on this message.')
    await tab.find('textarea').setValue('Send Ben an e-mail')
    await tab.find('form').trigger('submit')
    await flushPromises()
    expect(postEmpowered).toHaveBeenCalledWith('Send Ben an e-mail')
    expect(post).not.toHaveBeenCalled()

    // One message, one confirmation: the next one is plain.
    expect(tab.find('button[type="submit"]').text()).toBe('Send')
    await tab.find('textarea').setValue('Is the NAS fine?')
    await tab.find('form').trigger('submit')
    await flushPromises()
    expect(post).toHaveBeenCalledWith('Is the NAS fine?', 'cookie')
    expect(postEmpowered).toHaveBeenCalledTimes(1)
  })

  it('marks the empowered message and shows what came of the order under the reply', async () => {
    session.isAdmin = true
    messages.mockResolvedValue(
      thread([
        person(1, 'Send Ben an e-mail: the NAS is back.', true),
        {
          ...person(2, 'Beep: I told Ben.'),
          from: 'cookie',
          actions: [
            {
              decisionId: 11,
              kind: 'email',
              title: 'E-mail: The NAS is back',
              outcome: 'done',
              detail: "On Test Admin's order from the chat.",
            },
            {
              decisionId: 12,
              kind: 'pushover',
              title: 'Pushover: Hurry',
              outcome: 'rejected',
              detail: 'Seraph stopped the order (0.95): it deceives.',
            },
          ],
        },
      ]),
    )
    const tab = await open()
    const [mine, reply] = tab.findAll('.message')
    expect(mine!.text()).toContain('with powers')
    expect(reply!.text()).toContain('E-mail: The NAS is back')
    expect(reply!.text()).toContain('done')
    expect(reply!.text()).toContain('stopped by Seraph')
    expect(reply!.text()).toContain('Seraph stopped the order (0.95)')
  })
})
