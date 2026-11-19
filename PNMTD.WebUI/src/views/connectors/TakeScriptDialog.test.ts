import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import { describe, expect, it, vi } from 'vitest'
import { api } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import TakeScriptDialog from './TakeScriptDialog.vue'

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

// The library's selector (docs/connectors-plan.md §3.3): a version list of numbers once broke PrimeVue's label in production.
describe('TakeScriptDialog', () => {
  it('shows the current version of a library script, its text and parameters, without an error', async () => {
    vi.spyOn(api.connectors, 'library').mockResolvedValue([
      { id: 's1', name: 'rotate-logs', description: null, currentVersion: 1, versions: [1] },
    ])
    vi.spyOn(api.connectors, 'libraryVersion').mockResolvedValue({
      scriptId: 's1',
      name: 'rotate-logs',
      number: 1,
      text: 'echo hi',
      hash: 'h',
      parseState: 'Parsed',
      parsed: { parameters: [], help: null, errors: [], findings: [] } as never,
      takenFrom: 'library: rotate-logs v1',
    })
    const errors: unknown[] = []
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const wrapper = mountWithUi(TakeScriptDialog, {
      props: { visible: true, type: 'LinuxScript', source: 'library', current: '' },
      global: {
        plugins: [[PrimeVue, { unstyled: true }], ToastService, [VueQueryPlugin, { queryClient }]],
        stubs: { teleport: true },
        config: { errorHandler: (e: unknown) => errors.push(e) },
      },
    })
    await flushPromises()
    ;(wrapper.vm as unknown as { $: { setupState: { scriptId: string } } }).$.setupState.scriptId = 's1'
    await flushPromises()
    await flushPromises()
    expect(errors).toEqual([])
    expect(wrapper.html()).toContain('echo hi')
    expect(wrapper.html()).toContain('v1 (current)')
  })
})
