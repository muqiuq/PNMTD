import { ref } from 'vue'
import { readCookie } from '@/api/client'
import type { S } from '@/api/resources'
import { extensionOf, fileSize } from './files'

/**
 * Uploads from the editors (docs/locations-knowledge-plan.md §5.5), the browser's part:
 *
 * 1. What becomes an image: a pasted or dropped PNG, JPEG, GIF or WebP, or one picked with *Image*.
 *    Everything else, and anything picked with *Attach file*, is an attachment.
 * 2. A JPEG with metadata is drawn again through a canvas: its orientation applied, its metadata gone.
 * 3. An image over 2 MB is recompressed (WebP where the browser encodes it, JPEG otherwise; 90 %, then
 *    80 %), then scaled to 3,840 px on its longer side, and the person is told. An animated GIF isn't
 *    (it would lose its animation): it is offered as an attachment.
 * 4. POST /api/v1/files?kind=image|attachment&name=…, the file as the body, with its progress. An
 *    XMLHttpRequest, since fetch reports no upload progress; the CSRF header as client.ts sends it.
 * 5. A failure says why: too large, not an image, a blocked type, a proxy's 413, offline.
 *
 * The server checks everything again; these limits are its defaults (§8.4).
 */

export type FileKind = 'image' | 'attachment'

/** How a file arrived: pasted or dropped, picked with *Image*, or with *Attach file*. */
export type UploadIntent = 'paste' | 'image' | 'attach'

/** POST /api/v1/files's answer (§5.5, §9): id, kind (Image, Attachment), url, name, type, size, dimensions. */
export type UploadedFile = S['FileItem']

/** What the editors call: the uploaded file, or null when it failed (and the person was told). */
export type UploadHandler = (
  file: File,
  intent: UploadIntent,
  onProgress?: (fraction: number) => void,
) => Promise<UploadedFile | null>

export const limits = {
  /** KnowledgeBase:MaxImageBytes, 2 MiB. */
  image: 2 * 1024 * 1024,
  /** KnowledgeBase:MaxAttachmentBytes, 25 MiB. */
  attachment: 25 * 1024 * 1024,
  /** The longer side a recompressed image is scaled to. */
  maxSide: 3840,
}

/** The image types PNMT shows (§5.5): checked by their bytes on the server. */
export const imageTypes = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']
const imageExtensions = ['png', 'jpg', 'jpeg', 'gif', 'webp']

export type UploadFailure =
  | 'too_large'
  | 'not_an_image'
  | 'blocked_type'
  | 'proxy_limit'
  | 'animated_gif'
  | 'offline'
  | 'rate_limited'
  | 'forbidden'
  | 'signed_out'
  | 'failed'

export class UploadError extends Error {
  readonly reason: UploadFailure
  /** The file could go up as an attachment instead (an image too large, or not one PNMT shows). */
  readonly attachInstead: boolean

  constructor(reason: UploadFailure, message: string, attachInstead = false) {
    super(message)
    this.name = 'UploadError'
    this.reason = reason
    this.attachInstead = attachInstead
  }
}

function isImageType(file: Blob & { name?: string }): boolean {
  if (file.type) {
    return imageTypes.includes(file.type.toLowerCase())
  }
  return imageExtensions.includes(extensionOf(file.name ?? ''))
}

/** §5.5, 1.: an image if it is one of the four types, unless it was picked with *Attach file*. */
export function chooseKind(file: File, intent: UploadIntent): FileKind {
  if (intent === 'attach') {
    return 'attachment'
  }
  return isImageType(file) ? 'image' : 'attachment'
}

// --- bytes ------------------------------------------------------------------------------------

async function head(file: Blob, length: number): Promise<Uint8Array> {
  return new Uint8Array(await file.slice(0, length).arrayBuffer())
}

/** A JPEG's EXIF or XMP (APP1), IPTC (APP13) or comment, found before its image data (§5.5, 2.). */
export async function jpegHasMetadata(file: Blob): Promise<boolean> {
  const bytes = await head(file, 256 * 1024)
  if (bytes[0] !== 0xff || bytes[1] !== 0xd8) {
    return false
  }
  let i = 2
  while (i + 4 <= bytes.length) {
    if (bytes[i] !== 0xff) {
      return false
    }
    const marker = bytes[i + 1]!
    if (marker === 0xd9 || marker === 0xda) {
      return false
    }
    if (marker === 0xe1 || marker === 0xed || marker === 0xfe) {
      return true
    }
    const length = (bytes[i + 2]! << 8) | bytes[i + 3]!
    i += 2 + length
  }
  return false
}

