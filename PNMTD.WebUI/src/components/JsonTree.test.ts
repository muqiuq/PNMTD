import { flushPromises, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mountWithUi } from '@/test/mount'
import JsonTree from './JsonTree.vue'
import {
  changesSince,
  filterTree,
  keysOf,
  parseJson,
  prettyJson,
  stringifyOrdered,
  tableShape,
  type JsonMark,
} from './jsonTree/jsonTree'

/**
 * JsonTree (docs/advanced-sensor-plan.md §10.3, *The report*): a report readable with no setup — sections in the app's
 * order, tables for lists of objects and for objects of same-shaped objects (IETF checks, Spring components), chips,
 * times, pages past 100 items, the rules' marks, the filter, the changes since a previous report, the menu per value — and
 * the app's text never becoming HTML (§13).
 */
const NOW = Date.parse('2026-11-17T12:00:00Z')

const report = {
  status: 'ok',
  message: 'Nightly backup done',
  time: '2026-11-17T02:14:03+01:00',
  backup: { finished: '2026-11-17T02:14:03+01:00', size_bytes: 48213307392, duration_s: 3712 },
  disk: { free_gb: 3.2, total_gb: 2000 },
  jobs: [
    { name: 'db', state: 'done', errors: 0 },
    { name: 'files', state: 'failed', errors: 3 },
  ],
}

let tree: VueWrapper | null = null
function mountTree(props: { value: unknown } & Record<string, unknown>) {
  tree = mountWithUi(JsonTree, { props, global: { stubs: { teleport: true } } }) as VueWrapper
  return tree
}

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

/** The actions button of a value, by its path. */
const actions = (w: VueWrapper, path: string) => w.find(`button[aria-label="Actions for ${path}"]`)

async function pickMenu(w: VueWrapper, label: string) {
  await flushPromises()
  const item = w.findAll('[role="menuitem"]').find((i) => i.text() === label)
  expect(item, `menu item ${label}`).toBeTruthy()
  await item!.find('[data-pc-section="itemcontent"]').trigger('click')
}

