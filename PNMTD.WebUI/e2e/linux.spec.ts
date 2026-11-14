import { expect, test, type Page } from '@playwright/test'
import { shot, signIn } from './support'

/**
 * Linux servers (docs/linux-sensor-plan.md §11) on the seed's web-01 (Debian 12, key removal allowed) and pve-01 (Proxmox VE 9),
 * stored with every section but no agent connected: the fleet's Linux columns and filters, every tab of web-01, and Add server's
 * one line for Linux with its toggles. Only what the browser can break: the server side has its tests in PNMTD.Tests (§14).
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

test('the fleet and every tab of a Linux server', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/admin/servers')
  await expect(page.getByRole('link', { name: 'web-01 / Linux' })).toBeVisible()
  await expect(page.getByText('Debian 12.8').first()).toBeVisible()
  await expect(page.getByText('3 security updates').first()).toBeVisible()
  await page.getByRole('combobox', { name: 'Filter' }).click()
  await page.getByRole('option', { name: 'Key removal allowed (Linux)' }).click()
  await expect(page.getByRole('link', { name: 'web-01 / Linux' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'pve-01 / Linux' })).toBeHidden()
  await shot(page, 'servers-linux')

  await page.getByRole('link', { name: 'web-01 / Linux' }).click()
  await expect(page.getByRole('navigation', { name: 'Server pages' })).toBeVisible()
  await expect(page.getByText('Administrators and accounts')).toBeVisible()
  await expect(page.getByText('reboot pending').first()).toBeVisible()
  await expect(page.getByText('from scanners')).toBeVisible()
  await expect(page.getByText(/keys in 2 files/)).toBeVisible()
  await shot(page, 'linux-overview')
  for (const [name, text] of [
    ['Hardware', 'Disks'],
    ['Network', 'Listening ports'],
    ['Services', 'sync-daemon.service'],
    ['Remote access', 'root: key only'],
    ['Access', 'The logging check'],
    ['Journal', 'connect() failed'],
    ['SSH keys', "sshd's key settings"],
    ['Diagnostics', 'branch connectivity'],
    ['Changes', 'deploy added to sudo'],
    ['Scripts', 'Scripts are not allowed'],
    ['Agent', 'The rights the server gives PNMT'],
  ]) {
    await tab(page, name).click()
    await expect(page.getByText(text).first()).toBeVisible()
    if (['Services', 'Remote access', 'Access', 'Journal', 'SSH keys', 'Agent'].includes(name)) {
      await shot(page, `linux-${name.toLowerCase().replace(' ', '-')}`)
    }
  }
  await expect(page.getByText('scripts: { enabled: true, run_as: [root] }')).toBeVisible()

  // A key's logins: the Access tab filtered by its fingerprint.
  await tab(page, 'SSH keys').click()
  await expect(page.getByText('/home/deploy/.ssh/authorized_keys')).toBeVisible()
  await page
    .getByRole('link', { name: /^Logins with SHA256:/ })
    .first()
    .click()
  await expect(page.getByRole('button', { name: 'Every key' })).toBeVisible()
  await expect(page.getByRole('cell', { name: /SHA256:/ }).first()).toBeVisible()
})

test('Add server → Linux: the one line and its toggles', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/admin/servers')
  await page.getByRole('button', { name: 'Add server' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Linux' }).click()
  await page.getByRole('dialog').getByRole('combobox').click()
  await page.getByRole('option', { name: 'wiki', exact: true }).click()
  await page.getByRole('button', { name: 'Create token' }).click()
  const token = (await page.getByLabel('Join token').first().textContent()) ?? ''
  expect(token).toMatch(/^[a-z2-9]{4}-[a-z2-9]{4}$/)
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('What join will do')
  await expect(dialog).toContainText('/usr/local/sbin/pnmt-agent')
  // Without the release's hash (no internet, a development version) only the join line is offered.
  const install = dialog.getByLabel('Install line', { exact: true })
  const line = (await install.count()) ? install : dialog.getByLabel('Join line', { exact: true })
  await expect(line).toContainText(`--token ${token}`)
  if (await install.count()) {
    await expect(install).toContainText('sha256sum -c')
    await dialog.getByRole('button', { name: 'GitHub' }).click()
    await expect(install).toContainText('github.com')
    await dialog.getByRole('button', { name: 'curl' }).click()
    await expect(install).toContainText('curl -fsSLo')
    await dialog.getByRole('button', { name: 'arm64' }).click()
    await expect(install).toContainText('pnmt-agent-linux-arm64')
    await dialog.getByRole('button', { name: "sudo sh -c '…'" }).click()
    await expect(install).toContainText("sudo sh -c '")
  }
  await shot(page, 'servers-add-linux')
  await page.getByRole('button', { name: 'Done' }).click()

  // The Download tab: the lines for both architectures and each file's state.
  await page.getByRole('navigation', { name: 'Servers', exact: true }).getByRole('link', { name: 'Download' }).click()
  await expect(page.getByText("PNMT's download folder")).toBeVisible()
  await expect(page.getByRole('cell', { name: /^pnmt-agent-linux-x64 Linux/ })).toBeVisible()
  await expect(page.getByLabel('Update line', { exact: true })).toHaveText('pnmt-agent update')
  await shot(page, 'servers-download')
})
