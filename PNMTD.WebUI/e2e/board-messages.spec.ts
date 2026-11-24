import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Board messages and the boards' new elements (docs/board-messages-plan.md §15): a message from the Debug page to two boards, on both
 * within 6 s and gone within 6 s of its withdrawal; the five kinds and the themes with a frame of their own; the longest text at 720p
 * and 4K; and a board with a sensor's message (Fit), an uploaded animated GIF, a 12-hour clock with German day names, and on duty with
 * two deputies. Screenshots in test-results/screens/board-message-*.png and board-elements-*.png.
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

interface Board {
  id: string
  name: string
  url: string
}

async function board(page: Page, name: string, columns = 1): Promise<Board> {
  const created = await api(page, 'POST', '/api/v1/boards', {
    name: `${name} ${Date.now() % 100000}`,
    columns,
    from: 'start',
  })
  const url = ((await api(page, 'GET', `/api/v1/boards/${created.id}/url`)) as { url: string }).url
  return { id: created.id, name: created.name, url }
}

async function look(page: Page, id: string, changes: Record<string, unknown>, columns?: unknown) {
  const detail = await api(page, 'GET', `/api/v1/boards/${id}`)
  return api(page, 'PUT', `/api/v1/boards/${id}`, {
    name: detail.name,
    description: detail.description,
    enabled: true,
    networks: [],
    layout: { ...detail.layout, look: { ...detail.layout.look, ...changes }, ...(columns ? { columns } : {}) },
    revision: detail.revision,
  })
}

const send = (page: Page, boardIds: string[], kind: string, title: string, text: string, seconds = 300) =>
  api(page, 'POST', '/api/v1/boards/messages', { boardIds, kind, title, text, seconds }) as Promise<{ id: string }>

const withdraw = (page: Page, id: string) => api(page, 'POST', `/api/v1/boards/messages/${id}/withdraw`)

/** A two-frame animated GIF, 120 × 80, looping: blue and orange stripes that swap. LZW with a clear code every two pixels. */
function animatedGif(): Buffer {
  const width = 120
  const height = 80
  const palette = [
    [0x0d, 0x6e, 0xfd],
    [0xfd, 0x7e, 0x14],
    [0xff, 0xff, 0xff],
    [0x21, 0x25, 0x29],
  ]
  const frame = (shift: number) => {
    const pixels: number[] = []
    for (let y = 0; y < height; y++)
      for (let x = 0; x < width; x++)
        pixels.push(y < 8 || y >= height - 8 ? 3 : ((Math.floor((x + shift) / 20) % 2) as 0 | 1))
    const codes = [4]
    let since = 0
    for (const p of pixels) {
      if (since === 2) {
        codes.push(4)
        since = 0
      }
      codes.push(p)
      since++
    }
    codes.push(5)
    const bytes: number[] = []
    let buffer = 0
    let bits = 0
    for (const c of codes) {
      buffer |= c << bits
      bits += 3
      while (bits >= 8) {
        bytes.push(buffer & 0xff)
        buffer >>= 8
        bits -= 8
      }
    }
    if (bits > 0) bytes.push(buffer & 0xff)
    const blocks: number[] = []
    for (let i = 0; i < bytes.length; i += 255) {
      const part = bytes.slice(i, i + 255)
      blocks.push(part.length, ...part)
    }
    return [
      0x21,
      0xf9,
      4,
      4,
      50,
      0,
      0,
      0,
      0x2c,
      0,
      0,
      0,
      0,
      width & 0xff,
      width >> 8,
      height & 0xff,
      height >> 8,
      0,
      2,
      ...blocks,
      0,
    ]
  }
  return Buffer.from([
    ...Buffer.from('GIF89a'),
    width & 0xff,
    width >> 8,
    height & 0xff,
    height >> 8,
    0x91,
    0,
    0,
    ...palette.flat(),
    0x21,
    0xff,
    0x0b,
    ...Buffer.from('NETSCAPE2.0'),
    3,
    1,
    0,
    0,
    0,
    ...frame(0),
    ...frame(20),
    0x3b,
  ])
}