/** Whether a GIF has more than one frame (§5.5, 3.). */
export async function gifIsAnimated(file: Blob): Promise<boolean> {
  const bytes = new Uint8Array(await file.arrayBuffer())
  if (bytes.length < 13 || String.fromCharCode(...bytes.subarray(0, 3)) !== 'GIF') {
    return false
  }
  let i = 13
  if (bytes[10]! & 0x80) {
    i += 3 * 2 ** ((bytes[10]! & 0x07) + 1)
  }
  let frames = 0
  const skipSubBlocks = () => {
    while (i < bytes.length && bytes[i] !== 0) {
      i += bytes[i]! + 1
    }
    i++
  }
  while (i < bytes.length) {
    const block = bytes[i]
    if (block === 0x2c) {
      frames++
      if (frames > 1) {
        return true
      }
      const packed = bytes[i + 9]!
      i += 10
      if (packed & 0x80) {
        i += 3 * 2 ** ((packed & 0x07) + 1)
      }
      i++ // LZW minimum code size
      skipSubBlocks()
    } else if (block === 0x21) {
      i += 2
      skipSubBlocks()
    } else {
      break
    }
  }
  return false
}

// --- the canvas ---------------------------------------------------------------------------------

function encode(
  bitmap: ImageBitmap,
  width: number,
  height: number,
  type: string,
  quality: number,
): Promise<Blob | null> {
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const context = canvas.getContext('2d')
  if (!context) {
    return Promise.resolve(null)
  }
  if (type === 'image/jpeg') {
    // JPEG has no transparency: white behind it, not black.
    context.fillStyle = '#ffffff'
    context.fillRect(0, 0, width, height)
  }
  context.drawImage(bitmap, 0, 0, width, height)
  return new Promise((resolve) => canvas.toBlob(resolve, type, quality))
}

let webp: Promise<boolean> | null = null

/** Whether this browser's canvas writes WebP (Safari writes PNG instead). */
function canEncodeWebp(): Promise<boolean> {
  webp ??= new Promise((resolve) => {
    const canvas = document.createElement('canvas')
    canvas.width = canvas.height = 1
    canvas.toBlob((blob) => resolve(blob?.type === 'image/webp'), 'image/webp', 0.8)
  })
  return webp
}

/** Only for tests: forget what the browser could encode. */
export function resetEncoderCheck() {
  webp = null
}

function renamed(name: string, extension: string): string {
  const dot = name.lastIndexOf('.')
  return `${dot > 0 ? name.slice(0, dot) : name || 'image'}.${extension}`
}

export interface PreparedFile {
  blob: Blob
  name: string
  kind: FileKind
  /** What the browser did, to tell the person: *Screenshot of 3.4 MB stored as 1.1 MB WebP*. */
  notice: string | null
  /** The file as it was, for *Attach the original instead*. */
  original: File | null
}

/**
 * The file as it goes up (§5.5, 1.–3.). Throws an UploadError when it can't: too large, or (with the
 * *Image* button) not an image PNMT shows.
 */
export async function prepareFile(file: File, kind: FileKind): Promise<PreparedFile> {
  const name = file.name || (kind === 'image' ? 'pasted-image.png' : 'file')
  if (kind === 'attachment') {
    if (file.size > limits.attachment) {
      throw new UploadError(
        'too_large',
        `${name} is ${fileSize(file.size)}: attachments are at most ${fileSize(limits.attachment)}.`,
      )
    }
    return { blob: file, name, kind, notice: null, original: null }
  }

  if (!isImageType(file)) {
    throw new UploadError(
      'not_an_image',
      `${name} isn’t an image PNMT shows (PNG, JPEG, GIF or WebP). It can be attached as a file.`,
      true,
    )
  }

  const type = file.type.toLowerCase() || `image/${extensionOf(name).replace('jpg', 'jpeg')}`
  let blob: Blob = file
  let bitmap: ImageBitmap | null = null
  const draw = async () => (bitmap ??= await createImageBitmap(file, { imageOrientation: 'from-image' }))

  if (type === 'image/jpeg' && typeof createImageBitmap === 'function' && (await jpegHasMetadata(file))) {
    const image = await draw()
    blob = (await encode(image, image.width, image.height, 'image/jpeg', 0.92)) ?? file
  }

  if (blob.size <= limits.image) {
    return { blob, name, kind, notice: null, original: null }
  }

  const tooLarge = `${name} is ${fileSize(file.size)}: images are at most ${fileSize(limits.image)}.`
  if (type === 'image/gif' && (await gifIsAnimated(file))) {
    throw new UploadError('animated_gif', `${tooLarge} An animated GIF isn't made smaller; attach it as a file.`, true)
  }
  if (typeof createImageBitmap !== 'function') {
    throw new UploadError('too_large', `${tooLarge} This browser can’t make it smaller; attach it as a file.`, true)
  }

  const image = await draw()
  const target = (await canEncodeWebp()) ? { type: 'image/webp', label: 'WebP', extension: 'webp' } : null
  const format = target ?? { type: 'image/jpeg', label: 'JPEG', extension: 'jpg' }
  const longer = Math.max(image.width, image.height)
  const sizes = [{ width: image.width, height: image.height }]
  if (longer > limits.maxSide) {
    const scale = limits.maxSide / longer
    sizes.push({ width: Math.round(image.width * scale), height: Math.round(image.height * scale) })
  }
  for (const size of sizes) {
    for (const quality of [0.9, 0.8]) {
      const smaller = await encode(image, size.width, size.height, format.type, quality)
      if (smaller && smaller.size <= limits.image) {
        const what = file.name ? name : 'Pasted image'
        return {
          blob: smaller,
          name: renamed(name, format.extension),
          kind,
          notice: `${what} of ${fileSize(file.size)} stored as ${fileSize(smaller.size)} ${format.label}`,
          original: file,
        }
      }
    }
  }
  throw new UploadError('too_large', `${tooLarge} Even made smaller it is too large; attach it as a file.`, true)
}

