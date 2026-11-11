import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, call, client, onApiError, readCookie } from './client'

function respond(status: number, body?: unknown, headers: Record<string, string> = {}) {
  return vi.fn(async (request: Request) => {
    const response = new Response(body === undefined ? null : JSON.stringify(body), {
      status,
      headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json', ...headers },
    })
    Object.defineProperty(response, 'url', { value: request.url })
    return response
  })
}

afterEach(() => {
  vi.unstubAllGlobals()
  document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/'
})

describe('the API client', () => {
  it('echoes the XSRF-TOKEN cookie on unsafe calls only', async () => {
    document.cookie = 'XSRF-TOKEN=abc%2Fdef; path=/'
    const fetch = respond(204)
    vi.stubGlobal('fetch', fetch)

    await call(client.POST('/api/v1/auth/logout'))
    await call(client.GET('/api/v1/auth/me'))

    const [post, get] = fetch.mock.calls.map(([request]) => request as Request)
    expect(post!.headers.get('X-XSRF-TOKEN')).toBe('abc/def')
    expect(get!.headers.get('X-XSRF-TOKEN')).toBeNull()
    expect(readCookie('XSRF-TOKEN')).toBe('abc/def')
  })

  it('maps ProblemDetails to an ApiError with the code and the field messages', async () => {
    vi.stubGlobal(
      'fetch',
      respond(400, {
        status: 400,
        title: 'One or more validation errors occurred.',
        code: 'validation_failed',
        errors: { newPassword: ['At least 10 characters.'] },
      }),
    )

    const error = await call(
      client.POST('/api/v1/account/password', { body: { currentPassword: 'a', newPassword: 'b' } }),
    ).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    const api = error as ApiError
    expect([api.status, api.code, api.field('newPassword'), api.field('other')]).toEqual([
      400,
      'validation_failed',
      'At least 10 characters.',
      undefined,
    ])
  })

  it('reads Retry-After and tells the listeners which path failed', async () => {
    vi.stubGlobal(
      'fetch',
      respond(429, { status: 429, code: 'locked_out', detail: 'Too many.' }, { 'Retry-After': '240' }),
    )
    const seen: [string, string][] = []
    const stop = onApiError((e, path) => seen.push([e.code, path]))

    const error = (await call(client.POST('/api/v1/auth/login', { body: { username: 'a', password: 'b' } })).catch(
      (e: unknown) => e,
    )) as ApiError
    stop()

    expect([error.code, error.retryAfter, error.message]).toEqual(['locked_out', 240, 'Too many.'])
    expect(seen).toEqual([['locked_out', '/api/v1/auth/login']])
  })

  it('turns a call the server never answered into a network error', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => Promise.reject(new TypeError('Failed to fetch'))),
    )

    const error = (await call(client.GET('/api/v1/auth/me')).catch((e: unknown) => e)) as ApiError

    expect([error.status, error.code]).toEqual([0, 'network'])
  })

  it('returns the data of a success', async () => {
    vi.stubGlobal('fetch', respond(200, { identity: 'Home', version: '1.0.0' }))
    await expect(call(client.GET('/api/v1/system/info'))).resolves.toMatchObject({ identity: 'Home' })
  })
})
