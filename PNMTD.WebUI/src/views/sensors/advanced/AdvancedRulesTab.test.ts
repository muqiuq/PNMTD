import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import DataTable from 'primevue/datatable'
import ToastService from 'primevue/toastservice'
import ToggleSwitch from 'primevue/toggleswitch'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, type S } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import AdvancedRulesTab from './AdvancedRulesTab.vue'
import { advanced, OTHER_RULE_ID, result, rule, RULE_ID, sensor, SENSOR_ID } from './rules.fixture'

/**
 * The Rules tab (docs/advanced-sensor-plan.md §10.3): the verdict, each rule with its condition and what the last report
 * made of it (the values hidden without Details on data that names people), the empty state, and the changes it sends:
 * switching a rule off and a new order.
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
    api: {
      ...actual.api,
      advanced: {
        ...actual.api.advanced,
        rules: { ...actual.api.advanced.rules, update: vi.fn(), order: vi.fn() },
      },
    },
  }
})
const update = vi.mocked(api.advanced.rules.update)
const order = vi.mocked(api.advanced.rules.order)

const jobsRule = rule({
  id: OTHER_RULE_ID,
  order: 1,
  name: 'No job errors',
  path: 'jobs[*].errors',
  operator: 'Le',
  operand: '0',
  severity: 'Warn',
  condition: 'every jobs[*].errors ≤ 0',
  hasWildcard: true,
  note: 'Errors are retried once.',
  failingSince: null,
  last: result({
    ruleId: OTHER_RULE_ID,
    outcome: 'Warn',
    detail: 'must be at most 0',
    actual: [{ path: 'jobs[1].errors', value: '3' }],
  }),
})

function mountTab(a: S['AdvancedSensorDetail'], canWrite = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return mountWithUi(AdvancedRulesTab, {
    props: { sensor: sensor(a), canWrite },
    global: {
      plugins: [[PrimeVue, { unstyled: true }], ToastService, ConfirmationService, [VueQueryPlugin, { queryClient }]],
      stubs: { teleport: true },
    },
  })
}

describe('the Rules tab', () => {
  beforeEach(() => {
    update.mockReset()
    order.mockReset()
  })

  it('shows the verdict and each rule with what the last report made of it', () => {
    const tab = mountTab(advanced({ rules: [rule(), jobsRule] }))
    const text = tab.text()
    expect(text).toContain('495')
    expect(text).toContain('1 rule fails: Disk space (disk.free_gb = 3.2, must be at least 5)')
    expect(text).toContain('1 fails · 1 warns')
    const rows = tab.findAll('tbody tr')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.text()).toContain('Disk space')
    expect(rows[0]!.text()).toContain('disk.free_gb ≥ 5')
    expect(rows[0]!.text()).toContain('✗ fails')
    expect(rows[0]!.text()).toContain('disk.free_gb = 3.2 · must be at least 5')
    expect(rows[1]!.text()).toContain('every jobs[*].errors ≤ 0')
    expect(rows[1]!.text()).toContain('! warns')
    expect(rows[1]!.find('[aria-label="Note: Errors are retried once."]').exists()).toBe(true)
    tab.unmount()
  })

  it('hides the values when the data names people and the viewer lacks Details', () => {
    const hidden = rule({ last: result({ actual: [], matches: [] }) })
    const tab = mountTab(advanced({ canSeeData: false, namesPeople: true, rules: [hidden] }), false)
    expect(tab.text()).toContain("the rules' values need Monitoring: Details")
    expect(tab.find('tbody').text()).not.toContain('3.2')
    expect(tab.find('tbody').text()).toContain('disk.free_gb · must be at least 5')
    expect(tab.findAll('button').some((b) => b.text() === 'Add rule')).toBe(false)
    tab.unmount()
  })

  it('says how to start without rules', () => {
    const tab = mountTab(
      advanced({ rules: [], verdict: { ...advanced().verdict!, code: 295, message: 'Report received' } }),
    )
    expect(tab.text()).toContain('No rules yet.')
    expect(tab.text()).toContain('Pick a value on the Data tab, or add one here.')
    tab.unmount()
  })

  it('switches a rule off with the rest of it unchanged, and saves a new order', async () => {
    update.mockResolvedValue(advanced())
    order.mockResolvedValue(advanced({ rules: [jobsRule, rule()] }))
    const tab = mountTab(advanced({ rules: [rule(), jobsRule] }))

    const toggle = tab.findAllComponents(ToggleSwitch).find((t) => t.props('ariaLabel') === 'Disk space on')!
    expect(toggle.props('modelValue')).toBe(true)
    toggle.vm.$emit('update:modelValue', false)
    await flushPromises()
    expect(update).toHaveBeenCalledWith(SENSOR_ID, RULE_ID, {
      name: 'Disk space',
      enabled: false,
      note: null,
      path: 'disk.free_gb',
      operator: 'Ge',
      operand: '5',
      operand2: null,
      ignoreCase: false,
      quantifier: 'All',
      whenMissing: 'Fail',
      severity: 'Fail',
    })

    tab.findComponent(DataTable).vm.$emit('row-reorder', { value: [jobsRule, rule()] })
    await flushPromises()
    expect(order).toHaveBeenCalledWith(SENSOR_ID, [OTHER_RULE_ID, RULE_ID])
    tab.unmount()
  })
})
