import { expect, test, type Page } from '@playwright/test'
import { shot, signIn } from './support'

/**
 * Windows servers and diagnostics (docs/windows-sensor-plan.md §10) on the seed's two servers, which are stored but have no
 * agent connected: Admin → Servers (docs/linux-sensor-plan.md §11.1) and Add server's tokens for both platforms, every tab
 * of a Windows server, and Admin → Diagnostics with a run. Only what the browser can break: the server side has its tests in
 * PNMTD.Tests (§13).
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

// A tab's name may carry its badge ("Agent !" while offline).
const tab = (page: Page, name: string) =>
  page.getByRole('navigation', { name: 'Server pages' }).getByRole('link', { name: new RegExp(`^${name}`) })

test('the admin page, Add server and every tab of a server', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/admin/servers')
  await expect(page.getByRole('link', { name: 'backup-srv / Windows' })).toBeVisible()
  await shot(page, 'servers')

  await page.getByRole('button', { name: 'Add server' }).click()
  await page.getByRole('dialog').getByRole('combobox').click()
  await page.getByRole('option', { name: 'wiki', exact: true }).click()
  await page.getByRole('button', { name: 'Create token' }).click()
  await expect(page.getByLabel('Join token').first()).toHaveText(/^[a-z2-9]{4}-[a-z2-9]{4}$/)
  await expect(page.getByRole('dialog')).toContainText('Get-FileHash')
  await shot(page, 'servers-add-windows')
  await page.getByRole('button', { name: 'Done' }).click()

  // The platform comes first; a Linux token's one line is in e2e/linux.spec.ts.
  await page.getByRole('button', { name: 'Add server' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Linux' }).click()
  await page.getByRole('dialog').getByRole('combobox').click()
  await page.getByRole('option', { name: 'wiki', exact: true }).click()
  await page.getByRole('button', { name: 'Create token' }).click()
  await expect(page.getByLabel('Join token').first()).toHaveText(/^[a-z2-9]{4}-[a-z2-9]{4}$/)
  await expect(page.getByRole('dialog')).toContainText('What join will do')
  await page.getByRole('button', { name: 'Done' }).click()
  await page
    .getByRole('navigation', { name: 'Servers', exact: true })
    .getByRole('link', { name: 'Join tokens' })
    .click()
  await expect(page.getByRole('cell', { name: 'Linux' }).first()).toBeVisible()
  await page
    .getByRole('navigation', { name: 'Servers', exact: true })
    .getByRole('link', { name: 'Servers', exact: true })
    .click()

  await page.getByRole('link', { name: 'backup-srv / Windows' }).click()
  await expect(page.getByRole('navigation', { name: 'Server pages' })).toBeVisible()
  await expect(page.getByText('Microsoft Windows Server 2019 Standard').first()).toBeVisible()
  await expect(page.getByText('Local administrators')).toBeVisible()
  await expect(page.getByText('Service AnyDesk added').first()).toBeVisible()
  await shot(page, 'windows-overview')
  for (const [name, text] of [
    ['Hardware', 'Disks'],
    ['Network', 'Listening ports'],
    ['Services', 'AnyDesk Service'],
    ['Remote access', 'Remote Desktop (RDP)'],
    ['Access', 'The audit policy'],
    ['Event log', 'Service Control Manager'],
    ['Diagnostics', 'branch connectivity'],
    ['Changes', 'Service AnyDesk added'],
    ['Scripts', 'Registered on this server'],
    ['WMI', 'MSStorageDriver_FailurePredictStatus'],
    ['Agent', "PNMTD's key"],
  ]) {
    await tab(page, name).click()
    await expect(page.getByText(text).first()).toBeVisible()
    if (name === 'Access' || name === 'Diagnostics' || name === 'WMI') {
      await shot(page, `windows-${name.toLowerCase()}`)
    }
  }
})

test('scripts: a new one from the template, and the run-as accounts', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/admin/servers/scripts')
  await page.getByRole('button', { name: 'New script' }).click()
  await expect(page.getByRole('textbox', { name: 'PowerShell script' })).toHaveValue(/\[CmdletBinding\(\)\]/)
  await page.getByLabel('Name', { exact: true }).fill('e2e greeting')
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('link', { name: 'e2e greeting' })).toBeVisible()
  await expect(page.getByRole('dialog')).toBeHidden()
  await shot(page, 'servers-scripts')
  await page.goto('/pnmt-admin/admin/servers/run-as')
  await page.getByRole('button', { name: 'New account' }).click()
  await page.getByLabel('Name', { exact: true }).fill('deploy')
  await page.getByRole('textbox', { name: 'Account' }).fill('CORP\\deploy')
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('link', { name: 'deploy' })).toBeVisible()
})

test('diagnostics: the sets, the assignments and a run', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/admin/diagnostics')
  await expect(page.getByRole('link', { name: 'branch connectivity' })).toBeVisible()
  await page.getByRole('link', { name: 'branch connectivity' }).click()
  await expect(page.getByRole('dialog').getByPlaceholder('Name', { exact: true }).first()).toHaveValue('gw-zurich')
  await shot(page, 'diagnostics-set')
  await page.getByRole('button', { name: 'Cancel' }).click()
  await page.getByRole('navigation', { name: 'Diagnostics' }).getByRole('link', { name: 'Runs' }).click()
  await page
    .getByRole('cell', { name: /1 of 3 failed/ })
    .first()
    .click()
  await expect(page.getByRole('dialog')).toContainText('100 % loss')
  await page.waitForTimeout(400) // the drawer's slide
  await shot(page, 'diagnostics-run')
})
