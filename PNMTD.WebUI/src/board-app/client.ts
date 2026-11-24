import type { paths } from '@/api/schema'

/**
 * The board app's calls (docs/boards-plan.md §12.2): plain fetch to /pnmt-board/api, the token in a header so that it isn't in a
 * proxy's log every poll (§5.1). Nothing of the admin's client: no session, no CSRF, no redirects to a login.
 */
export type BoardView = paths['/pnmt-board/api/view']['get']['responses'][200]['content']['application/json']
export type LoungeData = paths['/pnmt-board/api/lounge']['get']['responses'][200]['content']['application/json']
export type MessagesData = paths['/pnmt-board/api/messages']['get']['responses'][200]['content']['application/json']

/** A refusal or a failure: the ProblemDetails code (board_not_found, board_disabled, board_network …), or "network". */
export class BoardError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    detail: string,
    readonly address: string | null = null,
    /** A 429's Retry-After, in seconds. */
    readonly retryAfter: number | null = null,
  ) {
    super(detail)
  }

  /** The board's own answer, not a passing failure: shown full screen until it changes. */
  get refused(): boolean {
    return ['board_not_found', 'board_disabled', 'board_network', 'blocked', 'rate_limited'].includes(this.code)
  }
}

/** The token from the page's address: /pnmt-board/<token>. */
export function tokenFromPath(path: string): string | null {
  const last = path.replace(/\/+$/, '').split('/').pop() ?? ''
  return /^pnmb_[A-Za-z0-9_-]{43}$/.test(last) ? last : null
}

async function get<T>(path: string, token: string): Promise<T> {
  let response: Response
  try {
    response = await fetch(path, {
      headers: { Authorization: `Board ${token}`, Accept: 'application/json' },
      credentials: 'same-origin',
      cache: 'no-store',
    })
  } catch {
    throw new BoardError(0, 'network', 'PNMT can’t be reached.')
  }
  if (!response.ok) {
    let body: Record<string, unknown> = {}
    try {
      body = (await response.json()) as Record<string, unknown>
    } catch {
      // A proxy's own page, or nothing.
    }
    const code = typeof body.code === 'string' ? body.code : response.status === 404 ? 'board_not_found' : 'error'
    const detail = typeof body.detail === 'string' ? body.detail : `PNMT answered ${response.status}.`
    throw new BoardError(response.status, code, detail, typeof body.address === 'string' ? body.address : null)
  }
  return (await response.json()) as T
}

/**
 * The board's open messages (docs/board-messages-plan.md §6): with the last ETag, a 304 says nothing changed (null). A 429 carries its
 * Retry-After in seconds.
 */
async function messages(
  token: string,
  etag: string | null,
): Promise<{ etag: string | null; data: MessagesData } | null> {
  let response: Response
  try {
    response = await fetch('/pnmt-board/api/messages', {
      headers: {
        Authorization: `Board ${token}`,
        Accept: 'application/json',
        ...(etag ? { 'If-None-Match': etag } : {}),
      },
      credentials: 'same-origin',
      cache: 'no-store',
    })
  } catch {
    throw new BoardError(0, 'network', 'PNMT can’t be reached.')
  }
  if (response.status === 304) return null
  if (!response.ok) {
    const retry = Number(response.headers.get('Retry-After'))
    throw new BoardError(
      response.status,
      response.status === 429 ? 'rate_limited' : 'error',
      `PNMT answered ${response.status}.`,
      null,
      retry || null,
    )
  }
  return { etag: response.headers.get('ETag'), data: (await response.json()) as MessagesData }
}

/** An image element's file (docs/board-messages-plan.md §9.4), with the token: files never change, so the browser may keep it. */
async function image(token: string, fileId: string): Promise<Blob> {
  const response = await fetch(`/pnmt-board/api/images/${encodeURIComponent(fileId)}`, {
    headers: { Authorization: `Board ${token}` },
    credentials: 'same-origin',
  })
  if (!response.ok) throw new BoardError(response.status, 'no_image', `PNMT answered ${response.status}.`)
  return await response.blob()
}

export const boardApi = {
  view: (token: string) => get<BoardView>('/pnmt-board/api/view', token),
  lounge: (token: string) => get<LoungeData>('/pnmt-board/api/lounge', token),
  messages,
  image,
}
