import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { api, type S } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import CertificateTab from './CertificateTab.vue'
import { certificateName, chain, changed, facts, item, sensor, summary, validation } from './certificate.fixture'

/**
 * The Certificate tab (docs/ssl-certificate-plan.md §8.3, §10) with a detail for each state: valid, about to expire,
 * expired, a failed check that keeps the last certificate, none read yet, untrusted with an incomplete chain, pinned, and
 * the fixes it offers (pin a self-signed certificate, trust a root the server sends).
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
    api: { ...actual.api, certificates: { ...actual.api.certificates, get: vi.fn(), versions: vi.fn() } },
  }
})
const get = vi.mocked(api.certificates.get)
const versions = vi.mocked(api.certificates.versions)

const NOW = Date.parse('2026-11-17T12:00:00Z')
const DAY = 86_400_000
const at = (ms: number) => new Date(ms).toISOString()

/** A detail without a change, whose chain expires at `expiresAt`. */
function detail(
  patch: {
    summary?: Partial<S['CertificateSensorSummary']>
    facts?: Partial<S['CertificateFacts']>
    validation?: Partial<S['CertificateValidation']>
    chain?: S['CertificateChainEntry'][]
    current?: null
  } = {},
): S['CertificateSensorDetail'] {
  const f = facts(patch.facts)
  const current = item(f)
  if (patch.chain) current.chain = patch.chain
  return {
    ...changed(f),
    summary: summary(patch.summary),
    current: patch.current === null ? null : current,
    accepted: null,
    change: null,
    validation: validation({ chainExpiresAt: f.notAfter, ...patch.validation }),
  }
}

async function mountTab(d: S['CertificateSensorDetail'], rights: { canEdit?: boolean; canManage?: boolean } = {}) {
  get.mockResolvedValue(d)
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/:any(.*)*', name: 'any', component: { template: '<div />' } }],
  })
  const tab = mountWithUi(CertificateTab, {
    props: { sensor: sensor(d.summary), canEdit: rights.canEdit ?? false, canManage: rights.canManage ?? false },
    global: {
      plugins: [
        [PrimeVue, { unstyled: true }],
        ToastService,
        ConfirmationService,
        [VueQueryPlugin, { queryClient }],
        router,
      ],
      stubs: { teleport: true, RouterLink: { template: '<a><slot /></a>' } },
    },
  })
  await vi.waitFor(async () => {
    await flushPromises()
    expect(get).toHaveBeenCalled()
    expect(tab.find('.center').exists()).toBe(false)
  })
  return tab
}

