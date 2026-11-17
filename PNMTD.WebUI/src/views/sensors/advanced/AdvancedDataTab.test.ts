import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises, type VueWrapper } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ToastService from 'primevue/toastservice'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import { api, type S } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import AdvancedDataTab from './AdvancedDataTab.vue'
import FieldCards from './FieldCards.vue'
import { fieldDraft, marksFrom, ruleDraft } from './dataTab'

/**
 * The Data tab (docs/advanced-sensor-plan.md §10.3): the latest report's headline, its fields, the report as a tree with
 * the rules' marks and *Raw*, the history whose rows open their report, *Add rule…* from a value, an invalid report's
 * first bytes as text, *Paste a sample* before the first report, and nothing of the data without Monitoring: Details.
 */
vi.stubGlobal('matchMedia', (query: string) => ({
  matches: false,
  media: query,
  addEventListener: () => {},
  removeEventListener: () => {},
}))

// The report URL's examples are tabs, which watch their size.
vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
)

vi.mock('@/api/resources', async (original) => {
  const actual = await original<typeof import('@/api/resources')>()
  return {
    ...actual,
    api: {
      ...actual.api,
      advanced: {
        ...actual.api.advanced,
        latest: vi.fn(),
        report: vi.fn(),
        reports: vi.fn(),
        samples: vi.fn(),
        test: vi.fn(),
      },
    },
  }
})
const latest = vi.mocked(api.advanced.latest)
const report = vi.mocked(api.advanced.report)
const reports = vi.mocked(api.advanced.reports)
const samples = vi.mocked(api.advanced.samples)
const test = vi.mocked(api.advanced.test)

const NOW = Date.parse('2026-11-17T12:00:00Z')
const at = (ms: number) => new Date(ms).toISOString()
const SENSOR_ID = '11111111-1111-1111-1111-111111111111'

const body = {
  status: 'ok',
  message: 'Nightly backup done',
  disk: { free_gb: 3.2, total_gb: 2000 },
  jobs: [
    { name: 'db', state: 'done' },
    { name: 'files', state: 'failed' },
  ],
  note: '<img src=x onerror=alert(1)>',
}

const diskRule: S['AdvancedRuleResult'] = {
  ruleId: 'r1',
  name: 'Disk space',
  path: 'disk.free_gb',
  condition: 'disk.free_gb ≥ 5',
  severity: 'Fail',
  outcome: 'Fail',
  reason: null,
  detail: 'must be at least 5',
  actual: [{ path: 'disk.free_gb', value: '3.2' }],
  matches: ['disk.free_gb'],
  matchCount: 1,
  assumedUtc: false,
}

function item(patch: Partial<S['AdvancedReportItem']> = {}): S['AdvancedReportItem'] {
  return {
    id: 'rep2',
    receivedAt: at(NOW - 120_000),
    lastReceivedAt: at(NOW - 60_000),
    repeats: 2,
    source: '10.0.0.5',
    bytes: 412,
    valid: true,
    errorKind: null,
    error: null,
    status: 'ok',
    message: 'Nightly backup done',
    measuredAt: at(NOW - 120_000 - 420_000),
    evaluated: true,
    code: 495,
    failed: 1,
    warnings: 0,
    ...patch,
  }
}

function detail(patch: Partial<S['AdvancedReportDetail']> = {}, reportPatch: Partial<S['AdvancedReportItem']> = {}) {
  return {
    report: item(reportPatch),
    body: JSON.stringify(body),
    results: [diskRule],
    fields: [],
    evaluationError: null,
    previousId: 'rep1',
    ...patch,
  } satisfies S['AdvancedReportDetail']
}

function advanced(patch: Partial<S['AdvancedSensorDetail']> = {}): S['AdvancedSensorDetail'] {
  const r = item()
  return {
    namesPeople: false,
    canSeeData: true,
    lastReportAt: r.lastReceivedAt,
    lastValidReportAt: r.lastReceivedAt,
    lastSource: '10.0.0.5',
    stale: false,
    staleAt: null,
    verdict: {
      code: 495,
      message: 'Disk space (disk.free_gb = 3.2, must be at least 5)',
      failed: 1,
      warnings: 0,
      evaluatedAt: r.lastReceivedAt,
    },
    rules: [
      {
        id: 'r1',
        order: 0,
        name: 'Disk space',
        enabled: true,
        note: null,
        path: 'disk.free_gb',
        operator: 'Ge',
        operand: '5',
        operand2: null,
        ignoreCase: false,
        quantifier: 'All',
        whenMissing: 'Fail',
        severity: 'Fail',
        condition: 'disk.free_gb ≥ 5',
        hasWildcard: false,
        last: diskRule,
        failingSince: r.receivedAt,
      },
    ],
    fields: [
      {
        id: 'f1',
        order: 0,
        name: 'Free space',
        path: 'disk.free_gb',
        format: 'Auto',
        unit: 'GB',
        decimals: null,
        pinned: true,
        chart: true,
        value: 3.2,
        outcome: 'Fail',
      },
    ],
    latest: { ...r, clockSkewSeconds: -420 },
    pending: 0,
    reportCount: 2,
    ...patch,
  }
}

