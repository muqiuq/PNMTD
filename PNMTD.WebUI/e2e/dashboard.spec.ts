import { expect, test, type Locator, type Page } from '@playwright/test'
import { shot, signIn } from './support'

/**
 * The dashboard's acceptance targets (docs/webui-migration-plan.md §5.5) on the seed (`pnmtd dev
 * seed`: 50 hosts, 300 sensors, 6 failing sensors in 3 hosts).
 *
 * Cookie's card heads the left side (docs/cookie-plan.md §19.8) and is measured complete, its summary for the reader
 * included: how tall it is depends on whether the case book had opened its cases when the summary was first written (it is
 * kept 15 minutes), up to five of them and "n more". Before, a fast run measured it without them, a slow one with them.
 */
const inView = async (page: Page, locator: Locator) => {
  const box = await locator.first().boundingBox()
  const viewport = page.viewportSize()!
  return box !== null && box.y >= 0 && box.y + box.height <= viewport.height + 1
}

/** The summary strip: Cookie's card has a summary too, the reader's. */
const summaryStrip = (page: Page) => page.getByRole('region', { name: 'Summary', exact: true })

/** Cookie's card, complete: the layout as it stays. */
const cookieCardComplete = (page: Page) => expect(page.getByLabel('Your summary')).toBeVisible()

test('1440×900: the summary, the problems beginning and 20 host rows without scrolling', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await expect(summaryStrip(page)).toContainText('3 hosts failing')
  await expect(page.locator('.problems .sensor')).toHaveCount(6)
  await cookieCardComplete(page)
  expect(await inView(page, summaryStrip(page)), 'the summary strip').toBe(true)
  // Below Cookie's card, the problems begin above the fold: their heading and the first failing host. All of them fit only
  // while the card's summary has no open cases.
  expect(await inView(page, page.locator('.problems .panel-header')), "the problems' heading").toBe(true)
  expect(await inView(page, page.locator('.problems .host-line')), 'the first failing host').toBe(true)
  const rows = await page.locator('.hosts-panel .row').evaluateAll(
    (els) =>
      els.filter((e) => {
        const r = e.getBoundingClientRect()
        return r.top >= 0 && r.bottom <= window.innerHeight
      }).length,
  )
  expect(rows, 'host rows in view').toBeGreaterThanOrEqual(20)
  await expect(page).toHaveTitle(/^\(6\) Dashboard · PNMT/)
  await shot(page, 'dashboard-1440')
  // Refresh asks for a build made now, not the cached one the polls get.
  const fresh = page.waitForResponse((r) => r.url().includes('/api/v1/dashboard?fresh=true'))
  await page.getByRole('button', { name: 'Refresh' }).click()
  expect((await fresh).status(), 'the fresh build').toBe(200)
  await expect(page.locator('.app-bar .updated')).toContainText('Updated just now')
  await page.getByRole('button', { name: 'Dark theme' }).click()
  await page.mouse.move(0, 400)
  await page.waitForTimeout(500) // the theme's colour transition
  await shot(page, 'dashboard-1440-dark')
  await page.getByRole('button', { name: 'Light theme' }).click()
})

// With Cookie's card heading the left side (docs/cookie-plan.md §19.8), the recent changes begin above the fold: their
// heading and the newest of them.
test('1920×1080: the recent state changes begin too', async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 1080 })
  await signIn(page)
  await expect(page.locator('.problems .sensor')).toHaveCount(6)
  await cookieCardComplete(page)
  expect(await inView(page, page.locator('.problems')), 'all problems').toBe(true)
  expect(await inView(page, page.locator('.recent .panel-header')), "the recent changes' heading").toBe(true)
  expect(await inView(page, page.locator('.recent .change')), 'the newest change').toBe(true)
  await shot(page, 'dashboard-1920')
})

test('390×844: the summary and the first failing host above the fold, nothing wider than 360', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await signIn(page)
  await expect(page.locator('.problems .host').first()).toBeVisible()
  await cookieCardComplete(page)
  expect(await inView(page, summaryStrip(page)), 'the summary strip').toBe(true)
  expect(await inView(page, page.locator('.problems .host-line')), 'the first failing host').toBe(true)
  await shot(page, 'dashboard-390')
  await page.setViewportSize({ width: 360, height: 780 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})
