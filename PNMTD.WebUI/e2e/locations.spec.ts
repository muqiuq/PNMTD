import { expect, test, type Page } from '@playwright/test'
import { collectProblems, pasteFile, png, shot, signIn } from './support'

/**
 * Locations (docs/locations-knowledge-plan.md §3, §4, §5.9, §12) on the seed (`pnmtd dev seed`): Zurich DC with Cage B (and
 * its Rack B4) and the Meet-me room, Zurich office, the typical fields with values, and the other offices' gateways with
 * only their location texts.
 */
let problems: string[] = []
test.beforeEach(({ page }) => {
  problems = collectProblems(page)
})
test.afterEach(() => expect(problems).toEqual([]))

async function openLocation(page: Page, name: string) {
  await page.goto('/pnmt-admin/locations')
  await page.getByRole('link', { name, exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
}

/** A field's card (FieldCard): on Information, its group is a region of the same name too. */
const card = (page: Page, label: string) => page.locator(`section.field-card[aria-label="${label}"]`)
/** One of Information's pages: Details, or a Markdown field. */
const infoPage = (page: Page, label: string) =>
  page.getByRole('navigation', { name: 'Information' }).getByRole('link', { name: label, exact: true })

test("a location from a host's text, a sub-location, its Overview with a pasted image, and the host on both pages", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await page.goto('/pnmt-admin/locations')
  await expect(page.getByRole('heading', { level: 1, name: 'Locations' })).toBeVisible()

  // From the hosts' texts…: the Lugano gateway's text becomes a location, and the gateway is assigned to it.
  await page.getByRole('button', { name: "From the hosts' texts…" }).first().click()
  const dialog = page.getByRole('dialog', { name: "From the hosts' texts" })
  await expect(dialog.getByText('Lugano office', { exact: true })).toBeVisible()
  await expect(dialog.getByText('— 1 host', { exact: true }).first()).toBeVisible()
  await dialog.getByRole('checkbox', { name: /Lugano office/ }).check()
  await expect(dialog.getByRole('textbox', { name: 'Name of the new location for Lugano office' })).toHaveValue(
    'Lugano office',
  )
  await expect(dialog.getByText('1 host will be assigned.')).toBeVisible()
  await shot(page, 'locations-from-texts')
  await dialog.getByRole('button', { name: 'Assign' }).click()
  await expect(dialog).toBeHidden()
  await page.getByRole('link', { name: 'Lugano office', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Lugano office' })).toBeVisible()
  await expect(page.locator('.hosts-panel').getByText('office-lugano-gw')).toBeVisible()

  // A sub-location under it, and the gateway moved there.
  await page.getByRole('button', { name: 'Sub-location' }).click()
  await page.getByRole('textbox', { name: 'Name' }).fill('Server room')
  await page.getByRole('textbox', { name: 'Description' }).fill('Behind the meeting room')
  await page.getByRole('button', { name: 'Create' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Server room' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Lugano office' }).first()).toBeVisible()
  await page.getByRole('button', { name: 'Add hosts…' }).click()
  const add = page.getByRole('dialog', { name: 'Add hosts to Server room' })
  await add.getByRole('textbox', { name: 'Search hosts' }).fill('office-lugano')
  await add.getByText('office-lugano-gw', { exact: true }).click()
  await add.getByRole('button', { name: 'Add 1 host' }).click()
  await expect(add).toBeHidden()
  await expect(page.locator('.hosts-panel').getByText('office-lugano-gw')).toBeVisible()

  // Overview, on the editing page: the drawer folds to its rail, the editor takes the window, and a pasted image is uploaded.
  await expect(page.locator('.app-layout')).not.toHaveClass(/\brail\b/)
  await card(page, 'Overview').getByRole('link', { name: 'Fill in' }).click()
  await expect(page).toHaveURL(/\/locations\/[0-9a-f-]+\/fields\/overview\/edit$/)
  await expect(page.locator('.app-layout')).toHaveClass(/\brail\b/)
  const editor = page.locator('[aria-label="Visual editor"]')
  await expect(editor).toBeVisible()
  await editor.click()
  await page.keyboard.type('Rack R1: the gateway at the top, the switch below it.')
  await page.keyboard.press('Enter')
  await pasteFile(editor, 'rack-r1.png', 'image/png', png(64, 40, [220, 120, 40]))
  await expect(editor.locator('img[src^="/api/v1/files/"]')).toBeVisible()
  await shot(page, 'locations-field-editing')
  await page.getByRole('button', { name: 'Save', exact: true }).click()

  // Back on the location's page: the text and the image, loaded from PNMT's files.
  await expect(page.getByRole('heading', { level: 1, name: 'Server room' })).toBeVisible()
  await expect(page.locator('.app-layout')).not.toHaveClass(/\brail\b/)
  const overview = card(page, 'Overview')
  await expect(overview.getByText('Rack R1: the gateway at the top')).toBeVisible()
  const image = overview.locator('img[src^="/api/v1/files/"]')
  await expect(image).toBeVisible()
  await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth)).toBe(64)
  // The information on the left, two thirds wide; the sub-locations and hosts on the right (§3.4).
  const info = await page.locator('.overview > .left').boundingBox()
  const side = await page.locator('.overview > .right').boundingBox()
  expect(info!.x).toBeLessThan(side!.x)
  expect(info!.width / side!.width).toBeCloseTo(2, 0)
  await expect(page.locator('.overview > .right .hosts-panel')).toBeVisible()
  await shot(page, 'locations-sub-location')

  // The parent counts the sub-location's host, and names where it is.
  await page.getByRole('link', { name: 'Lugano office' }).first().click()
  await expect(page.getByRole('heading', { level: 1, name: 'Lugano office' })).toBeVisible()
  await expect(page.getByRole('link', { name: /Server room.*1 host/ })).toBeVisible()
  const row = page.locator('.hosts-panel li', { hasText: 'office-lugano-gw' })
  await expect(row).toContainText('Server room')
  // The history names both.
  await page.getByRole('link', { name: 'History', exact: true }).click()
  await expect(page.getByText(/office-lugano-gw/).first()).toBeVisible()
})

