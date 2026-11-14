import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Cookie (docs/cookie-plan.md §9, §19.5) on a stack with Cookie__Enabled=true, Cookie stopped as after a fresh
 * installation: Admin → Cookie saves the working hours as a new version and a duty switched on; the dashboard shows the
 * card; Cookie's window lists the cases the case book opened for the seed's failing sensors, with the trace and the chat.
 * Skipped without Cookie.
 */
async function json(page: Page, path: string) {
  const response = await page.request.get(path)
  expect(response.ok(), `GET ${path}: ${response.status()}`).toBeTruthy()
  return response.json()
}

test("Admin → Cookie saves a version and a duty; the card; the window's cases, trace and chat", async ({ page }) => {
  // The case book's first run comes about 20 s after the start; the stack may have started just now.
  test.setTimeout(180_000)
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  const status = await json(page, '/api/v1/cookie/status')
  test.skip(!status.cookie.enabled, 'Cookie__Enabled is not set for this stack')

  // The dashboard's card: PNMT's own state while Cookie is stopped.
  const card = page.getByRole('region', { name: 'Cookie' })
  await expect(card).toBeVisible()
  await expect(card).toContainText('Stopped')
  await expect(card).toContainText('computed by PNMT')
  await shot(page, 'cookie-card')

  // Admin → Cookie opens on its overview.
  await page.goto('/pnmt-admin/admin/cookie')
  await expect(page.getByRole('heading', { level: 1, name: 'Cookie' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Status' })).toContainText('Stopped')
  await expect(page.getByRole('button', { name: 'Start' })).toBeVisible()
  await shot(page, 'cookie-admin-overview')

  // The working hours: Monday ends at another time, saved as a new version, which History shows in force.
  const before = (await json(page, '/api/v1/cookie/admin/settings/hours/versions')) as { version: number }[]
  const next = (before[0]?.version ?? 0) + 1
  await page.getByRole('link', { name: 'Working hours' }).click()
  const mondayTo = page.getByRole('textbox', { name: 'Monday, period 1, to' })
  await expect(mondayTo).toHaveValue(/\d\d:\d\d/)
  await mondayTo.fill((await mondayTo.inputValue()) === '16:30' ? '16:00' : '16:30')
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByText(`Version ${next}.`)).toBeVisible()
  const history = page.getByRole('region', { name: 'History of the working hours' })
  await history.getByRole('button', { name: 'Show' }).click()
  await expect(history.getByRole('row').nth(1)).toContainText(new RegExp(`${next}\\s*in force`))
  await shot(page, 'cookie-admin-hours')

  // A duty switched on and saved; then off again, as a fresh installation has it.
  await page.getByRole('link', { name: 'Actions' }).click()
  const closingNote = page.getByRole('switch', { name: 'Closing note' })
  await expect(closingNote).not.toBeChecked()
  await closingNote.click()
  await page.getByRole('button', { name: 'Save' }).click()
  await expect
    .poll(async () => (await json(page, '/api/v1/cookie/admin/settings')).actions.duties.closingNote)
    .toBe(true)
  await closingNote.click()
  await page.getByRole('button', { name: 'Save' }).click()
  await expect
    .poll(async () => (await json(page, '/api/v1/cookie/admin/settings')).actions.duties.closingNote)
    .toBe(false)

  // Cookie's window: the case book has opened a case for the seed's failing hosts.
  await expect
    .poll(async () => (await json(page, '/api/v1/cookie/cases')).totalCount, { timeout: 90_000, intervals: [3_000] })
    .toBeGreaterThan(0)
  await page.goto('/pnmt-admin/cookie')
  await expect(page.getByRole('heading', { level: 1, name: 'Cookie' })).toBeVisible()
  const firstCase = page.getByRole('row').nth(1)
  await expect(firstCase).toBeVisible()
  await firstCase.click()
  await expect(page).toHaveURL(/[?&]case=/)
  const detail = page.getByRole('region', { name: 'The case' })
  await expect(detail).toContainText('Timeline')
  await shot(page, 'cookie-cases')

  // The case's trace: empty while Cookie is stopped, and the full one for an admin.
  await detail.getByRole('link', { name: /Trace of this case/ }).click()
  await expect(page).toHaveURL(/\/cookie\/trace\?case=/)
  await expect(page.getByText('Nothing in the trace yet.')).toBeVisible()
  await expect(page.getByText("You see Cookie's notes and replies")).toHaveCount(0)

  // The chat says that Cookie is stopped, and takes nothing until it is started.
  await page.getByRole('link', { name: 'Chat' }).click()
  await expect(page.getByText('Cookie is stopped: an admin starts it on Admin → Cookie.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Send' })).toBeDisabled()
  await shot(page, 'cookie-chat')
  expect(problems).toEqual([])
})