function sensor(a: S['AdvancedSensorDetail']): S['SensorDetail'] {
  return {
    id: SENSOR_ID,
    host: { id: 'h1', name: 'backup-01' },
    parentSensorId: null,
    name: 'Nightly backup',
    type: 'ADVANCED',
    enabled: true,
    ignore: false,
    interval: 86_400,
    gracePeriod: 3_600,
    parameters: null,
    textId: null,
    status: null,
    created: '2026-11-01T00:00:00Z',
    state: 'Failing',
    lastCode: a.verdict?.code ?? null,
    lastMessage: null,
    lastEventAt: null,
    failingSince: null,
    uptime24Hours: 1,
    uptime30Days: 1,
    days: { start: '2026-10-18', values: [] },
    children: [],
    integration: {
      writeToken: 'w-token',
      readToken: null,
      writeUrl: 'https://pnmt.example.com/event/w-token',
      readUrl: null,
      reportUrl: 'https://pnmt.example.com/report',
    },
    advanced: a,
  }
}

let tab: VueWrapper | null = null
async function mountTab(a: S['AdvancedSensorDetail'], canWrite = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/:any(.*)*', name: 'any', component: { template: '<div />' } }],
  })
  tab = mountWithUi(AdvancedDataTab, {
    props: { sensor: sensor(a), canWrite },
    global: {
      plugins: [
        [PrimeVue, { unstyled: true }],
        ToastService,
        ConfirmationService,
        [VueQueryPlugin, { queryClient }],
        router,
      ],
      stubs: { teleport: true, RuleEditor: true },
    },
  }) as VueWrapper
  await vi.waitFor(async () => {
    await flushPromises()
    expect(tab!.find('.center').exists()).toBe(false)
  })
  await flushPromises()
  return tab
}

