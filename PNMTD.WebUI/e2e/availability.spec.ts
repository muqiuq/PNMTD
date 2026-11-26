import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Admin → Availability plan (docs/cookie-plan.md §8.2, §9.4, revision 7) on the seed: the first admin is in the plan as
 * ADM, responsible on the working days of the next two weeks, and the six types of the team's Excel plan exist (ANWE the
 * default, UNAV for days off). As an admin: the table with Today and today's row; ADM's cell on the next working day set
 * to HOME by a click and the menu; People and Settings. A viewer, neither in the plan nor with a right, has no menu entry
 * and is told so.
 */
async function json(page: Page, path: string) {
  const response = await page.request.get(path)
  expect(response.ok(), `GET ${path}: ${response.status()}`).toBeTruthy()
  return response.json()
}

interface Plan {
  today: string
  people: { id: string; code: string }[]
  types: { id: string; code: string }[]
  days: { date: string; closed: boolean; cells: { userId: string; typeId: string }[] }[]
}

/** 2026-11-18 as the plan writes it, ready for a RegExp: 18\.11\.2026. */
const dotted = (iso: string) => `${iso.slice(8, 10)}\\.${iso.slice(5, 7)}\\.${iso.slice(0, 4)}`

test('the availability plan: the table, a cell set to HOME, People and Settings', async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  const plan = (await json(page, '/api/v1/availability/plan')) as Plan
  const adm = plan.people.find((p) => p.code === 'ADM')
  expect(adm, 'the seed puts the first admin into the plan as ADM').toBeTruthy()
  const home = plan.types.find((t) => t.code === 'HOME')
  expect(home, 'the seed creates HOME').toBeTruthy()
  const day = plan.days.find((d) => d.date > plan.today && !d.closed)
  expect(day, 'a working day after today').toBeTruthy()

  await page.goto('/pnmt-admin/admin/availability')
  await expect(page.getByRole('heading', { level: 1, name: 'Availability plan' })).toBeVisible()
  await expect(page.locator('aside.drawer').getByRole('link', { name: 'Availability plan' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Today' })).toBeVisible()
  const grid = page.getByRole('region', { name: 'The plan' })
  await expect(grid.getByRole('columnheader', { name: 'ADM' })).toBeVisible()
  await expect(grid.locator('tr[aria-current="date"]')).toContainText(new RegExp(dotted(plan.today)))
  // The seed made ADM responsible on the working days of the next two weeks.
  await expect(grid.getByRole('cell', { name: new RegExp(`^Responsible ${dotted(day!.date)}: ADM$`) })).toBeVisible()
  await shot(page, 'availability-plan')

  // ADM's cell on the next working day: a click opens the menu of types, HOME sets it.
  const cell = grid.getByRole('cell', { name: new RegExp(`^ADM ${dotted(day!.date)}:`) })
  await cell.click()
  const menu = page.getByRole('group', { name: 'Set the selected cells' })
  await expect(menu).toBeVisible()
  await shot(page, 'availability-menu')
  await menu.getByRole('button', { name: /^HOME/ }).click()
  await expect(menu).toHaveCount(0)
  await expect(cell).toHaveText('HOME')
  await expect
    .poll(async () => {
      const saved = (await json(page, `/api/v1/availability/plan?from=${day!.date}&to=${day!.date}`)) as Plan
      return saved.days[0]?.cells.find((c) => c.userId === adm!.id)?.typeId
    })
    .toBe(home!.id)

  // Far into the future, and Today brings today's row back.
  await grid.evaluate((el) => (el.scrollTop = el.scrollHeight))
  await expect(grid.locator('tr[aria-current="date"]')).toHaveCount(0)
  await page.getByRole('button', { name: 'Today' }).click()
  await expect(grid.locator('tr[aria-current="date"]')).toBeVisible()

  // Each month starts with a row of its own, its name readable (docs/adjustments1.md): the next month's.
  const [y, m] = plan.today.split('-').map(Number)
  const next = new Date(Date.UTC(m === 12 ? y! + 1 : y!, m === 12 ? 0 : m!, 1))
  const label = `${next.toLocaleString('en', { month: 'long', timeZone: 'UTC' })} ${next.getUTCFullYear()}`
  const month = grid.locator('tr.month', { hasText: label })
  await month.scrollIntoViewIfNeeded()
  await expect(month.locator('td.c-month')).toHaveText(label)
  await expect(month.locator('td.c-month')).toBeVisible()
  await shot(page, 'availability-month')

  // A column is as wide as its header's edge is dragged, also after a reload; a double-click gives the default back.
  const comment = () => grid.getByRole('columnheader', { name: 'Comment' })
  const width = async () => (await comment().boundingBox())?.width ?? 0
  const before = await width()
  const edge = (await comment().locator('.column-edge').boundingBox())!
  await page.mouse.move(edge.x + edge.width / 2, edge.y + edge.height / 2)
  await page.mouse.down()
  await page.mouse.move(edge.x + edge.width / 2 + 80, edge.y + edge.height / 2, { steps: 5 })
  await page.mouse.up()
  await expect.poll(width).toBeGreaterThan(before + 70)
  await page.reload()
  await expect.poll(width).toBeGreaterThan(before + 70)
  await comment().locator('.column-edge').dblclick()
  await expect.poll(width).toBeLessThan(before + 2)

  // People: ADM is in the plan.
  const tabs = page.getByRole('navigation', { name: 'Availability plan' })
  await tabs.getByRole('link', { name: 'People' }).click()
  await expect(page).toHaveURL(/\/admin\/availability\/people$/)
  const admRow = page.getByRole('row').filter({ has: page.getByText('ADM', { exact: true }) })
  await expect(admRow).toContainText('in the plan')
  await shot(page, 'availability-people')

  // Settings: the seed's six types, and the working hours moved here from Admin → Cookie.
  await tabs.getByRole('link', { name: 'Settings' }).click()
  await expect(page).toHaveURL(/\/admin\/availability\/settings$/)
  const types = page.getByRole('region', { name: 'Availability types' })
  await expect(types.getByRole('textbox', { name: 'Type 1, code' })).toBeVisible()
  const codes = await types
    .getByRole('textbox', { name: /^Type \d+, code$/ })
    .evaluateAll((inputs) => inputs.map((i) => (i as HTMLInputElement).value))
  expect(codes).toEqual(expect.arrayContaining(['ANWE', 'HOME', 'EXTN', 'VACA', 'KOMP', 'UNAV']))
  await expect(page.getByRole('region', { name: 'Working hours' })).toContainText('Monday')
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled()
  await shot(page, 'availability-settings')
  expect(problems).toEqual([])
})

test('Fill again: the days typed, read and checked before anything is sent', async ({ page }) => {
  // docs/fill-again-typed-days-plan.md §3: far ahead, so the first test's HOME stays as it is.
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  const plan = (await json(page, '/api/v1/availability/plan')) as Plan
  const add = (iso: string, n: number) =>
    new Date(Date.parse(`${iso}T00:00:00Z`) + n * 86_400_000).toISOString().slice(0, 10)
  /** 2027-11-03 as the field writes it: 3.11.2027. */
  const field = (iso: string) => `${Number(iso.slice(8, 10))}.${Number(iso.slice(5, 7))}.${iso.slice(0, 4)}`
  const last = add(plan.today, 364)
  const first = add(plan.today, 300)
  const second = add(first, 1)

  await page.goto('/pnmt-admin/admin/availability')
  await page.getByRole('button', { name: 'Fill again…' }).click()
  const dialog = page.getByRole('dialog', { name: 'Fill again' })
  const from = dialog.getByLabel('From', { exact: true })
  const to = dialog.getByLabel('To', { exact: true })
  await expect(from).toHaveValue(field(plan.today))
  await expect(to).toHaveValue(field(last))
  await expect(dialog).toContainText(new RegExp(`until ${dotted(last)}\\.`))
  // The cursor in From once the dialog is open, and no calendar on focus: either moved the focus while text was typed,
  // and the field kept its old day (CI, 2026-11-24). The calendar opens from its button.
  await expect(from).toBeFocused()
  await expect(page.locator('.p-datepicker-panel')).toHaveCount(0)
  await dialog.getByRole('button', { name: 'Choose Date' }).first().click()
  await expect(page.locator('.p-datepicker-panel')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.locator('.p-datepicker-panel')).toHaveCount(0)
  await expect(dialog).toBeVisible()

  // A day that doesn't exist: said under the field, which is empty.
  await from.fill('31.4.')
  await from.press('Tab')
  await expect(dialog).toContainText(`"31.4." isn't a day: type 3.11.2027, 3.11.27 or 3.11.`)
  await expect(from).toHaveValue('')

  // The past.
  await from.fill(field(add(plan.today, -1)))
  await from.press('Tab')
  await expect(dialog).toContainText('From today on.')

  // Without a year: the next one, written out.
  await from.fill(`${Number(first.slice(8, 10))}.${Number(first.slice(5, 7))}.`)
  await from.press('Tab')
  await expect(from).toHaveValue(field(first))
  await expect(dialog.locator('.field-error')).toHaveCount(0)

  // To before From: Enter keeps the dialog open and says why.
  await to.fill(field(add(first, -1)))
  await to.press('Enter')
  await expect(dialog).toContainText('Not before From.')
  await shot(page, 'availability-fill-again')

  // Two digits for the year, then Enter at once: what is shown is sent.
  const sent = page.waitForRequest((r) => r.method() === 'POST' && r.url().endsWith('/api/v1/availability/fill'))
  await to.fill(`${Number(second.slice(8, 10))}.${Number(second.slice(5, 7))}.${second.slice(2, 4)}`)
  await to.press('Enter')
  expect((await sent).postDataJSON()).toMatchObject({ again: true, from: first, to: second })
  await expect(dialog).toHaveCount(0)
  expect(problems).toEqual([])
})

test('a viewer outside the plan has no menu entry, and the page says why', async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page, 'viewer')
  await expect(page.locator('aside.drawer').getByRole('link', { name: 'Availability plan' })).toHaveCount(0)
  await page.goto('/pnmt-admin/admin/availability')
  await expect(page.getByText("The plan isn't yours to see.")).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Availability plan' }).getByRole('link')).toHaveText([
    'Plan',
    'Status',
  ])
  await page.goto('/pnmt-admin/admin/availability/status')
  await expect(page.getByText("The plan isn't yours to see.")).toBeVisible()
  expect(problems).toEqual([])
})

