import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Cookie (docs/cookie-plan.md §9, §19.5, §21.5) on a stack with Cookie__Enabled=true, Cookie stopped as after a fresh
 * installation: Admin → Cookie saves the models (the sleep between rounds) as a new version and a duty switched on; the
 * dashboard shows the card, and Cookie's window too; the window lists the cases the case book opened for the seed's
 * failing sensors, with the trace and the chat. Package nine point five: the decision log, the actions' levels at their
 * defaults, a suppress created and ended on Mute, suppress, and the lessons. Skipped without Cookie. The working hours
 * are the availability plan's since revision 7 (availability.spec.ts).
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

  // Revision 7 moved Working hours and Notifications away.
  const tabs = page.getByRole('navigation', { name: 'Cookie' })
  await expect(tabs.getByRole('link', { name: 'Working hours' })).toHaveCount(0)
  await expect(tabs.getByRole('link', { name: 'Notifications' })).toHaveCount(0)

  // The models: another sleep between rounds, saved as a new version, which History shows in force.
  const before = (await json(page, '/api/v1/cookie/admin/settings/models/versions')) as { version: number }[]
  const next = (before[0]?.version ?? 0) + 1
  await tabs.getByRole('link', { name: 'Models' }).click()
  const sleep = page.getByRole('spinbutton', { name: 'Sleep between rounds' })
  await expect(sleep).toHaveValue(/\d+ min/)
  await sleep.fill(Number.parseInt(await sleep.inputValue(), 10) === 6 ? '5' : '6')
  await sleep.press('Tab')
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByText(`Version ${next}.`)).toBeVisible()
  const history = page.getByRole('region', { name: 'History of the models' })
  await history.getByRole('button', { name: 'Show' }).click()
  await expect(history.getByRole('row').nth(1)).toContainText(new RegExp(`${next}\\s*in force`))
  await shot(page, 'cookie-admin-models')

  // A duty switched on and saved; then off again, as a fresh installation has it.
  await tabs.getByRole('link', { name: 'Actions' }).click()
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
  // The dashboard's card heads the window (revision 7).
  await expect(page.getByRole('region', { name: 'Cookie' })).toContainText('computed by PNMT')
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

test("C and D: the decision log, the actions' levels, a suppress created and ended, the lessons", async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  const status = await json(page, '/api/v1/cookie/status')
  test.skip(!status.cookie.enabled, 'Cookie__Enabled is not set for this stack')

  // Cookie's window → Decisions: the log, empty while Cookie has never run, with its filters.
  await page.goto('/pnmt-admin/cookie/decisions')
  await expect(page.getByRole('heading', { level: 1, name: 'Cookie' })).toBeVisible()
  await expect(
    page.getByRole('navigation', { name: 'Cookie' }).getByRole('link', { name: 'Decisions' }),
  ).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('combobox', { name: 'Kind' })).toHaveText('Every kind')
  await expect(page.getByRole('combobox', { name: 'Outcome' })).toHaveText('Every outcome')
  await expect(
    page.getByText('No decisions yet.').or(page.getByRole('list', { name: 'The decision log' })),
  ).toBeVisible()
  await shot(page, 'cookie-decisions')

  // The cases' kind filter shows its "Every kind" (a null value is PrimeVue's nothing chosen).
  await page.getByRole('navigation', { name: 'Cookie' }).getByRole('link', { name: 'Cases' }).click()
  await expect(page.getByRole('combobox', { name: 'Kind of case' })).toHaveText('Every kind')

  // Admin → Cookie → Actions: each action's level and threshold at the defaults of §21.1, the big brother off.
  await page.goto('/pnmt-admin/admin/cookie/actions')
  const actions = page.getByRole('region', { name: "Cookie's actions" })
  await expect(
    actions.getByRole('group', { name: 'Pushover, level' }).getByRole('button', { name: 'Propose' }),
  ).toHaveAttribute('aria-pressed', 'true')
  await expect(
    actions.getByRole('group', { name: 'Ticket, level' }).getByRole('button', { name: 'Propose' }),
  ).toHaveAttribute('aria-pressed', 'true')
  await expect(
    actions.getByRole('group', { name: 'Note on a host or sensor, level' }).getByRole('button', { name: 'Auto' }),
  ).toHaveAttribute('aria-pressed', 'true')
  await expect(actions.getByRole('spinbutton', { name: 'Pushover, minimum confidence' })).toHaveValue('0.70')
  await expect(actions.getByRole('switch', { name: 'The big brother' })).not.toBeChecked()
  await expect(page.getByRole('switch', { name: "Cookie writes the duties' texts" })).not.toBeChecked()
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled()
  await shot(page, 'cookie-admin-actions')

  // Mute, suppress: no Pushover for a day, in force at once, then ended.
  const tabs = page.getByRole('navigation', { name: 'Cookie' })
  await tabs.getByRole('link', { name: 'Mute, suppress' }).click()
  await expect(page).toHaveURL(/\/admin\/cookie\/instructions$/)
  const create = page.getByRole('region', { name: 'New', exact: true })
  await create.getByRole('group', { name: 'Kind' }).getByRole('button', { name: 'Suppress' }).click()
  await expect(create.getByRole('combobox', { name: 'Applies to' })).toHaveText('Channel')
  await create.getByRole('combobox', { name: 'Channel' }).click()
  await page.getByRole('option', { name: 'Pushover' }).click()
  await create.getByRole('group', { name: 'For' }).getByRole('button', { name: '1 day' }).click()
  const why = `e2e: no Pushover for a day (${Date.now()})`
  await create.getByRole('textbox', { name: 'Why (if you like)' }).fill(why)
  await create.getByRole('button', { name: 'Create' }).click()
  const list = page.getByRole('region', { name: /^In force and recent/ })
  const row = list.getByRole('row').filter({ hasText: why })
  await expect(row).toContainText('Channel Pushover')
  await expect(row).toContainText('active')
  await shot(page, 'cookie-admin-instructions')
  await row.getByRole('button', { name: 'End now' }).click()
  await expect(row).toContainText('ended')
  await expect(row.getByRole('button', { name: 'End now' })).toHaveCount(0)
  await expect
    .poll(async () => {
      const all = (await json(page, '/api/v1/cookie/admin/instructions')) as { text: string; endedAt: string | null }[]
      return all.find((i) => i.text === why)?.endedAt ?? null
    })
    .not.toBeNull()

  // Lessons: none yet; a row added, then undone.
  await tabs.getByRole('link', { name: 'Lessons' }).click()
  await expect(page).toHaveURL(/\/admin\/cookie\/lessons$/)
  const lessons = page.getByRole('region', { name: /^Lessons/ })
  await expect(lessons).toContainText(/\d+ of 30 active/)
  await lessons.getByRole('button', { name: 'Add' }).click()
  await expect(lessons.getByRole('textbox', { name: /^Lesson \d+$/ }).last()).toBeVisible()
  await expect(page.getByRole('button', { name: 'Save' })).toBeEnabled()
  await page.getByRole('button', { name: 'Undo changes' }).click()
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled()
  await expect(page.getByRole('region', { name: 'History of the lessons' })).toBeVisible()
  await shot(page, 'cookie-admin-lessons')
  expect(problems).toEqual([])
})
