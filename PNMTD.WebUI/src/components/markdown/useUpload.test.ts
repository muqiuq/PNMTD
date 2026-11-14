import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  chooseKind,
  gifIsAnimated,
  jpegHasMetadata,
  limits,
  prepareFile,
  resetEncoderCheck,
  sendFile,
  UploadError,
  uploadFailure,
} from './useUpload'

// docs/locations-knowledge-plan.md §5.5 and §12: what becomes an image, the limits, recompression.

const MB = 1024 * 1024
const file = (size: number, name: string, type: string, head: number[] = []) => {
  const bytes = new Uint8Array(size)
  bytes.set(head)
  return new File([bytes], name, { type })
}

const jpegWithExif = [0xff, 0xd8, 0xff, 0xe1, 0x00, 0x08, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00, 0xff, 0xda]
const jpegPlain = [0xff, 0xd8, 0xff, 0xe0, 0x00, 0x04, 0x4a, 0x46, 0xff, 0xda]
const gifFrame = [0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0x00, 0x02, 0x01, 0x00, 0x00]
const gifHeader = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0, 0x00, 0, 0]
const animatedGif = [...gifHeader, 0x21, 0xf9, 0x04, 0, 0, 0, 0, 0, ...gifFrame, ...gifFrame, 0x3b]
const stillGif = [...gifHeader, ...gifFrame, 0x3b]

describe('the kind of a file', () => {
  it.each([
    ['shot.png', 'image/png', 'image'],
    ['photo.jpg', 'image/jpeg', 'image'],
    ['anim.gif', 'image/gif', 'image'],
    ['pic.webp', 'image/webp', 'image'],
    ['plan.svg', 'image/svg+xml', 'attachment'],
    ['photo.heic', 'image/heic', 'attachment'],
    ['photo.avif', 'image/avif', 'attachment'],
    ['manual.pdf', 'application/pdf', 'attachment'],
    ['router.rsc', '', 'attachment'],
    ['nameless.png', '', 'image'],
  ])('%s (%s) pasted is an %s', (name, type, kind) => {
    expect(chooseKind(file(10, name, type), 'paste')).toBe(kind)
  })

  it('is an attachment when picked with Attach file, image or not', () => {
    expect(chooseKind(file(10, 'shot.png', 'image/png'), 'attach')).toBe('attachment')
  })
})

describe('the limits', () => {
  it('refuses an attachment over 25 MB before uploading', async () => {
    await expect(
      prepareFile(file(limits.attachment + 1, 'backup.tar', 'application/x-tar'), 'attachment'),
    ).rejects.toMatchObject({ reason: 'too_large' })
  })

  it('takes an attachment of 25 MB as it is', async () => {
    const big = file(limits.attachment, 'backup.tar', 'application/x-tar')
    expect(await prepareFile(big, 'attachment')).toMatchObject({ blob: big, kind: 'attachment', notice: null })
  })

  it('takes an image up to 2 MB as it is', async () => {
    const shot = file(limits.image, 'shot.png', 'image/png')
    expect(await prepareFile(shot, 'image')).toMatchObject({ blob: shot, kind: 'image', notice: null })
  })

  it('refuses what is not an image PNMT shows when picked with Image, offering to attach it', async () => {
    const error = await prepareFile(file(10, 'plan.svg', 'image/svg+xml'), 'image').catch((e: unknown) => e)
    expect(error).toBeInstanceOf(UploadError)
    expect(error).toMatchObject({ reason: 'not_an_image', attachInstead: true })
  })
})

describe('bytes', () => {
  it('finds EXIF in a JPEG, and none in a plain one', async () => {
    expect(await jpegHasMetadata(file(64, 'a.jpg', 'image/jpeg', jpegWithExif))).toBe(true)
    expect(await jpegHasMetadata(file(64, 'b.jpg', 'image/jpeg', jpegPlain))).toBe(false)
  })

  it('tells an animated GIF from a still one', async () => {
    expect(await gifIsAnimated(new Blob([new Uint8Array(animatedGif)]))).toBe(true)
    expect(await gifIsAnimated(new Blob([new Uint8Array(stillGif)]))).toBe(false)
  })
})