describe('JsonTree', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(NOW)
  })
  afterEach(() => {
    tree?.unmount()
    tree = null
    vi.useRealTimers()
  })

  it('shows objects as sections in the app’s order, and a list of objects as a sortable table', async () => {
    const w = mountTree({ value: report })
    const keys = w.findAll(
      '.jt-body > .jt-members > .jt-row > .jt-key, .jt-body > .jt-members > .jt-section > .jt-head .jt-key',
    )
    expect(keys.map((k) => k.text())).toEqual(['status', 'message', 'time', 'backup', 'disk', 'jobs'])
    // The first two levels are open: the members of backup and disk show.
    expect(w.text()).toContain('free_gb')
    expect(w.text()).toContain('3.2')

    const table = w.find('table')
    expect(table.findAll('thead th').map((th) => th.text())).toEqual(['#', 'name', 'state', 'errors'])
    const rows = () => table.findAll('tbody tr').map((tr) => tr.findAll('td')[0]!.text())
    expect(rows()).toEqual(['db', 'files'])
    await table
      .findAll('thead button')
      .find((b) => b.text() === 'name')!
      .trigger('click')
    await table
      .findAll('thead button')
      .find((b) => b.text() === 'name')!
      .trigger('click')
    expect(rows()).toEqual(['files', 'db'])
    expect(table.findAll('thead th')[1]!.attributes('aria-sort')).toBe('descending')
  })

  it('makes tables of objects whose members share a shape: IETF checks with their unit, Spring components', () => {
    const ietf = {
      status: 'pass',
      checks: {
        'db:responseTime': [{ componentType: 'datastore', observedValue: 250, observedUnit: 'ms', status: 'pass' }],
        'disk:utilization': [{ componentType: 'system', observedValue: 91, observedUnit: 'percent', status: 'warn' }],
      },
    }
    const w = mountTree({ value: ietf })
    const table = w.find('table')
    expect(table.findAll('thead th').map((th) => th.text())).toEqual([
      'Name',
      'componentType',
      'observedValue',
      'status',
    ])
    const first = table.findAll('tbody tr')[0]!
    expect(first.find('th').text()).toContain('db:responseTime')
    expect(first.text()).toContain('250')
    expect(first.text()).toContain('ms')
    // Health words in their colour.
    expect(table.findAll('.jt-health').map((h) => [h.text(), h.classes()])).toEqual([
      ['pass', expect.arrayContaining(['tone-ok'])],
      ['warn', expect.arrayContaining(['tone-warn'])],
    ])
    w.unmount()

    const spring = mountTree({
      value: {
        status: 'UP',
        components: { db: { status: 'UP', details: { database: 'PostgreSQL' } }, ping: { status: 'UP' } },
      },
    })
    const springTable = spring.find('table')
    expect(springTable.findAll('thead th').map((th) => th.text())).toEqual(['Name', 'status', 'details'])
    // A nested value opens in its cell.
    const toggle = springTable.find('.jt-cell-toggle')
    expect(toggle.text()).toContain('1 key')
    expect(spring.text()).not.toContain('PostgreSQL')
    return toggle.trigger('click').then(() => expect(spring.text()).toContain('PostgreSQL'))
  })

  it('keeps sections for objects of different shapes, and for a single object', () => {
    expect(tableShape({ backup: { finished: 'x', size: 1 }, disk: { free: 2 } }, ['x'])).toBeNull()
    expect(tableShape(report, [])).toBeNull()
    // One object in an object: its members, unless it is an IETF check.
    expect(tableShape({ build: { sha: 'abc', date: '2026-11-17' } }, ['meta'])).toBeNull()
    expect(tableShape({ 'db:ping': [{ status: 'pass' }] }, ['checks'])?.rows.map((r) => r.path)).toEqual([
      'checks["db:ping"][0]',
    ])
  })

  it('shows a list of scalars as chips, +n more past 50', async () => {
    const tags = Array.from({ length: 60 }, (_, i) => `tag-${i}`)
    const w = mountTree({ value: { tags } })
    expect(w.findAll('.jt-chip')).toHaveLength(50)
    const more = w.findAll('button').find((b) => b.text() === '+10 more')!
    await more.trigger('click')
    expect(w.findAll('.jt-chip')).toHaveLength(60)
  })

  it('pages a list past 100 items', async () => {
    const items = Array.from({ length: 250 }, (_, i) => ({ n: i, label: `item ${i}` }))
    const w = mountTree({ value: { items } })
    expect(w.findAll('tbody tr')).toHaveLength(100)
    expect(w.find('.jt-pager').text()).toContain('1–100 of 250')
    await w.find('button[aria-label="Next page"]').trigger('click')
    expect(w.find('.jt-pager').text()).toContain('101–200 of 250')
    expect(w.findAll('tbody tr')[0]!.text()).toContain('item 100')
  })

  it('shows times in the browser’s zone with how long ago, the original on hover; booleans and null', () => {
    const w = mountTree({ value: { finished: '2026-11-17T02:14:03+01:00', ok: true, broken: false, note: null } })
    const time = w.find('time')
    expect(time.attributes('title')).toBe('2026-11-17T02:14:03+01:00')
    expect(time.attributes('datetime')).toBe('2026-11-17T01:14:03.000Z')
    expect(w.find('.jt-ago').text()).toBe('11 h ago')
    const bools = w.findAll('.jt-bool')
    expect(bools.map((b) => b.text())).toEqual(['✓true', '✗false'])
    expect(w.find('.jt-null').text()).toBe('null')
  })

  it('collapses long multi-line text past 10 lines', async () => {
    const log = Array.from({ length: 25 }, (_, i) => `line ${i + 1}`).join('\n')
    const w = mountTree({ value: { log } })
    expect(w.find('.jt-text').text()).toContain('line 10')
    expect(w.find('.jt-text').text()).not.toContain('line 11')
    await w
      .findAll('button')
      .find((b) => b.text() === 'Show all 25 lines')!
      .trigger('click')
    expect(w.find('.jt-text').text()).toContain('line 25')
  })

  it('marks the values a rule looks at with its outcome, its name on hover', () => {
    const marks = new Map<string, JsonMark[]>([
      ['disk.free_gb', [{ outcome: 'Fail', name: 'Disk space', detail: 'must be at least 5' }]],
      ['jobs[1].state', [{ outcome: 'Fail', name: 'No failed job', detail: null }]],
      ['jobs[0].state', [{ outcome: null, name: 'No failed job', detail: 'fails at other values' }]],
      ['status', [{ outcome: 'Pass', name: "The app's status", detail: 'status is healthy' }]],
    ])
    const w = mountTree({ value: report, marks })
    const mark = (path: string) => actions(w, path).element.parentElement!.querySelector('.jt-mark')!
    expect(mark('disk.free_gb').textContent).toBe('✗')
    expect(mark('disk.free_gb').getAttribute('title')).toBe('Disk space: fails (must be at least 5)')
    expect(mark('disk.free_gb').classList).toContain('tone-fail')
    expect(mark('jobs[1].state').textContent).toBe('✗')
    expect(mark('jobs[0].state').textContent).toBe('•')
    expect(mark('status').textContent).toBe('✓')
    expect(mark('status').getAttribute('aria-label')).toBe("The app's status: holds (status is healthy)")
  })

  it('filters over keys and values, showing the way to each match', async () => {
    const w = mountTree({ value: report })
    await w.find('input[aria-label="Filter keys and values"]').setValue('free')
    await wait(200)
    await flushPromises()
    expect(w.text()).toContain('free_gb')
    expect(w.text()).not.toContain('total_gb')
    expect(w.text()).not.toContain('Nightly backup done')
    expect(w.find('table').exists()).toBe(false)
    expect(w.text()).toContain('1 match')

    await w.find('input[aria-label="Filter keys and values"]').setValue('failed')
    await wait(200)
    await flushPromises()
    expect(w.findAll('tbody tr')).toHaveLength(1)
    expect(w.find('tbody').text()).toContain('files')
  })

  it('filters a table of checks by their names', async () => {
    const w = mountTree({
      value: {
        checks: {
          'db:ping': [{ status: 'pass', observedValue: 2 }],
          'disk:free': [{ status: 'warn', observedValue: 9 }],
        },
      },
    })
    expect(w.findAll('tbody tr')).toHaveLength(2)
    await w.find('input[aria-label="Filter keys and values"]').setValue('disk:')
    await wait(200)
    await flushPromises()
    expect(w.findAll('tbody tr').map((r) => r.find('th').text())).toEqual(['disk:free'])
  })

  it('marks what changed since the previous report', () => {
    const previous = { ...report, disk: { free_gb: 3.5, total_gb: 2000 }, old: 1 }
    const w = mountTree({ value: report, previous })
    const changed = w.findAll('.jt-changed')
    expect(changed).toHaveLength(1)
    expect(changed[0]!.text()).toContain('3.2')
    expect(changed[0]!.attributes('title')).toBe('Before: 3.5')
    expect(w.text()).toContain('1 value changed or new, 1 gone since the previous report.')
  })

  it('shows what the app sends as text, never as HTML', () => {
    const w = mountTree({
      value: { note: '<img src=x onerror=alert(1)>', '<b>key</b>': 'https://example.com/<script>' },
    })
    expect(w.find('img').exists()).toBe(false)
    expect(w.find('b').exists()).toBe(false)
    expect(w.find('script').exists()).toBe(false)
    expect(w.find('a').exists()).toBe(false)
    expect(w.text()).toContain('<img src=x onerror=alert(1)>')
    expect(w.text()).toContain('<b>key</b>')
  })

  it('offers Copy path and Copy value, and with Edit Add rule… (also over every element) and Add field…', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    const w = mountTree({ value: report, canWrite: true })

    await actions(w, 'disk.free_gb').trigger('click')
    await pickMenu(w, 'Copy path')
    expect(writeText).toHaveBeenLastCalledWith('disk.free_gb')

    await actions(w, 'disk.free_gb').trigger('click')
    await pickMenu(w, 'Add rule…')
    expect(w.emitted('add-rule')![0]![0]).toEqual({
      path: 'disk.free_gb',
      value: 3.2,
      segments: ['disk', 'free_gb'],
      every: false,
    })

    await actions(w, 'jobs[1].state').trigger('click')
    await pickMenu(w, 'Add rule for every element…')
    expect(w.emitted('add-rule')![1]![0]).toMatchObject({ path: 'jobs[*].state', value: 'failed', every: true })

    await actions(w, 'backup.duration_s').trigger('click')
    await pickMenu(w, 'Add field…')
    expect(w.emitted('add-field')![0]![0]).toMatchObject({ path: 'backup.duration_s', value: 3712 })
  })

  it('offers no changes to a viewer', async () => {
    const w = mountTree({ value: report })
    await actions(w, 'disk.free_gb').trigger('click')
    await flushPromises()
    const labels = w.findAll('[role="menuitem"]').map((i) => i.text())
    expect(labels).toEqual(['Copy path', 'Copy value'])
  })
})

