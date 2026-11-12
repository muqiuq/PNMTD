import { createHash, createHmac, randomBytes, timingSafeEqual } from 'node:crypto'
import { createSocket } from 'node:dgram'
import { readFile } from 'node:fs/promises'
import { expect, test, type Page } from '@playwright/test'
import { shot, signIn } from './support'

/**
 * UDP nodes (docs/udp-iot-plan.md §8, U6) in the browser, with a real device: the test sends
 * PNMT-UDP datagrams (HMAC-SHA256/32) to the stack's listener and checks every page on the way —
 * the node's events, last packets, a command and its log, the protocol page, the admin page, a
 * firmware upload and an update from offer to applied.
 */
const base = new URL(process.env.E2E_BASE_URL ?? 'http://localhost:7398')
const udpHost = base.hostname
const udpPort = Number(process.env.E2E_UDP_PORT ?? (udpHost === 'localhost' ? 7397 : 5028))

let problems: string[] = []
test.beforeEach(({ page }) => {
  problems = []
  page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`))
  page.on('console', (m) => {
    if (m.text().includes('Content Security Policy') || (m.type() === 'error' && m.text().includes('[Vue warn]'))) {
      problems.push(m.text().slice(0, 300))
    }
  })
})
test.afterEach(() => expect(problems).toEqual([]))

// --- a device: HMAC-SHA256/32, an id of 1 to 4 bytes (docs/udp-protocol.md §3.2, §3.6) -----------

const T = { heartbeat: 0x0, data: 0x1, cmdResult: 0x3, boot: 0x4, ota: 0x6 }

class Device {
  counter = 1
  constructor(
    readonly id: number,
    readonly key: Buffer,
  ) {}

  /** The id's bytes, big-endian, as few as it fits in (a random id usually takes two). */
  get idBytes(): Buffer {
    const length = this.id < 0x100 ? 1 : this.id < 0x10000 ? 2 : this.id < 0x1000000 ? 3 : 4
    const bytes = Buffer.alloc(4)
    bytes.writeUInt32BE(this.id)
    return bytes.subarray(4 - length)
  }

  header(type: number, fcnt: number, ack: boolean): Buffer {
    const id = this.idBytes
    return Buffer.concat([
      Buffer.from([0x40 | (type << 2) | (id.length - 1), (ack ? 0x80 : 0) | 0x02]),
      id,
      Buffer.from([(fcnt >> 8) & 0xff, fcnt & 0xff]),
    ])
  }

  tag(fcnt: number, signed: Buffer): Buffer {
    const prefix = Buffer.alloc(4)
    prefix.writeUInt32BE(fcnt)
    return createHmac('sha256', this.key).update(prefix).update(signed).digest().subarray(0, 4)
  }

  build(type: number, payload: Buffer = Buffer.alloc(0), ack = false): { bytes: Buffer; fcnt: number } {
    const fcnt = this.counter++
    const signed = Buffer.concat([this.header(type, fcnt, ack), payload])
    return { bytes: Buffer.concat([signed, this.tag(fcnt, signed)]), fcnt }
  }

  /** The answer to the uplink with `fcnt`, verified (§3.15); null when none comes. */
  async exchange(uplink: {
    bytes: Buffer
    fcnt: number
  }): Promise<{ type: number; more: boolean; payload: Buffer } | null> {
    const socket = createSocket('udp4')
    try {
      const answer = await new Promise<Buffer | null>((resolve) => {
        const timer = setTimeout(() => resolve(null), 3000)
        socket.on('message', (message) => {
          clearTimeout(timer)
          resolve(message)
        })
        socket.send(uplink.bytes, udpPort, udpHost)
      })
      if (!answer) return null
      const type = (answer[0]! >> 2) & 0xf
      expect(type & 0x8, 'a downlink type').toBe(0x8)
      const counterAt = 2 + this.idBytes.length
      expect(answer.readUInt16BE(counterAt), 'the uplink counter, echoed').toBe(uplink.fcnt & 0xffff)
      const signed = answer.subarray(0, answer.length - 4)
      expect(
        timingSafeEqual(this.tag(uplink.fcnt, signed), answer.subarray(answer.length - 4)),
        'the tag verifies',
      ).toBe(true)
      return { type, more: (answer[1]! & 0x40) !== 0, payload: answer.subarray(counterAt + 2, answer.length - 4) }
    } finally {
      socket.close()
    }
  }
}

/** No field table scrolls sideways, and nothing in the page is wider than the page. */
async function expectNoSidewaysSpill(page: Page) {
  const wide = await page
    .locator('main .fields-wrap')
    .evaluateAll((tables) => tables.filter((t) => t.scrollWidth > t.clientWidth + 1).length)
  expect(wide, 'field tables wider than their packet').toBe(0)
  const spill = await page.locator('main').evaluate((main) => main.scrollWidth - main.clientWidth)
  expect(spill, 'the page scrolls sideways').toBeLessThanOrEqual(1)
}

async function api(page: Page, method: 'GET' | 'POST' | 'PUT' | 'DELETE', url: string, data?: unknown) {
  const xsrf = (await page.context().cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value ?? ''
  const response = await page.request.fetch(url, { method, data, headers: { 'X-XSRF-TOKEN': xsrf } })
  expect(response.ok(), `${method} ${url}: ${response.status()} ${await response.text()}`).toBe(true)
  return response.status() === 204 ? null : response.json()
}

async function createNode(page: Page) {
  const hosts = await api(page, 'GET', '/api/v1/hosts?pageSize=1&search=office-zurich')
  const hostId = hosts.items[0]?.id ?? (await api(page, 'GET', '/api/v1/hosts?pageSize=1')).items[0].id
  const name = `e2e node ${randomBytes(3).toString('hex')}`
  const sensor = await api(page, 'POST', '/api/v1/sensors', {
    hostId,
    type: 'UDP_NODE',
    name,
    enabled: true,
    interval: 600,
    gracePeriod: 300,
    notes: 'Mounted behind the e2e rack.\nSecond line.',
    udp: { keyFamily: 'HmacSha256', allowedSuites: ['HmacSha256_32', 'HmacSha256_64'], ackMode: 'OnRequest' },
  })
  return { sensor, device: new Device(sensor.udp.deviceId, Buffer.from(sensor.integration.udp.key, 'hex')) }
}

test('a UDP node: heartbeats, a command, the protocol page and a firmware update', async ({ page }) => {
  test.setTimeout(180_000)
  await signIn(page)
  const { sensor, device } = await createNode(page)
  expect(sensor.udp.keySource).toBe('Generated')

  // The node page: the panel with the endpoint and the key, the notes, the tabs.
  await page.goto(`/pnmt-admin/sensors/${sensor.id}`)
  await expect(page.getByRole('heading', { name: sensor.name })).toBeVisible()
  await expect(page.getByText('Mounted behind the e2e rack.')).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Node pages' })).toBeVisible()
  await expect(page.getByText(/:5028/).first()).toBeVisible()
  await shot(page, 'udp-node-new')

  // A heartbeat with A: its ACK, then the event and the last packet.
  const ack = await device.exchange(device.build(T.heartbeat, undefined, true))
  expect(ack?.type).toBe(0x8)
  await page.reload()
  await expect(page.locator('main')).toContainText('Alive')
  await page.getByRole('navigation', { name: 'Node pages' }).getByText('Last packets').click()
  await expect(page).toHaveURL(new RegExp(`/sensors/${sensor.id}/last-packets$`))
  await expect(page.locator('main')).toContainText('HEARTBEAT')
  await shot(page, 'udp-last-packets')

  // A command from the UI, delivered with the next uplink, its result in the log.
  await page.getByRole('navigation', { name: 'Node pages' }).getByText('Commands').click()
  await page.getByRole('button', { name: 'New command' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByPlaceholder('reboot').fill('status')
  await shot(page, 'udp-command-dialog')
  // Wait for the POST itself: "Queued" is also a column header, which shows before the command exists.
  const queued = page.waitForResponse((r) => r.url().endsWith('/udp/commands') && r.request().method() === 'POST')
  await dialog.getByRole('button', { name: 'Queue command' }).click()
  expect((await queued).ok()).toBe(true)
  await expect(page.locator('main')).toContainText('status')

  const command = await device.exchange(device.build(T.heartbeat))
  expect(command?.type).toBe(0x9)
  const cmdId = command!.payload[0]!
  expect(command!.payload.subarray(1).toString()).toBe('status')
  const done = await device.exchange(
    device.build(T.cmdResult, Buffer.concat([Buffer.from([cmdId, 0]), Buffer.from('OK')]), true),
  )
  expect(done?.type).toBe(0x8)
  await page.reload()
  await expect(page.locator('main')).toContainText('Completed')
  await page.locator('main').getByText('Completed').first().click()
  await expect(page.locator('main')).toContainText(/Result received/i)
  await shot(page, 'udp-commands')

  // The protocol page: worked examples from the server's codec, for this node.
  await page.getByRole('navigation', { name: 'Node pages' }).getByText('Protocol').click()
  await expect(page.locator('main')).toContainText('HEARTBEAT')
  await expect(page.locator('main')).toContainText('xxd -r -p')
  await shot(page, 'udp-protocol')
  await expectNoSidewaysSpill(page)
  await page.locator('#examples').scrollIntoViewIfNeeded()
  await shot(page, 'udp-protocol-examples')
  await page.setViewportSize({ width: 1000, height: 900 })
  await expectNoSidewaysSpill(page)
  await page.locator('#examples article').nth(1).scrollIntoViewIfNeeded()
  await shot(page, 'udp-protocol-examples-1000')
  await page.setViewportSize({ width: 1440, height: 900 })

  // The page as one Markdown file, the node's key in it (this is an Admin).
  const downloading = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Download as Markdown' }).click()
  const download = await downloading
  expect(download.suggestedFilename()).toBe(`pnmt-udp-${sensor.name.replace(/ /g, '-')}.md`)
  const markdown = await readFile(await download.path(), 'utf8')
  expect(markdown.startsWith(`# PNMT-UDP v1: ${sensor.name}\n`)).toBe(true)
  for (const heading of [
    '## 1. Exchange model',
    '## 9. Device checklist',
    '## 10. Worked examples for this node',
    '## 11. Code',
  ]) {
    expect(markdown).toContain(`\n${heading}\n`)
  }
  expect(markdown).toContain("**This file contains the node's key and token**")
  expect(markdown).toContain(`DEV_ID = ${sensor.udp.deviceId}`)
  expect(markdown).toContain('```text\n0000  ')
  expect(markdown).not.toContain('Highlighted')

  // The admin page: the node in the list, a firmware upload, an update to the end.
  await page.goto('/pnmt-admin/admin/udp-nodes')
  await expect(page.locator('main')).toContainText(sensor.name)
  await shot(page, 'udp-admin-nodes')
  await page.getByRole('navigation', { name: 'UDP sections' }).getByText('Firmware').click()
  const image = randomBytes(2000)
  image[0] = 0xe9
  await page
    .locator('main input[type=file]')
    .setInputFiles({ name: 'e2e.bin', mimeType: 'application/octet-stream', buffer: image })
  await page.locator('#fw-name').fill('E2E image')
  await page.locator('#fw-version').fill('9.9.9')
  await page.getByRole('button', { name: 'Upload' }).click()
  await expect(page.locator('main')).toContainText('E2E image')
  await expect(page.locator('main')).toContainText(createHash('sha256').update(image).digest('hex').slice(0, 8))
  await shot(page, 'udp-admin-firmware')

  const firmware = (await api(page, 'GET', '/api/v1/udp/firmware')).find(
    (f: { name: string }) => f.name === 'E2E image',
  )
  await api(page, 'POST', '/api/v1/udp/ota-jobs', { firmwareId: firmware.id, sensorIds: [sensor.id], chunkSize: 448 })
  const offer = await device.exchange(device.build(T.heartbeat))
  expect(offer?.type).toBe(0xb)
  const updateId = offer!.payload[0]!
  const size = offer!.payload.readUInt32BE(1)
  const sha = offer!.payload.subarray(7, 39)
  const received: Buffer[] = []
  let have = 0
  while (have < size) {
    const get = Buffer.alloc(6)
    get[0] = updateId
    get[1] = 0x01
    get.writeUInt32BE(have, 2)
    const chunk = await device.exchange(device.build(T.ota, get))
    expect(chunk?.type).toBe(0xc)
    expect(chunk!.payload.readUInt32BE(1)).toBe(have)
    received.push(chunk!.payload.subarray(5))
    have += chunk!.payload.length - 5
  }
  expect(createHash('sha256').update(Buffer.concat(received)).digest().equals(sha)).toBe(true)
  await device.exchange(device.build(T.ota, Buffer.from([updateId, 0x03]), true))
  await device.exchange(device.build(T.boot, Buffer.concat([Buffer.from([4]), Buffer.from('9.9.9')]), true))

  await page.getByRole('navigation', { name: 'UDP sections' }).getByText('Updates').click()
  await expect(page.locator('main')).toContainText('Applied')
  await shot(page, 'udp-admin-updates')
  await page.goto(`/pnmt-admin/sensors/${sensor.id}/firmware`)
  await expect(page.locator('main')).toContainText('9.9.9')
  await shot(page, 'udp-node-firmware')
})

