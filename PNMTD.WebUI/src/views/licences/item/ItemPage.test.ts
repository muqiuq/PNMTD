import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { licensesApi, type LicenseDetail } from '@/api/licenses'
import { useSession } from '@/stores/session'
import { mountWithUi } from '@/test/mount'
import LicenceFormView from '../LicenceFormView.vue'
import LicenceView from '../LicenceView.vue'

/**
 * The item's page and form (docs/licences-plan.md §8.7) mounted with their calls answered: the header, each tab, the keys masked,
 * who isn't told, the seats over, and the form's past-charges offer. View alone shows no buttons.
 */
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

vi.mock('@/api/licenses', async (original) => {
  const actual = await original<typeof import('@/api/licenses')>()
  const empty = () => vi.fn(async () => [])
  return {
    ...actual,
    licensesApi: {
      ...actual.licensesApi,
      byNumber: vi.fn(),
      categories: vi.fn(async () => [
        {
          id: 'c1',
          name: 'Software',
          description: null,
          sortOrder: 1,
          defaultFor: 'License',
          items: 1,
          templates: 0,
          perYear: [],
        },
        {
          id: 'c2',
          name: 'Connectivity',
          description: null,
          sortOrder: 2,
          defaultFor: null,
          items: 0,
          templates: 0,
          perYear: [],
        },
      ]),
      names: vi.fn(async () => ({ manufacturers: [], vendors: [], costCenters: [], seatUnits: [] })),
      people: empty(),
      assignments: vi.fn(async () => [
        {
          hostId: 'h1',
          hostName: 'web-01',
          hostEnabled: true,
          locationId: null,
          locationName: 'Zürich',
          sensorId: 's1',
          sensorEnabled: true,
          sensorIgnore: false,
          lastCode: 449,
          lastMessage: 'Over-assigned: 7 of 5 devices · Veeam (LIC-9)',
          seats: 7,
          note: null,
          assignedAt: '2026-11-01T10:00:00Z',
          assignedBy: 'admin',
        },
      ]),
      sensorLinks: empty(),
      itemCharges: vi.fn(async () => [
        {
          id: 'ch1',
          licenseId: 'l1',
          number: 9,
          display: 'LIC-9',
          type: 'License',
          name: 'Veeam',
          kind: 'Period',
          source: 'Automatic',
          on: '2026-03-01',
          periodFrom: '2026-03-01',
          periodTo: '2027-02-28',
          amount: 1450,
          currency: 'EUR',
          estimated: false,
          reference: 'INV-1',
          note: null,
          createdAt: '2026-03-01T00:00:00Z',
          createdBy: null,
          updatedAt: null,
          updatedBy: null,
          invoiceFileId: null,
          invoiceName: null,
        },
      ]),
      renewals: empty(),
      documents: empty(),
      templates: empty(),
      history: vi.fn(async () => ({ items: [], page: 1, pageSize: 25, totalCount: 0 })),
      costs: vi.fn(async () => ({
        currency: 'EUR',
        currencies: ['EUR'],
        group: 'item',
        show: 'forecast',
        columns: [],
        forecast: [],
        groups: [
          {
            key: 'l1',
            label: 'LIC-9',
            itemId: 'l1',
            display: 'LIC-9',
            values: [],
            total: 1450,
            perYearNow: 1450,
            items: [],
          },
        ],
        totals: [],
        total: 1450,
        perYearNow: 1450,
        ifRenewed: 0,
        otherCurrencies: [],
        sharedByHosts: false,
      })),
    },
  }
})
const byNumber = vi.mocked(licensesApi.byNumber)

