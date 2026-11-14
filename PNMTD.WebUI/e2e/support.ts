import { createHmac } from 'node:crypto'
import { deflateSync } from 'node:zlib'
import type { Locator, Page } from '@playwright/test'

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

/**
 * Script errors, CSP violations and Vue's warnings while a test runs (as pages.spec.ts collects them): call it first, and
 * expect the list to be empty at the end.
 */
export function collectProblems(page: Page): string[] {
  const problems: string[] = []
  page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`))
  page.on('console', (m) => {
    if (m.text().includes('Content Security Policy') || (m.type() === 'error' && m.text().includes('[Vue warn]'))) {
      problems.push(m.text().slice(0, 300))
    }
  })
  return problems
}

/**
 * Pastes a file into an element as Ctrl/⌘ V does after copying one: a ClipboardEvent carrying a DataTransfer with the file
 * (docs/locations-knowledge-plan.md §12).
 */
export async function pasteFile(target: Locator, name: string, type: string, bytes: Buffer) {
  await target.evaluate(
    (element, file) => {
      const data = Uint8Array.from(atob(file.data), (c) => c.charCodeAt(0))
      const transfer = new DataTransfer()
      transfer.items.add(new File([data], file.name, { type: file.type }))
      element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true, cancelable: true }))
    },
    { name, type, data: bytes.toString('base64') },
  )
}

/** A PNG of one colour, written here: an image the server takes (PNG by its bytes, its size from the header). */
export function png(width: number, height: number, [r, g, b]: [number, number, number]): Buffer {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    return c >>> 0
  })
  const crc = (bytes: Buffer) => {
    let c = 0xffffffff
    for (const byte of bytes) c = crcTable[(c ^ byte) & 0xff]! ^ (c >>> 8)
    return (c ^ 0xffffffff) >>> 0
  }
  const chunk = (type: string, data: Buffer) => {
    const typed = Buffer.concat([Buffer.from(type, 'ascii'), data])
    const length = Buffer.alloc(4)
    length.writeUInt32BE(data.length)
    const sum = Buffer.alloc(4)
    sum.writeUInt32BE(crc(typed))
    return Buffer.concat([length, typed, sum])
  }
  const header = Buffer.alloc(13)
  header.writeUInt32BE(width, 0)
  header.writeUInt32BE(height, 4)
  header[8] = 8 // bit depth
  header[9] = 2 // RGB
  const row = Buffer.concat([Buffer.from([0]), Buffer.from(Array.from({ length: width }, () => [r, g, b]).flat())])
  const pixels = deflateSync(Buffer.concat(Array.from({ length: height }, () => row)))
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', pixels),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

/** A one-page PDF, enough for an attachment: PNMT stores any file that isn't an executable as it is. */
export function pdf(text: string): Buffer {
  const stream = `BT /F1 14 Tf 72 770 Td (${text.replace(/[()\\]/g, '')}) Tj ET`
  return Buffer.from(
    `%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n` +
      `3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >> endobj\n` +
      `4 0 obj << /Length ${stream.length} >> stream\n${stream}\nendstream endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n`,
    'ascii',
  )
}
