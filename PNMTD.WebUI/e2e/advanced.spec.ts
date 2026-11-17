import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Advanced sensors (docs/advanced-sensor-plan.md §12, E2E): a sensor made in the form opens on its Data tab; a report posted
 * with the token in the header shows there as a tree; *Add rule…* on a value writes the rule; a failing report turns the
 * sensor 495 with the rule failing on the Rules tab; a good report brings 295 back; and the history compares a report with
 * the one before it. The reports go through Playwright's request, as an app's would; the evaluator judges each within a
 * second (tools/e2e.sh turns the 5-second rate limit off for the stack).
 */
test.describe.configure({ mode: 'serial' })

let problems: string[] = []
test.beforeEach(({ page }) => {
  problems = collectProblems(page)
})
test.afterEach(() => expect(problems).toEqual([]))

async function api(page: Page, method: 'GET' | 'POST' | 'PUT' | 'DELETE', url: string, data?: unknown) {
  const xsrf = (await page.context().cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value ?? ''
  const response = await page.request.fetch(url, {
    method,
    data,
    headers: { 'X-XSRF-TOKEN': decodeURIComponent(xsrf) },
  })
  expect(response.ok(), `${method} ${url}: ${response.status()} ${await response.text()}`).toBe(true)
  return response.status() === 204 ? null : response.json()
}

const run = Date.now().toString(36)
let hostId = ''
let sensorId = ''
let token = ''

/** A report as an app sends it: anonymous, the token in the header, no cookie. */
async function report(page: Page, body: unknown) {
  const response = await page.request.fetch('/report', {
    method: 'POST',
    data: JSON.stringify(body),
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', Cookie: '' },
  })
  expect(response.status(), await response.text()).toBe(202)
  return response.json()
}

/** The sensor's code once the evaluator judged the latest report. */
async function settled(page: Page, code: number) {
  await expect
    .poll(async () => (await api(page, 'GET', `/api/v1/sensors/${sensorId}`)).lastCode, {
      timeout: 20_000,
      intervals: [500],
    })
    .toBe(code)
}

const tabs = (page: Page) => page.getByRole('navigation', { name: 'Sensor pages' })

const sample = (free: number, state = 'done') => ({
  status: 'ok',
  message: 'Nightly backup done',
  disk: { free_gb: free, total_gb: 2000 },
  jobs: [
    { name: 'db', state: 'done', errors: 0 },
    { name: 'files', state, errors: 0 },
  ],
})

test('an Advanced sensor made in the form, a report on its Data tab, and a rule from a value', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  // A disabled host: its sensor's failures stay off the dashboard, whose spec counts the seed's six problems.
  hostId = (await api(page, 'POST', '/api/v1/hosts', { name: `e2e-app-${run}`, enabled: false })).id

  await page.goto(`/pnmt-admin/hosts/${hostId}/sensors/new?type=ADVANCED`)
  await expect(page.getByRole('heading', { level: 1, name: 'New sensor' })).toBeVisible()
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill('nightly backup')
  await expect(page.getByLabel("Add a rule for the app's own status")).toBeChecked()
  await page.getByRole('button', { name: 'Create sensor' }).click()
  await page.waitForURL(/\/sensors\/[0-9a-f-]+\/data$/)
  sensorId = /\/sensors\/([0-9a-f-]+)\/data$/.exec(page.url())![1]!
  await expect(page.getByRole('heading', { level: 1, name: 'nightly backup' })).toBeVisible()
  // No report yet: the URL with the token in the header first, and a sample to paste.
  await expect(page.getByText('Report URL', { exact: true })).toBeVisible()
  await expect(page.getByText('Paste a sample', { exact: true })).toBeVisible()
  token = (await api(page, 'GET', `/api/v1/sensors/${sensorId}`)).integration.writeToken

  const answer = await report(page, sample(412.5))
  expect(answer.preview.code).toBe(295)
  await settled(page, 295)
  await page.reload()
  const tree = page.getByRole('region', { name: "The report's values" })
  await expect(tree).toContainText('free_gb')
  await expect(tree).toContainText('412.5')
  await expect(page.getByRole('region', { name: "The report's headline" })).toContainText('Nightly backup done')
  await shot(page, 'advanced-data')

  // Add rule… on disk.free_gb: the path, at least its value, which the person raises to 100.
  await tree.getByRole('button', { name: 'Actions for disk.free_gb' }).click()
  await page.getByRole('menuitem', { name: 'Add rule…' }).click()
  const editor = page.getByRole('dialog', { name: 'Add rule' })
  await expect(editor.getByRole('combobox', { name: 'Path' })).toHaveValue('disk.free_gb')
  await expect(editor.getByRole('textbox', { name: 'Value' })).toHaveValue('412.5')
  await editor.getByRole('textbox', { name: 'Name' }).fill('Disk space')
  await editor.getByRole('textbox', { name: 'Value' }).fill('100')
  await expect(editor.getByText(/412\.5/).last()).toBeVisible()
  await editor.getByRole('button', { name: 'Add rule' }).click()
  await expect(editor).toBeHidden()
  await settled(page, 295)
})

test('a failing report turns it 495, the Rules tab names the rule, and a good one brings it back', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)

  const answer = await report(page, sample(3.2, 'failed'))
  expect(answer.preview.code).toBe(495)
  expect(answer.preview.failed).toEqual(['Disk space'])
  await settled(page, 495)
  await page.goto(`/pnmt-admin/sensors/${sensorId}/rules`)
  await expect(tabs(page).getByRole('link', { name: /Rules/ })).toBeVisible()
  const row = page.getByRole('row', { name: /Disk space/ })
  await expect(row).toContainText('disk.free_gb ≥ 100')
  await expect(row).toContainText('3.2')
  await expect(page.getByRole('region', { name: 'Report status' })).toContainText('1 fails')
  await shot(page, 'advanced-rules')

  await report(page, sample(450))
  await settled(page, 295)
  const events = await api(page, 'GET', `/api/v1/sensors/${sensorId}/events?pageSize=10&sort=created`)
  expect(events.items.map((e: { code: number }) => e.code)).toEqual([295, 495, 295])
  expect(events.items[1].message).toBe('1 rule fails: Disk space (disk.free_gb = 3.2, must be at least 100)')
})

test('the history compares a report with the one before it', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto(`/pnmt-admin/sensors/${sensorId}/data`)
  const history = page.getByRole('table').filter({ hasText: 'Received' }).last()
  await expect(history.getByRole('row')).toHaveCount(4)
  await history
    .getByRole('button', { name: /Compare the report of .* with the previous/ })
    .first()
    .click()
  const compare = page.getByRole('dialog')
  await expect(compare).toContainText('450')
  await expect(compare).toContainText('3.2')
  await shot(page, 'advanced-compare')
  await page.keyboard.press('Escape')

  // The sensor and its host go; its reports and rules with it.
  await api(page, 'DELETE', `/api/v1/sensors/${sensorId}`)
  await api(page, 'DELETE', `/api/v1/hosts/${hostId}`)
})
