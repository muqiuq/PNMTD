import { expect, test, type Page } from '@playwright/test'
import { shot, signIn } from './support'

/**
 * OS updates on Linux servers (docs/linux-os-updates-plan.md §10) on the seed's web-01 (Debian 12: pending packages with a
 * high-risk openssl finding, and some runs) and pve-01 (Proxmox VE: a failed upgrade, an acknowledged finding), stored with no
 * agent connected, so nothing runs: web-01's OS updates tab, a run's log, the dashboard's card, Admin → Servers → Advisories
 * and the docs. Only what the browser can break: the server side has its tests in PNMTD.Tests (§12).
 */
let problems: string[] = []
test.beforeEach(({ page }) => {
  problems = []
  page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`))
  page.on('console', (m) => {
    if (m.text().includes('Content Security Policy') || (m.type() === 'error' && m.text().includes('[Vue warn]'))) {
      problems.push(m.text().slice(0, 300))
    }
  })
})
test.afterEach(() => expect(problems).toEqual([]))

// A tab's name may carry its badge ("OS updates 3", "OS updates !").
const tab = (page: Page, name: string) =>
  page.getByRole('navigation', { name: 'Server pages' }).getByRole('link', { name: new RegExp(`^${name}`) })

test("a Linux server's OS updates tab: the state, the pending packages and the runs", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/admin/servers')
  await page.getByRole('link', { name: 'web-01 / Linux' }).click()
  await expect(page.getByRole('navigation', { name: 'Server pages' })).toBeVisible()
  await tab(page, 'OS updates').click()
  await expect(page).toHaveURL(/\/os-updates$/)

  // The state with its buttons: offline in the seed, so they say why they are disabled.
  await expect(page.getByRole('region', { name: 'OS updates', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Install updates…' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Refresh lists' })).toBeVisible()

  // The pending packages, openssl's (or its library's) among them; a row opens its findings and changelog.
  const pending = page.getByRole('region', { name: 'Pending packages' })
  const openssl = pending.getByRole('button', { name: /^(openssl|libssl3)$/ }).first()
  await expect(openssl).toBeVisible()
  await openssl.click()
  await expect(openssl).toHaveAttribute('aria-expanded', 'true')
  await shot(page, 'linux-os-updates')

  // The log of runs: a run opens its summary and its log.
  const runs = page.getByRole('region', { name: 'Runs' })
  await expect(runs).toBeVisible()
  await expect(runs.getByText('No run yet', { exact: false })).toBeHidden()
  await runs.locator('tbody tr').first().click()
  const run = page.getByRole('dialog')
  await expect(run).toBeVisible()
  await expect(run.getByRole('heading', { name: 'Log' })).toBeVisible()
  await shot(page, 'linux-os-updates-run')
  await page.keyboard.press('Escape')
  await expect(run).toBeHidden()

  // The overview's Updates line links here.
  await tab(page, 'Overview').click()
  await page.getByRole('link', { name: 'OS updates tab' }).click()
  await expect(page).toHaveURL(/\/os-updates$/)
})

test("the dashboard's OS updates card, Admin → Servers → Advisories and the docs", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  const card = page.getByRole('region', { name: 'OS updates', exact: true })
  await expect(card).toBeVisible()
  await shot(page, 'dashboard-os-updates')
  await card.getByRole('button', { name: 'By package' }).click()
  await card.getByRole('button', { name: 'By server' }).click()

  await page.goto('/pnmt-admin/admin/servers/advisories')
  await expect(page.getByRole('navigation', { name: 'Servers', exact: true })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Coverage' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Sources' })).toBeVisible()
  await shot(page, 'servers-advisories')

  await page.goto('/pnmt-admin/docs')
  await page.getByLabel('Search the documentation').fill('acknowledge')
  await expect(page.getByRole('region', { name: 'OS updates on Linux servers' })).toBeVisible()
})