test('a message from the Debug page reaches two boards and goes when withdrawn; the kinds and themes', async ({
  page,
  browser,
}) => {
  test.setTimeout(300_000)
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1600, height: 1100 })
  await signIn(page)
  const a = await board(page, 'E2E messages A')
  const b = await board(page, 'E2E messages B')
  const wall = await browser.newContext({ viewport: { width: 1920, height: 1080 } })
  const screenA = await wall.newPage()
  const screenB = await wall.newPage()
  await screenA.goto(a.url)
  await screenB.goto(b.url)
  await expect(screenA.locator('.board-bar')).toBeVisible()
  await expect(screenB.locator('.board-bar')).toBeVisible()

  // The Debug page: both boards, an alert, the preview in the first board's theme.
  await page.goto('/pnmt-admin/admin/debug')
  await expect(page.getByText('Send a board message')).toBeVisible()
  await page.locator('[data-test="message-boards"]').click()
  await page.getByRole('option', { name: a.name }).click()
  await page.getByRole('option', { name: b.name }).click()
  await page.keyboard.press('Escape')
  await page.locator('#message-kind').click()
  await page.getByRole('option', { name: 'Alert' }).click()
  await page.locator('[data-test="message-title"]').fill('Core switch down in building B')
  await page
    .locator('[data-test="message-text"]')
    .fill('Phones and Wi-Fi in building B are out. **We are on it**; the next update follows at 14:45.')
  await expect(page.locator('[data-test="message-preview"] .board-msg-title')).toHaveText(
    'Core switch down in building B',
  )
  await shot(page, 'board-message-debug')
  await page.locator('[data-test="message-send"]').click()
  await expect(page.getByTestId('message-result')).toContainText('Shown on')

  // On both screens within about 5 s.
  await expect(screenA.locator('[data-test="board-message"]')).toBeVisible({ timeout: 6_000 })
  await expect(screenB.locator('[data-test="board-message"]')).toBeVisible({ timeout: 6_000 })
  await expect(screenA.locator('.board-bar')).toBeVisible()
  await expect(screenA).toHaveTitle(/^⚠ Core switch down/)
  await screenA.waitForTimeout(400)
  await shot(screenA, 'board-message-alert-default')

  // Withdrawn on the Debug page: gone from both within about 5 s.
  await expect(page.locator('[data-test="open-messages"]')).toContainText('Core switch down')
  await page.locator('[data-test="open-messages"]').getByRole('button', { name: 'Withdraw' }).click()
  await page.locator('.p-confirmdialog').getByRole('button', { name: 'Withdraw' }).click()
  await expect(screenA.locator('[data-test="board-message"]')).toBeHidden({ timeout: 6_000 })
  await expect(screenB.locator('[data-test="board-message"]')).toBeHidden({ timeout: 6_000 })

  // The other kinds in the default theme, each on its own.
  const texts: Record<string, [string, string]> = {
    Warning: ['Mail delays', 'Outbound mail is delayed by about 20 minutes. Nothing is lost.'],
    Maintenance: ['Patching tonight', 'The VPN and the file servers restart between 22:00 and 22:30.'],
    Information: ['New coffee machine', 'On the second floor, next to the printers. Enjoy.'],
    Success: ['Building B is back', 'Phones and Wi-Fi work again. Thank you for your patience.'],
  }
  for (const [kind, [title, text]] of Object.entries(texts)) {
    const sent = await send(page, [a.id], kind, title, text)
    await screenA.goto(a.url)
    await expect(screenA.locator('.board-msg-title')).toHaveText(title, { timeout: 8_000 })
    await screenA.waitForTimeout(400)
    await shot(screenA, `board-message-${kind.toLowerCase()}-default`)
    await withdraw(page, sent.id)
  }

  // An alert in the themes that frame it as their era's dialog, and in dark mode.
  const alert = await send(
    page,
    [a.id],
    'Alert',
    'Core switch down in building B',
    'Phones and Wi-Fi in building B are out.',
  )
  for (const theme of ['dark', 'win95', 'winxp', 'terminal', 'amiga']) {
    await look(page, a.id, { theme })
    await screenA.goto(a.url)
    await expect(screenA.locator(`.board[data-board-theme="${theme}"] [data-test="board-message"]`)).toBeVisible({
      timeout: 8_000,
    })
    await screenA.waitForTimeout(400)
    await shot(screenA, `board-message-alert-${theme}`)
  }
  await withdraw(page, alert.id)

  // The longest text at 720p and 4K, with the box full.
  await look(page, a.id, { theme: 'default', messageSize: 'Full' })
  const long = 'All services in building B are affected. '.repeat(24).slice(0, 1000)
  const longest = await send(
    page,
    [a.id],
    'Warning',
    'A title that uses most of the hundred characters it may have, to see it wrap',
    long,
  )
  for (const [w, h] of [
    [1280, 720],
    [3840, 2160],
  ] as const) {
    const screen = await (await browser.newContext({ viewport: { width: w, height: h } })).newPage()
    await screen.goto(a.url)
    await expect(screen.locator('[data-test="board-message"]')).toBeVisible({ timeout: 8_000 })
    await screen.waitForTimeout(600)
    await shot(screen, `board-message-longest-${w}x${h}`)
    await screen.context().close()
  }
  await withdraw(page, longest.id)

  await wall.close()
  await api(page, 'DELETE', `/api/v1/boards/${a.id}`)
  await api(page, 'DELETE', `/api/v1/boards/${b.id}`)
  expect(problems).toEqual([])
})

