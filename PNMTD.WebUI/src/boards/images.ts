import type { ImageLoader } from './inject'

/**
 * A board's images as blob: URLs (docs/board-messages-plan.md §9.4), once per file and screen: files never change. `still` is the first
 * frame, for a board without motion: createImageBitmap takes an animation's first frame, drawn once to a canvas and kept as a PNG.
 */
const blobs = new Map<string, Promise<Blob>>()
const urls = new Map<string, Promise<string>>()

function once<T>(cache: Map<string, Promise<T>>, key: string, make: () => Promise<T>): Promise<T> {
  let value = cache.get(key)
  if (!value) {
    value = make()
    cache.set(key, value)
    // A failure is tried again next time.
    value.catch(() => cache.delete(key))
  }
  return value
}

export function imageUrl(load: ImageLoader, fileId: string, still: boolean): Promise<string> {
  const blob = () => once(blobs, fileId, () => load(fileId))
  if (!still) return once(urls, `${fileId}:original`, async () => URL.createObjectURL(await blob()))
  return once(urls, `${fileId}:still`, async () => {
    const source = await blob()
    if (typeof createImageBitmap === 'undefined') return URL.createObjectURL(source)
    const bitmap = await createImageBitmap(source)
    const canvas = document.createElement('canvas')
    canvas.width = bitmap.width
    canvas.height = bitmap.height
    canvas.getContext('2d')?.drawImage(bitmap, 0, 0)
    bitmap.close()
    const frame = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/png'))
    return URL.createObjectURL(frame ?? source)
  })
}

/** For tests. */
export function forgetImages() {
  blobs.clear()
  urls.clear()
}