/** docs/availability-extensions-plan.md: the clock, Go to a day, the year's row, notes and the Status tab. */
test('the plan: its zone and time, a day far ahead, the year’s row, a note, and the Status tab', async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  const plan = (await json(page, '/api/v1/availability/plan')) as Plan & { timeZone: string }
  const add = (iso: string, n: number) =>
    new Date(Date.parse(`${iso}T00:00:00Z`) + n * 86_400_000).toISOString().slice(0, 10)
  const typed = (iso: string) => `${Number(iso.slice(8, 10))}.${Number(iso.slice(5, 7))}.${iso.slice(0, 4)}`

  await page.goto('/pnmt-admin/admin/availability')
  const grid = page.getByRole('region', { name: 'The plan' })
  await expect(grid.locator('tr[aria-current="date"]')).toBeVisible()
  // §3.3: PNMT's zone, today's date and the time.
  const clock = page.getByTestId('plan-clock')
  await expect(clock).toContainText(plan.timeZone)
  await expect(clock).toContainText(new RegExp(dotted(plan.today)))

  // §5: a day far beyond the days loaded: typed, Enter, and the plan loads around it; the address keeps it.
  const far = add(plan.today, 300)
  const go = page.getByRole('combobox', { name: 'Go to a day' })
  await go.fill(typed(far))
  await go.press('Enter')
  await expect(page).toHaveURL(new RegExp(`[?&]day=${far}`))
  const farRow = grid.locator('tr.day', { hasText: new RegExp(dotted(far)) })
  await expect(farRow).toBeVisible()
  await expect(grid.locator('tr.flash')).toHaveCount(1)
  // Today goes back, and the address forgets the day.
  await page.getByRole('button', { name: 'Today' }).click()
  await expect(grid.locator('tr[aria-current="date"]')).toBeVisible()
  await expect(page).not.toHaveURL(/[?&]day=/)

  // §4: 1 January has a year's row before January's.
  const nextYear = Number(plan.today.slice(0, 4)) + 1
  await go.fill(`1.1.${nextYear}`)
  await go.press('Enter')
  const yearRow = grid.locator('tr.year', { hasText: String(nextYear) })
  await expect(yearRow).toBeVisible()
  await expect(grid.locator('tr.month', { hasText: `January ${nextYear}` })).toBeVisible()
  await shot(page, 'availability-year-row')

  // §6: a note under one's own name, on that day; then deleted.
  const day = `${nextYear}-01-04`
  await go.fill(typed(day))
  await go.press('Enter')
  const written = `${day.slice(8, 10)}.${day.slice(5, 7)}.${day.slice(0, 4)}`
  await grid.getByRole('button', { name: `Add a note on ${written}` }).click()
  const notes = page.getByRole('dialog', { name: /^Notes on / })
  await expect(notes).toBeVisible()
  await notes.getByRole('textbox', { name: 'Add a note' }).fill('Dentist until 10')
  await notes.getByRole('textbox', { name: 'Add a note' }).press('Enter')
  await expect(notes.locator('.note-text')).toHaveText(['Dentist until 10'])
  // tools/e2e.sh signs in as e2e-admin, "Ada Admin": the note carries the name.
  await expect(notes).toContainText('Ada Admin')
  await expect(grid.locator(`td[data-notes="${day}"]`)).toContainText('Ada Admin')
  await expect(grid.locator(`td[data-notes="${day}"]`)).toContainText('Dentist until 10')
  await shot(page, 'availability-notes')
  await notes.getByRole('button', { name: /^Delete the note by / }).click()
  await expect(notes.locator('.note-text')).toHaveCount(0)
  await page.keyboard.press('Escape')
  await expect(notes).toHaveCount(0)
  await expect(grid.locator(`td[data-notes="${day}"]`)).not.toContainText('Dentist')

  // §10: the Status tab, its tables and CSV.
  await page.getByRole('navigation', { name: 'Availability plan' }).getByRole('link', { name: 'Status' }).click()
  await expect(page).toHaveURL(/\/admin\/availability\/status/)
  await expect(page.getByRole('region', { name: /^Days as Responsible, against each share/ })).toBeVisible()
  const perPerson = page.getByRole('region', { name: /^Days per person/ })
  await expect(perPerson.getByRole('rowheader', { name: /ADM/ })).toBeVisible()
  await expect(page.getByRole('region', { name: /^The office/ })).toBeVisible()
  await shot(page, 'availability-status')
  const download = page.waitForEvent('download')
  await perPerson.getByRole('button', { name: 'CSV' }).click()
  expect((await download).suggestedFilename()).toMatch(/^availability-\d{4}.*\.csv$/)
  // A tile with a first day opens the plan there.
  const without = page.getByTestId('tile-without')
  if ((await without.evaluate((el) => el.tagName)) === 'BUTTON') {
    await without.click()
    await expect(page).toHaveURL(/\/admin\/availability\/plan\?day=\d{4}-\d{2}-\d{2}/)
  }
  expect(problems).toEqual([])
})

