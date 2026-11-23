import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Boards (docs/boards-plan.md §16): an admin builds a board on its full page (a note added to a column, the theme picked from the
 * tiles, a network set), saves it, and opens its URL: signed in, from anywhere; signed out and from outside the network, the refusal
 * with the address PNMT sees. Then the seed's NOC board in every theme, for review (test-results/screens/board-theme-*.png).
 */
async function api(page: Page, method: 'GET' | 'POST' | 'PUT' | 'DELETE', path: string, data?: unknown) {
  const xsrf = (await page.context().cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value ?? ''
  const response = await page.request.fetch(path, {
    method,
    data,
    headers: { 'X-XSRF-TOKEN': decodeURIComponent(xsrf) },
  })
  expect(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`).toBeTruthy()
  return response.status() === 204 ? null : response.json()
}

const themes = [
  'default',
  'dark',
  'high-contrast',
  'high-contrast-dark',
  'slay',
  'switzerland',
  'terminal',
  'brasil',
  'atomic-age',
  'wtf',
  'suse',
  'win95',
  'winxp',
  'ubuntu',
  'amiga',
]

test('a board built on its page, shown at its URL, and refused outside its networks', async ({ page, browser }) => {
  test.setTimeout(180_000)
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1600, height: 1000 })
  await signIn(page)

  await page.goto('/pnmt-admin/boards')
  await expect(page.getByRole('heading', { level: 1, name: 'Boards' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'NOC', exact: true })).toBeVisible()
  await shot(page, 'boards-list')

  // A new board from the starting layout: its page, with the preview.
  await page.locator('[data-test="new-board"]').click()
  await page.getByLabel('Name').fill('E2E wall')
  await page.locator('[data-test="create-board"]').click()
  await page.waitForURL(/\/pnmt-admin\/boards\/[0-9a-f-]{36}/)
  const preview = page.locator('[data-test="board-preview"]')
  await expect(preview.getByRole('region', { name: 'Sensors', exact: true })).toBeVisible()
  await expect(preview.locator('.board-bar')).toBeVisible()

  // A note in column 2, shown in the preview as it is typed.
  await page.locator('[data-test="add-element-1"]').click()
  await page.locator('[data-test="add-Note"]').click()
  await page.getByLabel('Text (Markdown)').fill('**Hotline** 044 555 12 34')
  await expect(preview.locator('.board-note strong')).toHaveText('Hotline')
  await page.getByRole('button', { name: 'Layout' }).click()
  await expect(page.locator('[data-test="element-Note"]')).toBeVisible()

  // The theme, from the tiles: the preview changes at once.
  await page.getByRole('link', { name: 'Look' }).click()
  await page.locator('[data-test="change-theme"]').click()
  await expect(page.locator('[data-test^="theme-"]')).toHaveCount(15)
  await page.locator('[data-test="theme-terminal"]').click()
  await expect(preview.locator('.board')).toHaveAttribute('data-board-theme', 'terminal')
  await shot(page, 'boards-theme-picker')
  await page.locator('[data-test="use-theme"]').click()

  // A network, then Save.
  await page.getByRole('link', { name: 'Access' }).click()
  await page.getByRole('button', { name: 'Add a network' }).click()
  await page.getByLabel('Network 1', { exact: true }).fill('192.0.2.0/24')
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByText('saved', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Show' }).click()
  await expect(page.locator('[data-test="board-url"]')).toHaveText(/\/pnmt-board\/pnmb_[A-Za-z0-9_-]{43}$/)
  const url = (await page.locator('[data-test="board-url"]').textContent())!.trim()
  await page.getByRole('link', { name: 'Layout' }).click()
  await shot(page, 'boards-editor')

  // Signed in, the URL opens from anywhere.
  const board = await page.context().newPage()
  await board.setViewportSize({ width: 1920, height: 1080 })
  await board.goto(url)
  await expect(board.locator('.board[data-board-theme="terminal"]')).toBeVisible()
  await expect(board.locator('.board-note strong')).toHaveText('Hotline')
  await expect(board.locator('.board-bar')).toBeVisible()
  await shot(board, 'board-e2e-wall')

  // Signed out, from outside 192.0.2.0/24: refused, with the address PNMT sees.
  const outsider = await browser.newContext()
  const stranger = await outsider.newPage()
  await stranger.goto(url)
  await expect(stranger.getByRole('heading', { name: 'Not from this network' })).toBeVisible()
  await expect(stranger.getByText(/PNMT sees this screen as/)).toBeVisible()
  await shot(stranger, 'board-refused')
  await outsider.close()

  // The seed's NOC board (from anywhere) in every theme.
  const boards = (await api(page, 'GET', '/api/v1/boards')) as { id: string; name: string }[]
  const noc = boards.find((b) => b.name === 'NOC')!
  // Renewed: a URL the seed made under another key ring couldn't be shown again.
  const nocUrl = ((await api(page, 'POST', `/api/v1/boards/${noc.id}/url/renew`)) as { url: string }).url
  const wall = await browser.newContext({ viewport: { width: 1920, height: 1080 } })
  const screen = await wall.newPage()
  for (const theme of themes) {
    const detail = await api(page, 'GET', `/api/v1/boards/${noc.id}`)
    await api(page, 'PUT', `/api/v1/boards/${noc.id}`, {
      name: detail.name,
      description: detail.description,
      enabled: true,
      networks: [],
      layout: { ...detail.layout, look: { ...detail.layout.look, theme } },
      revision: detail.revision,
    })
    await screen.goto(nocUrl)
    await expect(screen.locator(`.board[data-board-theme="${theme}"]`)).toBeVisible()
    await expect(screen.locator('.board-bar')).not.toHaveAttribute('data-colour', 'Grey', { timeout: 15_000 })
    await screen.waitForTimeout(500)
    await shot(screen, `board-theme-${theme}`)
  }
  await wall.close()

  // The e2e board goes again; its URL stops at once.
  const mine = boards.find((b) => b.name === 'E2E wall')!
  await api(page, 'DELETE', `/api/v1/boards/${mine.id}`)
  await board.reload()
  await expect(board.getByRole('heading', { name: 'No board here' })).toBeVisible()
  expect(problems).toEqual([])
})
