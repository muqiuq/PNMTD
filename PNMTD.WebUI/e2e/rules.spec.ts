import { expect, test } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * Notification rules to people (docs/notification-recipients-plan.md §7, §10): a rule to a user made through the form, whom it
 * reaches now, changed to an address in the dialog and deleted again; and the seed's rule to the responsible, as a viewer sees it.
 * The seed's first admin has an e-mail address and a Pushover key, so a rule to them reaches someone whatever the day.
 */
test('a rule to a user through the form, whom it reaches, changed to an address, deleted', async ({ page }) => {
  const problems = collectProblems(page)
  await signIn(page)
  await page.goto('/pnmt-admin/notification-rules/new')
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill('e2e-admin-by-mail')
  // An address is the default: the same two fields as before.
  await expect(page.getByRole('textbox', { name: 'Recipient', exact: true })).toBeVisible()

  await page.getByRole('group', { name: 'Send to' }).getByRole('button', { name: 'A user', exact: true }).click()
  await expect(page.getByRole('textbox', { name: 'Recipient', exact: true })).toHaveCount(0)
  await page.getByRole('combobox', { name: 'User' }).click()
  await page.getByRole('searchbox').fill('admin')
  await page.getByRole('option', { name: 'admin', exact: true }).click()
  await page.getByRole('checkbox', { name: 'Pushover' }).uncheck()
  await shot(page, 'rule-form-to-a-user')
  await page.getByRole('button', { name: 'Create rule' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'e2e-admin-by-mail' })).toBeVisible()
  // How it tells them, and whom it reaches now: the admin, by e-mail alone.
  await expect(page.getByText('by e-mail', { exact: true })).toBeVisible()
  await expect(page.getByText('admin: e-mail', { exact: true })).toBeVisible()
  await expect(page.getByText('never', { exact: true })).toBeVisible()
  await shot(page, 'rule-to-a-user')

  // Changed to an address in the dialog: the same block, then the provider it matched.
  await page.getByRole('button', { name: 'Change whom the rule tells' }).click()
  const dialog = page.getByRole('dialog', { name: 'Whom the rule tells' })
  await dialog.getByRole('group', { name: 'Send to' }).getByRole('button', { name: 'An address', exact: true }).click()
  await dialog.getByRole('textbox', { name: 'Recipient', exact: true }).fill('ops@example.com')
  await dialog.getByRole('button', { name: 'Save' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByText('ops@example.com', { exact: true })).toBeVisible()
  await expect(page.getByText('1 message by E-mail', { exact: true })).toBeVisible()
  await expect(page.getByText('admin: e-mail', { exact: true })).toHaveCount(0)

  await page.getByRole('button', { name: 'More actions for the rule' }).click()
  await page.getByRole('menuitem', { name: 'Delete rule…' }).click()
  await page.getByRole('button', { name: 'Delete', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Notification rules' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'e2e-admin-by-mail' })).toHaveCount(0)
  expect(problems).toEqual([])
})

test("the seed's rule to the responsible, as a viewer sees it", async ({ page }) => {
  const problems = collectProblems(page)
  await signIn(page, 'viewer')
  await page.goto('/pnmt-admin/notification-rules')
  await expect(page.getByRole('link', { name: 'Admin by Pushover' })).toBeVisible()
  await page.getByRole('link', { name: 'Storage on duty' }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Storage on duty' })).toBeVisible()
  await expect(page.getByText('The responsible of the day', { exact: true })).toBeVisible()
  await expect(page.getByText(/^at any hour · /)).toBeVisible()
  // Whom it reaches depends on the day; that the page says is what matters here. A viewer changes nothing.
  await expect(page.getByText('Reaches now', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Change whom the rule tells' })).toHaveCount(0)
  await shot(page, 'rule-to-the-responsible')
  expect(problems).toEqual([])
})