test("a board with a sensor's message, an animated GIF, a 12-hour clock in German and two deputies", async ({
  page,
  browser,
}) => {
  test.setTimeout(180_000)
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1600, height: 1000 })
  await signIn(page)
  const b = await board(page, 'E2E elements', 2)
  const sensors = (
    (await api(page, 'GET', '/api/v1/boards/catalog/targets?q=e')) as { kind: string; id: string }[]
  ).filter((t) => t.kind === 'Sensor')
  const element = (type: string, settings: Record<string, unknown>, height: number | null = null) => ({
    id: Math.random().toString(16).slice(2, 10).padEnd(8, '0'),
    type,
    title: null,
    showTitle: true,
    height,
    settings,
  })
  await look(page, b.id, { dateLanguage: 'German', timeFormat: 'Hours24' }, [
    {
      width: 1,
      elements: [
        element(
          'SensorMessage',
          { targets: sensors.slice(0, 1).map((s) => ({ kind: 'Sensor', id: s.id })), textSize: 'Fit' },
          1,
        ),
        element('OnDuty', { deputies: 2, where: true, tomorrow: true, dayComment: true }),
      ],
    },
    {
      width: 1,
      elements: [
        element('Clock', { seconds: true, date: true, timeFormat: 'Hours12', weekNumber: true }),
        element('Image', {}, 2),
      ],
    },
  ])

  // The image, uploaded in the editor: the preview shows it before the board is saved.
  await page.goto(`/pnmt-admin/boards/${b.id}`)
  const preview = page.locator('[data-test="board-preview"]')
  await preview.locator('[data-type="Image"]').click()
  await page
    .locator('[data-test="image-file"]')
    .setInputFiles({ name: 'stripes.gif', mimeType: 'image/gif', buffer: animatedGif() })
  await expect(preview.locator('img[data-test="board-image"]')).toBeVisible({ timeout: 10_000 })
  await page.getByLabel('Caption').fill('Stripes, animated')
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByText('saved', { exact: true })).toBeVisible()
  await shot(page, 'board-elements-editor')

  // At its URL.
  for (const theme of ['default', 'dark']) {
    await look(page, b.id, { theme })
    const screen = await (await browser.newContext({ viewport: { width: 1920, height: 1080 } })).newPage()
    await screen.goto(b.url)
    await expect(screen.locator(`.board[data-board-theme="${theme}"]`)).toBeVisible()
    await expect(screen.locator('img[data-test="board-image"]')).toBeVisible({ timeout: 10_000 })
    await expect(screen.locator('[data-type="Clock"] .period')).toHaveText(/AM|PM/)
    await expect(screen.locator('[data-type="Clock"]')).toContainText(
      /(Montag|Dienstag|Mittwoch|Donnerstag|Freitag|Samstag|Sonntag)/,
    )
    await expect(screen.locator('[data-type="Clock"]')).toContainText(/Week \d+/)
    if (sensors.length) await expect(screen.locator('[data-test="sensor-message"]')).toBeVisible()
    await expect(screen.locator('[data-type="OnDuty"]')).toBeVisible()
    await screen.waitForTimeout(500)
    await shot(screen, `board-elements-${theme}`)
    await screen.context().close()
  }
  await api(page, 'DELETE', `/api/v1/boards/${b.id}`)
  expect(problems).toEqual([])
})
