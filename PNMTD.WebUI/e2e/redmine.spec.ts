import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Redmine (docs/audit-redmine-zabbix-plan.md §6, §12), against the tickets recorded in R0 (tools/e2e.sh's fake, or a real
 * Redmine through E2E_ENV): the overview's tiles and lists, the bar with its refresh button (docs/redmine-queue-outbound-plan.md
 * §5), the search, a number opening the ticket, the ticket's page linking a host, and the host's Tickets panel with its two
 * buttons, its header chip and Unlink. Skipped without Redmine.
 */
async function api(page: Page, method: 'POST', path: string, data: unknown) {
  const xsrf = (await page.context().cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value ?? ''
  const response = await page.request.fetch(path, {
    method,
    data,
    headers: { 'X-XSRF-TOKEN': decodeURIComponent(xsrf) },
  })
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy()
  return response.json()
}

test("the overview, the search, a ticket linked to a host, and the host's panel", async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1600, height: 1000 })
  await signIn(page)
  const info = await (await page.request.get('/api/v1/system/info')).json()
  test.skip(!info.redmine, 'Redmine is not configured for this stack')

  // The overview: tiles, and the urgent ticket in its list, with its two buttons.
  await page.goto('/pnmt-admin/redmine')
  await expect(page.getByRole('heading', { level: 1, name: 'Redmine' })).toBeVisible()
  const urgent = page.getByRole('region', { name: 'Urgent' })
  await expect(urgent).toContainText('Printer on floor 2 does not print')
  await expect(urgent.getByRole('link', { name: '#9 in Redmine' })).toHaveAttribute('href', /\/issues\/9$/)
  await expect(page.getByRole('region', { name: 'Counts' })).toContainText('Open')
  // Several projects: the switch narrows the overview to one.
  await expect(page.getByRole('combobox', { name: 'Project' })).toContainText('All projects')
  await shot(page, 'redmine-overview')

  // The bar: when the overview was read from Redmine; ⟳ reads it again, and the time is now.
  const refreshed = page.getByText(/^Refreshed /)
  await expect(refreshed).toBeVisible()
  const refresh = page.getByRole('button', { name: 'Refresh from Redmine' })
  const answer = page.waitForResponse(
    (r) => r.url().includes('/api/v1/redmine/refresh') && r.request().method() === 'POST',
  )
  await refresh.click()
  expect([200, 202]).toContain((await answer).status())
  await expect(refresh).toBeEnabled()
  await expect(refreshed).toHaveText('Refreshed just now')

  // A tile opens the search with its filter applied, not every open ticket (the page stays mounted between the tabs).
  await page
    .getByRole('region', { name: 'Counts' })
    .getByRole('link', { name: /Overdue/ })
    .click()
  await expect(page).toHaveURL(/\/redmine\/search\?.*overdue=true/)
  await expect(page.getByRole('switch', { name: 'Overdue' })).toBeChecked()
  await expect(page.getByRole('row', { name: /Replace the switch in rack 4/ })).toBeVisible()
  await expect(page.getByRole('row', { name: /New laptop for Ben/ })).toHaveCount(0)
  await page.getByRole('link', { name: 'Overview' }).click()

  // The search: text, then a number opens the ticket's page.
  await page.getByRole('link', { name: 'Search' }).click()
  const search = page.getByPlaceholder('Subject, description, notes, or a number')
  await search.fill('switch')
  await expect(page.getByRole('row', { name: /Replace the switch in rack 4/ })).toBeVisible()
  await expect(page.getByRole('row', { name: /New laptop for Ben/ })).toHaveCount(0)
  await shot(page, 'redmine-search')
  await search.fill('#9')
  await search.press('Enter')
  await expect(page).toHaveURL(/\/redmine\/issues\/9$/)
  await expect(page.getByRole('heading', { level: 1 })).toContainText('Printer on floor 2 does not print')
  await expect(page.getByRole('link', { name: 'Open in Redmine' })).toHaveAttribute('href', /\/issues\/9$/)
  await expect(page.getByText(/^Refreshed /)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Refresh from Redmine' })).toBeVisible()

  // Linked to a host from the ticket's page.
  const name = `e2e-redmine-${Date.now()}`
  await api(page, 'POST', '/api/v1/hosts', { name })
  await page.getByLabel('Link the ticket to').fill(name)
  await page.getByRole('option', { name: new RegExp(name) }).click()
  const links = page.getByRole('region', { name: 'In PNMT' })
  await expect(links.getByRole('link', { name })).toBeVisible()
  await shot(page, 'redmine-issue')

  // The host's panel and its header's chip.
  await links.getByRole('link', { name }).click()
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
  await expect(page.getByRole('link', { name: '1 open ticket' })).toBeVisible()
  const panel = page.getByRole('region', { name: 'Tickets' })
  await expect(panel).toContainText('Printer on floor 2 does not print')
  await expect(panel.getByRole('link', { name: '#9 in Redmine' })).toHaveAttribute('href', /\/issues\/9$/)
  await panel.scrollIntoViewIfNeeded()
  await shot(page, 'redmine-host-panel')

  // Unlinked again: the ticket stays in Redmine.
  await panel.getByRole('button', { name: 'Unlink #9' }).click()
  await page.getByRole('button', { name: 'Unlink', exact: true }).click()
  await expect(panel).toContainText('No tickets linked yet.')

  // On a phone: two-line rows with the two buttons on the right.
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/pnmt-admin/redmine/search')
  await expect(page.getByRole('link', { name: 'Replace the switch in rack 4' })).toBeVisible()
  await shot(page, 'redmine-search-phone')
  expect(problems).toEqual([])
})
