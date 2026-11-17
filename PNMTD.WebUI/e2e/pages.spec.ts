import { expect, test, type Page } from '@playwright/test'
import { shot, signIn } from './support'

/**
 * Phase 6 (docs/webui-migration-plan.md §5.6): every page renders without a script error or a CSP
 * violation, for an Admin and for a Viewer, and the main flows work against the real API on the seed.
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

const pages: [string, string, string][] = [
  ['/hosts', 'Hosts', 'hosts'],
  ['/locations', 'Locations', 'locations'],
  ['/kb', 'Knowledge base', 'kb'],
  ['/events', 'Events', 'events'],
  ['/dns-zones', 'DNS zones', 'dns-zones'],
  ['/mail-inputs', 'Mail inputs', 'mail-inputs'],
  ['/mail-log', 'Mail log', 'mail-log'],
  ['/notification-rules', 'Notification rules', 'rules'],
  ['/admin/key-values', 'Key values', 'key-values'],
  ['/admin/users', 'Users', 'users'],
  ['/admin/audit-log', 'Audit log', 'audit-log'],
  ['/admin/groups', 'Groups', 'groups'],
  ['/admin/location-fields', 'Location fields', 'location-fields'],
  ['/admin/knowledge-base', 'Knowledge base', 'admin-kb'],
  ['/admin/system', 'System health', 'system'],
  ['/admin/availability', 'Availability plan', 'availability'],
  ['/docs', 'Documentation', 'docs'],
]

test('every page renders for an admin', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  for (const [path, title, name] of pages) {
    await page.goto(`/pnmt-admin${path}`)
    await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
    await page.waitForLoadState('networkidle')
    await shot(page, `page-${name}`)
  }
})

async function open(page: Page, path: string) {
  await page.goto(`/pnmt-admin${path}`)
  await page.waitForLoadState('networkidle')
}

test('a host, a sensor, an event through the sensor API, and both deleted again', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await open(page, '/hosts')
  await page.getByRole('button', { name: 'New host' }).click()
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill('e2e-host')
  await page.getByRole('textbox', { name: 'Location text', exact: true }).fill('Playwright lab')
  await page.getByRole('button', { name: 'Create host' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'e2e-host' })).toBeVisible()

  await page.getByRole('button', { name: 'Add sensor' }).click()
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill('nightly')
  await page.getByRole('button', { name: 'Create sensor' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'nightly' })).toBeVisible()
  await expect(page.getByText('Integration', { exact: true })).toBeVisible()
  const writeUrl = (await page.getByLabel('Write URL', { exact: true }).textContent())!.replace('/{code}/{message}', '')
  expect(writeUrl).toMatch(/\/event\/[A-Za-z0-9-]+$/)
  await shot(page, 'page-sensor-new-created')

  // A report through the frozen sensor API, as a script would.
  const path = new URL(writeUrl).pathname
  const reported = await page.request.get(`${path}/200/hello%20from%20playwright`)
  expect(reported.status()).toBe(200)
  await page.reload()
  await expect(page.getByText('hello from playwright').first()).toBeVisible()
  await expect(page.locator('.status-badge[data-status="Ok"]').first()).toBeVisible()

  // Inline edit: nothing is stored before Save (or Enter). The name, saved with Enter.
  await page.getByRole('button', { name: 'Edit Name' }).click()
  await page.getByRole('textbox', { name: 'Name' }).fill('nightly backup')
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { level: 1, name: 'nightly backup' })).toBeVisible()

  // The type: text until clicked; a pick only changes the draft, Discard drops it, Save stores it.
  await expect(page.getByRole('combobox', { name: 'Type' })).toHaveCount(0)
  await page.getByRole('button', { name: 'Edit Type' }).click()
  await page.getByRole('option', { name: 'Encapsulated (JSON)', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Save Type' })).toBeVisible()
  await page.getByRole('button', { name: 'Discard Type' }).click()
  await expect(page.getByRole('button', { name: 'Edit Type' })).toHaveText('Simple')
  await page.reload()
  await expect(page.getByRole('button', { name: 'Edit Type' })).toHaveText('Simple')
  await page.getByRole('button', { name: 'Edit Type' }).click()
  await page.getByRole('option', { name: 'Encapsulated (JSON)', exact: true }).click()
  await page.getByRole('button', { name: 'Save Type' }).click()
  await expect(page.getByRole('button', { name: 'Edit Type' })).toHaveText('Encapsulated (JSON)')
  await page.reload()
  await expect(page.getByRole('button', { name: 'Edit Type' })).toHaveText('Encapsulated (JSON)')

  // A switch: Yes or No until clicked; a flip only changes the draft, Esc drops it, Save stores it.
  await expect(page.getByRole('switch', { name: 'Enabled' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Edit Enabled' })).toHaveText('Yes')
  await page.getByRole('button', { name: 'Edit Enabled' }).click()
  await expect(page.getByRole('switch', { name: 'Enabled' })).toBeFocused()
  await page.keyboard.press('Space')
  await expect(page.getByRole('switch', { name: 'Enabled' })).not.toBeChecked()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('button', { name: 'Edit Enabled' })).toHaveText('Yes')
  await page.reload()
  await expect(page.getByRole('button', { name: 'Edit Enabled' })).toHaveText('Yes')
  await page.getByRole('button', { name: 'Edit Enabled' }).click()
  await page.getByRole('switch', { name: 'Enabled' }).click()
  await page.getByRole('button', { name: 'Save Enabled' }).click()
  await expect(page.getByRole('button', { name: 'Edit Enabled' })).toHaveText('No')
  await page.reload()
  await expect(page.getByRole('button', { name: 'Edit Enabled' })).toHaveText('No')

  await page.getByRole('button', { name: 'More actions for the sensor' }).click()
  await page.getByRole('menuitem', { name: 'Delete sensor…' }).click()
  await page.getByRole('button', { name: 'Delete', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'e2e-host' })).toBeVisible()
  await page.getByRole('button', { name: 'More actions for the host' }).click()
  await page.getByRole('menuitem', { name: 'Delete host…' }).click()
  await expect(page.getByText(/Its 0 sensors and 0 events/)).toBeVisible()
  await page.getByRole('button', { name: 'Delete', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Hosts' })).toBeVisible()
})

test('a host page, a sensor page and a zone, as on the seed', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await page
    .locator('.problems')
    .getByRole('link', { name: /web-03/ })
    .click()
  await expect(page.getByRole('heading', { level: 1, name: 'web-03' })).toBeVisible()
  await page.waitForLoadState('networkidle')
  await shot(page, 'page-host')
  await page.getByRole('link', { name: 'http', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'http' })).toBeVisible()
  await page.waitForLoadState('networkidle')
  await shot(page, 'page-sensor')

  await open(page, '/dns-zones')
  await page.getByRole('link', { name: 'example.org' }).click()
  await expect(page.getByText('Mismatches only (1)')).toBeVisible()
  await shot(page, 'page-dns-zone')

  await open(page, '/notification-rules')
  await page.getByRole('link', { name: 'Ops on-call' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Ops on-call' })).toBeVisible()
  await shot(page, 'page-rule')

  await open(page, '/mail-inputs')
  await page.getByRole('link', { name: 'Veeam job report' }).click()
  await expect(page.getByText('Latest mails')).toBeVisible()
  await shot(page, 'page-mail-input')
})

test('an SSL certificate with a change waiting, its window, and the private CAs, as on the seed', async ({ page }) => {
  // docs/ssl-certificate-plan.md §8.3 to §8.5: the seed's vpn.pnmt.test has an older accepted certificate.
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  const found = await (await page.request.get('/api/v1/sensors?type=SSL_CERTIFICATE&pageSize=100')).json()
  const vpn = found.items.find((s: { name: string }) => s.name === 'vpn.pnmt.test:443')
  expect(vpn, 'the seed has the sensor vpn.pnmt.test:443').toBeTruthy()
  await open(page, `/sensors/${vpn.id}/certificate`)
  await expect(page.getByRole('heading', { level: 1, name: 'vpn.pnmt.test:443' })).toBeVisible()
  await expect(page.getByText(/The certificate changed unexpectedly on/)).toBeVisible()
  await expect(page.getByText('Certificates seen')).toBeVisible()
  await shot(page, 'page-certificate')

  await page.getByRole('button', { name: 'Review and accept…' }).click()
  const dialog = page.getByRole('dialog', { name: 'Accept the new certificate?' })
  await expect(dialog.getByRole('table', { name: 'What changed' })).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Accept the new certificate' })).toBeDisabled()
  await shot(page, 'page-certificate-accept')
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(dialog).toBeHidden()

  await open(page, '/admin/certificates')
  await expect(page.getByRole('heading', { level: 1, name: 'Certificates' })).toBeVisible()
  await expect(page.getByRole('table', { name: 'Private CAs' })).toContainText('PNMT demo root')
  await shot(page, 'page-certificates')
})

test('the global search finds a host and a sensor by its token', async ({ page }) => {
  await signIn(page)
  await page.keyboard.press('Control+k')
  await page.keyboard.type('backup-s')
  await page
    .getByRole('option', { name: /backup-srv/ })
    .first()
    .click()
  await expect(page.getByRole('heading', { level: 1, name: 'backup-srv' })).toBeVisible()
})

test('events: failures of the last 24 hours, and the detail drawer', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await page
    .getByRole('link', { name: /Sensors/ })
    .first()
    .click()
  await expect(page).toHaveURL(/events\?status=failed&range=24h|events\?range=24h&status=failed/)
  await expect(page.locator('.list-table tbody tr').first()).toBeVisible()
  await page.locator('.list-table tbody tr').first().click()
  await expect(page.locator('.p-drawer')).toContainText('Source')
  await shot(page, 'page-events-drawer')
})

test('a viewer sees no tokens, no admin pages and no write buttons', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page, 'viewer')
  await expect(page.getByRole('link', { name: 'Users' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'Mail log' })).toHaveCount(0)
  await expect(page.locator('.problems').getByRole('button', { name: /^Ignore/ })).toHaveCount(0)
  await open(page, '/admin/users')
  await expect(page).toHaveURL(/\/pnmt-admin\/$/)
  await open(page, '/hosts')
  await expect(page.getByRole('button', { name: 'New host' })).toHaveCount(0)
  await page.getByRole('link', { name: 'web-03' }).click()
  await page.getByRole('link', { name: 'http', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'http' })).toBeVisible()
  await expect(page.getByText('Integration', { exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Edit Name' })).toHaveCount(0)
  await shot(page, 'page-sensor-viewer')
})

test('the pages fit a phone', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 780 })
  await signIn(page)
  for (const path of [
    '/hosts',
    '/locations',
    '/kb',
    '/events',
    '/notification-rules',
    '/dns-zones',
    '/admin/users',
    '/docs',
  ]) {
    await open(page, path)
    const wide = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1)
    expect(wide, `${path} scrolls sideways`).toBe(false)
  }
  await open(page, '/hosts')
  await shot(page, 'page-hosts-phone')
})
