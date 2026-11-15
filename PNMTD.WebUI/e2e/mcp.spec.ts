import { expect, test, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * MCP (docs/mcp-plan.md §4.5, §10) on a stack with Mcp__Enabled=true: the dashboard's card switches the read-only token on
 * and shows its value once; Admin → MCP documents the tools from the catalog and lists the token; an admin turns it off
 * with a reason, which the card then shows. e2e-admin has no 2FA, so the read & write token stays off: the .NET tests
 * cover it, and the protocol itself through the SDK's own client.
 */
async function json(page: Page, path: string) {
  const response = await page.request.get(path)
  expect(response.ok(), `GET ${path}: ${response.status()}`).toBeTruthy()
  return response.json()
}

test('The MCP card shows a token once; Admin → MCP documents the tools and turns the token off', async ({ page }) => {
  const problems = collectProblems(page)
  await page.setViewportSize({ width: 1440, height: 900 })
  await signIn(page)
  const account = await json(page, '/api/v1/account/mcp')
  test.skip(!account.enabled, 'Mcp__Enabled is not set for this stack')

  const card = page.getByRole('region', { name: 'MCP access' })
  await expect(card).toBeVisible()
  await expect(card).toContainText('needs 2FA')
  if (account.read.on) {
    await card.getByRole('button', { name: 'Turn off' }).first().click()
    await expect(card.getByRole('button', { name: 'Turn on', exact: true })).toBeVisible()
  }
  await card.getByRole('button', { name: 'Turn on', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Your new token' })
  if (await dialog.isVisible().catch(() => false)) {
    await expect(dialog).toContainText('pnmt_ro_')
    await expect(dialog).toContainText('claude mcp add')
    await shot(page, 'mcp-token-once')
    await dialog.getByRole('button', { name: "I've stored it" }).click()
  }
  await expect(card).toContainText('On')
  await shot(page, 'mcp-card')

  // The documentation, from the catalog: the rules and every tool.
  await page.goto('/pnmt-admin/admin/mcp')
  await expect(page.getByRole('heading', { level: 1, name: 'MCP' })).toBeVisible()
  await expect(page.locator('code.name', { hasText: /^sensor_delete$/ })).toBeVisible()
  await expect(page.getByText('Read & write, at most')).toBeVisible()
  await shot(page, 'mcp-docs')

  // Everyone's tokens: turn this one off, with a reason the user reads.
  await page.goto('/pnmt-admin/admin/mcp/tokens')
  const row = page.getByRole('row').filter({ hasText: 'e2e-admin' }).filter({ hasText: 'Read-only' })
  await row.getByRole('button', { name: 'Turn off…' }).click()
  const off = page.getByRole('dialog', { name: 'Turn this token off?' })
  await off.getByPlaceholder('Why').fill('End of the e2e test')
  await off.getByRole('button', { name: 'Turn off' }).click()
  await expect(row).toContainText('End of the e2e test')
  await shot(page, 'mcp-tokens')

  await page.goto('/pnmt-admin/')
  await expect(card).toContainText('Turned off by an admin')
  await expect(card).toContainText('End of the e2e test')
  expect(problems).toEqual([])
})
