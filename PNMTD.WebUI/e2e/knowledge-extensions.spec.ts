import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * The knowledge base's extensions (docs/knowledge-base-extensions-plan.md §16) on the seed: the sensitive mark and Written
 * with AI in the editing page, the recent changes, mutual links between pages, likes and uses, and the knowledge map.
 */
let problems: string[] = []
test.beforeEach(({ page }) => {
  problems = collectProblems(page)
})
test.afterEach(() => expect(problems).toEqual([]))

/** Saves on the editing page; an edit may draw the AI question, which is answered *Not now*. */
async function save(page: Page) {
  const response = page.waitForResponse(
    (r) => /\/api\/v1\/kb\/pages(\/[0-9a-f-]+)?$/.test(r.url()) && r.request().method() !== 'GET',
  )
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  const answer = (await (await response).json()) as { page: { number: number; title: string }; askAiTags: boolean }
  await expect(page.getByRole('heading', { level: 1, name: answer.page.title })).toBeVisible()
  if (answer.askAiTags) {
    await page.getByRole('dialog', { name: 'Find tags with AI' }).getByRole('button', { name: 'Not now' }).click()
  }
  return answer
}

async function newPage(page: Page, title: string, text: string) {
  await page.goto('/pnmt-admin/kb/new')
  await page.getByRole('textbox', { name: 'Title' }).fill(title)
  const editor = page.locator('[aria-label="Visual editor"]')
  await expect(editor).toBeVisible()
  await editor.click()
  await page.keyboard.type(text)
}

test('the sensitive switch and Written with AI in the details, and the page on top of the recent changes', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await newPage(page, 'Rack power budget, Cage B', 'Each rack in Cage B has 6 kW; the PDUs are A and B feeds.')
  // A new page opens its details: both are there before the first save (§4, §5).
  const sensitive = page.getByRole('switch', { name: /Sensitive/ })
  await expect(sensitive).not.toBeChecked()
  await sensitive.click()
  await page.getByRole('checkbox', { name: /Written mostly with AI/ }).check()
  await shot(page, 'kbx-details-flags')
  // Folded, the details' line names both.
  await page.getByRole('button', { name: /Details/ }).click()
  await expect(page.getByRole('button', { name: /Details/ })).toContainText('sensitive · written with AI')
  const created = await save(page)
  // Sensitive: the AI question never comes.
  expect(created.askAiTags).toBe(false)
  const header = page.locator('.page-header')
  await expect(header.getByText('Sensitive', { exact: true })).toBeVisible()
  await expect(header.getByText('Written with AI', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Unmark…' })).toBeVisible()
  await shot(page, 'kbx-page-flags')

  // Unmarking in the editor takes both statements, about the text being saved.
  await page.getByRole('button', { name: 'Edit', exact: true }).click()
  await page.getByRole('button', { name: /Details/ }).click()
  await page.getByRole('switch', { name: /Sensitive/ }).click()
  const statements = page.getByRole('group', { name: 'Statements to unmark the page' })
  await expect(statements.getByRole('checkbox', { name: /no credentials/ })).not.toBeChecked()
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByText('Check both statements, or keep the page sensitive.')).toBeVisible()
  await statements.getByRole('checkbox', { name: /no credentials/ }).check()
  await statements.getByRole('checkbox', { name: /no personal data/ }).check()
  await save(page)
  await expect(page.locator('.page-header').getByText('Sensitive', { exact: true })).toHaveCount(0)
  await expect(page.getByText('Sensitive: never shown to AI or MCP.')).toHaveCount(0)

  // The recent changes on top of the list: the page once, with its creation (the mark is housekeeping, §6).
  await page.goto('/pnmt-admin/kb')
  const recent = page.getByRole('list', { name: 'Recent changes' })
  await expect(recent.getByRole('listitem').first()).toContainText('Rack power budget, Cage B')
  await expect(recent.getByRole('listitem').first()).toContainText('created by')
  await expect(recent.getByRole('link', { name: 'Rack power budget, Cage B' })).toHaveCount(1)
  await shot(page, 'kbx-recent-changes')

  // The list's filter by Written with AI, and the recent changes out of the way while filtering.
  await page.getByRole('combobox', { name: 'Written with AI' }).click()
  await page.getByRole('option', { name: 'Written with AI', exact: true }).click()
  await expect(page).toHaveURL(/ai=true/)
  await expect(recent).toHaveCount(0)
  await expect(
    page.getByRole('list', { name: 'Pages' }).getByRole('link', { name: 'Rack power budget, Cage B' }),
  ).toBeVisible()

  // Folded, it stays folded.
  await page.goto('/pnmt-admin/kb')
  await page.getByRole('button', { name: /Recent changes/ }).click()
  await expect(recent).toHaveCount(0)
  await page.reload()
  await expect(page.getByRole('button', { name: /Recent changes/ })).toHaveAttribute('aria-expanded', 'false')
  await page.getByRole('button', { name: /Recent changes/ }).click()
  await expect(recent).toBeVisible()
})

