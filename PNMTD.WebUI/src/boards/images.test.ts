import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { forgetImages, imageUrl } from './images'

/** docs/board-messages-plan.md §9.4: each image once per screen; with motion off, its first frame drawn once to a canvas. */
describe('board images', () => {
  let made = 0
  beforeEach(() => {
    forgetImages()
    made = 0
    vi.stubGlobal('URL', { ...URL, createObjectURL: (b: Blob) => `blob:${b.type}#${++made}` })
  })
  afterEach(() => vi.unstubAllGlobals())

  it('fetches a file once, however often it is shown', async () => {
    const load = vi.fn(async () => new Blob(['gif'], { type: 'image/gif' }))
    const a = await imageUrl(load, 'f1', false)
    const b = await imageUrl(load, 'f1', false)
    expect(a).toBe(b)
    expect(load).toHaveBeenCalledTimes(1)
  })

  it('draws the first frame for a board without motion', async () => {
    const close = vi.fn()
    vi.stubGlobal(
      'createImageBitmap',
      vi.fn(async () => ({ width: 3, height: 2, close })),
    )
    const drawImage = vi.fn()
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({
      drawImage,
    } as unknown as CanvasRenderingContext2D)
    vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation((done) =>
      done(new Blob(['png'], { type: 'image/png' })),
    )
    const load = vi.fn(async () => new Blob(['gif'], { type: 'image/gif' }))
    const still = await imageUrl(load, 'f2', true)
    expect(still).toMatch(/^blob:image\/png/)
    expect(drawImage).toHaveBeenCalledOnce()
    expect(close).toHaveBeenCalledOnce()
    // The moving one shares the fetch.
    expect(await imageUrl(load, 'f2', false)).toMatch(/^blob:image\/gif/)
    expect(load).toHaveBeenCalledTimes(1)
  })

  it('tries a failed fetch again', async () => {
    const load = vi
      .fn()
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValue(new Blob(['x'], { type: 'image/png' }))
    await expect(imageUrl(load, 'f3', false)).rejects.toThrow('offline')
    await expect(imageUrl(load, 'f3', false)).resolves.toMatch(/^blob:/)
  })
})