describe('recompression, with a canvas mocked', () => {
  let encoded: { type: string; quality: number; width: number }[]
  /** The size the canvas writes, by type, quality and width. */
  let output: (type: string, quality: number, width: number) => number
  let webp = true

  beforeEach(() => {
    resetEncoderCheck()
    encoded = []
    webp = true
    vi.stubGlobal(
      'createImageBitmap',
      vi.fn(async () => ({ width: 6000, height: 4000, close() {} })),
    )
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({
      fillRect() {},
      drawImage() {},
      fillStyle: '',
    } as unknown as CanvasRenderingContext2D)
    HTMLCanvasElement.prototype.toBlob = function (this: HTMLCanvasElement, done, type = 'image/png', quality = 1) {
      const written = type === 'image/webp' && !webp ? 'image/png' : type
      if (this.width > 1) {
        encoded.push({ type: written, quality, width: this.width })
      }
      done(new Blob([new Uint8Array(this.width > 1 ? output(written, quality, this.width) : 1)], { type: written }))
    }
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('writes WebP at 90 % when that is small enough, and says so', async () => {
    output = () => 1.1 * MB
    const prepared = await prepareFile(file(3.4 * MB, 'Screenshot.png', 'image/png'), 'image')
    expect(encoded).toEqual([{ type: 'image/webp', quality: 0.9, width: 6000 }])
    expect(prepared.name).toBe('Screenshot.webp')
    expect(prepared.notice).toBe('Screenshot.png of 3.4 MB stored as 1.1 MB WebP')
    expect(prepared.original?.name).toBe('Screenshot.png')
  })

  it('writes JPEG where the browser has no WebP encoder', async () => {
    webp = false
    output = () => 1 * MB
    const prepared = await prepareFile(file(3 * MB, 'photo.png', 'image/png'), 'image')
    expect(encoded[0]!.type).toBe('image/jpeg')
    expect(prepared.name).toBe('photo.jpg')
    expect(prepared.notice).toContain('JPEG')
  })

  it('tries 80 %, then 3,840 px on the longer side', async () => {
    output = (_type, quality, width) => (width <= limits.maxSide && quality === 0.8 ? 1.5 * MB : 2.5 * MB)
    const prepared = await prepareFile(file(9 * MB, 'plan.png', 'image/png'), 'image')
    expect(encoded.map((e) => `${e.width}@${e.quality}`)).toEqual(['6000@0.9', '6000@0.8', '3840@0.9', '3840@0.8'])
    expect(prepared.blob.size).toBe(1.5 * MB)
  })

  it('gives up when even that is too large, offering to attach it', async () => {
    output = () => 3 * MB
    await expect(prepareFile(file(20 * MB, 'huge.png', 'image/png'), 'image')).rejects.toMatchObject({
      reason: 'too_large',
      attachInstead: true,
    })
  })

  it('never recompresses an animated GIF: it is offered as an attachment', async () => {
    output = () => 1 * MB
    const gif = file(3 * MB, 'anim.gif', 'image/gif', animatedGif)
    await expect(prepareFile(gif, 'image')).rejects.toMatchObject({ reason: 'animated_gif', attachInstead: true })
    expect(encoded).toEqual([])
  })

  it('recompresses a still GIF', async () => {
    output = () => 1 * MB
    const prepared = await prepareFile(file(3 * MB, 'still.gif', 'image/gif', stillGif), 'image')
    expect(prepared.name).toBe('still.webp')
  })

  it('draws a JPEG with metadata again, even when small', async () => {
    output = () => 0.5 * MB
    const prepared = await prepareFile(file(1 * MB, 'phone.jpg', 'image/jpeg', jpegWithExif), 'image')
    expect(encoded).toEqual([{ type: 'image/jpeg', quality: 0.92, width: 6000 }])
    expect(prepared.blob.size).toBe(0.5 * MB)
    expect(prepared.notice).toBeNull()
  })

  it('leaves a JPEG without metadata alone', async () => {
    output = () => 0.5 * MB
    const jpeg = file(1 * MB, 'plain.jpg', 'image/jpeg', jpegPlain)
    expect((await prepareFile(jpeg, 'image')).blob).toBe(jpeg)
    expect(encoded).toEqual([])
  })
})

describe('a failed upload says why', () => {
  const problem = (code: string, detail = 'x') => JSON.stringify({ code, detail, status: 0 })

  it.each([
    [413, problem('too_large', 'cage.png is larger than 2 MB.'), 'too_large'],
    [413, '<html><body><h1>413 Request Entity Too Large</h1></body></html>', 'proxy_limit'],
    [415, problem('not_an_image'), 'not_an_image'],
    [422, problem('blocked_type'), 'blocked_type'],
    [429, problem('rate_limited'), 'rate_limited'],
    [401, '', 'signed_out'],
    [403, problem('forbidden_role'), 'forbidden'],
    [0, '', 'offline'],
    [500, '', 'failed'],
  ])('%i %s → %s', (status, body, reason) => {
    expect(uploadFailure(status, body, 'cage.png').reason).toBe(reason)
  })

  it("names a proxy's limit as such", () => {
    expect(uploadFailure(413, '<html>nginx</html>', 'manual.pdf').message).toMatch(/proxy.*client_max_body_size/)
  })
})

describe('the request', () => {
  it('posts the file as the body, with its kind, name and the CSRF header, and reports progress', async () => {
    const sent: { method: string; url: string; headers: Record<string, string>; body: unknown } = {
      method: '',
      url: '',
      headers: {},
      body: null,
    }
    class FakeRequest {
      status = 0
      responseText = ''
      withCredentials = false
      upload = {
        onprogress: null as ((e: { lengthComputable: boolean; loaded: number; total: number }) => void) | null,
      }
      onload: (() => void) | null = null
      onerror: (() => void) | null = null
      onabort: (() => void) | null = null
      open(method: string, url: string) {
        sent.method = method
        sent.url = url
      }
      setRequestHeader(name: string, value: string) {
        sent.headers[name] = value
      }
      send(body: unknown) {
        sent.body = body
        this.upload.onprogress?.({ lengthComputable: true, loaded: 50, total: 100 })
        this.status = 201
        this.responseText = JSON.stringify({
          id: '0193',
          kind: 'Image',
          url: '/api/v1/files/0193/cage-b.png',
          name: 'cage b.png',
          contentType: 'image/png',
          size: 100,
          width: 10,
          height: 10,
        })
        this.onload?.()
      }
    }
    vi.stubGlobal('XMLHttpRequest', FakeRequest)
    document.cookie = 'XSRF-TOKEN=token%201'
    const progress: number[] = []
    const blob = new Blob([new Uint8Array(100)], { type: 'image/png' })

    const uploaded = await sendFile(blob, 'image', 'cage b.png', (p) => progress.push(p))

    expect(sent.method).toBe('POST')
    expect(sent.url).toBe('/api/v1/files?kind=image&name=cage%20b.png')
    expect(sent.headers['X-XSRF-TOKEN']).toBe('token 1')
    expect(sent.headers['Content-Type']).toBe('image/png')
    expect(sent.body).toBe(blob)
    expect(progress).toEqual([0.5])
    expect(uploaded.url).toBe('/api/v1/files/0193/cage-b.png')
    vi.unstubAllGlobals()
  })
})