describe('the tree’s helpers', () => {
  it('keeps the app’s key order, integer-like keys too', () => {
    const value = parseJson('{"b":1,"10":{"z":1,"2":2},"a":[{"9":1,"x":2}]}') as Record<string, unknown>
    expect(keysOf(value)).toEqual(['b', '10', 'a'])
    expect(keysOf(value['10'] as object)).toEqual(['z', '2'])
    expect(keysOf((value.a as object[])[0]!)).toEqual(['9', 'x'])
    expect(stringifyOrdered(value['10'])).toBe('{\n  "z": 1,\n  "2": 2\n}')
    // A key named __proto__ is data, as JSON.parse has it.
    const proto = parseJson('{"1":1,"__proto__":{"polluted":true}}') as Record<string, unknown>
    expect(keysOf(proto)).toEqual(['1', '__proto__'])
    expect(({} as Record<string, unknown>).polluted).toBeUndefined()
    expect(() => parseJson('{"a":')).toThrow()
  })

  it('pretty-prints the text as received: its order and its numbers as written', () => {
    expect(prettyJson('{"b":3.20,"a":[1e3,{}],"s":"x, y: {z}","e":[]}')).toBe(
      '{\n  "b": 3.20,\n  "a": [\n    1e3,\n    {}\n  ],\n  "s": "x, y: {z}",\n  "e": []\n}',
    )
    expect(prettyJson('not json')).toBe('not json')
  })

  it('finds the changes and the filter’s matches by position', () => {
    const changes = changesSince({ a: 1, b: [1, 2], c: { d: 'x' } }, { a: 1, b: [1, 3, 4], c: { d: 'y', e: 1 } })
    expect([...changes.changed.keys()]).toEqual(['b[1]', 'c.d'])
    expect(changes.removed).toBe(2)
    expect(changes.inside.has('c')).toBe(true)

    const filter = filterTree({ disk: { free_gb: 3.2 }, jobs: [{ state: 'failed' }] }, 'FAIL')!
    expect([...filter.show].sort()).toEqual(['', 'jobs', 'jobs[0]', 'jobs[0].state'])
    expect(filter.matches).toBe(1)
    expect(filterTree({}, '  ')).toBeNull()
  })
})
