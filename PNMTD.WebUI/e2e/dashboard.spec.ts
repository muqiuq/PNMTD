import { expect, test, type Page } from '@playwright/test'
import { shot, signIn } from './support'

/**
 * The dashboard's acceptance targets (docs/webui-migration-plan.md §5.5) on the seed (`pnmtd dev
 * seed`: 50 hosts, 300 sensors, 6 failing sensors in 3 hosts).
 */
const inView = async (page: Page, selector: string) => {
  const box = await page.locator(selector).first().boundingBox()
  const viewport = page.viewportSize()!
  return box !== null && box.y >= 0 && box.y + box.height <= viewport.height + 1
}

test('1440×900: the summary, all problems and 20 host rows without scrolling', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await expect(page.locator('.summary')).toContainText('3 hosts failing')
  await expect(page.locator('.problems .sensor')).toHaveCount(6)
  expect(await inView(page, '.summary')).toBe(true)
  expect(await inView(page, '.problems')).toBe(true)
  const rows = await page.locator('.hosts-panel .row').evaluateAll(
    (els) =>
      els.filter((e) => {
        const r = e.getBoundingClientRect()
        return r.top >= 0 && r.bottom <= window.innerHeight
      }).length,
  )
  expect(rows).toBeGreaterThanOrEqual(20)
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
  expect(await inView(page, '.problems')).toBe(true)
  expect(await inView(page, '.recent .panel-header')).toBe(true)
  expect(await inView(page, '.recent .change')).toBe(true)
  await shot(page, 'dashboard-1920')
})

test('390×844: the summary and the first failing host above the fold, nothing wider than 360', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await signIn(page)
  await expect(page.locator('.problems .host').first()).toBeVisible()
  expect(await inView(page, '.summary')).toBe(true)
  expect(await inView(page, '.problems .host-line')).toBe(true)
  await shot(page, 'dashboard-390')
  await page.setViewportSize({ width: 360, height: 780 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})
