import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import { describe, expect, it, vi } from 'vitest'
import { api } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import ConnectorsDocsTab from './ConnectorsDocsTab.vue'

const { downloadText } = vi.hoisted(() => ({ downloadText: vi.fn() }))
vi.mock('@/format/markdown', async (original) => ({
  ...(await original<typeof import('@/format/markdown')>()),
  downloadText,
}))
vi.mock('@/components/markdown/MarkdownView.vue', () => ({
  default: { props: ['source'], template: '<div class="markdown">{{ source }}</div>' },
}))

// *Documentation* (docs/connectors-plan.md §13.1): shown without its title, which the page's header has; downloaded with it.
describe('ConnectorsDocsTab', () => {
  it('shows the API without its title and downloads it whole as a .md file', async () => {
    const markdown = '# PNMT 3rd Party Connectors: the API for callers\n\nA connector lets your system do one thing.\n'
    vi.spyOn(api.connectors, 'docs').mockResolvedValue({ markdown })
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const wrapper = mountWithUi(ConnectorsDocsTab, {
      global: { plugins: [[PrimeVue, { unstyled: true }], ToastService, [VueQueryPlugin, { queryClient }]] },
    })
    await flushPromises()
    expect(wrapper.get('.markdown').text()).toBe('A connector lets your system do one thing.')

    await wrapper.get('button').trigger('click')
    expect(downloadText).toHaveBeenCalledWith('pnmt-connectors-api.md', markdown)
  })
})