test("an existing node's settings are edited in a dialog, its key stays", async ({ page }) => {
  await signIn(page)
  const { sensor, device } = await createNode(page)
  // A random free id of at most two bytes (Udp:DeviceIdBytes).
  expect(sensor.udp.deviceId).toBeGreaterThanOrEqual(1)
  expect(sensor.udp.deviceId).toBeLessThanOrEqual(65_535)
  expect((await device.exchange(device.build(T.heartbeat, undefined, true)))?.type).toBe(0x8)

  await page.goto(`/pnmt-admin/sensors/${sensor.id}`)
  const panel = page.locator('section.panel', { has: page.getByText('Reporting over UDP') })
  await expect(panel.locator('dd.settings')).toHaveText(
    'HMAC-SHA256/32, HMAC-SHA256/64 · responses as received · ACK on request · no JOIN · replay protection on',
  )
  await panel.getByRole('button', { name: 'Edit settings…' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText(`UDP settings of ${sensor.name}`)
  await expect(dialog.locator('#udp-device-id')).toHaveValue(String(sensor.udp.deviceId))
  await dialog.getByRole('button', { name: 'Always' }).click()
  await dialog.locator('label.suite', { hasText: 'HMAC-SHA256/32' }).click()
  // Next to Save, in view, once it has opened (PrimeVue's Message grows in, from no height).
  const hint = dialog.locator('.p-dialog-footer .hint', {
    hasText: 'Packets in HMAC-SHA256/32 are rejected from now on.',
  })
  await expect(hint).toBeInViewport()
  await expect.poll(async () => (await hint.boundingBox())?.height ?? 0).toBeGreaterThan(24)
  await shot(page, 'udp-settings-dialog')

  const saved = page.waitForResponse(
    (r) => r.url().endsWith(`/api/v1/sensors/${sensor.id}`) && r.request().method() === 'PUT',
  )
  await dialog.getByRole('button', { name: 'Save' }).click()
  expect((await saved).ok()).toBe(true)
  await expect(dialog).toBeHidden()
  await expect(panel.locator('dd.settings')).toHaveText(
    'HMAC-SHA256/64 · responses as received · ACK always · no JOIN · replay protection on',
  )
  await shot(page, 'udp-settings-saved')

  // The key and the id stayed; HMAC-SHA256/32, the device's suite, is no longer allowed.
  const after = await api(page, 'GET', `/api/v1/sensors/${sensor.id}`)
  expect(after.integration.udp.key).toBe(sensor.integration.udp.key)
  expect(after.udp.deviceId).toBe(sensor.udp.deviceId)
  expect(await device.exchange(device.build(T.heartbeat, undefined, true))).toBeNull()
})

test('the sensor form has a UDP section and notes', async ({ page }) => {
  await signIn(page)
  const hosts = await api(page, 'GET', '/api/v1/hosts?pageSize=1')
  await page.goto(`/pnmt-admin/hosts/${hosts.items[0].id}/sensors/new?type=UDP_NODE`)
  await expect(page.locator('main')).toContainText('Notes')
  await expect(page.locator('main')).toContainText(/generated automatically/i)
  await expect(page.locator('main')).toContainText('HMAC-SHA256')
  await shot(page, 'udp-form')
})

test('a Viewer sees a node without its key, and the reference key on the protocol page', async ({ page }) => {
  await signIn(page)
  const { sensor } = await createNode(page)
  await page.context().clearCookies()
  await signIn(page, 'viewer')
  await page.goto(`/pnmt-admin/sensors/${sensor.id}/protocol`)
  await expect(page.locator('main')).toContainText(/reference key/i)
  await expect(page.locator('main')).not.toContainText(sensor.integration.udp.key)
  await shot(page, 'udp-protocol-viewer')
  await page.goto('/pnmt-admin/admin/udp-nodes')
  await expect(page.locator('main')).not.toContainText(sensor.name)
})
