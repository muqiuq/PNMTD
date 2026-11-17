import { expect, test } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Zabbix (docs/audit-redmine-zabbix-plan.md §7, §12), against the hosts and problems Z0 set up: the overview's counts and
 * matrix, a tile opening its problems, a problem's row with its description, the hosts with an unavailable interface, a
 * host's page with its latest data and links, and attaching it to a new PNMT host, whose Zabbix sensor fails with the
 * problem. Against tools/e2e.sh's fake Zabbix 7.0, or a real one through E2E_ENV; skipped without Zabbix.
 */
test('the overview, problems, hosts, a host page, and a host attached as a sensor', async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1600, height: 1000 })
  await signIn(page)
  const info = await (await page.request.get('/api/v1/system/info')).json()
  test.skip(!info.zabbix, 'Zabbix is not configured for this stack')

  // The overview: a tile per severity, the matrix by host group, the hosts.
  await page.goto('/pnmt-admin/zabbix')
  await expect(page.getByRole('heading', { level: 1, name: 'Zabbix' })).toBeVisible()
  const tiles = page.getByRole('region', { name: 'By severity' })
  await expect(tiles).toContainText('Disaster')
  await expect(page.getByRole('region', { name: 'By category' })).toContainText('PNMT Servers')
  await expect(page.getByRole('region', { name: 'Hosts' })).toContainText('Unavailable')
  await shot(page, 'zabbix-overview')

  // A tile opens its severity's problems; a row opens with the trigger's description.
  await tiles.getByRole('link', { name: /High/ }).click()
  await expect(page).toHaveURL(/tab=problems|\/zabbix\/problems/)
  await expect(page).toHaveURL(/severity=High/)
  const row = page.getByRole('row', { name: /\/var is 95 % full/ })
  await expect(row).toBeVisible()
  await expect(page.getByRole('row', { name: /High CPU load/ })).toHaveCount(0)
  await row.getByRole('button').first().click()
  await expect(page.getByText('The partition /var is almost full.')).toBeVisible()
  await shot(page, 'zabbix-problems')

  // The hosts: nas01's agent is unavailable.
  await page.getByRole('link', { name: 'Hosts', exact: true }).click()
  await expect(page.getByRole('row', { name: /nas01/ })).toBeVisible()
  await expect(page.getByRole('row', { name: /nas01/ }).getByLabel('Agent unavailable')).toBeVisible()
  await shot(page, 'zabbix-hosts')

  // A host's page: its problems, its latest data, the links into Zabbix.
  await page.getByRole('link', { name: 'Web 01' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Web 01' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Problems' })).toContainText('/var is 95 % full')
  await expect(page.getByRole('region', { name: 'Latest data' })).toContainText('Disk /var used')
  await expect(page.getByRole('link', { name: 'Open in Zabbix' })).toHaveAttribute(
    'href',
    /action=host\.view.*filter_host=web01\.example\.com/,
  )
  await shot(page, 'zabbix-host')

  // Attached to a new PNMT host: its Zabbix sensor fails with the problems.
  const name = `e2e-zbx-${Date.now()}`
  await page.getByRole('button', { name: 'Attach to PNMT host…' }).click()
  await page.getByRole('button', { name: 'A new host' }).click()
  await page.getByLabel('Name of the new host').fill(name)
  await page.getByRole('button', { name: 'Attach', exact: true }).click()
  await expect(page.getByRole('region', { name: 'PNMT' })).toContainText(name)
  await page.getByRole('link', { name: 'its Zabbix sensor' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Web 01' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Problems' })).toContainText('/var is 95 % full')
  // Exact: the new host's name holds a timestamp, which contains any three digits now and then.
  await expect(page.getByText('490', { exact: true })).toBeVisible()
  await shot(page, 'zabbix-sensor')

  // Detached again: the sensor goes, the host stays in Zabbix.
  await page.getByRole('link', { name: 'Setup' }).click()
  await page.getByRole('button', { name: 'Detach…' }).click()
  await page.getByRole('button', { name: 'Detach', exact: true }).click()
  await expect(page).toHaveURL(/\/hosts\//)
  expect(problems).toEqual([])
})