describe('the Certificate tab', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(NOW)
    get.mockReset()
    versions.mockReset()
    versions.mockResolvedValue({ items: [], page: 1, pageSize: 10, totalCount: 0 })
  })
  afterEach(() => vi.useRealTimers())

  it('shows a valid certificate: the days left, the trust, who accepted it, and what it says', async () => {
    const tab = await mountTab(detail({ facts: { notAfter: at(NOW + 76 * DAY + 3_600_000) } }))
    const text = tab.text()
    expect(text).toContain('Valid · 76 days left')
    expect(text).toContain('Trusted by public CAs (USERTrust RSA Certification Authority)')
    expect(text).toContain('Accepted at the first check')
    expect(tab.findAll('.chip').map((c) => c.text())).toEqual(['shop.example.com', 'www.shop.example.com'])
    expect(text).toContain('Domain validated (DV)')
    expect(text).toContain('B2:B2:B2')
    expect(text).toContain('sent by the server')
    expect(text).toContain('public root')
    expect(text).toContain('TLS 1.3')
    expect(text).toContain('The name matches')
    expect(text).not.toContain('Switch to pinned')
    tab.unmount()
  })

  it('says when it is about to expire, and when it has expired', async () => {
    const soon = await mountTab(detail({ facts: { notAfter: at(NOW + 5 * DAY + 3_600_000) } }))
    expect(soon.find('.title').text()).toBe('Expires in 5 days')
    expect(soon.find('.title').classes()).toContain('tone-warn')
    soon.unmount()

    const expired = await mountTab(detail({ facts: { notAfter: at(NOW - 2 * DAY - 3_600_000) } }))
    expect(expired.find('.title').text()).toBe('Expired 2 days ago')
    expect(expired.find('.title').classes()).toContain('tone-fail')
    expired.unmount()
  })

  it("names an intermediate that expires first, and warns of a warning too long for the certificate's lifetime", async () => {
    const tab = await mountTab(
      detail({
        facts: { lifetimeDays: 30, notAfter: at(NOW + 20 * DAY) },
        validation: {
          chainExpiresAt: at(NOW + 10 * DAY),
          chainExpiresSubject: 'ACME Sub CA',
          chainExpiresIsLeaf: false,
        },
      }),
    )
    expect(tab.text()).toContain('The intermediate ACME Sub CA expires on')
    expect(tab.text()).toContain('more than a third')
    expect(tab.find('.title').text()).toBe('Expires in 10 days')
    tab.unmount()
  })

  it('keeps the last certificate seen under the error of a failed check', async () => {
    const tab = await mountTab(
      detail({
        summary: { consecutiveFailures: 2, lastError: 'connection refused', firstFailureAt: at(NOW - 3_600_000) },
        facts: { notAfter: at(NOW + 76 * DAY + 3_600_000) },
      }),
    )
    expect(tab.text()).toContain('The last check failed: connection refused')
    expect(tab.text()).toContain('The certificate below is the last one seen')
    expect(tab.text()).toContain('Valid · 76 days left')
    tab.unmount()
  })

  it('says when no certificate was read yet', async () => {
    const tab = await mountTab(detail({ current: null, summary: { lastCheckAt: null } }))
    expect(tab.text()).toContain('No certificate read yet.')
    expect(tab.text()).toContain('The first check runs within 15 seconds')
    tab.unmount()
  })

  it('shows an untrusted, incomplete chain with its problem', async () => {
    const problem = "the chain doesn't reach a trusted root: the server doesn't send its intermediate"
    const tab = await mountTab(
      detail({
        summary: { trust: 'Private', authorities: [{ id: 'ca1', name: 'PNMT demo root' }] },
        validation: { trusted: false, trustedBy: null, complete: false, problems: [problem] },
      }),
    )
    const text = tab.text()
    expect(text).toContain(`Not trusted: ${problem}`)
    expect(text).toContain("Incomplete chain: the server doesn't send its intermediate")
    expect(text).toContain('a finding only')
    tab.unmount()
  })

  it('shows who pinned a pinned certificate', async () => {
    const tab = await mountTab(
      detail({
        summary: { trust: 'Pinned', acceptedBy: 'anna', acceptedAt: '2026-03-03T10:00:00Z' },
        facts: { selfSigned: true },
        validation: { trusted: false, trustedBy: null, publiclyTrusted: false, selfSigned: true },
      }),
      { canEdit: true },
    )
    expect(tab.text()).toMatch(/Pinned since .+ by anna; self-signed/)
    expect(tab.text()).toContain('the pin is what counts')
    // Pinned already: no fix to offer.
    expect(tab.text()).not.toContain('Switch to pinned')
    tab.unmount()
  })

  it('offers the fixes: pinned for a self-signed certificate, a private CA for a root the server sends', async () => {
    const selfSigned = await mountTab(
      detail({
        facts: { selfSigned: true, issuer: certificateName('shop.example.com') },
        validation: { trusted: false, trustedBy: null, publiclyTrusted: false, selfSigned: true },
      }),
      { canEdit: true },
    )
    expect(selfSigned.text()).toContain('Switch to pinned…')
    selfSigned.unmount()

    const f = facts()
    const sent = chain(f)
    sent[2] = { ...sent[2]!, source: 'Server', trustAnchor: false, subject: certificateName('Contoso Root CA') }
    const unknown = await mountTab(
      detail({ chain: sent, validation: { trusted: false, trustedBy: null, publiclyTrusted: false, sendsRoot: true } }),
      { canManage: true },
    )
    expect(unknown.text()).toContain("The chain ends at a root PNMT doesn't know, Contoso Root CA.")
    expect(unknown.text()).toContain('Trust this root as a private CA…')
    expect(unknown.text()).toContain('The server sends the root too')
    unknown.unmount()

    // Without Certificates: Manage, no private CA to add.
    const viewer = await mountTab(
      detail({ chain: sent, validation: { trusted: false, trustedBy: null, publiclyTrusted: false, sendsRoot: true } }),
    )
    expect(viewer.text()).not.toContain('Trust this root as a private CA…')
    viewer.unmount()
  })
})
