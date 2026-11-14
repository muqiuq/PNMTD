import { readFileSync } from 'node:fs'
import { expect, test, type Page } from '@playwright/test'
import { collectProblems, pasteFile, pdf, png, shot, signIn } from './support'

/**
 * The knowledge base (docs/locations-knowledge-plan.md §5, §6, §7, §12) on the seed (`pnmtd dev seed`: about twenty pages
 * with tags, links, revisions and diagrams). tools/e2e.sh turns AI on against an address that never answers, so the AI
 * question opens; nothing in these tests sends anything.
 */
let problems: string[] = []
test.beforeEach(({ page }) => {
  problems = collectProblems(page)
})
test.afterEach(() => expect(problems).toEqual([]))

async function openPage(page: Page, title: string) {
  await page.goto('/pnmt-admin/kb')
  await page.getByRole('link', { name: title, exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: title })).toBeVisible()
}

const more = (page: Page) => page.getByRole('button', { name: 'More actions for the page' })

/** Saves on the editing page; an edit may draw the AI question (§7.2: about one in ten), which is answered *Not now*. */
async function save(page: Page) {
  const response = page.waitForResponse(
    (r) => /\/api\/v1\/kb\/pages(\/[0-9a-f-]+)?$/.test(r.url()) && r.request().method() !== 'GET',
  )
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  const answer = await (await response).json()
  await expect(page.getByRole('heading', { level: 1, name: answer.page.title })).toBeVisible()
  return answer as { page: { number: number; title: string }; askAiTags: boolean }
}

