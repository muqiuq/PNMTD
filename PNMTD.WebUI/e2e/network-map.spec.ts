import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/** The seed's five offices. Other specs may add routers of their own (mikrotik.spec.ts does), so these are checked by name. */
const seedRouters = ['office-st-gallen-gw', 'office-bern-gw', 'office-basel-gw', 'office-geneva-gw', 'office-lugano-gw']

async function seedOnMap(page: Page) {
  for (const name of seedRouters)
    await expect(page.getByRole('button', { name: new RegExp(`^Router ${name}\\b`) })).toBeVisible()
}

/**
 * The network map (docs/network-map-plan.md §8, §12) on the seed: St. Gallen's WireGuard star on the map, a router's panel,
 * the Zurich DC's hosts by the filter, Basel ↔ Bern's fibre made a connection from its suggestion, the scans and the schedule,
 * and the interfaces. Only what the browser can break: the rules have their tests in PNMTD.Tests.
 */
test('the map, a suggestion made a connection, the scans and the interfaces', async ({ page }) => {
  const problems = collectProblems(page)
  page.on('console', (m) => {
    if (m.type() === 'error' && m.text().includes('TypeError'))
      problems.push(`${page.url()}: ${m.text().slice(0, 300)}`)
  })
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)

  // The map: the five routers, the hub's WireGuard star.
  await page.goto('/pnmt-admin/network-map')
  await expect(page.getByRole('heading', { level: 1, name: 'Network map' })).toBeVisible()
  const map = page.getByTestId('network-map')
  await seedOnMap(page)
  await expect(map.locator('[data-edge]')).toHaveCount(4)
  await shot(page, 'network-map')

  // A router's panel: its connections and ports.
  await page.getByRole('button', { name: 'Router office-st-gallen-gw' }).click()
  const panel = page.getByRole('complementary', { name: 'Details' })
  await expect(panel.getByRole('heading', { level: 2, name: 'office-st-gallen-gw' })).toBeVisible()
  await expect(panel.getByRole('heading', { level: 3, name: /^Connections\s*5$/ })).toBeVisible()
  await expect(panel.getByRole('cell', { name: 'sfp-sfpplus1', exact: true })).toBeVisible()
  // 30rem at the right edge, once it has slid in (PrimeVue's own 20rem must not win).
  const box = async () => {
    const b = await panel.boundingBox()
    return b && { width: Math.round(b.width), right: Math.round(b.x + b.width) }
  }
  await expect.poll(box).toEqual({ width: 420, right: 1440 })
  await shot(page, 'network-map-panel')

  // On a phone the panel takes the whole width; its close button goes back to the map.
  await page.setViewportSize({ width: 390, height: 844 })
  await expect.poll(box).toEqual({ width: 390, right: 390 })
  await shot(page, 'network-map-panel-phone')
  await panel.getByRole('button', { name: 'Close' }).click()
  await expect(panel).toBeHidden()
  await page.setViewportSize({ width: 1440, height: 1000 })

  // The Zurich DC's hosts: router-core in its circle, with the fibre.
  // PrimeVue's MultiSelect keeps its combobox input hidden: its visible placeholder opens it.
  await page.getByText('Hosts of…').click()
  await page.getByRole('option', { name: 'Zurich DC', exact: true }).click()
  await page.keyboard.press('Escape')
  await expect(map.locator('[data-node^="host:"]').first()).toBeVisible()
  await expect(page.getByRole('button', { name: 'Host router-core' })).toBeVisible()
  await expect(map.locator('.group text', { hasText: 'Zurich DC' })).toBeVisible()
  await shot(page, 'network-map-zurich')

  // Suggestions: Basel ↔ Bern's fibre, seen by both, made a connection.
  await page.getByRole('link', { name: /^Suggestions/ }).click()
  const fibre = page.getByRole('row').filter({ hasText: 'office-bern-gw' }).filter({ hasText: 'Fiber SMF 10G' })
  await expect(fibre).toBeVisible()
  await expect(fibre.getByText('High')).toBeVisible()
  await fibre.getByRole('button', { name: 'Make connection…' }).click()
  await page.getByRole('textbox', { name: 'Notes' }).fill('Dark fibre, e2e')
  await page.getByRole('button', { name: 'Make connection', exact: true }).click()
  await expect(page.getByText('Made a connection')).toBeVisible()
  await page.getByRole('link', { name: /^Connections/ }).click()
  await expect(page.getByRole('row').filter({ hasText: 'Dark fibre, e2e' })).toBeVisible()
  await shot(page, 'network-map-connections')

  // Scans: the seed's full scan, the schedule.
  await page.getByRole('link', { name: /^Scans/ }).click()
  await expect(page.getByTestId('scan-schedule')).toContainText('Last full scan')
  await expect(page.getByRole('row').filter({ hasText: 'All routers' }).first()).toBeVisible()
  await shot(page, 'network-map-scans')

  // Network interfaces: the offices' printers by DHCP, unassigned.
  await page.getByRole('link', { name: 'Network interfaces' }).click()
  await page.getByPlaceholder('MAC, IP or host name').fill('printer-basel')
  await expect(page.getByRole('row').filter({ hasText: 'printer-basel' })).toBeVisible()
  await expect(page.getByRole('row').filter({ hasText: 'printer-basel' }).getByText('Unassigned')).toBeVisible()
  await shot(page, 'network-map-interfaces')

  expect(problems).toEqual([])
})

