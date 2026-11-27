import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Licences & services (docs/licences-plan.md §8, §14) on the seeded stack, which turns the feature on (tools/e2e.sh): the page and
 * its tabs, an item's page and its tabs, the form, a host's panel and the two strips on sensors' pages; then a service on no host,
 * made, without a sensor, and assigned to a host, which gets one. The seed's items are numbered in its order: LIC-4 is Veeam,
 * over-assigned on seven hosts; SVC-15 the firewalls' support on fw-01 and fw-02; SVC-9 the internet line linked to
 * office-basel-gw's ping.
 */

async function hostId(page: Page, name: string): Promise<string> {
  const response = await page.request.get(`/api/v1/search?q=${encodeURIComponent(name)}`)
  const found = (await response.json()) as { hosts: { id: string; name: string }[] }
  const host = found.hosts.find((h) => h.name === name)
  if (!host) throw new Error(`No host ${name} in the seed`)
  return host.id
}

/** The network quiet and nothing left loading: a tab's query may start after the route's own requests. */
async function settle(page: Page) {
  await page.waitForLoadState('networkidle')
  await expect(page.getByText('Loading…')).toHaveCount(0)
}

test('the page, its tabs, an item and its tabs, without a problem', async ({ page }) => {
  const problems = collectProblems(page)
  await signIn(page)
  await page.getByRole('link', { name: 'Licences & services' }).first().click()
  await expect(page.getByRole('heading', { level: 1, name: 'Licences & services' })).toBeVisible()
  await settle(page)
  await shot(page, 'licences-overview')

  for (const tab of ['Licences', 'Services', 'Hardware', 'Upcoming', 'Costs', 'Templates']) {
    await page
      .getByRole('navigation', { name: 'Licences & services' })
      .getByRole('link', { name: new RegExp(`^${tab}`) })
      .click()
    await expect(page).toHaveURL(new RegExp(`/licences/${tab.toLowerCase()}`))
    await settle(page)
    await shot(page, `licences-${tab.toLowerCase()}`)
  }
  await page.goto('/pnmt-admin/licences/costs?show=charges')
  await settle(page)
  await shot(page, 'licences-charges')

  // LIC-4, over-assigned: its page and every tab.
  await page.goto('/pnmt-admin/licences/4')
  await expect(page.getByRole('heading', { level: 1 })).toContainText('Veeam')
  await settle(page)
  await shot(page, 'licence-overview')
  for (const tab of ['hosts', 'costs', 'renewals', 'documents', 'history']) {
    await page.goto(`/pnmt-admin/licences/4/${tab}`)
    await settle(page)
    await shot(page, `licence-${tab}`)
  }
  await page.goto('/pnmt-admin/licences/new?type=Service')
  await expect(page.locator('[data-test="lf-name"]')).toBeVisible()
  await shot(page, 'licence-form')

  // The firewalls' support on fw-01: the host's panel, and its item sensor's strip.
  await page.goto(`/pnmt-admin/hosts/${await hostId(page, 'fw-01')}`)
  const panel = page.locator('[data-test="licences-panel"]')
  await expect(panel).toContainText('SVC-15')
  await shot(page, 'licences-host-panel')
  await page
    .getByRole('link', { name: /SVC-15\)$/ })
    .first()
    .click()
  await expect(page.locator('[data-test="license-strip"]')).toContainText('SVC-15')
  await shot(page, 'licence-sensor-strip')

  // The internet line's support details on the ping sensor it is linked to.
  await page.goto(`/pnmt-admin/hosts/${await hostId(page, 'office-basel-gw')}`)
  await page.getByRole('link', { name: 'ping', exact: true }).first().click()
  await expect(page.locator('[data-test="linked-items-strip"]')).toContainText('0800 800 800')
  await shot(page, 'licence-linked-strip')

  expect(problems).toEqual([])
})

test('a service on no host has no sensor until it is assigned to a host', async ({ page }) => {
  const problems = collectProblems(page)
  await signIn(page)
  await page.goto('/pnmt-admin/licences/new?type=Service')
  const name = `E2E workshop ${Date.now()}`
  await page.locator('[data-test="lf-name"]').fill(name)
  await page.locator('#lf-kind').click()
  await page.getByRole('option', { name: 'One-off job' }).click()
  await page.locator('[data-test="lf-save"]').click()
  await expect(page.getByRole('heading', { level: 1 })).toContainText(name)
  const number = /\/licences\/(\d+)/.exec(page.url())?.[1]
  expect(number).toBeTruthy()

  await page.goto(`/pnmt-admin/licences/${number}/hosts`)
  await expect(page.getByText('On no host: this item has no sensor. Its owner is told by e-mail.')).toBeVisible()
  await page.getByRole('button', { name: 'Assign to hosts…' }).first().click()
  const dialog = page.getByRole('dialog')
  await dialog.locator('#la-host').fill('printer-hub')
  await page
    .getByRole('option', { name: /printer-hub/ })
    .first()
    .click()
  await dialog.getByRole('button', { name: 'Assign', exact: true }).click()
  await expect(page.getByRole('link', { name: 'printer-hub' }).first()).toBeVisible()
  await shot(page, 'licence-assigned')

  // The host's panel names it now.
  await page.goto(`/pnmt-admin/hosts/${await hostId(page, 'printer-hub')}`)
  await expect(page.locator('[data-test="licences-panel"]')).toContainText(name)
  expect(problems).toEqual([])
})

test("a viewer without the area doesn't see it", async ({ page }) => {
  await signIn(page, 'viewer')
  await expect(page.getByRole('link', { name: 'Licences & services' })).toHaveCount(0)
  await page.goto('/pnmt-admin/licences')
  await expect(page).not.toHaveURL(/\/licences/)
})
