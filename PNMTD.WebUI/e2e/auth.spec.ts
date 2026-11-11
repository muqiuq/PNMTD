import { expect, test } from '@playwright/test'
import { credentials, freshCode, shot, typeCode } from './support'

/**
 * Phase 5's acceptance (docs/webui-migration-plan.md §7): a new user with a temporary password signs
 * in, chooses a password, sets up 2FA from the prompt, changes the password again, signs out, and
 * signs in with the password and a code.
 */
test.describe.configure({ mode: 'serial' })

// Whatever the CSP blocks (a data: font, an inline script) is a bug of the build: no test may cause one.
let violations: string[] = []
test.beforeEach(({ page }) => {
  violations = []
  page.on('console', (message) => {
    if (message.text().includes('Content Security Policy')) {
      violations.push(message.text().slice(0, 200))
    }
  })
})
test.afterEach(() => expect(violations).toEqual([]))

const { user, password: temporary } = credentials()
const first = `e2e-first-${Date.now()}`
const second = `e2e-second-${Date.now()}`
let secret = ''

test('a new user chooses a password and sets up 2FA from the prompt', async ({ page }) => {
  await page.goto('/pnmt-admin/hosts')
  await expect(page).toHaveURL(/\/pnmt-admin\/login\?returnUrl=(%2F|\/)hosts$/)
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  await shot(page, '01-login')

  await page.getByLabel('Username').fill(user)
  await page.getByLabel('Password', { exact: true }).fill('not-the-password')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('alert')).toHaveText('Wrong username or password.')

  await page.getByLabel('Password', { exact: true }).fill(temporary)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).toHaveURL(/\/change-password/)
  await shot(page, '02-change-password')
  await page.getByLabel('Password you signed in with').fill(temporary)
  await page.getByLabel('New password', { exact: true }).fill(first)
  await page.getByLabel('New password again').fill(first)
  await page.getByRole('button', { name: 'Save and continue' }).click()

  await expect(page.getByRole('heading', { name: 'Protect your account with two-factor authentication' })).toBeVisible()
  await shot(page, '03-totp-prompt')
  await page.getByRole('button', { name: 'Set up now' }).click()
  const key = page.getByLabel('Setup key', { exact: true })
  await expect(key).toBeVisible()
  await expect(page.getByAltText('QR code for the authenticator app')).toBeVisible()
  secret = (await key.textContent())!.replace(/\s/g, '')
  expect(secret).toMatch(/^[A-Z2-7]{32}$/)
  await shot(page, '04-totp-setup')

  await typeCode(page, await freshCode(secret))
  await expect(page).toHaveURL(/\/pnmt-admin\/hosts$/)
  await expect(page.getByText('Two-factor authentication is on', { exact: true })).toBeVisible()
})

test('the password changes on the account page, and sign-in then asks for a code', async ({ page }) => {
  await page.goto('/pnmt-admin/login')
  await page.getByLabel('Username').fill(user)
  await page.getByLabel('Password', { exact: true }).fill(first)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).toHaveURL(/\/login\/2fa/)
  await shot(page, '05-login-2fa')
  // The setup used this step's code: the next one counts.
  await typeCode(page, await freshCode(secret))
  await expect(page).toHaveURL(/\/pnmt-admin\/$/)
  await shot(page, '06-signed-in')

  await page.getByRole('button', { name: /Account menu/ }).click()
  await page.getByRole('menuitem', { name: 'Account' }).click()
  await expect(page.getByRole('heading', { name: 'Account' })).toBeVisible()
  await page.getByLabel('Current password').fill(first)
  await page.getByLabel('New password', { exact: true }).fill(second)
  await page.getByLabel('New password again').fill(second)
  await page.getByRole('button', { name: 'Change password' }).click()
  await expect(page.getByText('Password changed', { exact: true })).toBeVisible()
  await shot(page, '07-account')

  await page.getByRole('button', { name: /Account menu/ }).click()
  await page.getByRole('menuitem', { name: 'Security' }).click()
  await expect(page.getByText('Two-factor authentication').first()).toBeVisible()
  await shot(page, '08-security')

  await page.getByRole('button', { name: /Account menu/ }).click()
  await page.getByRole('menuitem', { name: 'Sign out' }).click()
  await expect(page).toHaveURL(/\/login$/)

  await page.goto('/pnmt-admin/')
  await expect(page).toHaveURL(/\/login/)
  await page.getByLabel('Username').fill(user)
  await page.getByLabel('Password', { exact: true }).fill(first)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('alert')).toHaveText('Wrong username or password.')
  await page.getByLabel('Password', { exact: true }).fill(second)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).toHaveURL(/\/login\/2fa/)
  await typeCode(page, '000000')
  await expect(page.getByRole('alert')).toContainText('Wrong code')
})

test('the shell works on a phone and in the dark theme', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/pnmt-admin/login')
  await shot(page, '09-login-phone')
  await page.getByLabel('Username').fill(user)
  await page.getByLabel('Password', { exact: true }).fill(second)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).toHaveURL(/\/login\/2fa/)
  await typeCode(page, await freshCode(secret))
  await expect(page).toHaveURL(/\/pnmt-admin\/$/)
  await expect(page.locator('aside.drawer')).toBeHidden()
  await page.getByRole('button', { name: 'Open navigation' }).click()
  await expect(page.locator('aside.drawer')).toBeVisible()
  await shot(page, '10-drawer-phone')
  await page.getByRole('link', { name: 'Hosts' }).click()
  await expect(page.locator('aside.drawer')).toBeHidden()

  await page.setViewportSize({ width: 1440, height: 900 })
  await page.getByRole('button', { name: 'Dark theme' }).click()
  await expect(page.locator('html')).toHaveClass(/pnmt-dark/)
  await page.goto('/pnmt-admin/account/security')
  await shot(page, '11-security-dark')
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth > document.documentElement.clientWidth,
  )
  expect(overflow).toBe(false)
})
