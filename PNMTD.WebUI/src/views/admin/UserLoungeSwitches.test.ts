import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import UserLoungeSwitches from './UserLoungeSwitches.vue'

/** A person's lounge switches on Admin → Users (docs/lounge-plan.md §9.4, §15.4): shown as saved, a change saved at once. */
vi.mock('@/api/resources', async (original) => {
  const actual = await original<typeof import('@/api/resources')>()
  return { ...actual, api: { ...actual.api, users: { ...actual.api.users, lounge: vi.fn(), saveLounge: vi.fn() } } }
})
const lounge = vi.mocked(api.users.lounge)
const saveLounge = vi.mocked(api.users.saveLounge)

async function open() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const view = mountWithUi(UserLoungeSwitches, {
    props: { userId: 'u1', username: 'amuster' },
    global: { plugins: [[PrimeVue, { unstyled: true }], ToastService, [VueQueryPlugin, { queryClient }]] },
  })
  await flushPromises()
  return view
}

describe("a user's lounge switches", () => {
  beforeEach(() => {
    lounge.mockReset()
    saveLounge.mockReset()
  })

  it('shows them as saved, and saves a change at once through the admin endpoint', async () => {
    lounge.mockResolvedValue({ mentions: true, emails: true })
    saveLounge.mockResolvedValue({ mentions: false, emails: true })
    const view = await open()
    expect(lounge).toHaveBeenCalledWith('u1')
    expect(view.text()).toContain('Cookie and Seraph may talk about them')
    expect(view.text()).toContain('Cookie and Seraph may e-mail them')
    const mentions = view.find('#ul-mentions')
    expect((mentions.element as HTMLInputElement).checked).toBe(true)
    await mentions.setValue(false)
    await flushPromises()
    expect(saveLounge).toHaveBeenCalledWith('u1', { mentions: false, emails: true })
  })
})
