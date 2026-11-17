import { createHash } from 'node:crypto'
import { expect, test, type Locator, type Page } from '@playwright/test'
import { collectProblems, shot, signIn } from './support'

/**
 * SSL certificate sensors (docs/ssl-certificate-plan.md §10) against the fakes' TLS port, which picks a certificate by SNI:
 * the fake private CA added on Admin → Certificates; a private-mode sensor for valid made in the form, OK (285) and trusted
 * by that CA; soon about to expire (485); noint, without its intermediate, invalid (488) with the hint; a pinned
 * self-signed certificate OK, rotated, *Check now* failing it (487) with the banner, and accepted in the warning window,
 * whose button waits for the box; and what a Viewer, without Certificates' rights, sees. Skipped without the fakes.
 */
const fakes = process.env.E2E_FAKES_URL
const tlsHost = process.env.E2E_TLS_HOST
const tlsPort = process.env.E2E_TLS_PORT
test.skip(!fakes || !tlsHost || !tlsPort, "The fakes' TLS port is not part of this stack (E2E_FAKES_URL, E2E_TLS_*)")
test.describe.configure({ mode: 'serial' })

let problems: string[] = []
test.beforeEach(({ page }) => {
  problems = collectProblems(page)
})
test.afterEach(() => expect(problems).toEqual([]))

async function api(page: Page, method: 'GET' | 'POST' | 'PUT' | 'DELETE', url: string, data?: unknown) {
  const xsrf = (await page.context().cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value ?? ''
  const response = await page.request.fetch(url, {
    method,
    data,
    headers: { 'X-XSRF-TOKEN': decodeURIComponent(xsrf) },
  })
  expect(response.ok(), `${method} ${url}: ${response.status()} ${await response.text()}`).toBe(true)
  return response.status() === 204 ? null : response.json()
}

const address = (name: string) => `${name}:${tlsPort}`
const run = Date.now().toString(36)
let hostId = ''
let authorityId = ''
let pinnedId = ''

/** The sensor's code once its first check is done (within 15 seconds of its creation). */
async function settled(page: Page, id: string, code: number) {
  await expect
    .poll(async () => (await api(page, 'GET', `/api/v1/sensors/${id}`)).lastCode, {
      timeout: 60_000,
      intervals: [1_000],
    })
    .toBe(code)
}

async function create(page: Page, name: string, certificate: Record<string, unknown>) {
  return api(page, 'POST', '/api/v1/sensors', {
    hostId,
    type: 'SSL_CERTIFICATE',
    name: address(name),
    enabled: true,
    ignore: false,
    interval: 0,
    gracePeriod: 0,
    parameters: address(name),
    certificate: { connectTo: tlsHost, ...certificate },
  })
}

const pages = (page: Page) => page.getByRole('navigation', { name: 'Sensor pages' })

/**
 * Clicks a control of the long form in the middle of the window: choosing an option can scroll the page, and Playwright's
 * own scrolling may leave the control under the sticky app bar, which then takes the click.
 */
async function clickCentred(control: Locator) {
  await control.evaluate((e) => e.scrollIntoView({ block: 'center' }))
  await control.click()
}

test('a private CA, and a private-mode sensor for it made in the form', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  // A disabled host: its sensors are checked all the same (Enabled is the sensor's), but their failures stay off the
  // dashboard, whose spec counts the seed's six problems.
  hostId = (await api(page, 'POST', '/api/v1/hosts', { name: `e2e-tls-${run}`, enabled: false })).id

  // Admin → Certificates: the fake CA's root, read before it is saved.
  const pem = await (await page.request.get(`${fakes}/tls/private-ca.pem`)).text()
  const der = Buffer.from(pem.replace(/-----(BEGIN|END) CERTIFICATE-----|\s/g, ''), 'base64')
  const sha = createHash('sha256').update(der).digest('hex').toUpperCase()
  const name = `e2e fake CA ${run}`
  await page.goto('/pnmt-admin/admin/certificates')
  await expect(page.getByRole('heading', { level: 1, name: 'Certificates' })).toBeVisible()
  const known = (await api(page, 'GET', '/api/v1/certificate-authorities')).find(
    (a: { rootSha256: string }) => a.rootSha256 === sha,
  )
  if (known) {
    authorityId = known.id
  } else {
    await page.getByRole('button', { name: 'Add a private CA…' }).click()
    const dialog = page.getByRole('dialog', { name: 'Add a private CA' })
    await dialog.getByRole('textbox', { name: /The root certificate/ }).fill(pem)
    await expect(dialog.getByRole('list', { name: 'What the PEM holds' })).toContainText('root')
    await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill(name)
    await expect(dialog.getByText('Every sensor that trusts this CA accepts any certificate it signs.')).toBeVisible()
    await shot(page, 'certificate-add-ca')
    await dialog.getByRole('button', { name: 'Add', exact: true }).click()
    await expect(page.getByRole('table', { name: 'Private CAs' })).toContainText(name)
    authorityId = (await api(page, 'GET', '/api/v1/certificate-authorities')).find(
      (a: { name: string }) => a.name === name,
    ).id
  }
  expect(authorityId).toBeTruthy()

  // Add sensor: the address names the sensor, the private mode picks the CA, Advanced… connects to the fakes.
  await page.goto(`/pnmt-admin/hosts/${hostId}/sensors/new?type=SSL_CERTIFICATE`)
  await page.getByRole('textbox', { name: 'Address' }).fill(address('valid.pnmt.test'))
  await expect(page.getByRole('textbox', { name: 'Name', exact: true })).toHaveValue(address('valid.pnmt.test'))
  await clickCentred(page.getByRole('combobox', { name: 'Trust' }))
  await page.getByRole('option', { name: 'A private CA' }).click()
  // PrimeVue's MultiSelect: the combobox is a visually hidden input; its box is what a person clicks.
  await clickCentred(page.locator('.p-multiselect', { has: page.getByRole('combobox', { name: 'Private CAs' }) }))
  await page.getByRole('option', { name: known?.name ?? name }).click()
  await page.keyboard.press('Escape')
  await clickCentred(page.getByRole('button', { name: 'Advanced…' }))
  await page.getByRole('textbox', { name: /Connect to/ }).fill(tlsHost!)
  await shot(page, 'certificate-form')
  await page.getByRole('button', { name: 'Create sensor' }).click()
  await expect(page.getByRole('heading', { level: 1, name: address('valid.pnmt.test') })).toBeVisible()
  const id = /\/sensors\/([0-9a-f-]{36})/.exec(page.url())![1]!

  await settled(page, id, 285)
  await page.reload()
  await expect(page.getByRole('region', { name: 'Certificate status' })).toContainText('days left')
  await expect(page.getByText('285', { exact: true }).first()).toBeVisible()
  await pages(page).getByRole('link', { name: 'Certificate' }).click()
  await expect(page.getByText(/^Valid · \d+ days left$/)).toBeVisible()
  await expect(page.getByText(`Trusted by the private CA ${known?.name ?? name}`)).toBeVisible()
  await expect(page.getByText('sent by the server').first()).toBeVisible()
  await shot(page, 'certificate-private')
})