describe('the Data tab', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(NOW)
    for (const m of [latest, report, reports, samples, test]) m.mockReset()
    latest.mockResolvedValue(detail())
    reports.mockResolvedValue({
      items: [item(), item({ id: 'rep1', repeats: 0, evaluated: false, code: null, status: 'ok', message: 'earlier' })],
      page: 1,
      pageSize: 25,
      totalCount: 2,
    })
    samples.mockResolvedValue({
      fieldId: 'f1',
      range: '24h',
      from: at(NOW - 86_400_000),
      to: at(NOW),
      stepSeconds: 60,
      points: [
        { at: at(NOW - 180_000), min: 3.3, max: 3.4, last: 3.3, count: 1 },
        { at: at(NOW - 120_000), min: 3.2, max: 3.3, last: 3.2, count: 2 },
      ],
      thresholds: [{ rule: 'Disk space', operator: 'Ge', value: 5, severity: 'Fail' }],
    })
  })
  afterEach(() => {
    tab?.unmount()
    tab = null
    vi.useRealTimers()
  })

  it('shows the latest report: its headline, the fields, the tree with the rules’ marks, and the history', async () => {
    const t = await mountTab(advanced())
    expect(latest).toHaveBeenCalledWith(SENSOR_ID)
    const text = t.text()
    expect(text).toContain('×3 until')
    expect(text).toContain("the app's clock is 7 min behind")
    expect(text).toContain('10.0.0.5')
    expect(text).toContain('495')
    expect(text).toContain('Nightly backup done')
    // The field card, red for the failing rule on its path, with its chart.
    const card = t.find('.field-cards .card')
    expect(card.text()).toContain('Free space')
    expect(card.text()).toContain('3.2 GB')
    expect(card.classes()).toContain('tone-fail')
    expect(samples).toHaveBeenCalledWith(SENSOR_ID, 'f1', '24h')
    expect(t.find('.sample-chart polyline').exists()).toBe(true)
    expect(t.find('.sample-chart').text()).toContain('Disk space: ≥ 5 GB')
    // The tree, with the rule's mark on the value it judged; the app's text stays text.
    const mark = t
      .find('button[aria-label="Actions for disk.free_gb"]')
      .element.parentElement!.querySelector('.jt-mark')
    expect(mark?.textContent).toBe('✗')
    expect(t.find('.json-tree img').exists()).toBe(false)
    expect(t.find('.json-tree').text()).toContain('<img src=x onerror=alert(1)>')
    // The history, with an unjudged report.
    expect(reports).toHaveBeenCalled()
    expect(t.find('.history').text()).toContain('judging…')
    expect(t.find('.history').text()).toContain('earlier')
  })

  it('shows the raw report pretty-printed and highlighted, as text', async () => {
    const t = await mountTab(advanced())
    await t
      .findAll('button')
      .find((b) => b.text() === 'Raw')!
      .trigger('click')
    const raw = t.find('pre.json-highlight')
    expect(raw.text()).toContain('"free_gb": 3.2')
    expect(raw.findAll('span.hljs-attr').length).toBeGreaterThan(0)
    expect(raw.find('img').exists()).toBe(false)
    expect(t.findAll('button').some((b) => b.text() === 'Download')).toBe(true)
  })

  it('opens the rule editor from a value, with its natural rule', async () => {
    const t = await mountTab(advanced())
    await t.find('button[aria-label="Actions for disk.free_gb"]').trigger('click')
    await flushPromises()
    const add = t.findAll('[role="menuitem"]').find((i) => i.text() === 'Add rule…')!
    await add.find('[data-pc-section="itemcontent"]').trigger('click')
    const editor = t.findComponent({ name: 'RuleEditor' })
    expect(editor.props('visible')).toBe(true)
    expect(editor.props('initial')).toEqual({
      name: 'Disk free gb',
      path: 'disk.free_gb',
      operator: 'Ge',
      operand: '3.2',
    })
  })

  it('opens a report of the history in the tree, with its outcomes then', async () => {
    report.mockResolvedValue(
      detail(
        { body: JSON.stringify({ status: 'ok', disk: { free_gb: 9 } }), results: [], previousId: null },
        { id: 'rep1' },
      ),
    )
    const t = await mountTab(advanced())
    const row = t.findAll('.history tbody tr').find((r) => r.text().includes('earlier'))!
    await row.trigger('click')
    await vi.waitFor(async () => {
      await flushPromises()
      expect(report).toHaveBeenCalledWith(SENSOR_ID, 'rep1')
      expect(t.text()).toContain('An earlier report')
    })
    expect(t.find('.json-tree').text()).toContain('9')
    expect(t.find('.json-tree .jt-mark').exists()).toBe(false)
    await t
      .findAll('button')
      .find((b) => b.text() === 'Back to the latest')!
      .trigger('click')
    expect(t.text()).not.toContain('An earlier report')
  })

  it('shows an invalid report’s reason and the first 2 KB of what arrived, as text', async () => {
    const arrived = `{"status": "ok", <b>bad</b> ${'x'.repeat(3000)}`
    latest.mockResolvedValue(
      detail(
        { body: arrived, results: [] },
        { valid: false, errorKind: 'NotJson', error: "The report isn't valid JSON: line 1, position 17", code: 496 },
      ),
    )
    const t = await mountTab(advanced({ latest: { ...advanced().latest!, valid: false, code: 496 } }))
    const text = t.text()
    expect(text).toContain("The report isn't valid JSON: line 1, position 17")
    expect(text).toContain('What arrived (the first 2 KB)')
    const head = t.find('pre.head')
    expect(head.text().length).toBeLessThanOrEqual(2049)
    expect(head.find('b').exists()).toBe(false)
    expect(t.find('.json-tree').exists()).toBe(false)
  })

  it('before the first report: the report URL, and a sample to try the rules on', async () => {
    test.mockResolvedValue({
      code: 495,
      state: 'failing',
      message: '1 rule fails: Disk space (disk.free_gb = 3.2, must be at least 5)',
      results: [diskRule],
      fields: [],
      error: null,
      reportId: null,
    })
    const t = await mountTab(advanced({ latest: null, verdict: null, lastReportAt: null, reportCount: 0 }))
    expect(latest).not.toHaveBeenCalled()
    expect(t.text()).toContain('No report yet.')
    expect(t.text()).toContain('https://pnmt.example.com/report')
    await t.find('textarea#advanced-sample').setValue(JSON.stringify(body))
    await t.find('form').trigger('submit')
    await flushPromises()
    expect(test).toHaveBeenCalledWith(SENSOR_ID, { report: JSON.stringify(body) })
    expect(t.text()).toContain('1 rule fails: Disk space')
    expect(t.find('.outcomes').text()).toContain('disk.free_gb = 3.2')
    expect(t.find('.json-tree').text()).toContain('free_gb')
    expect(t.find('button[aria-label="Actions for disk.free_gb"]').exists()).toBe(true)
  })

  it('without Monitoring: Details on a sensor naming people: the verdict and outcomes, no data and no call for it', async () => {
    const withoutValues = { ...diskRule, actual: [], matches: [] }
    const a = advanced({ namesPeople: true, canSeeData: false })
    a.rules[0]!.last = withoutValues
    const t = await mountTab(a, false)
    expect(latest).not.toHaveBeenCalled()
    expect(reports).not.toHaveBeenCalled()
    expect(samples).not.toHaveBeenCalled()
    const text = t.text()
    expect(text).toContain('The data of this sensor names people: Monitoring: Details')
    expect(text).toContain('Disk space')
    expect(text).toContain('fails')
    expect(text).not.toContain('3.2')
  })
})