/**
 * docs/availability-extensions-plan.md §3.2 and §7: "Periods may not overlap" went with the server's answer and stayed after
 * the periods were fixed, until the next Save; and the responsible's name is a setting.
 */
test('Settings: the overlap message goes once the periods are fixed, and the responsible is renamed', async ({
  page,
}) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await page.goto('/pnmt-admin/admin/availability/settings')
  const hours = page.getByRole('region', { name: 'Working hours' })
  const monday = (n: number, end: 'from' | 'to') => hours.getByRole('textbox', { name: `Monday, period ${n}, ${end}` })
  await monday(1, 'to').fill('12:00')
  await hours.getByRole('button', { name: 'Add a period on Monday' }).click()
  await monday(2, 'from').fill('11:30')
  await monday(2, 'to').fill('17:00')
  await expect(hours).toContainText('Periods may not overlap.')
  const answer = page.waitForResponse(
    (r) => r.request().method() === 'PUT' && r.url().endsWith('/api/v1/availability/settings'),
  )
  await page.getByRole('button', { name: 'Save' }).click()
  expect((await answer).status()).toBe(400)
  await expect(hours).toContainText('Periods may not overlap.')
  // Fixed: 08:00–11:30 and 13:00–17:00, as reported. The message goes at once, without another Save.
  await monday(1, 'to').fill('11:30')
  await monday(2, 'from').fill('13:00')
  await expect(hours).not.toContainText('Periods may not overlap.')
  await shot(page, 'availability-hours-fixed')
  await page.getByRole('button', { name: 'Undo changes' }).click()

  // §7: Hero, in the plan's header; then back.
  const name = page.getByRole('textbox', { name: 'Called' })
  await name.fill('Hero')
  await expect(page.getByRole('region', { name: "The day's responsible" })).toContainText('Hero for 14 days')
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled()
  await page.goto('/pnmt-admin/admin/availability')
  await expect(page.getByRole('region', { name: 'The plan' }).getByRole('columnheader', { name: 'Hero' })).toBeVisible()
  await page.goto('/pnmt-admin/admin/availability/settings')
  await page.getByRole('textbox', { name: 'Called' }).fill('Responsible')
  await page.getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled()
  expect(problems).toEqual([])
})
