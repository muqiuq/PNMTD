import { readFile } from 'node:fs/promises'
import { expect, test } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * 3rd Party Connectors (docs/connectors-plan.md §13, §16) on the seed: the area opens on Calls; a Linux connector added on web-01 with
 * its script taken from the library; an argument exposed with a pattern; a caller with its networks, its key shown; a call through
 * /connector/v1, wrong first (422), then right (202: the seed's agents aren't connected, so it waits for the server with its ticket);
 * both on Calls; the documentation downloaded as one .md file. Only what the browser can break: the server side has its tests in
 * PNMTD.Tests.
 */
test('a connector from the library, a caller with its key, and its calls', async ({ page }) => {
  const problems = collectProblems(page)
  // A render error in a production build is only a console error: this page's must not have one.
  page.on('console', (m) => {
    if (m.type() === 'error' && m.text().includes('TypeError'))
      problems.push(`${page.url()}: ${m.text().slice(0, 300)}`)
  })
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)

  // The area opens on Calls, with the seed's calls of every kind.
  await page.goto('/pnmt-admin/connectors')
  await expect(page.getByRole('heading', { level: 1, name: '3rd Party Connectors' })).toBeVisible()
  await expect(page.getByText(/running$/).first()).toBeVisible()
  await expect(page.getByRole('link', { name: 'Shop: flush cache' }).first()).toBeVisible()
  await shot(page, 'connectors-calls')

  // Add connector…: Linux, web-01, the library's rotate-logs.
  const name = `E2E rotate ${Date.now()}`
  await page.getByRole('link', { name: 'Connectors', exact: true }).click()
  await page.getByRole('button', { name: 'Add connector…' }).click()
  await page.getByRole('button', { name: /Linux Server Script/ }).click()
  await page.getByLabel('Server', { exact: true }).fill('web-01 / Linux')
  await page.getByRole('button', { name: 'From the library…' }).click()
  await page.getByRole('combobox', { name: 'Script' }).click()
  await page.getByRole('option', { name: 'rotate-logs' }).click()
  await expect(page.getByText('Rotating ${PNMT_SITE}')).toBeVisible()
  await page.getByRole('button', { name: 'Take it' }).click()
  await expect(page.getByText('Taken from library: rotate-logs')).toBeVisible()
  await shot(page, 'connectors-add-target')
  await page.getByRole('button', { name: 'Next' }).click()
  await page.getByLabel('Name', { exact: true }).fill(name)
  await page.getByLabel('Purpose', { exact: true }).fill("The web team's tool rotates a site's logs after a release.")
  await page.getByRole('button', { name: 'Add, disabled' }).click()

  // Arguments: site is mandatory, so exposed; a pattern with its tester.
  await expect(page).toHaveURL(/\/connectors\/connector\/[0-9a-f-]+\/arguments$/)
  await page.getByLabel('Pattern').first().fill('[a-z]{3,4}')
  await page.getByLabel('Try values, one per line').first().fill('shop\nshop; rm -rf /')
  await expect(page.getByText('✓ shop')).toBeVisible()
  await expect(page.getByText('✗ shop; rm -rf /')).toBeVisible()
  await shot(page, 'connectors-arguments')
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByText('Saved').first()).toBeVisible()

  // Overview: ticket mode, enabled.
  await page.getByRole('link', { name: 'Overview' }).click()
  await page.getByRole('button', { name: 'A ticket at once' }).click()
  await page.getByLabel('Enabled', { exact: true }).click()
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByText(/The name finds/)).toBeVisible()
  await shot(page, 'connectors-overview')

  // A caller with its networks and the connector granted; its key, shown.
  await page.goto('/pnmt-admin/connectors/callers')
  await page.getByRole('button', { name: 'Add caller…' }).click()
  await page.getByLabel('Name', { exact: true }).fill(`${name} caller`)
  await page.getByLabel('Network 1', { exact: true }).fill('0.0.0.0/0')
  await page.getByRole('button', { name: 'Add a network' }).click()
  await page.getByLabel('Network 2', { exact: true }).fill('::/0')
  await expect(page.getByText('Any address: the key alone lets a call in.')).toBeVisible()
  await page.getByRole('dialog').getByText('None yet').click()
  await page.getByRole('option', { name }).click()
  await page.keyboard.press('Escape')
  await page.getByRole('button', { name: 'Add, disabled' }).click()
  await expect(page).toHaveURL(/\/connectors\/caller\//)
  await page.getByLabel('Enabled', { exact: true }).click()
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByText('Saved').first()).toBeVisible()
  await page.getByRole('button', { name: 'Show', exact: true }).click()
  const key = (await page.getByLabel('The key', { exact: true }).textContent())!.trim()
  expect(key).toMatch(/^pnmt_cx_[A-Za-z0-9_-]{43}$/)
  await shot(page, 'connectors-caller')

  // Calls through /connector/v1: the arguments wrong first, then right.
  // The slug as the form suggested it from the name.
  const connectorSlug = name.toLowerCase().replace(/[^a-z0-9]+/g, '-')
  const headers = { Authorization: `Bearer ${key}` }
  const wrong = await page.request.post(`/connector/v1/${connectorSlug}`, {
    headers,
    data: { arguments: { site: 'shop; rm -rf /' } },
  })
  expect(wrong.status()).toBe(422)
  const refused = await wrong.json()
  expect(refused.fields.site).toBe("doesn't match the pattern [a-z]{3,4}")
  const right = await page.request.post(`/connector/v1/${connectorSlug}`, {
    headers,
    data: { arguments: { site: 'shop' }, reference: 'E2E-1' },
  })
  expect(right.status()).toBe(202)
  const ticket = await right.json()
  expect(ticket.state).toBe('queued')

  // Both on Calls: the queued one among the active ones, the refused one below.
  await page.goto('/pnmt-admin/connectors')
  await expect(page.getByRole('row', { name: /E2E-1/ })).toBeVisible()
  await expect(
    page.getByRole('row', { name: new RegExp(`refused · arguments.*${name.slice(0, 10)}`) }).first(),
  ).toBeVisible()
  await shot(page, 'connectors-calls-after')

  // The documentation, to hand on: the whole API as one .md file, its title included.
  await page.getByRole('link', { name: 'Documentation', exact: true }).click()
  const downloading = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Download as Markdown' }).click()
  const download = await downloading
  expect(download.suggestedFilename()).toBe('pnmt-connectors-api.md')
  const markdown = await readFile(await download.path(), 'utf8')
  expect(markdown.startsWith('# PNMT 3rd Party Connectors: the API for callers\n')).toBe(true)
  for (const heading of ['## A call', '## Wait or ticket', '## Errors', '## Examples']) {
    expect(markdown).toContain(`\n${heading}\n`)
  }
  expect(problems).toEqual([])
})
