import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { S } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import NodesTab from './NodesTab.vue'

const node: S['UdpNodeListItem'] = {
  sensorId: '11111111-1111-4111-8111-111111111111',
  name: 'pump-01',
  host: { id: '22222222-2222-4222-8222-222222222222', name: 'garden' },
  deviceId: 300,
  deviceIdBytes: '01 2C',
  enabled: true,
  state: 'Ok',
  keyFamily: 'HmacSha256',
  allowedSuites: ['Token32', 'HmacSha256_32', 'HmacSha256_64'],
  keySource: 'Manual',
  keyChangedAt: '2026-11-12T10:00:00Z',
  keyChangedBy: 'alice',
  tokenSource: 'Generated',
  sessionMode: 'None',
  sessionActive: false,
  ackMode: 'OnRequest',
  responseSuite: null,
  lastSeenAt: null,
  lastEndpoint: null,
  lastSuite: null,
  fcnt: 42,
  openCommands: 2,
  firmwareVersion: '1.4.2',
  openUpdate: null,
  packets24Hours: 140,
  loss24Hours: 0.007,
  rejected24Hours: 3,
  badTagWarning: false,
  notes: 'In the shed\nSIM 8944 0000',
}

afterEach(() => vi.unstubAllGlobals())

function mountTab() {
  const fetch = vi.fn(async (request: Request) => {
    const body =
      new URL(request.url).pathname === '/api/v1/udp/nodes'
        ? { items: [node], page: 1, pageSize: 50, totalCount: 1 }
        : {}
    return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
  })
  vi.stubGlobal('fetch', fetch)
  // jsdom has no matchMedia, which PrimeVue's Select uses when it mounts.
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: false,
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }))
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/:any(.*)*', name: 'any', component: { template: '<div />' } }],
  })
  const tab = mountWithUi(NodesTab, {
    global: {
      plugins: [
        [PrimeVue, { unstyled: true }],
        ToastService,
        ConfirmationService,
        [VueQueryPlugin, { queryClient }],
        router,
      ],
      stubs: { RouterLink: { template: '<a><slot /></a>' } },
    },
  })
  return { tab, fetch }
}

describe('the admin page: nodes', () => {
  it('lists every node with its settings, sorted by name on the server', async () => {
    const { tab, fetch } = mountTab()
    await vi.waitFor(async () => {
      await flushPromises()
      expect(tab.text()).toContain('pump-01')
    })

    const url = new URL((fetch.mock.calls[0][0] as Request).url)
    expect(url.pathname).toBe('/api/v1/udp/nodes')
    expect(url.searchParams.get('sort')).toBe('name')

    const text = tab.text()
    expect(text).toContain('garden')
    expect(text).toContain('300 · 01 2C')
    expect(text).toContain('HMAC-SHA256')
    expect(text).toContain('entered manually')
    expect(text).toContain('TOK')
    expect(text).toContain('H32')
    expect(text).toContain('H64')
    expect(text).toContain('– · on request')
    expect(text).toContain('1.4.2')
    expect(text).toContain('140 · 0.7 % · 3')
    // The notes' first line, the rest on hover.
    expect(text).toContain('In the shed')
    expect(text).not.toContain('SIM 8944')
    expect(tab.find('[title="TOKEN32"]').classes()).toContain('weak')
    tab.unmount()
  })

  it('offers "Update firmware…" for the selected nodes', async () => {
    const { tab } = mountTab()
    await vi.waitFor(async () => {
      await flushPromises()
      expect(tab.text()).toContain('pump-01')
    })
    expect(tab.text()).not.toContain('Update firmware… (1)')
    const boxes = tab.findAll('input[type="checkbox"]')
    // The header's box selects the page; the row's is the second.
    await boxes[1].setValue(true)
    expect(tab.text()).toContain('Update firmware… (1)')
    tab.unmount()
  })
})