function detail(patch: Partial<LicenseDetail> = {}): LicenseDetail {
  return {
    id: 'l1',
    number: 9,
    display: 'LIC-9',
    type: 'License',
    name: 'Veeam Backup & Replication',
    templateId: null,
    templateName: null,
    categoryId: 'c1',
    categoryName: 'Software',
    manufacturer: 'Veeam',
    productName: 'Backup & Replication',
    edition: 'v12',
    vendor: 'Comp AG',
    kind: 'Subscription',
    licensedTo: null,
    reference: null,
    customerNumber: null,
    portalUrl: 'https://portal.example.com',
    supportContact: null,
    supportUrl: null,
    sla: null,
    costCenter: null,
    locationId: null,
    locationName: null,
    ownerId: null,
    ownerName: null,
    hasLicenseKey: true,
    licenseKeyHint: 'CC9DR',
    hasProductKey: false,
    productKeyHint: null,
    seats: 5,
    seatUnit: 'devices',
    seatsFromHosts: true,
    usedSeats: 7,
    priceAmount: 1450,
    currency: 'EUR',
    priceBasis: 'Total',
    billing: 'Recurring',
    billingMonths: 12,
    recordCharges: true,
    chargesFrom: '2026-03-01',
    startsOn: '2025-03-01',
    expiresOn: '2027-02-28',
    termMonths: 12,
    renewal: 'Manual',
    warnDays: 30,
    noticeDays: null,
    cancelBy: null,
    warnBeforeRenewal: false,
    endOfLifeFromTemplate: false,
    endOfLifeOn: null,
    endOfLifeNote: null,
    ownEndOfLifeOn: null,
    ownEndOfLifeNote: null,
    eolWarnDays: 90,
    reviewedOn: null,
    reviewedBy: null,
    reviewNote: null,
    reviewMonths: 12,
    reviewDueOn: '2026-12-01',
    reviewDue: false,
    status: 'Active',
    statusChangedAt: null,
    statusChangedBy: null,
    note: 'Renewed **every March**.',
    verdictCode: 449,
    verdictMessage: 'Over-assigned: 7 of 5 devices · Veeam Backup & Replication (LIC-9)',
    verdictAt: '2026-11-01T10:00:00Z',
    findings: [{ code: 449, text: 'Over-assigned: 7 of 5 devices · Veeam Backup & Replication (LIC-9)', on: null }],
    unitPrice: 1450,
    perMonth: 120.83,
    perYear: 1450,
    spentThisYear: 1450,
    hosts: 1,
    links: 0,
    renewals: 0,
    charges: 1,
    documents: 0,
    notifying: { hasOwner: false, ownerHasEmail: false, hosts: 0, unownedChoosers: 0, nobody: true },
    createdAt: '2025-03-01T00:00:00Z',
    createdBy: 'admin',
    updatedAt: '2026-11-01T10:00:00Z',
    ...patch,
  }
}

async function mountPage(component: object, path: string, rights: Record<string, string> | 'admin' = 'admin') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/licences/new', name: 'licence-new', component },
      { path: '/licences/:number(\\d+)/edit', name: 'licence-edit', component, props: true },
      { path: '/licences/:number(\\d+)/:tab?', name: 'licence', component, props: true },
      { path: '/licences/:tab?', name: 'licences', component: { template: '<div />' } },
      { path: '/:any(.*)*', name: 'any', component: { template: '<div />' } },
    ],
  })
  await router.push(path)
  await router.isReady()
  const pinia = createPinia()
  setActivePinia(pinia)
  useSession().set(
    {
      id: 'u1',
      username: 'admin',
      displayName: 'Admin',
      isAdmin: rights === 'admin',
      rights: rights === 'admin' ? {} : (rights as never),
      isTotpEnabled: false,
    },
    null,
  )
  const route = router.currentRoute.value
  const page = mountWithUi(component, {
    props: { number: route.params.number as string | undefined, tab: route.params.tab as string | undefined },
    attachTo: document.body,
    global: {
      plugins: [
        [PrimeVue, { unstyled: true }],
        ToastService,
        ConfirmationService,
        [VueQueryPlugin, { queryClient }],
        router,
        pinia,
      ],
      stubs: { teleport: true, RouterLink: { template: '<a><slot /></a>' } },
    },
  })
  await flushPromises()
  await flushPromises()
  return page
}