test('a page from the editor to its deletion: the AI question, sensitive, a PDF, the history, restore and the search', async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await page.goto('/pnmt-admin/kb')
  await expect(page.getByRole('heading', { level: 1, name: 'Knowledge base' })).toBeVisible()
  await page.getByRole('button', { name: 'New page' }).click()

  // A new page: the details are open, the editor fills the window.
  await page.getByRole('textbox', { name: 'Title' }).fill('Generator test, Zurich DC')
  await page
    .getByRole('textbox', { name: 'Summary' })
    .fill("The provider's monthly generator test, and what PNMT shows meanwhile.")
  await page.getByRole('combobox', { name: 'Tags' }).fill('power')
  await page.keyboard.press('Enter')
  await expect(page.getByText('#power')).toBeVisible()
  const editor = page.locator('[aria-label="Visual editor"]')
  await expect(editor).toBeVisible()
  await editor.click()
  await page.keyboard.type(
    'The provider tests the generator on the first Monday of the month; the Dieseltank is refilled afterwards.',
  )
  await shot(page, 'kb-new-page')
  const created = await save(page)
  const number = created.page.number
  expect(created.askAiTags).toBe(true)

  // The AI question after a new page's first save (§7.2): the statements unchecked, or the page marked sensitive instead.
  const ask = page.getByRole('dialog', { name: 'Find tags with AI' })
  await expect(ask).toBeVisible()
  await expect(ask.getByRole('checkbox', { name: /no credentials/ })).not.toBeChecked()
  await expect(ask.getByRole('checkbox', { name: /no personal data/ })).not.toBeChecked()
  await expect(ask.getByRole('button', { name: 'Find tags' })).toBeDisabled()
  await shot(page, 'kb-ai-question')
  await ask.getByRole('button', { name: 'Mark as sensitive' }).click()
  await expect(ask).toBeHidden()
  await expect(page.getByText('Sensitive: never shown to AI or MCP.')).toBeVisible()
  await more(page).click()
  await expect(page.getByRole('menuitem', { name: 'Unmark as sensitive…' })).toBeVisible()
  await expect(page.getByRole('menuitem', { name: 'Find tags with AI' })).toHaveCount(0)
  await page.keyboard.press('Escape')

  // An edit: a PDF pasted into the visual editor becomes an attachment, and the note goes into the history.
  await page.getByRole('button', { name: 'Edit', exact: true }).click()
  await expect(editor).toBeVisible()
  await editor.click()
  await page.keyboard.press('Control+End')
  await page.keyboard.press('Enter')
  await page.keyboard.type("The provider's checklist: ")
  await pasteFile(editor, 'generator-test.pdf', 'application/pdf', pdf('Generator test checklist'))
  await expect(editor.getByText('generator-test.pdf')).toBeVisible()
  await page.getByRole('button', { name: 'Details' }).click()
  await page.getByRole('textbox', { name: 'Note for the history' }).fill("The provider's checklist")
  const edited = await save(page)
  expect(edited.askAiTags).toBe(false)

  // The attachment at the side, downloaded as it was uploaded.
  const attachments = page.getByRole('complementary').locator('section', { hasText: 'Attachments' })
  const download = page.waitForEvent('download')
  await attachments.getByRole('link', { name: 'generator-test.pdf' }).click()
  const file = await download
  expect(file.suggestedFilename()).toBe('generator-test.pdf')
  expect(
    readFileSync(await file.path())
      .subarray(0, 8)
      .toString('ascii'),
  ).toBe('%PDF-1.4')

  // The history: the note, the diff of the last edit, and the first revision restored as a new one.
  await more(page).click()
  await page.getByRole('menuitem', { name: 'History' }).click()
  await expect(page).toHaveURL(new RegExp(`/kb/${number}/history`))
  await expect(page.getByText("The provider's checklist", { exact: true })).toBeVisible()
  const changes = page.getByRole('region', { name: 'Changes' })
  await expect(changes).toBeVisible()
  await expect(changes).toContainText('generator-test.pdf')
  await shot(page, 'kb-history')
  const first = page.locator('.revisions li').filter({ has: page.getByText('#1', { exact: true }) })
  await first.getByRole('button', { name: 'Restore…' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Restore' }).click()
  await expect(page.locator('.revisions li').filter({ has: page.getByText('#4', { exact: true }) })).toBeVisible()
  await page
    .getByRole('link', { name: `KB-${number}` })
    .first()
    .click()
  await expect(page.getByText('the Dieseltank is refilled afterwards')).toBeVisible()
  await expect(page.getByText("The provider's checklist:")).toHaveCount(0)

  // The content search finds it by a word of its text, with the match marked in the snippet.
  await page.goto('/pnmt-admin/kb')
  await page.getByRole('textbox', { name: /Search the pages' content/ }).fill('dieseltank')
  const row = page.locator('.list-table tr, li, .row').filter({ hasText: 'Generator test, Zurich DC' }).first()
  await expect(row.locator('mark')).toHaveText('Dieseltank')
  await shot(page, 'kb-search')

  // Archived, then deleted by an Admin after typing its number.
  await page.getByRole('link', { name: 'Generator test, Zurich DC' }).click()
  await more(page).click()
  await page.getByRole('menuitem', { name: 'Archive' }).click()
  await expect(page.getByText(/read-only, and left out of lists and panels/)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Edit', exact: true })).toHaveCount(0)
  await more(page).click()
  await page.getByRole('menuitem', { name: 'Delete…' }).click()
  const confirm = page.getByRole('dialog', { name: `Delete KB-${number}?` })
  await expect(confirm.getByRole('button', { name: 'Delete' })).toBeDisabled()
  await confirm.getByRole('textbox', { name: `Type KB-${number} to confirm` }).fill(`KB-${number}`)
  await confirm.getByRole('button', { name: 'Delete' }).click()
  await expect(page).toHaveURL(/\/kb$/)
  const gone = await page.request.get(`/api/v1/kb/pages/${number}`)
  expect(gone.status()).toBe(404)
})

test('a flowchart and an xychart, drawn in the light theme and again in the dark one', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  await openPage(page, 'Network overview, Zurich DC')
  const diagrams = page.locator('figure.md-diagram')
  await expect(diagrams).toHaveCount(2)
  const flowchart = diagrams.nth(0).locator('.svg svg')
  const chart = diagrams.nth(1).locator('.svg svg')
  const drawn = async () => {
    await flowchart.scrollIntoViewIfNeeded()
    await expect(flowchart).toContainText('router-core')
    await chart.scrollIntoViewIfNeeded()
    await expect(chart).toContainText('Init7 uplink')
    // Six months: a bar each, and the line.
    await expect.poll(() => chart.locator('rect').count()).toBeGreaterThanOrEqual(6)
    await expect(chart.locator('path').first()).toBeAttached()
  }
  await drawn()
  await expect(diagrams.locator('.error')).toHaveCount(0)
  await flowchart.scrollIntoViewIfNeeded()
  await shot(page, 'kb-diagrams-light')
  const light = [await flowchart.innerHTML(), await chart.innerHTML()]

  await page.getByRole('button', { name: 'Dark theme' }).click()
  await expect.poll(async () => (await flowchart.innerHTML()) !== light[0]).toBe(true)
  await expect.poll(async () => (await chart.innerHTML()) !== light[1]).toBe(true)
  await drawn()
  await expect(diagrams.locator('.error')).toHaveCount(0)
  await flowchart.scrollIntoViewIfNeeded()
  await shot(page, 'kb-diagrams-dark')
})

test("the fallback: with the editors' chunks blocked, the textarea edits, takes a pasted image and saves", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.route(/\/assets\/(VisualEditor|CodeEditor|monacoSetup|MonacoDiff)-[\w-]+\.js$/, (route) => route.abort())
  await signIn(page)
  await openPage(page, 'Wi-Fi for guests')
  await page.getByRole('button', { name: 'Edit', exact: true }).click()
  await expect(page.getByText('The editor couldn’t start; editing as plain text.')).toBeVisible()
  await expect(page.getByRole('radiogroup', { name: 'Editor' })).toHaveCount(0)
  const textarea = page.getByRole('textbox', { name: 'Markdown', exact: true })
  await expect(textarea).toHaveValue(/The guest SSID is \*\*PNMT-Guest\*\*/)
  await textarea.click()
  await page.keyboard.press('Control+End')
  await page.keyboard.type('\n\nThe sign at reception:\n\n')
  await pasteFile(textarea, 'reception-sign.png', 'image/png', png(48, 32, [40, 120, 200]))
  await expect(textarea).toHaveValue(/!\[[^\]]*\]\(\/api\/v1\/files\/[0-9a-f-]+\/[\w.-]+\)/)
  await shot(page, 'kb-fallback')
  const saved = await save(page)
  if (saved.askAiTags) {
    await page.getByRole('dialog', { name: 'Find tags with AI' }).getByRole('button', { name: 'Not now' }).click()
  }
  await expect(page.getByText('The sign at reception:')).toBeVisible()
  const image = page.locator('article img[src^="/api/v1/files/"]')
  await expect(image).toBeVisible()
  await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth)).toBe(48)
})

test('a Viewer reads pages, sensitive ones too, without edit actions', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page, 'viewer')
  await page.goto('/pnmt-admin/kb')
  await expect(page.getByRole('heading', { level: 1, name: 'Knowledge base' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'New page' })).toHaveCount(0)
  await openPage(page, 'Power outage procedure')
  await expect(page.getByRole('button', { name: 'Edit', exact: true })).toHaveCount(0)
  await more(page).click()
  await expect(page.getByRole('menuitem', { name: 'History' })).toBeVisible()
  for (const action of ['Archive', 'Mark as sensitive', 'Status', 'Find tags with AI']) {
    await expect(page.getByRole('menuitem', { name: action })).toHaveCount(0)
  }
  await page.keyboard.press('Escape')
  await shot(page, 'kb-page-viewer')
  await openPage(page, 'Break-glass accounts')
  await expect(page.getByText('Sensitive: never shown to AI or MCP.')).toBeVisible()

  // The global search finds pages by their tags (§6.7).
  await page.keyboard.press('Control+k')
  await page.keyboard.type('#ups')
  await page.getByRole('option', { name: /Replacing a UPS battery/ }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Replacing a UPS battery' })).toBeVisible()
})
