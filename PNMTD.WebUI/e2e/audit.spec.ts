import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * The audit log (docs/audit-redmine-zabbix-plan.md §4, §12): a change is an entry with its changes; on a wide screen the
 * detail runs to its column's edge (the bug: it stopped at 24 rem); the old address redirects; the type filter knows every
 * event; a host's History shows it.
 */
async function api(page: Page, method: 'POST' | 'PUT', path: string, data: unknown) {
  const xsrf = (await page.context().cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value ?? ''
  const response = await page.request.fetch(path, {
    method,
    data,
    headers: { 'X-XSRF-TOKEN': decodeURIComponent(xsrf) },
  })
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy()
  return response.json()
}

test('a change is in the audit log, its detail fills its column, and the host has its history', async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1920, height: 1080 })
  await signIn(page)
  const name = `e2e-audit-${Date.now()}`
  const long = 'a location text long enough to need the whole column of the audit log on a wide screen'
  const host = await api(page, 'POST', '/api/v1/hosts', { name })
  await api(page, 'PUT', `/api/v1/hosts/${host.id}`, {
    name: `${name}-renamed`,
    location: long,
    notes: `${long}, and notes that say the same again, so that the detail is longer than any column`,
  })

  // The old address still works, with its filters.
  await page.goto(`/pnmt-admin/admin/auth-log?search=${encodeURIComponent(name)}`)
  await expect(page).toHaveURL(/\/admin\/audit-log\?/)
  await expect(page.getByRole('heading', { level: 1, name: 'Audit log' })).toBeVisible()
  const row = page.locator('tr', { hasText: 'Host updated' }).first()
  await expect(row).toBeVisible()

  // The detail runs to its column's edge: the span is as wide as its cell, and far wider than 24 rem (384 px).
  const cell = row.locator('td').nth(5)
  const span = cell.locator('.cell')
  const [cellWidth, spanWidth, scrollWidth] = await Promise.all([
    cell.evaluate((e) => e.getBoundingClientRect().width),
    span.evaluate((e) => e.getBoundingClientRect().width),
    span.evaluate((e) => e.scrollWidth),
  ])
  expect(spanWidth).toBeGreaterThan(700)
  expect(spanWidth).toBeGreaterThan(cellWidth - 40)
  expect(scrollWidth, 'the text is longer than the column, so the column is full').toBeGreaterThan(spanWidth)
  await shot(page, 'audit-log-wide')

  // A row opens with its changes, field by field.
  await row.locator('button.p-datatable-row-toggle-button').click()
  const changes = page.locator('table.changes')
  await expect(changes.getByRole('cell', { name: 'Location', exact: true })).toBeVisible()
  await expect(changes.getByRole('cell', { name: `${name}-renamed`, exact: true })).toBeVisible()
  await expect(page.getByText('PUT /api/v1/hosts/{id:guid}')).toBeVisible()
  await shot(page, 'audit-log-entry')

  // The type filter has every event of the catalog, not 23 of them.
  await page.getByRole('combobox', { name: 'Event type' }).click()
  await page.getByRole('searchbox').fill('join script')
  await expect(page.getByRole('option', { name: 'MikroTik join script viewed' })).toBeVisible()
  await page.keyboard.press('Escape')

  // The host's History.
  await page.goto(`/pnmt-admin/hosts/${host.id}/history`)
  await expect(page.getByRole('heading', { level: 1, name: `${name}-renamed` })).toBeVisible()
  await expect(page.getByText('Host created')).toBeVisible()
  await expect(page.getByText('Host updated')).toBeVisible()
  await shot(page, 'host-history')
  expect(problems).toEqual([])
})

test('a Viewer is refused a change, and it is in the audit log as Denied', async ({ page, browser }) => {
  await signIn(page)
  const name = `e2e-refused-${Date.now()}`
  const host = await api(page, 'POST', '/api/v1/hosts', { name })

  const viewer = await browser.newPage()
  await signIn(viewer, 'viewer')
  const xsrf = (await viewer.context().cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value ?? ''
  const refused = await viewer.request.fetch(`/api/v1/hosts/${host.id}`, {
    method: 'DELETE',
    headers: { 'X-XSRF-TOKEN': decodeURIComponent(xsrf) },
  })
  expect(refused.status()).toBe(403)
  expect((await refused.json()).code).toBe('missing_right')

  await page.goto(`/pnmt-admin/admin/audit-log?outcome=Denied&search=${encodeURIComponent('e2e-viewer')}`)
  await expect(page.locator('tr', { hasText: 'Host deleted' }).filter({ hasText: 'Denied' }).first()).toBeVisible()
})