/**
 * Moving routers and saving their positions (docs/network-map-positions-plan.md §7): a drag marks the router and shows the
 * bar; Discard puts it back; Save keeps it there after a reload; leaving with a move asks first; the panel's Back to
 * automatic and the Layout menu's reset. Saving needs Edit: PNMTD.Tests' NetworkMapTests check the refusals.
 */
test('routers moved by hand and their positions saved', async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/network-map')
  const map = page.getByTestId('network-map')
  await seedOnMap(page)
  const routers = await map.locator('[data-node^="router:"]').count()
  const node = page.getByRole('button', { name: /^Router office-st-gallen-gw/ })
  const centre = async () => {
    const b = (await node.locator('circle.body').boundingBox())!
    return { x: Math.round(b.x + b.width / 2), y: Math.round(b.y + b.height / 2) }
  }
  // Where it is on the map, whatever the page's scroll (focusing a node scrolls it into view).
  const onMap = async () => {
    const c = await centre()
    const m = (await map.boundingBox())!
    return { x: c.x - Math.round(m.x), y: c.y - Math.round(m.y) }
  }
  const near = (a: { x: number; y: number }, b: { x: number; y: number }) =>
    Math.abs(a.x - b.x) <= 1 && Math.abs(a.y - b.y) <= 1
  const drag = async (dx: number, dy: number) => {
    const from = await centre()
    await page.mouse.move(from.x, from.y)
    await page.mouse.down()
    await page.mouse.move(from.x + dx, from.y + dy, { steps: 8 })
    await page.mouse.up()
  }
  const start = await centre()

  // A drag marks the router and shows the bar; Discard puts it back.
  await drag(150, 80)
  await expect(page.getByText('1 router moved')).toBeVisible()
  await expect(node).toHaveAttribute('data-unsaved', 'true')
  expect(near(await centre(), { x: start.x + 150, y: start.y + 80 })).toBe(true)
  await shot(page, 'network-map-moved')
  await page.getByRole('button', { name: 'Discard' }).click()
  await expect(page.getByText('1 router moved')).toBeHidden()
  await expect.poll(async () => near(await centre(), start)).toBe(true)

  // The keyboard moves a focused router too.
  await node.focus()
  await page.keyboard.press('Shift+ArrowRight')
  await expect(page.getByText('1 router moved')).toBeVisible()

  // Leaving the map with a move asks first.
  await page.getByRole('link', { name: /^Connections/ }).click()
  await expect(page.getByText('Leave without saving?')).toBeVisible()
  await page.getByRole('button', { name: 'Cancel' }).click()
  await expect(page.getByText('1 router moved')).toBeVisible()

  // Saved: the moved router and the others, where they are; after a reload, the same picture.
  await drag(120, -60)
  await page.getByRole('button', { name: 'Save positions' }).click()
  await expect(page.getByText(`Saved the positions of ${routers} routers`)).toBeVisible()
  await expect(page.getByText('1 router moved')).toBeHidden()
  await expect(node).not.toHaveAttribute('data-unsaved', 'true')
  await page.getByRole('button', { name: 'Fit' }).click()
  const saved = await onMap()
  await page.reload()
  await seedOnMap(page)
  await expect.poll(async () => near(await onMap(), saved)).toBe(true)

  // The panel: saved, and back to automatic.
  await node.click()
  const panel = page.getByRole('complementary', { name: 'Details' })
  await expect(panel.getByText('Saved', { exact: true })).toBeVisible()
  await panel.getByRole('button', { name: 'Back to automatic' }).click()
  await expect(page.getByText('office-st-gallen-gw is placed automatically again')).toBeVisible()
  await expect(panel.getByText('Automatic', { exact: true })).toBeVisible()
  await panel.getByRole('button', { name: 'Close' }).click()

  // The Layout menu: all routers back to automatic, for everyone.
  await page.getByRole('button', { name: 'Layout' }).click()
  await page.getByRole('menuitem', { name: 'Back to automatic for all routers…' }).click()
  await expect(page.getByText(`The saved positions of ${routers - 1} routers are cleared`)).toBeVisible()
  await page.getByRole('button', { name: 'Back to automatic', exact: true }).click()
  await expect(page.getByText('Every router is placed automatically again')).toBeVisible()
  await page.getByRole('button', { name: 'Layout' }).click()
  await expect(page.getByRole('menuitem', { name: 'Back to automatic for all routers…' })).toHaveAttribute(
    'aria-disabled',
    'true',
  )
  await page.keyboard.press('Escape')
  expect(problems).toEqual([])
})

