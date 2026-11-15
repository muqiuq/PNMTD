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
  await expect(page.getByText(/keys in \d+ files/)).toBeVisible()
  await expect(page.getByText('retired, still here').first()).toBeVisible()
  await expect(page.getByText('key Philipp, laptop (philipp)').first()).toBeVisible()
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

  // The keys with their registered names; a key's logins: the Access tab filtered by its fingerprint, named.
  await tab(page, 'SSH keys').click()
  await expect(page.getByText('/home/deploy/.ssh/authorized_keys')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Philipp, laptop' }).first()).toBeVisible()
  await expect(page.getByText('unregistered', { exact: true }).first()).toBeVisible()
  await expect(page.getByText('Jan, old laptop').first()).toBeVisible()
  // Remove… opens PNMTD's preview (not submitted): deploy's file allows it.
  await page.getByRole('button', { name: 'Remove the key on line 1 of /home/deploy/.ssh/authorized_keys' }).click()
  const preview = page.getByRole('dialog', { name: /Remove keys from web-01/ })
  await expect(preview).toContainText('allowed')
  await expect(preview.getByLabel(/the number of removals/)).toBeVisible()
  await shot(page, 'linux-ssh-keys-remove')
  await preview.getByRole('button', { name: 'Cancel' }).click()
  await page
    .getByRole('link', { name: /^Logins with SHA256:/ })
    .first()
    .click()
  await expect(page.getByRole('button', { name: 'Every key' })).toBeVisible()
  await expect(page.getByRole('button', { name: /^Philipp, laptop \(/ }).first()).toBeVisible()
})

test('Admin → SSH keys: the strip, the register, a key, unregistered keys and removals', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/admin/ssh-keys')
  await expect(page.getByRole('navigation', { name: 'Key figures' })).toBeVisible()
  await expect(page.getByText('Philipp, laptop', { exact: true })).toBeVisible()
  await expect(page.getByText('Backup server bkp-01')).toBeVisible()
  await shot(page, 'ssh-keys')

  // A key's drawer, and its Remove from servers… preview (not submitted).
  await page.getByText('Philipp, laptop', { exact: true }).click()
  await expect(page.getByText('Where it is authorized')).toBeVisible()
  await expect(page.getByText('Comments seen')).toBeVisible()
  await shot(page, 'ssh-keys-drawer')
  await page.getByRole('button', { name: 'Remove from servers…' }).click()
  const preview = page.getByRole('dialog', { name: /Remove Philipp, laptop from servers/ })
  await expect(preview).toContainText("refused by the server's rights")
  await expect(preview).toContainText('Sessions that already use a key stay open')
  await shot(page, 'ssh-keys-remove')
  await preview.getByRole('button', { name: 'Cancel' }).click()
  await page.keyboard.press('Escape')

  // A figure opens its filter: the retired key still in deploy's file.
  await page.getByRole('link', { name: /^Retired, still authorized/ }).click()
  await expect(page.getByText('Jan, old laptop', { exact: true })).toBeVisible()
  await expect(page.getByText('Philipp, laptop', { exact: true })).toBeHidden()

  const tabs = page.getByRole('navigation', { name: 'SSH keys' })
  await tabs.getByRole('link', { name: /^Unregistered/ }).click()
  await expect(page.getByText('admin@old-laptop')).toBeVisible()
  await shot(page, 'ssh-keys-unregistered')
  await tabs.getByRole('link', { name: 'Removals' }).click()
  await expect(page.getByText('removed', { exact: true }).first()).toBeVisible()
  await expect(page.getByText('refused', { exact: true }).first()).toBeVisible()
  await expect(page.getByText('pending', { exact: true }).first()).toBeVisible()
  await shot(page, 'ssh-keys-removals')

  // Add keys…: a pasted private key is refused before anything is stored.
  await page.getByRole('button', { name: 'Add keys…' }).click()
  await page
    .getByLabel('Public keys, one per line')
    .fill('-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----')
  await expect(page.getByText('This is a private key.', { exact: false })).toBeVisible()
  const adding = page.getByRole('dialog', { name: 'Add keys' })
  await expect(adding.getByRole('button', { name: 'Register', exact: true })).toBeDisabled()
  await adding.getByRole('button', { name: 'Cancel' }).click()
})

