import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises, type VueWrapper } from '@vue/test-utils'
import AutoComplete from 'primevue/autocomplete'
import PrimeVue from 'primevue/config'
import Select from 'primevue/select'
import ToastService from 'primevue/toastservice'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/client'
import { api, type S } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import RuleEditor from './RuleEditor.vue'
import { advanced, PATHS, result, rule, RULE_ID, sensor, SENSOR_ID, tested } from './rules.fixture'

/**
 * The rule editor (docs/advanced-sensor-plan.md §10.3): the operators grouped and the ones for the path's kind first,
 * every one reachable; the operand fields with their hints and the editor's own check; the live outcome against the latest
 * report (POST test, debounced, only for a complete draft) with the server's 400 under the fields; and what Save sends.
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
        paths: vi.fn(),
        test: vi.fn(),
        rules: { ...actual.api.advanced.rules, create: vi.fn(), update: vi.fn() },
      },
    },
  }
})
const paths = vi.mocked(api.advanced.paths)
const test = vi.mocked(api.advanced.test)
const create = vi.mocked(api.advanced.rules.create)
const update = vi.mocked(api.advanced.rules.update)

type Props = {
  sensor?: S['SensorDetail']
  rule?: S['AdvancedRuleItem'] | null
  initial?: Partial<S['AdvancedRuleRequest']> | null
}

async function open(props: Props = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const editor = mountWithUi(RuleEditor, {
    props: { visible: true, sensor: props.sensor ?? sensor(), rule: props.rule, initial: props.initial },
    global: {
      plugins: [[PrimeVue, { unstyled: true }], ToastService, [VueQueryPlugin, { queryClient }]],
      stubs: { teleport: true },
    },
  })
  await flushPromises()
  return editor
}

const operatorSelect = (editor: VueWrapper) =>
  editor.findAllComponents(Select).find((s) => s.props('inputId') === 'rule-operator')!
type Group = { label: string; items: { value: string; suits: boolean }[] }
const groups = (editor: VueWrapper) => operatorSelect(editor).props('options') as Group[]
const group = (editor: VueWrapper, label: string) => groups(editor).find((g) => g.label === label)!
async function chooseOperator(editor: VueWrapper, op: string) {
  operatorSelect(editor).vm.$emit('update:modelValue', op)
  await flushPromises()
}
const saveButton = (editor: VueWrapper) =>
  editor.findAll('button').find((b) => ['Add rule', 'Save rule'].includes(b.text()))!
const waitForPreview = (editor: VueWrapper, text: string) =>
  vi.waitFor(
    async () => {
      await flushPromises()
      expect(editor.find('.preview').text()).toContain(text)
    },
    { timeout: 3000 },
  )

const diskRule = { name: 'Disk space', path: 'disk.free_gb', operator: 'Ge', operand: '5' } as const

describe('the rule editor', () => {
  beforeEach(() => {
    paths.mockReset()
    test.mockReset()
    create.mockReset()
    update.mockReset()
    paths.mockResolvedValue(PATHS)
    test.mockResolvedValue(tested([result()]))
  })

  it("lists the operators grouped, those for the path's kind first, and every one", async () => {
    const editor = await open({ initial: diskRule })
    expect(groups(editor).map((g) => g.label)).toEqual(['Compare', 'Text', 'Kind', 'State', 'Age', 'Count', 'Change'])
    expect(groups(editor).flatMap((g) => g.items)).toHaveLength(35)
    // A number: of the text operators, only the lists suit it, and they come first.
    expect(group(editor, 'Text').items.map((i) => [i.value, i.suits])).toEqual([
      ['OneOf', true],
      ['NotOneOf', true],
      ['Contains', false],
      ['NotContains', false],
      ['StartsWith', false],
      ['EndsWith', false],
      ['Matches', false],
      ['NotMatches', false],
    ])
    expect(group(editor, 'Kind').items[0]!.value).toBe('Exists')
    expect(editor.text()).toContain('The value is a number: the conditions for it come first in each group.')
    expect(editor.text()).toContain('Now 3.2 (a number), in the latest report.')
    editor.unmount()
  })

  it('takes the kind of a boolean, and of an element through [*]', async () => {
    const editor = await open({ initial: { name: 'Verified', path: 'backup.verified', operator: 'IsTrue' } })
    expect(
      group(editor, 'Kind')
        .items.slice(0, 2)
        .map((i) => i.value),
    ).toEqual(['IsTrue', 'IsFalse'])
    expect(group(editor, 'Kind').items.find((i) => i.value === 'IsNull')!.suits).toBe(false)
    editor.unmount()

    const jobs = await open({ initial: { name: 'Job', path: 'jobs[3].state', operator: 'Eq', operand: 'failed' } })
    expect(
      group(jobs, 'Text')
        .items.slice(0, 6)
        .every((i) => i.suits),
    ).toBe(true)
    expect(group(jobs, 'Compare').items.find((i) => i.value === 'Ge')!.suits).toBe(true)
    expect(group(jobs, 'Count').items.every((i) => i.suits)).toBe(true)
    jobs.unmount()
  })

  it("shows the operand's fields with their hints, and the editor's own check before the server's", async () => {
    const editor = await open({ initial: diskRule })
    expect(editor.find('#rule-operand').exists()).toBe(true)
    expect(editor.text()).toContain('A number, such as 5 or 0.25.')

    await chooseOperator(editor, 'Between')
    await editor.find('#rule-operand').setValue('10')
    await editor.find('#rule-operand2').setValue('5')
    expect(editor.find('[role="alert"]').text()).toBe('The second number must be at least the first.')

    await chooseOperator(editor, 'OlderThan')
    await editor.find('#rule-operand').setValue('26h')
    expect(editor.text()).toContain('A duration: 90s, 15m, 26h or 7d')
    expect(editor.text()).toContain('That is 1 d 2 h.')
    expect(editor.find('#rule-operand2').exists()).toBe(false)

    await chooseOperator(editor, 'Matches')
    await editor.find('#rule-operand').setValue('(')
    expect(editor.find('[role="alert"]').text()).toContain('Not a valid pattern')
    expect(editor.find('#rule-ignore-case').exists()).toBe(true)

    await chooseOperator(editor, 'Healthy')
    expect(editor.find('#rule-operand').exists()).toBe(false)
    expect(editor.find('#rule-ignore-case').exists()).toBe(false)
    expect(editor.text()).toContain('Healthy: ok, pass, passed')
    editor.unmount()
  })

  it('asks "for several matches" only for a path with a wildcard', async () => {
    const plain = await open({ initial: diskRule })
    expect(plain.text()).not.toContain('For several matches')
    plain.unmount()

    const jobs = await open({ initial: { name: 'No failed job', path: 'jobs[*].state', operator: 'Eq', operand: 'x' } })
    expect(jobs.text()).toContain('For several matches')
    await jobs.find('#rule-quantifier-None').setValue(true)
    await chooseOperator(jobs, 'CountGe')
    expect(jobs.text()).not.toContain('For several matches')
    expect(jobs.text()).not.toContain('When missing')
    jobs.unmount()
  })

  it('shows what the latest report makes of the draft, trying it once the typing pauses', async () => {
    const editor = await open({ initial: diskRule })
    await waitForPreview(editor, 'disk.free_gb = 3.2 → fails (must be at least 5)')
    expect(test).toHaveBeenCalledTimes(1)
    expect(test).toHaveBeenCalledWith(SENSOR_ID, {
      rules: [
        {
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
        },
      ],
    })

    // Three quick changes: one try, with the last.
    test.mockResolvedValue(
      tested([result({ outcome: 'Pass', detail: null, condition: 'disk.free_gb ≥ 3', actual: [] })]),
    )
    for (const value of ['4', '3.', '3']) await editor.find('#rule-operand').setValue(value)
    await waitForPreview(editor, 'disk.free_gb → holds')
    expect(test).toHaveBeenCalledTimes(2)
    expect(test.mock.calls[1]![1].rules![0]!.operand).toBe('3')
    editor.unmount()
  })

  it("doesn't try a draft the editor already knows is wrong, and shows the server's 400 under the field", async () => {
    const editor = await open({ initial: { ...diskRule, operand: 'five' } })
    await new Promise((r) => setTimeout(r, 800))
    await flushPromises()
    expect(test).not.toHaveBeenCalled()
    expect(editor.find('.preview').text()).toContain('Complete the rule')
    expect(editor.find('[role="alert"]').text()).toBe('A number, such as 5 or 0.25.')

    test.mockRejectedValue(
      new ApiError(400, {
        code: 'validation_failed',
        errors: { 'rules[0].path': ["Not a valid path: ']' expected at position 5."] },
      }),
    )
    await editor.find('#rule-operand').setValue('5')
    await editor.find('#rule-path').setValue('jobs[')
    await waitForPreview(editor, 'The server doesn’t take this rule yet')
    expect(editor.find('[role="alert"]').text()).toBe("Not a valid path: ']' expected at position 5.")
    editor.unmount()
  })

  it('says when there is no report to try the draft against', async () => {
    const editor = await open({ sensor: sensor(advanced({ latest: null, lastReportAt: null })), initial: diskRule })
    expect(editor.find('.preview').text()).toContain('No report yet')
    expect(editor.findComponent(AutoComplete).exists()).toBe(false)
    expect(editor.find('#rule-path').exists()).toBe(true)
    expect(paths).not.toHaveBeenCalled()
    editor.unmount()

    const hidden = await open({ sensor: sensor(advanced({ canSeeData: false, namesPeople: true })), initial: diskRule })
    expect(hidden.find('.preview').text()).toContain('needs Monitoring: Details')
    hidden.unmount()
    await new Promise((r) => setTimeout(r, 700))
    expect(test).not.toHaveBeenCalled()
  })

  it('adds a rule with what the editor shows, and hands the new detail on', async () => {
    const detail = advanced({ rules: [rule(), rule({ id: 'new-rule', name: 'Status', path: 'status' })] })
    create.mockResolvedValue(detail)
    const editor = await open({ initial: diskRule })
    await editor.find('#rule-note').setValue('  Clean up /var/backups  ')
    await editor.find('#rule-severity-Warn').setValue(true)
    await editor.find('#rule-missing-Pass').setValue(true)
    await saveButton(editor).trigger('click')
    await flushPromises()

    expect(create).toHaveBeenCalledWith(SENSOR_ID, {
      name: 'Disk space',
      enabled: true,
      note: 'Clean up /var/backups',
      path: 'disk.free_gb',
      operator: 'Ge',
      operand: '5',
      operand2: null,
      ignoreCase: false,
      quantifier: 'All',
      whenMissing: 'Pass',
      severity: 'Warn',
    })
    expect(editor.emitted('saved')?.[0]).toEqual([detail])
    expect(editor.emitted('update:visible')?.at(-1)).toEqual([false])
    editor.unmount()
  })

  it("changes a rule, sending only what its operator uses, and maps the server's 400 to the fields", async () => {
    update.mockRejectedValueOnce(
      new ApiError(400, { code: 'validation_failed', errors: { name: ['A name of 1 to 200 characters.'] } }),
    )
    const editor = await open({ rule: rule({ operator: 'Between', operand: '1', operand2: '9' }) })
    expect(saveButton(editor).text()).toBe('Save rule')
    await chooseOperator(editor, 'Healthy')
    await saveButton(editor).trigger('click')
    await flushPromises()
    expect(update).toHaveBeenCalledWith(
      SENSOR_ID,
      RULE_ID,
      expect.objectContaining({ operator: 'Healthy', operand: null, operand2: null }),
    )
    expect(editor.find('[role="alert"]').text()).toBe('A name of 1 to 200 characters.')
    expect(editor.emitted('saved')).toBeUndefined()

    // An empty name never leaves the editor.
    await editor.find('#rule-name').setValue('  ')
    await saveButton(editor).trigger('click')
    await flushPromises()
    expect(update).toHaveBeenCalledTimes(1)
    editor.unmount()
  })
})