describe("the item's page (§8.7)", () => {
  beforeEach(() => byNumber.mockResolvedValue(detail()))

  it('shows the header, the headline, the findings, who isn’t told, the masked key and the seats over', async () => {
    const page = await mountPage(LicenceView, '/licences/9')
    const text = page.text()
    expect(text).toContain('LIC-9 · Veeam Backup & Replication')
    expect(text).toContain('Renew…')
    expect(page.find('[data-test="headline"]').text()).toMatch(/Valid · \d+ days left|Expires/)
    expect(page.find('[data-test="findings"]').text()).toContain('Over-assigned: 7 of 5 devices')
    expect(page.find('[data-test="nobody-notified"]').text()).toContain(
      'On no host and without an owner, so nobody is told when it expires.',
    )
    expect(page.find('[data-test="licenseKey-masked"]').text()).toBe('•••••-CC9DR')
    expect(page.find('[data-test="seats-used"]').text()).toContain('7 of 5 devices, over by 2')
    expect(page.find('[data-test="costs-line"]').text()).toContain('EUR 120.83 per month')
    expect(page.find('[data-test="term-bar"]').exists()).toBe(true)
    expect(text).toContain('Renewed every March.')
    page.unmount()
  })

  it('shows the tabs: hosts, costs, renewals, documents, history', async () => {
    const hosts = await mountPage(LicenceView, '/licences/9/hosts')
    expect(hosts.find('[data-test="assignments"]').text()).toContain('web-01')
    hosts.unmount()
    const costs = await mountPage(LicenceView, '/licences/9/costs')
    expect(costs.find('[data-test="spent-by-year"]').text()).toContain('2026: EUR 1,450.00')
    expect(costs.find('[data-test="item-forecast"]').text()).toContain('EUR 1,450.00')
    expect(costs.find('[data-test="charges"]').text()).toContain('INV-1')
    costs.unmount()
    const renewals = await mountPage(LicenceView, '/licences/9/renewals')
    expect(renewals.text()).toContain('Not renewed yet.')
    renewals.unmount()
    const documents = await mountPage(LicenceView, '/licences/9/documents')
    expect(documents.text()).toContain('No documents yet.')
    documents.unmount()
    const history = await mountPage(LicenceView, '/licences/9/history')
    expect(history.text()).toContain('No changes recorded yet.')
    history.unmount()
  })

  it('offers no changes with View alone, and no Show without Details', async () => {
    const page = await mountPage(LicenceView, '/licences/9', { Licenses: 'View' })
    expect(page.find('[data-test="renew"]').exists()).toBe(false)
    expect(page.find('[data-test="show-keys"]').exists()).toBe(false)
    expect(page.find('[data-test="set-owner"]').exists()).toBe(false)
    page.unmount()
  })
})

describe("the item's form (§8.7)", () => {
  it('starts a new service with its kind’s defaults, and offers the charges since a past start', async () => {
    const form = await mountPage(LicenceFormView, '/licences/new?type=Service')
    expect(form.text()).toContain('New service')
    expect(form.text()).toContain('Provider')
    expect(form.find('[data-test="lf-past-charges"]').exists()).toBe(false)
    form.unmount()
  })

  it('loads an item to edit, with its keys left empty', async () => {
    byNumber.mockResolvedValue(detail())
    const form = await mountPage(LicenceFormView, '/licences/9/edit')
    expect(form.text()).toContain('Edit LIC-9 · Veeam Backup & Replication')
    expect((form.find('#lf-name').element as HTMLInputElement).value).toBe('Veeam Backup & Replication')
    expect((form.find('#lf-license-key').element as HTMLInputElement).value).toBe('')
    expect(form.text()).toContain('Set; empty keeps it.')
    form.unmount()
  })
})
