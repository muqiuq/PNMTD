import { expect, test } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * System health → Outbound (docs/redmine-queue-outbound-plan.md §7, §13): once a Redmine page was read, the Redmine log
 * lists its calls; a call's details open in a window (a dialog), with the Redmine key masked; → steps to the next call, and
 * the URL (?op=) opens the window again. Skipped without Redmine.
 */
test("the Redmine log, a call's window with the key masked, and its link", async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1600, height: 1000 })
  await signIn(page)
  const info = await (await page.request.get('/api/v1/system/info')).json()
  test.skip(!info.redmine, 'Redmine is not configured for this stack')

  // A Redmine page: its data comes from the worker, whose calls are recorded.
  await page.goto('/pnmt-admin/redmine')
  await expect(page.getByRole('region', { name: 'Counts' })).toBeVisible()

  // The tab, with what runs now on top and the notifications first.
  await page.goto('/pnmt-admin/admin/system/outbound')
  await expect(page.getByRole('heading', { level: 1, name: 'System health' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Outbound' })).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('region', { name: /^Running now/ })).toContainText('Redmine:')
  const logs = page.getByRole('group', { name: 'Log' })
  await expect(logs.getByRole('button', { name: 'Notifications' })).toHaveAttribute('aria-pressed', 'true')

  // The Redmine log: the page's reads, newest first.
  await logs.getByRole('button', { name: 'Redmine' }).click()
  await expect(page).toHaveURL(/[?&]log=redmine/)
  const read = page.getByRole('row', { name: /GET/ }).first()
  await expect(read).toBeVisible()
  await shot(page, 'outbound-redmine')

  // A call's window: a dialog, not a drawer, with the key masked on purpose and the record in the URL.
  await read.getByRole('button', { name: 'Details' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  const key = dialog.getByRole('table', { name: 'Request headers' }).getByRole('row', { name: /X-Redmine-API-Key/i })
  await expect(key).toContainText('•••')
  await expect(dialog).not.toContainText('test-redmine-key-0123456789')
  await expect(dialog).toContainText('masked on purpose')
  await expect(dialog.getByRole('region', { name: 'Response' })).toContainText(/Status \d{3}/)
  await expect(page).toHaveURL(/[?&]op=\d+/)
  await shot(page, 'outbound-dialog')

  // → steps to the next call without closing the window.
  const first = new URL(page.url()).searchParams.get('op')
  await page.keyboard.press('ArrowRight')
  await expect.poll(() => new URL(page.url()).searchParams.get('op')).not.toBe(first)
  await expect(dialog).toBeVisible()

  // The URL opens the window again; Escape closes it.
  await page.reload()
  await expect(page.getByRole('dialog')).toContainText('X-Redmine-API-Key')
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page).not.toHaveURL(/[?&]op=/)
  expect(problems).toEqual([])
})