// --- the upload ---------------------------------------------------------------------------------

function problemOf(text: string): { code?: string; detail?: string; title?: string } | null {
  try {
    const body = JSON.parse(text) as unknown
    return typeof body === 'object' && body !== null ? (body as { code?: string }) : null
  } catch {
    return null
  }
}

/** The reason for a failed upload, in the words of §5.5, 5. */
export function uploadFailure(status: number, body: string, name: string): UploadError {
  const problem = problemOf(body)
  const code = problem?.code
  if (status === 0) {
    return new UploadError('offline', `${name} wasn’t uploaded: PNMT can’t be reached. Are you offline?`)
  }
  if (status === 413) {
    if (!problem) {
      return new UploadError(
        'proxy_limit',
        `${name} was refused by a proxy in front of PNMT (413): its body limit is lower than PNMT's ${fileSize(limits.attachment)}. An administrator can raise it (client_max_body_size 32m for nginx; deploy/README.md).`,
      )
    }
    return new UploadError('too_large', problem.detail ?? `${name} is larger than PNMT accepts.`, true)
  }
  if (status === 415 || code === 'not_an_image') {
    return new UploadError(
      'not_an_image',
      problem?.detail ?? `${name} isn’t an image PNMT shows (PNG, JPEG, GIF or WebP). It can be attached as a file.`,
      true,
    )
  }
  if (code === 'blocked_type') {
    return new UploadError(
      'blocked_type',
      problem?.detail ?? `${name} can’t be attached: files of this type (programs, scripts) are refused.`,
    )
  }
  if (status === 429) {
    return new UploadError(
      'rate_limited',
      `${name} wasn’t uploaded: too many uploads in the last hour. Try again later.`,
    )
  }
  if (status === 401) {
    return new UploadError('signed_out', `${name} wasn’t uploaded: your session has ended. Sign in again.`)
  }
  if (status === 403) {
    return new UploadError('forbidden', `${name} wasn’t uploaded: your role can’t upload files.`)
  }
  return new UploadError('failed', problem?.detail ?? problem?.title ?? `${name} wasn’t uploaded (${status}).`)
}

/** POST /api/v1/files with the file as the body (§5.5, 4.). */
export function sendFile(
  blob: Blob,
  kind: FileKind,
  name: string,
  onProgress?: (fraction: number) => void,
): Promise<UploadedFile> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open('POST', `/api/v1/files?kind=${kind}&name=${encodeURIComponent(name)}`)
    xhr.withCredentials = true
    xhr.setRequestHeader('Accept', 'application/json')
    xhr.setRequestHeader('Content-Type', blob.type || 'application/octet-stream')
    const token = readCookie('XSRF-TOKEN')
    if (token) {
      xhr.setRequestHeader('X-XSRF-TOKEN', token)
    }
    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable && event.total > 0) {
        onProgress?.(event.loaded / event.total)
      }
    }
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        const file = problemOf(xhr.responseText) as UploadedFile | null
        if (file && typeof file.url === 'string') {
          resolve(file)
          return
        }
      }
      reject(uploadFailure(xhr.status, xhr.responseText, name))
    }
    xhr.onerror = () => reject(uploadFailure(0, '', name))
    xhr.onabort = () => reject(new UploadError('failed', `${name}: the upload was cancelled.`))
    xhr.send(blob)
  })
}

export interface UploadResult {
  file: UploadedFile
  /** What the browser did to the image, if anything (§5.5, 3.). */
  notice: string | null
  /** The file as it was, for *Attach the original instead*. */
  original: File | null
}

/**
 * The editors' uploads: `upload(file, kind)` prepares and sends one file; `active` lists those in
 * flight with their progress, for a status line.
 */
export function useUpload() {
  const active = ref<{ id: number; name: string; progress: number }[]>([])
  let next = 0

  async function upload(file: File, kind: FileKind, onProgress?: (fraction: number) => void): Promise<UploadResult> {
    const prepared = await prepareFile(file, kind)
    const entry = { id: ++next, name: prepared.name, progress: 0 }
    active.value = [...active.value, entry]
    try {
      const uploaded = await sendFile(prepared.blob, prepared.kind, prepared.name, (fraction) => {
        active.value = active.value.map((a) => (a.id === entry.id ? { ...a, progress: fraction } : a))
        onProgress?.(fraction)
      })
      return { file: uploaded, notice: prepared.notice, original: prepared.original }
    } finally {
      active.value = active.value.filter((a) => a.id !== entry.id)
    }
  }

  return { upload, active, chooseKind }
}
