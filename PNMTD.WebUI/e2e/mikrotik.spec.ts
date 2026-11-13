import { readFileSync } from 'node:fs'
import { expect, test, type Page } from '@playwright/test'
import { shot, signIn } from './support'

/**
 * MikroTik routers (docs/mikrotik-plan.md §11, M7) on the seed's office gateways, which are stored but not
 * reachable: a router's tabs (the status strip, Setup with its join script, Backups with a diff, Updates with
 * a job log, Commands with the history and a change's diff), the admin page with the SSH key's rotation and a
 * command batch grouped by output, what a Viewer does not see, and a new router through the form.
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

async function openRouter(page: Page, host: string) {
  await page.goto('/pnmt-admin/hosts')
  await page.getByRole('link', { name: host, exact: true }).click()
  await page.getByRole('link', { name: 'router', exact: true }).first().click()
  await expect(page.getByRole('navigation', { name: 'Router pages' })).toBeVisible()
}

const tab = (page: Page, name: string) =>
  page.getByRole('navigation', { name: 'Router pages' }).getByRole('link', { name })

test('a router: status, setup, backups, updates and commands', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await openRouter(page, 'office-bern-gw')
  const status = page.getByRole('region', { name: 'Router status' })
  await expect(status).toContainText('bern-gw')
  await expect(status).toContainText('7.25.1')

  await tab(page, 'Setup').click()
  await page.getByRole('button', { name: 'Show join script' }).click()
  await expect(page.locator('pre[aria-label="Join script"]')).toContainText('/user add')
  await shot(page, 'mikrotik-setup')

  await tab(page, 'Backups').click()
  await page.getByRole('button', { name: /^Changes of version 3/ }).click()
  await expect(page.getByRole('table', { name: 'Changes' })).toContainText('8.8.8.8')
  await shot(page, 'mikrotik-backups')

  await tab(page, 'Commands').click()
  await page.getByRole('button', { name: /\/ip dns set servers=1\.1\.1\.1,8\.8\.8\.8/ }).click()
  await expect(page.getByLabel('Output').first()).toContainText('1.1.1.1;8.8.8.8')
  await page.getByRole('button', { name: 'Show the change' }).click()
  await expect(page.getByRole('table', { name: 'Changes' })).toContainText('8.8.8.8')
  await expect(page.getByText('Router admin')).toBeVisible()
  await shot(page, 'mikrotik-commands')

  await openRouter(page, 'office-geneva-gw')
  await tab(page, 'Updates').click()
  await page.getByRole('button', { name: /7\.24\.2 → 7\.25\.1/ }).click()
  await expect(page.getByText('Back after 81 s')).toBeVisible()
  await shot(page, 'mikrotik-updates')
})

test('the admin page: the SSH key, the fleet and a command batch', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/admin/mikrotik')
  await expect(page.getByText('current', { exact: true })).toBeVisible()

  // Rotating keeps the old key offered while the routers wait for their new scripts; retiring is forced.
  await page.getByRole('button', { name: 'Rotate…' }).click()
  await page.getByRole('button', { name: 'Rotate', exact: true }).click()
  await expect(page.getByText('still offered')).toBeVisible()
  await expect(page.getByText(/5 router\(s\) may still need an older key/)).toBeVisible()
  await page.getByRole('button', { name: 'Retire older keys…' }).click()
  await page.getByRole('button', { name: 'Retire anyway' }).click()
  await expect(page.getByText('still offered')).toHaveCount(0)

  await page.goto('/pnmt-admin/admin/mikrotik?tab=routers')
  await expect(page.getByRole('link', { name: 'router', exact: true })).toHaveCount(5)
  await page.getByRole('combobox', { name: 'Filter' }).click()
  await page.getByRole('option', { name: 'Behind on updates' }).click()
  await expect(page.getByRole('link', { name: 'router', exact: true })).toHaveCount(1)
  await expect(page.getByText('7.25.1 available')).toBeVisible()

  await page.goto('/pnmt-admin/admin/mikrotik?tab=jobs')
  await expect(page.getByText('7.24.2 → 7.25.1')).toBeVisible()
  await page
    .getByRole('listitem')
    .filter({ hasText: '/ip address print' })
    .first()
    .getByRole('button', { name: 'Batch' })
    .click()
  await expect(page.getByText('Command on 5 router(s)')).toBeVisible()
  await expect(page.getByText('192.168.22.1/24')).toBeVisible()
  await shot(page, 'mikrotik-batch')
})

test('a viewer sees a router, but neither its commands nor its script', async ({ page }) => {
  await signIn(page, 'viewer')
  await openRouter(page, 'office-bern-gw')
  await expect(tab(page, 'Backups')).toBeVisible()
  await expect(tab(page, 'Commands')).toHaveCount(0)
  await tab(page, 'Setup').click()
  await expect(page.getByRole('button', { name: 'Show join script' })).toHaveCount(0)
})

test('a new router through the form, with its join script', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/hosts')
  await page.getByRole('link', { name: 'router-core', exact: true }).click()
  await page.waitForURL(/\/hosts\/[0-9a-f-]{36}$/)
  const hostUrl = page.url()
  await page.goto(`${hostUrl}/sensors/new?type=MIKROTIK`)
  await expect(page.getByRole('heading', { name: 'New MikroTik router' })).toBeVisible()
  await page.locator('#sensor-name').fill('core router')
  await page.locator('#mikrotik-address').fill('192.0.2.99')
  await page.getByRole('button', { name: 'Add router' }).click()
  await page.waitForURL(/\/sensors\/[^/]+\/setup$/)
  await page.getByRole('button', { name: 'Show join script' }).click()
  await expect(page.locator('pre[aria-label="Join script"]')).toContainText('core router')
})

// docs/mikrotik-backup-plan.md §3: st-gallen encrypts its backups since version 3, with the demo key, whose private
// half is e2e/fixtures/backup-test.age-key. The browser decrypts it; then a new key is made in the browser.
const demoKey = readFileSync(new URL('./fixtures/backup-test.age-key', import.meta.url))

test('encrypted backups: read with the key in the tab, and a new key made in the browser', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await openRouter(page, 'office-st-gallen-gw')
  await tab(page, 'Backups').click()
  await expect(page.getByText('encrypted', { exact: true })).toHaveCount(1)

  // Version 3 asks for the key, then shows; its change against the plaintext version 2 is made in the browser.
  await page.getByRole('button', { name: 'View version 3' }).click()
  await expect(page.getByRole('dialog', { name: 'Load the backup key' })).toBeVisible()
  await page
    .locator('#key-file')
    .setInputFiles({ name: 'backup-test.age-key', mimeType: 'text/plain', buffer: demoKey })
  await page.getByRole('button', { name: 'Load', exact: true }).click()
  await expect(page.locator('pre[aria-label="Export"]')).toContainText('1.1.1.1,8.8.8.8')
  await page.getByRole('button', { name: 'Changes of version 3' }).click()
  await expect(page.getByRole('table', { name: 'Changes' })).toContainText('8.8.8.8')
  await expect(page.getByText('key loaded')).toBeVisible()
  await shot(page, 'mikrotik-backups-encrypted')

  // A new key: generated here, the file downloaded and loaded back, then activated; PNMT gets the public key only.
  await page.goto('/pnmt-admin/admin/mikrotik?tab=backups')
  const before = await page.locator('dd.mono').first().textContent()
  await page.getByRole('button', { name: 'Replace…' }).click()
  await page.getByRole('button', { name: 'Generate a key', exact: true }).click()
  const downloading = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Download the key file' }).click()
  const file = readFileSync((await (await downloading).path())!)
  expect(file.toString()).toMatch(/^AGE-SECRET-KEY-1/m)
  await page.locator('#confirm-file').setInputFiles({ name: 'key.age-key', mimeType: 'text/plain', buffer: file })
  await expect(page.getByText('The key file matches.')).toBeVisible()
  await page.getByRole('button', { name: 'Replace', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.getByText('Earlier keys')).toBeVisible()
  await expect(page.locator('dd.mono').first()).not.toHaveText(before ?? '')
  await shot(page, 'mikrotik-backup-key')
})