test('the seed: the tree, inherited and overridden values, a sensitive field, the floor plan and a diagram', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await page.goto('/pnmt-admin/locations')
  // Expanded one level: the sites and their sub-locations.
  for (const name of ['Zurich DC', 'Cage B', 'Meet-me room', 'Zurich office']) {
    await expect(page.getByRole('link', { name, exact: true })).toBeVisible()
  }
  await shot(page, 'locations-tree')

  // Zurich DC: its Overview, a sensitive field, and the Network field's diagram on Information.
  await openLocation(page, 'Zurich DC')
  await expect(card(page, 'Overview').getByText('Interxion ZUR1')).toBeVisible()
  await expect(card(page, 'Access').getByLabel('Sensitive')).toBeVisible()
  await expect(card(page, 'On-site contact')).toContainText('Anna Muster')
  await shot(page, 'locations-site')
  await page.getByRole('link', { name: 'Information', exact: true }).click()
  // The simple fields under Details, each Markdown field a page of its own (docs/adjustments1.md).
  await expect(infoPage(page, 'Details')).toBeVisible()
  await infoPage(page, 'Network').click()
  // Drawn when it scrolls into view (§5.8).
  await card(page, 'Network').scrollIntoViewIfNeeded()
  await expect(card(page, 'Network').locator('.md-diagram svg')).toBeVisible()
  await expect(card(page, 'Network').locator('.md-diagram svg')).toContainText('router-core')

  // The Meet-me room overrides the opening hours; Rack B4, two levels down, inherits them.
  await openLocation(page, 'Meet-me room')
  await expect(card(page, 'Opening hours')).toContainText('Escorted only; book 24 hours ahead')
  await expect(card(page, 'Opening hours')).not.toContainText('from Zurich DC')
  await page.goto('/pnmt-admin/locations')
  await page.getByRole('link', { name: 'Cage B', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Cage B' })).toBeVisible()
  await page.locator('a.sub', { hasText: 'Rack B4' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Rack B4' })).toBeVisible()
  await expect(card(page, 'Opening hours')).toContainText('24/7')
  await expect(card(page, 'Opening hours')).toContainText('from Zurich DC')
  await expect(page.locator('.hosts-panel').getByText('fw-01', { exact: true })).toBeVisible()

  // Cage B's floor plan: an image from the files folder, which the seed stored through PNMT's own store.
  await page.getByRole('link', { name: 'Cage B' }).first().click()
  await page.getByRole('link', { name: 'Information', exact: true }).click()
  await infoPage(page, 'Floor plan').click()
  const plan = card(page, 'Floor plan').locator('img[src^="/api/v1/files/"]')
  await plan.scrollIntoViewIfNeeded()
  await expect(plan).toBeVisible()
  await expect.poll(() => plan.evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth)).toBe(800)
  await shot(page, 'locations-information')
})

test('a Viewer reads a location, without edit actions and without empty fields', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page, 'viewer')
  await page.goto('/pnmt-admin/locations')
  await expect(page.getByRole('link', { name: 'Zurich DC', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'New location' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: "From the hosts' texts…" })).toHaveCount(0)
  await openLocation(page, 'Zurich office')
  await expect(card(page, 'Overview').getByText('40 desks')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Sub-location' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Add hosts…' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: /^Edit / })).toHaveCount(0)
  await expect(page.getByText('Not filled in')).toHaveCount(0)
  await shot(page, 'locations-viewer')

  // The global search finds locations, with their paths (§6.7).
  await page.keyboard.press('Control+k')
  await page.keyboard.type('rack b4')
  await page.getByRole('option', { name: 'Rack B4' }).filter({ hasText: 'Zurich DC › Cage B' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Rack B4' })).toBeVisible()
})
