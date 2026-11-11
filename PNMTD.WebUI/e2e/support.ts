import { createHmac } from 'node:crypto'
import type { Page } from '@playwright/test'

const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'

/** RFC 6238 (SHA1, 6 digits, 30 s) for one time step, as the authenticator app computes it. */
export function codeAt(secret: string, step: number): string {
  const clean = secret.replace(/[\s=]/g, '').toUpperCase()
  let bits = ''
  for (const c of clean) {
    bits += alphabet.indexOf(c).toString(2).padStart(5, '0')
  }
  const key = Buffer.from(bits.match(/.{8}/g)!.map((b) => parseInt(b, 2)))
  const counter = Buffer.alloc(8)
  counter.writeBigUInt64BE(BigInt(step))
  const hash = createHmac('sha1', key).update(counter).digest()
  const start = hash[hash.length - 1]! & 0x0f
  const binary = hash.readUInt32BE(start) & 0x7fffffff
  return String(binary % 1_000_000).padStart(6, '0')
}

let lastStep = 0

/**
 * A code the server takes: within its one step of drift, and later than the last one used (each
 * code counts once). Waits for the next 30-second step when the window is used up.
 */
export async function freshCode(secret: string): Promise<string> {
  for (;;) {
    const now = Math.floor(Date.now() / 30_000)
    const step = Math.max(lastStep + 1, now)
    if (step <= now + 1) {
      lastStep = step
      return codeAt(secret, step)
    }
    await new Promise((resolve) => setTimeout(resolve, 1_000))
  }
}

/** Screenshots for review go to test-results/screens (not a test failure artefact). */
export async function shot(page: Page, name: string) {
  await page.screenshot({ path: `test-results/screens/${name}.png`, fullPage: false })
}

/**
 * Types a code into PrimeVue's InputOtp (one box per digit), as a person does after the page focused
 * the first box. Not after a click: InputOtp selects the clicked box again 1 ms later, which pulls
 * the focus back from the second box when the first digit follows the click at machine speed.
 */
export async function typeCode(page: Page, code: string) {
  await page.locator('.p-inputotp input').first().focus()
  await page.keyboard.type(code, { delay: 30 })
}

export function credentials() {
  const user = process.env.E2E_USER
  const password = process.env.E2E_PASSWORD
  if (!user || !password) {
    throw new Error('E2E_USER and E2E_PASSWORD (a user with a temporary password): tools/e2e.sh sets them.')
  }
  return { user, password }
}

/** Signs in as e2e-admin or e2e-viewer (no 2FA, own password) and skips the 2FA prompt. */
export async function signIn(page: Page, who: 'admin' | 'viewer' = 'admin') {
  const password = who === 'admin' ? process.env.E2E_ADMIN_PASSWORD : process.env.E2E_VIEWER_PASSWORD
  if (!password) {
    throw new Error('E2E_ADMIN_PASSWORD / E2E_VIEWER_PASSWORD: tools/e2e.sh sets them.')
  }
  await page.goto('/pnmt-admin/login')
  await page.getByLabel('Username').fill(`e2e-${who}`)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await page.getByRole('button', { name: 'Later' }).click()
  await page.waitForURL(/\/pnmt-admin\/$/)
}
