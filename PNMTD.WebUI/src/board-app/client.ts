import type { paths } from '@/api/schema'

/**
 * The board app's calls (docs/boards-plan.md §12.2): plain fetch to /pnmt-board/api, the token in a header so that it isn't in a
 * proxy's log every poll (§5.1). Nothing of the admin's client: no session, no CSRF, no redirects to a login.
 */
export type BoardView = paths['/pnmt-board/api/view']['get']['responses'][200]['content']['application/json']
export type LoungeData = paths['/pnmt-board/api/lounge']['get']['responses'][200]['content']['application/json']

/** A refusal or a failure: the ProblemDetails code (board_not_found, board_disabled, board_network …), or "network". */
export class BoardError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    detail: string,
    readonly address: string | null = null,
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

export const boardApi = {
  view: (token: string) => get<BoardView>('/pnmt-board/api/view', token),
  lounge: (token: string) => get<LoungeData>('/pnmt-board/api/lounge', token),
}
