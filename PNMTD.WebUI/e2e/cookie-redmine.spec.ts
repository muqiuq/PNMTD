import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Admin → Cookie → Redmine with its own right (docs/cookie-redmine-tab-plan.md §3, §4, §6): someone with only Redmine: Manage sees
 * Admin → Cookie with the Redmine tab alone, even when they ask for another tab; links a PNMT user to a Redmine user picked from
 * Redmine's members, and unlinks them; and sees the away watch's settings with its switch read only. Skipped without Redmine or
 * Cookie.
 */
async function api(page: Page, method: 'POST' | 'PUT' | 'DELETE', path: string, data?: unknown) {
  const xsrf = (await page.context().cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value ?? ''
  const response = await page.request.fetch(path, {
    method,
    data,
    headers: { 'X-XSRF-TOKEN': decodeURIComponent(xsrf) },
  })
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy()
  return response.status() === 204 ? null : response.json()
}

test('Redmine: Manage alone opens the Redmine tab, links a user, and sees the away watch', async ({
  page,
  browser,
}) => {
  test.setTimeout(120_000)
  await signIn(page)
  const info = await (await page.request.get('/api/v1/system/info')).json()
  test.skip(!info.redmine, 'Redmine is not configured for this stack')
  const status = await (await page.request.get('/api/v1/cookie/status')).json()
  test.skip(!status.cookie.enabled, 'Cookie__Enabled is not set for this stack')

  // A group with Redmine: Manage only, for the viewer.
  const users = await (await page.request.get('/api/v1/users?search=e2e-viewer&pageSize=5')).json()
  const viewerId = users.items.find((u: { username: string }) => u.username === 'e2e-viewer').id
  const group = await api(page, 'POST', '/api/v1/groups', {
    name: `e2e-redmine-managers-${Date.now()}`,
    rights: { Redmine: 'Manage' },
  })
  await api(page, 'PUT', `/api/v1/groups/${group.id}/members`, { userIds: [viewerId] })

  const viewer = await browser.newPage()
  try {
    const problems = collectProblems(viewer)
    await viewer.setViewportSize({ width: 1600, height: 1000 })
    await signIn(viewer, 'viewer')
    // Another tab asked for: the Redmine tab, the only one there is.
    await viewer.goto('/pnmt-admin/admin/cookie/actions')
    await viewer.waitForURL(/\/admin\/cookie\/redmine$/)
    const tabs = viewer.getByRole('navigation', { name: 'Cookie' })
    await expect(tabs.getByRole('link')).toHaveCount(1)
    await expect(tabs.getByRole('link', { name: 'Redmine' })).toBeVisible()

    // The away watch: off, switched on under Actions by someone else.
    const away = viewer.getByRole('region', { name: 'While someone is away' })
    await expect(away).toContainText('Off')
    await expect(away).toContainText('by someone with Cookie: Manage')

    // The links: Vera Viewer has no Redmine user; pick one of Redmine's members and link her.
    const links = viewer.getByRole('region', { name: 'People and their Redmine users' })
    const row = links.locator('tr', { hasText: 'Vera Viewer' })
    await expect(row).toContainText('not set')
    await row.getByRole('combobox', { name: 'Redmine user of Vera Viewer' }).click()
    await viewer.getByRole('option').first().click()
    await row.getByRole('button', { name: 'Link' }).click()
    await expect(row).toContainText('linked')
    await shot(viewer, 'cookie-redmine-links')
    await row.getByRole('button', { name: 'Unlink Vera Viewer' }).click()
    await expect(row).toContainText('not set')
    expect(problems).toEqual([])
  } finally {
    await viewer.close()
    await api(page, 'DELETE', `/api/v1/groups/${group.id}`)
  }
})
