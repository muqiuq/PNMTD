import createClient, { type Middleware } from 'openapi-fetch'
import type { components, paths } from './schema'

/** The v1 contract's types, generated from openapi.json (tools/npm.sh run gen:api). */
export type Schemas = components['schemas']

/**
 * A v1 error: the ProblemDetails `code` (docs/webui-migration-plan.md §3.7), the HTTP status, and
 * for validation_failed the messages per field (JSON names).
 */
export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly detail: string | undefined
  readonly errors: Record<string, string[]>
  /** Seconds, from Retry-After (locked_out, rate_limited). */
  readonly retryAfter: number | undefined
  /**
   * The whole ProblemDetails body, for the members a code adds: an edit_conflict's revision, by and at, a
   * looks_like_credential's lines (docs/locations-knowledge-plan.md §5.7, §7.3).
   */
  readonly body: Record<string, unknown>

  constructor(status: number, body: unknown, retryAfter?: number) {
    const problem = (typeof body === 'object' && body !== null ? body : {}) as Record<string, unknown>
    const code = typeof problem.code === 'string' ? problem.code : status === 0 ? 'network' : 'error'
    const detail = typeof problem.detail === 'string' ? problem.detail : undefined
    const title = typeof problem.title === 'string' ? problem.title : undefined
    super(detail ?? title ?? (status === 0 ? 'The server cannot be reached.' : `Request failed (${status}).`))
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.detail = detail
    this.errors = (typeof problem.errors === 'object' && problem.errors !== null ? problem.errors : {}) as Record<
      string,
      string[]
    >
    this.retryAfter = retryAfter
    this.body = problem
  }

  /** The first message for a field, for showing it under the input. */
  field(name: string): string | undefined {
    return this.errors[name]?.[0]
  }
}

/** A cookie's value (document.cookie), decoded. */
export function readCookie(name: string): string | null {
  for (const part of document.cookie.split(';')) {
    const [key, ...rest] = part.trim().split('=')
    if (key === name) {
      return decodeURIComponent(rest.join('='))
    }
  }
  return null
}

const unsafe = (method: string) => !['GET', 'HEAD', 'OPTIONS'].includes(method.toUpperCase())

/** §4.7: unsafe calls echo the XSRF-TOKEN cookie (issued by /auth/me and the login) in X-XSRF-TOKEN. */
const csrf: Middleware = {
  onRequest({ request }) {
    if (unsafe(request.method)) {
      const token = readCookie('XSRF-TOKEN')
      if (token) {
        request.headers.set('X-XSRF-TOKEN', token)
      }
    }
    return request
  },
}

// Absolute URLs on the page's origin; fetch is looked up per call (tests replace it).
export const client = createClient<paths>({
  baseUrl: typeof location !== 'undefined' ? location.origin : '',
  credentials: 'same-origin',
  fetch: (request) => globalThis.fetch(request),
})
client.use(csrf)

type Listener = (error: ApiError, path: string) => void
const listeners = new Set<Listener>()

/**
 * Called for every failed call, before the caller sees it: the session store sends a 401 to the
 * login and a 403 must_change_password / totp_setup_required to the matching screen (§5.7).
 */
export function onApiError(listener: Listener): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

/**
 * The data of an openapi-fetch call, or an {@link ApiError}. A call the server never answered
 * (offline, proxy down) is an ApiError with status 0 and code "network".
 */
export async function call<T>(request: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  let result: { data?: T; error?: unknown; response: Response }
  try {
    result = await request
  } catch {
    const error = new ApiError(0, undefined)
    listeners.forEach((l) => l(error, ''))
    throw error
  }
  const { data, error, response } = result
  if (!response.ok) {
    const retryAfter = Number.parseInt(response.headers.get('Retry-After') ?? '', 10)
    const failure = new ApiError(response.status, error, Number.isNaN(retryAfter) ? undefined : retryAfter)
    const path = new URL(response.url || '/', 'http://x').pathname
    listeners.forEach((l) => l(failure, path))
    throw failure
  }
  return data as T
}