describe('the fields on the overview', () => {
  it('shows only the pinned fields, and nothing without any', async () => {
    samples.mockResolvedValue({
      fieldId: 'f1',
      range: '24h',
      from: at(NOW - 86_400_000),
      to: at(NOW),
      stepSeconds: 60,
      points: [],
      thresholds: [],
    })
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const mountCards = (a: S['AdvancedSensorDetail']) =>
      mountWithUi(FieldCards, {
        props: { sensor: sensor(a), pinnedOnly: true },
        global: { plugins: [[PrimeVue, { unstyled: true }], ToastService, [VueQueryPlugin, { queryClient }]] },
      })
    const a = advanced()
    a.fields.push({ ...a.fields[0]!, id: 'f2', name: 'Total', pinned: false, chart: false })
    const cards = mountCards(a)
    expect(cards.findAll('.card').map((c) => c.find('.label').text())).toEqual(['Free space'])
    cards.unmount()

    const none = mountCards(advanced({ fields: [{ ...a.fields[1]! }] }))
    expect(none.find('section').exists()).toBe(false)
    none.unmount()
  })
})

describe('the Data tab’s helpers', () => {
  it('marks the deciding values with the outcome, and the other matches by how the rule went', () => {
    const marks = marksFrom([
      {
        ...diskRule,
        name: 'No failed job',
        path: 'jobs[*].state',
        actual: [{ path: 'jobs[1].state', value: '"failed"' }],
        matches: ['jobs[0].state', 'jobs[1].state'],
        matchCount: 2,
      },
      { ...diskRule, ruleId: 'r9', name: 'Off rule', outcome: 'Off' },
    ])
    expect(marks.get('jobs[1].state')).toEqual([
      { outcome: 'Fail', name: 'No failed job', detail: 'must be at least 5' },
    ])
    expect(marks.get('jobs[0].state')).toEqual([
      { outcome: null, name: 'No failed job', detail: 'fails at other values' },
    ])
    expect(marks.has('disk.free_gb')).toBe(false)
  })

  it('suggests rules and fields from a value', () => {
    expect(ruleDraft({ path: 'jobs[*].state', value: 'running', segments: ['jobs', 1, 'state'], every: true })).toEqual(
      {
        name: 'Jobs state',
        path: 'jobs[*].state',
        operator: 'Eq',
        operand: 'running',
        quantifier: 'All',
      },
    )
    expect(ruleDraft({ path: 'jobs[1].state', value: 'failed', segments: ['jobs', 1, 'state'], every: false })).toEqual(
      {
        name: 'Jobs state',
        path: 'jobs[1].state',
        operator: 'Healthy',
        operand: null,
      },
    )
    expect(fieldDraft({ path: 'backup.size_bytes', value: 4821, segments: [], every: false }, 0)).toMatchObject({
      format: 'Bytes',
      chart: true,
    })
    expect(fieldDraft({ path: 'disk.free_gb', value: 3.2, segments: [], every: false }, 10)).toMatchObject({
      format: 'Auto',
      unit: 'GB',
      chart: false,
    })
    expect(fieldDraft({ path: 'backup.duration_s', value: 3712, segments: [], every: false }, 0).format).toBe(
      'Duration',
    )
    expect(fieldDraft({ path: 'status', value: 'UP', segments: [], every: false }, 0).format).toBe('Status')
    expect(fieldDraft({ path: 'finished', value: '2026-11-17T02:14:03Z', segments: [], every: false }, 0).format).toBe(
      'DateTime',
    )
  })
})
