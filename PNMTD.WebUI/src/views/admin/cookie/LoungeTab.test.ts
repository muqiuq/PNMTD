import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import LoungeTab from './LoungeTab.vue'

/**
 * Admin → Cookie → Lounge (docs/lounge-plan.md §15.3, §16 Web): Start is disabled, with the reason, without a group; the
 * rhythm line says what the pace means; a value out of range is marked before Save.
 */
// jsdom has neither matchMedia, which PrimeVue's Select uses when it mounts, nor ResizeObserver.
vi.stubGlobal('matchMedia', (query: string) => ({
  matches: false,
  media: query,
  addEventListener: () => {},
  removeEventListener: () => {},
}))
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
)
vi.mock('primevue/useconfirm', () => ({ useConfirm: () => ({ require: () => undefined }) }))
vi.mock('@/stores/session', () => ({ useSession: () => ({ isAdmin: true, can: () => true }) }))
vi.mock('@/api/resources', async (original) => {
  const actual = await original<typeof import('@/api/resources')>()
  return {
    ...actual,
    api: {
      ...actual.api,
      cookie: { ...actual.api.cookie, settings: vi.fn(), people: vi.fn() },
      lounge: { ...actual.api.lounge, status: vi.fn(), opinions: vi.fn(), start: vi.fn() },
    },
  }
})
const settings = vi.mocked(api.cookie.settings)
const people = vi.mocked(api.cookie.people)
const status = vi.mocked(api.lounge.status)
const opinions = vi.mocked(api.lounge.opinions)
const start = vi.mocked(api.lounge.start)
type Status = Awaited<ReturnType<typeof api.lounge.status>>
type All = Awaited<ReturnType<typeof api.cookie.settings>>

const week = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map((day, i) => ({
  day: day as never,
  periods: i < 5 ? [{ from: '08:00', to: '17:00' }] : [],
}))
const lounge = (groupId: string | null, messagesPerHour = 6) => ({
  awake: week,
  asleepOnHolidays: true,
  messagesPerHour,
  longestScene: 6,
  disagreeShare: 0.3,
  repliesPerHour: 20,
  importantPerDay: 3,
  groupId,
  effort: 'low',
  toolCallsPerMessage: 3,
  casesPerDay: 2,
  emailLevel: 'Propose' as const,
  emailsPerDay: 2,
  emailsPerPersonWeek: 1,
  halfLifeDays: 7,
  memoriesInPrompt: 15,
  maxDriftPerDay: 0.05,
  convictions: [
    {
      key: 'reboot',
      question: 'Is a reboot a fix?',
      minus: 'No.',
      plus: 'Yes.',
      owner: 'cookie',
      cookie: 0.85,
      seraph: -0.7,
    },
    {
      key: 'friday',
      question: 'Changes on a Friday?',
      minus: 'Yes.',
      plus: 'Never.',
      owner: 'seraph',
      cookie: -0.5,
      seraph: 0.9,
    },
  ],
  budgetUsd: 10,
})
const stopped = (cantStart: string | null): Status => ({
  enabled: true,
  running: false,
  startedAt: null,
  startedBy: null,
  stoppedAt: null,
  stoppedBy: null,
  state: 'Stopped',
  reason: null,
  speaking: null,
  nextAt: null,
  cantStart,
  scenesToday: 0,
  messagesToday: 0,
  commentsToday: 0,
  emailsToday: 0,
  casesToday: 0,
  budget: { limit: 10, spent: 0 },
  rhythm: 'About 54 messages a day, a scene about every 35 minutes.',
  budgetLasts: null,
  budgetWarning: false,
  lastError: null,
  lastErrorAt: null,
  readsWith: [],
  gap: [],
  users: 3,
  writers: [],
})

async function open(groupId: string | null, cantStart: string | null, messagesPerHour = 6) {
  settings.mockResolvedValue({ lounge: lounge(groupId, messagesPerHour) } as unknown as All)
  status.mockResolvedValue(stopped(cantStart))
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const tab = mountWithUi(LoungeTab, {
    global: {
      plugins: [[PrimeVue, { unstyled: true }], ToastService, [VueQueryPlugin, { queryClient }]],
      stubs: { LoungeMemoryPanel: true, LoungeScenesPanel: true, PromptsPanel: true, SettingsHistory: true },
    },
  })
  await flushPromises()
  return tab
}
const startButton = (tab: Awaited<ReturnType<typeof open>>) => tab.findAll('button').find((b) => b.text() === 'Start')!

describe('Admin → Cookie → Lounge', () => {
  beforeEach(() => {
    settings.mockReset()
    status.mockReset()
    start.mockReset()
    people.mockResolvedValue({ users: [], groups: [{ id: 'g1', name: 'Lounge readers' }] })
    opinions.mockResolvedValue([])
  })

  it('disables Start without a group, and says why', async () => {
    const tab = await open(
      null,
      'Choose what the lounge reads with first: everyone reads what Cookie and Seraph say about it.',
    )
    expect(startButton(tab).attributes('disabled')).toBeDefined()
    expect(tab.text()).toContain('Choose what the lounge reads with first')
  })

  it('enables Start once there is a group and nothing else is missing', async () => {
    start.mockResolvedValue(stopped(null))
    const tab = await open('g1', null)
    expect(startButton(tab).attributes('disabled')).toBeUndefined()
    await startButton(tab).trigger('click')
    await flushPromises()
    expect(start).toHaveBeenCalled()
  })

  it('says what the saved rhythm means, as the server paces it', async () => {
    const tab = await open('g1', null, 6)
    expect(tab.find('[data-test="rhythm"]').text()).toBe('About 54 messages a day, a scene about every 35 minutes.')
  })

  it('marks a value out of range before Save', async () => {
    const tab = await open('g1', null, 241)
    expect(tab.text()).toContain('0 to 240.')
    expect(tab.text()).toContain('See the marked fields before saving.')
  })
})
