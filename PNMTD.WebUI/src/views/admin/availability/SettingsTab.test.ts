import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/client'
import { api, type S } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import SettingsTab from './SettingsTab.vue'
import { peopleKey, settingsKey } from './useAvailability'

/**
 * docs/availability-extensions-plan.md §3.2, as e2e/availability.spec.ts does it in the browser: a period already saved takes
 * a change (before 1.28 it kept its old end, read-only from the query, and 08:00–11:30 with 13:00–17:00 said "Periods may
 * not overlap."); and the server's message goes once the periods are fixed, without another Save.
 */
vi.stubGlobal('matchMedia', (query: string) => ({
  matches: false,
  media: query,
  addEventListener: () => {},
  removeEventListener: () => {},
}))

vi.mock('@/api/resources', async (original) => {
  const actual = await original<typeof import('@/api/resources')>()
  return {
    ...actual,
    api: { ...actual.api, availability: { ...actual.api.availability, saveSettings: vi.fn() } },
  }
})
const saveSettings = vi.mocked(api.availability.saveSettings)

const item: S['AvailabilitySettingsItem'] = {
  types: [
    {
      id: 't1',
      name: 'On site',
      code: 'ANWE',
      color: '#d9f2d9',
      flags: ['OnSite'],
      isDefault: true,
      isDayOff: false,
      order: 0,
      retired: false,
      works: true,
    },
  ],
  settings: {
    week: [
      { day: 'Monday', periods: [{ from: '08:00', to: '17:00' }] },
      { day: 'Tuesday', periods: [{ from: '08:00', to: '17:00' }] },
    ],
    holidays: [],
    countColumns: [],
    responsibleLabel: 'Responsible',
    globalFallbackUserId: null,
    globalFallbackGroupId: null,
  },
  groups: [],
  timeZone: 'Europe/Zurich',
}

function mountTab() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } })
  queryClient.setQueryData(settingsKey, item)
  queryClient.setQueryData(peopleKey, [])
  return mountWithUi(SettingsTab, {
    global: { plugins: [[PrimeVue, { unstyled: true }], ToastService, [VueQueryPlugin, { queryClient }]] },
  })
}

describe("the plan's settings", () => {
  it("hide the server's overlap message once the periods are fixed", async () => {
    saveSettings.mockRejectedValue(
      new ApiError(400, { code: 'validation_failed', errors: { 'week.Monday': ['Periods may not overlap.'] } }),
    )
    const tab = mountTab()
    await flushPromises()
    const field = (label: string) => tab.find(`input[aria-label="${label}"]`)
    await field('Monday, period 1, to').setValue('12:00')
    await tab.find('button[aria-label="Add a period on Monday"]').trigger('click')
    await field('Monday, period 2, from').setValue('11:30')
    await field('Monday, period 2, to').setValue('17:00')
    const hours = () => tab.find('section[aria-labelledby="hours-week-title"]').text()
    expect(hours()).toContain('Periods may not overlap.')

    const save = tab.findAll('button').find((b) => b.text() === 'Save')!
    await save.trigger('click')
    await flushPromises()
    expect(saveSettings).toHaveBeenCalledOnce()
    // What was sent is what the fields showed: the saved period's new end too.
    expect(saveSettings.mock.calls[0]![0].settings.week!.find((d) => d.day === 'Monday')!.periods).toEqual([
      { from: '08:00', to: '12:00' },
      { from: '11:30', to: '17:00' },
    ])
    expect(hours()).toContain('Periods may not overlap.')

    await field('Monday, period 1, to').setValue('11:30')
    await field('Monday, period 2, from').setValue('13:00')
    await flushPromises()
    expect(hours()).not.toContain('Periods may not overlap.')
    // The query's data stays as the server sent it.
    expect(item.settings.week![0]!.periods).toEqual([{ from: '08:00', to: '17:00' }])
    tab.unmount()
  })
})