test('users: the keys of a user, and my keys', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/admin/users')
  await page.getByText('philipp', { exact: true }).first().click()
  await expect(page.getByRole('heading', { name: 'SSH keys' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Philipp, laptop' })).toBeVisible()
  await expect(page.getByRole('button', { name: /Remove all from every server/ })).toBeVisible()
  await page.keyboard.press('Escape')
  await page.goto('/pnmt-admin/account')
  await expect(page.getByText('My SSH keys')).toBeVisible()
  await expect(page.getByText('None registered for you.')).toBeVisible()
})

test('bash scripts: the library, the editor and Run… for Linux servers', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/admin/servers/scripts')
  await page.getByRole('combobox', { name: 'Language' }).click()
  await page.getByRole('option', { name: 'bash' }).click()
  await expect(page.getByRole('link', { name: 'rotate-logs' })).toBeVisible()
  await page.getByRole('link', { name: 'rotate-logs' }).click()
  await expect(page.getByRole('textbox', { name: 'bash script' })).toHaveValue(/# pnmt:/)
  await expect(page.getByText('$PNMT_SITE')).toBeVisible()
  await shot(page, 'scripts-bash')
  await page
    .getByRole('dialog', { name: /Script: rotate-logs/ })
    .getByRole('button', { name: 'Run…' })
    .click()
  const dialog = page.getByRole('dialog', { name: 'Run a script' })
  await expect(dialog.getByText('Linux servers')).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'root (the service)' })).toBeVisible()
  await dialog.getByRole('button', { name: 'Cancel' }).click()

  // A new bash script starts from the template.
  await page.getByRole('button', { name: 'New script' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'bash' }).click()
  await expect(page.getByRole('textbox', { name: 'bash script' })).toHaveValue(/# pnmt:/)
  await page.keyboard.press('Escape')
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

  // The Download tab: the lines for both architectures; each file's state is on the Versions tab.
  const tabs = page.getByRole('navigation', { name: 'Servers', exact: true })
  await tabs.getByRole('link', { name: 'Download' }).click()
  await expect(page.getByText(/Linux: the PNMT agent \d/)).toBeVisible()
  await expect(page.getByLabel('Update line', { exact: true })).toHaveText('pnmt-agent update')
  await shot(page, 'servers-download')

  // The Versions tab (docs/agent-update-plan.md §8.1): what each platform's agents get, PNMT's own by default, and the versions
  // with their four files. Nothing here starts an update.
  await tabs.getByRole('link', { name: 'Versions' }).click()
  await expect(page.getByText('What the agents get')).toBeVisible()
  await expect(page.getByRole('combobox', { name: 'Linux' })).toBeVisible()
  await expect(page.getByRole('combobox', { name: 'Windows' })).toBeVisible()
  // Both follow PNMT's own version, which the selects show as their choice.
  await expect(page.getByText(/^PNMT's version \(\d/)).toHaveCount(2)
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled()
  await expect(page.getByText('Linux get it')).toBeVisible()
  await expect(page.getByText('Linux x64').first()).toBeVisible()
  await expect(page.getByRole('button', { name: 'Upload…' })).toBeVisible()
  await page.getByRole('button', { name: 'Fetch a version…' }).click()
  const fetch = page.getByRole('dialog', { name: 'Fetch a version' })
  await fetch.getByLabel('Version').fill('1.8')
  await fetch.getByRole('button', { name: 'Fetch', exact: true }).click()
  await expect(fetch).toContainText('A version as 1.8.0 or 1.8.0-rc1.')
  await fetch.getByRole('button', { name: 'Cancel' }).click()
  await expect(fetch).toBeHidden()
  await shot(page, 'servers-versions')
})