/**
 * Skip rules (docs/network-suggestion-rules-plan.md §4.5, §6): a suggestion's Skip like this… opens the form prefilled from
 * its evidence, the preview counts what it removes, saving removes it after the analysis, turning the rule off brings it
 * back. The rules' matching has its tests in PNMTD.Tests.
 */
test('a skip rule made from a suggestion, previewed, turned off and deleted', async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  await page.goto('/pnmt-admin/network-map?tab=suggestions')
  const behind = () => page.getByRole('row').filter({ hasText: 'nas-01' }).filter({ hasText: 'router-core' })
  await expect(behind()).toBeVisible()

  // Prefilled from the evidence: the hub's port where nas-01's MAC is learned; the preview counts the suggestion.
  await behind()
    .getByRole('button', { name: /^Skip like/ })
    .click()
  const dialog = page.getByRole('dialog', { name: 'New skip rule' })
  await expect(dialog.getByRole('textbox', { name: 'Interface' })).toHaveValue('sfp-sfpplus1')
  await expect(dialog.getByText(/Removes \d+ open suggestion/)).toBeVisible()
  await shot(page, 'network-map-skip-rule')
  await dialog.getByRole('textbox', { name: 'Name' }).fill('Zurich DC fibre, e2e')
  await dialog.getByRole('button', { name: 'Add rule' }).click()
  await expect(page.getByText(/Skip rule added/)).toBeVisible()

  // The analysis follows a few seconds later: the list as it is after a reload.
  async function afterReload(rows: number) {
    await expect(async () => {
      await page.reload()
      await page.getByRole('table').first().waitFor()
      await expect(behind()).toHaveCount(rows, { timeout: 1000 })
    }).toPass({ timeout: 30_000, intervals: [2000] })
  }
  await afterReload(0)

  // The tab lists it; off, the suggestion comes back.
  await page.getByRole('link', { name: /^Skip rules/ }).click()
  const row = page.getByRole('row').filter({ hasText: 'Zurich DC fibre, e2e' })
  await expect(row).toBeVisible()
  await expect(row.getByText('sfp-sfpplus1')).toBeVisible()
  await shot(page, 'network-map-skip-rules')
  await row.getByRole('switch', { name: 'Zurich DC fibre, e2e on' }).click()
  await expect(page.getByText('Zurich DC fibre, e2e: off')).toBeVisible()
  await page.getByRole('link', { name: /^Suggestions/ }).click()
  await afterReload(1)

  // Deleted, after asking.
  await page.getByRole('link', { name: /^Skip rules/ }).click()
  await page.getByRole('button', { name: 'Delete Zurich DC fibre, e2e' }).click()
  await page.getByRole('button', { name: 'Delete', exact: true }).click()
  await expect(page.getByText('Skip rule deleted')).toBeVisible()
  await expect(page.getByRole('row').filter({ hasText: 'Zurich DC fibre, e2e' })).toBeHidden()
  expect(problems).toEqual([])
})