async function openPage(page: Page, title: string) {
  await page.goto('/pnmt-admin/kb')
  await page.getByRole('list', { name: 'Pages' }).getByRole('link', { name: title, exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
}

test('a link between two pages is mutual: made on one, shown on both, in both histories, removed from the other', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await openPage(page, 'Printer setup, 3rd floor')
  const related = () => page.getByRole('list', { name: 'Related' })
  await expect(page.getByText('Not linked with another page.')).toBeVisible()
  await page.getByRole('combobox', { name: 'Link to a page' }).fill('Mail relay')
  await page.getByRole('option', { name: /Mail relay and SPF/ }).click()
  await expect(related().getByRole('link', { name: 'Mail relay and SPF' })).toBeVisible()
  await shot(page, 'kbx-related-pages')

  // The other page shows it too, and its history says who linked them.
  await related().getByRole('link', { name: 'Mail relay and SPF' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Mail relay and SPF' })).toBeVisible()
  await expect(related().getByRole('link', { name: 'Printer setup, 3rd floor' })).toBeVisible()
  await page.getByRole('button', { name: 'More actions for the page' }).click()
  await page.getByRole('menuitem', { name: 'History' }).click()
  await expect(page.getByText(/Linked with KB-\d+/).first()).toBeVisible()
  await shot(page, 'kbx-history-linked')
  await page.goBack()
  await expect(page.getByRole('heading', { level: 1, name: 'Mail relay and SPF' })).toBeVisible()

  // Removed on this side: gone from both.
  await related().getByRole('button', { name: 'Unlink Printer setup, 3rd floor' }).click()
  await expect(page.getByText('Not linked with another page.')).toBeVisible()
  await openPage(page, 'Printer setup, 3rd floor')
  await expect(page.getByText('Not linked with another page.')).toBeVisible()

  // The seed's links: the network overview with the firewall pair, the switch firmware and the rack.
  await openPage(page, 'Network overview, Zurich DC')
  for (const title of ['Firewall failover, fw-01 and fw-02', 'Core switch firmware update', 'Rack B4 layout']) {
    await expect(related().getByRole('link', { name: title })).toBeVisible()
  }
})

test('a Viewer likes a page and records a use with a note, undoes it, and finds the e-mails on the account', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page, 'viewer')
  await openPage(page, 'Wi-Fi for guests')
  await page.getByRole('button', { name: 'Like', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Unlike' })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByRole('button', { name: 'Unlike' })).toContainText('1')

  // A use: the bar with its note and Undo, the recent uses, and no second one for 12 hours (§9).
  const used = page.getByRole('button', { name: /Used to solve a problem/ })
  await used.click()
  await expect(page.getByText(/You used it to solve a problem at/)).toBeVisible()
  await page.getByRole('textbox', { name: 'What it solved' }).fill('The code for the visitors in room 2')
  await page.getByRole('button', { name: 'Save note' }).click()
  await expect(page.getByRole('list', { name: 'Recent uses' })).toContainText('The code for the visitors in room 2')
  await expect(used).toBeDisabled()
  await expect(used).toContainText('1')
  await shot(page, 'kbx-reactions')
  await page.getByRole('button', { name: 'Undo' }).click()
  await expect(page.getByText(/You used it to solve a problem at/)).toHaveCount(0)
  await expect(used).toBeEnabled()
  await expect(page.getByRole('list', { name: 'Recent uses' })).toHaveCount(0)

  // Who likes it.
  await page.getByRole('link', { name: '1 person' }).click()
  await expect(page.getByRole('dialog', { name: 'Liked by' }).getByRole('listitem')).toHaveCount(1)
  await page.keyboard.press('Escape')

  // The seed's most liked pages first.
  await page.goto('/pnmt-admin/kb?sort=likes&period=all')
  await expect(page.getByRole('list', { name: 'Pages' }).getByRole('listitem').first()).toContainText(
    /Power outage procedure|How PNMT watches us/,
  )

  // Account → Notifications: the two kinds a reader of the knowledge base can choose (§10.2).
  await page.goto('/pnmt-admin/account')
  await expect(page.getByRole('switch', { name: 'Someone liked a page I wrote' })).toBeVisible()
  await expect(page.getByRole('switch', { name: 'Someone used a page I wrote to solve a problem' })).toBeVisible()
  await shot(page, 'kbx-account-notifications')
})
