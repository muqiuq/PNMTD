import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/api/client'
import { api } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import AcceptChangeDialog from './AcceptChangeDialog.vue'
import { changed, facts, NEW_SHA, NEWER_SHA, sensor, SENSOR_ID, summary } from './certificate.fixture'

/**
 * Accepting a change (docs/ssl-certificate-plan.md §8.4, §10): *Accept* stays disabled until the box is ticked, the request
 * carries confirmed and the SHA-256 shown, and a certificate that changed again (409) reloads the comparison and unticks
 * the box, saying why.
 */
vi.stubGlobal('matchMedia', (query: string) => ({
  matches: false,
  media: query,
  addEventListener: () => {},
  removeEventListener: () => {},
}))

vi.mock('@/api/resources', async (original) => {
  const actual = await original<typeof import('@/api/resources')>()
  return {
    ...actual,
    api: { ...actual.api, certificates: { ...actual.api.certificates, get: vi.fn(), accept: vi.fn() } },
  }
})
const get = vi.mocked(api.certificates.get)
const accept = vi.mocked(api.certificates.accept)

async function open() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const dialog = mountWithUi(AcceptChangeDialog, {
    props: { visible: true, sensor: sensor(summary()) },
    global: {
      plugins: [[PrimeVue, { unstyled: true }], ToastService, [VueQueryPlugin, { queryClient }]],
      stubs: { teleport: true },
    },
  })
  await vi.waitFor(async () => {
    await flushPromises()
    expect(dialog.text()).toContain('What changed')
  })
  return dialog
}

const acceptButton = (dialog: Awaited<ReturnType<typeof open>>) =>
  dialog.findAll('button').find((b) => b.text().includes('Accept the new certificate'))!
const box = (dialog: Awaited<ReturnType<typeof open>>) => dialog.find('#accept-confirmed')

describe('the accept window', () => {
  beforeEach(() => {
    get.mockReset()
    accept.mockReset()
    get.mockResolvedValue(changed())
  })

  it('warns first, shows the comparison and the command, and leaves Cancel as the default', async () => {
    const dialog = await open()
    const text = dialog.text()
    expect(text).toContain('Only accept this if you know why the certificate changed')
    expect(text).toContain('intercepting the connection to shop.example.com')
    expect(text).toContain('new key')
    expect(text).toContain("issuer Let's Encrypt → ZeroSSL")
    expect(dialog.findAll('tr.changed').length).toBe(4)
    expect(dialog.find('[aria-label="Command"]').text()).toContain(
      'openssl s_client -connect shop.example.com:443 -servername shop.example.com',
    )
    const cancel = dialog.findAll('button').find((b) => b.text() === 'Cancel')!
    expect(cancel.attributes('autofocus')).toBeDefined()
    dialog.unmount()
  })

  it('keeps Accept disabled until the box is ticked, and sends confirmed with the SHA-256 shown', async () => {
    accept.mockResolvedValue(changed(facts(), { change: null, accepted: null }))
    const dialog = await open()
    expect(acceptButton(dialog).attributes('disabled')).toBeDefined()

    await box(dialog).trigger('change')
    expect(acceptButton(dialog).attributes('disabled')).toBeUndefined()
    await dialog.find('#accept-note').setValue('Renewed by anna')
    await acceptButton(dialog).trigger('click')
    await flushPromises()

    expect(accept).toHaveBeenCalledWith(SENSOR_ID, { sha256: NEW_SHA, confirmed: true, note: 'Renewed by anna' })
    expect(dialog.emitted('update:visible')?.at(-1)).toEqual([false])
    dialog.unmount()
  })

  it('reloads the comparison and unticks the box when the certificate changed again (409)', async () => {
    const dialog = await open()
    get.mockResolvedValue(changed(facts({ sha256: NEWER_SHA })))
    accept.mockRejectedValueOnce(
      new ApiError(409, {
        code: 'certificate_changed_again',
        detail: 'The certificate changed again since you looked: review the new comparison before accepting.',
      }),
    )
    await box(dialog).trigger('change')
    await acceptButton(dialog).trigger('click')
    await flushPromises()

    expect(get).toHaveBeenCalledTimes(2)
    expect(dialog.text()).toContain('The certificate changed again since you looked')
    expect(dialog.text()).toContain(NEWER_SHA)
    expect(acceptButton(dialog).attributes('disabled')).toBeDefined()
    expect(dialog.emitted('update:visible')).toBeUndefined()

    // Ticked again, it sends the newer certificate's SHA-256.
    accept.mockResolvedValue(changed(facts({ sha256: NEWER_SHA }), { change: null, accepted: null }))
    await box(dialog).trigger('change')
    await acceptButton(dialog).trigger('click')
    await flushPromises()
    expect(accept).toHaveBeenLastCalledWith(SENSOR_ID, { sha256: NEWER_SHA, confirmed: true, note: null })
    dialog.unmount()
  })
})