test('a certificate about to expire, and an incomplete chain in the private mode', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  const soon = await create(page, 'soon.pnmt.test', { trust: 'Private', authorityIds: [authorityId] })
  const noint = await create(page, 'noint.pnmt.test', { trust: 'Private', authorityIds: [authorityId] })

  await settled(page, soon.id, 485)
  await page.goto(`/pnmt-admin/sensors/${soon.id}/certificate`)
  await expect(page.getByText('485', { exact: true }).first()).toBeVisible()
  await expect(page.getByText(/^Expires in \d days?$/)).toBeVisible()
  await shot(page, 'certificate-soon')

  await settled(page, noint.id, 488)
  await page.goto(`/pnmt-admin/sensors/${noint.id}/certificate`)
  await expect(page.getByText('488', { exact: true }).first()).toBeVisible()
  await expect(pages(page).getByRole('link', { name: /Certificate/ })).toContainText('!')
  await expect(page.getByText(/Not trusted: .*intermediate/)).toBeVisible()
  await expect(page.getByText("Incomplete chain: the server doesn't send its intermediate.")).toBeVisible()
  await shot(page, 'certificate-noint')
})

test('a pinned self-signed certificate: rotated, failing, and accepted in the warning window', async ({ page }) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 1000 })
  await signIn(page)
  const pinned = await create(page, 'selfsigned.pnmt.test', { trust: 'Pinned' })
  pinnedId = pinned.id
  await settled(page, pinned.id, 285)

  // A new certificate, with a new key: the next check fails the sensor until the change is accepted.
  const rotated = await page.request.post(`${fakes}/tls/rotate?name=selfsigned.pnmt.test`)
  expect(rotated.ok()).toBe(true)
  await page.goto(`/pnmt-admin/sensors/${pinned.id}`)
  const checked = page.waitForResponse((r) => r.url().endsWith('/certificate/check') && r.request().method() === 'POST')
  await page.getByRole('button', { name: 'Check now' }).click()
  expect((await checked).ok()).toBe(true)
  await expect(page.getByText('487', { exact: true }).first()).toBeVisible()
  await expect(page.getByText(/The certificate changed unexpectedly on .*new certificate/)).toBeVisible()
  await shot(page, 'certificate-changed')

  // The window: the warning, the comparison, a box to tick before Accept works.
  await page.getByRole('button', { name: 'Review and accept…' }).click()
  const dialog = page.getByRole('dialog', { name: 'Accept the new certificate?' })
  await expect(dialog).toContainText('Only accept this if you know why the certificate changed')
  await expect(dialog.getByRole('table', { name: 'What changed' })).toContainText('SHA-256')
  await expect(dialog.getByLabel('Command', { exact: true })).toContainText(`-servername selfsigned.pnmt.test`)
  const accept = dialog.getByRole('button', { name: 'Accept the new certificate' })
  await expect(accept).toBeDisabled()
  await dialog.getByLabel('I have checked this change and confirm that the new certificate is expected.').check()
  await expect(accept).toBeEnabled()
  await dialog.getByRole('textbox', { name: /A note for the audit log/ }).fill('e2e: rotated on purpose')
  await shot(page, 'certificate-accept')
  await accept.click()
  await expect(page.getByText('New certificate accepted')).toBeVisible()
  await expect(dialog).toBeHidden()
  await expect(page.getByText(/The certificate changed unexpectedly on/)).toHaveCount(0)
  await settled(page, pinned.id, 285)

  // Accepted by this admin, with the note, on the tab and in the sensor's history.
  await pages(page).getByRole('link', { name: 'Certificate' }).click()
  await expect(page.locator('p.line', { hasText: 'Pinned since' })).toContainText(/ by \S+; self-signed/)
  await expect(page.getByText('“e2e: rotated on purpose”')).toBeVisible()
  await pages(page).getByRole('link', { name: 'History' }).click()
  await expect(page.locator('main')).toContainText('rotated on purpose')
})

test('a viewer sees the sensor and its state, but neither the Certificate tab nor Check now', async ({ page }) => {
  await signIn(page, 'viewer')
  await page.goto(`/pnmt-admin/sensors/${pinnedId}`)
  await expect(page.getByRole('region', { name: 'Certificate status' })).toBeVisible()
  await expect(pages(page).getByRole('link', { name: 'Certificate' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Check now' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Edit Trust' })).toHaveCount(0)
})
