import type { Role, SessionFlags } from '@/api/auth'

/** What the guard needs to know about the session. */
export interface GuardState {
  signedIn: boolean
  flags: SessionFlags | null
  can: (role: Role) => boolean
}

/** What the guard needs to know about the target route. */
export interface GuardTarget {
  name: string | symbol | null | undefined
  fullPath: string
  public?: boolean
  role?: Role
}

export type GuardResult = true | { name: string; query?: Record<string, string> }

/**
 * A return URL that stays inside the app (§4.5): a path of the router (which lives under
 * /pnmt-admin/), never another origin ("//evil", "/\\evil", "https://…").
 */
export function safeReturnUrl(value: unknown): string | null {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//') || value.includes('\\')) {
    return null
  }
  if (/^\/(login|change-password|account\/setup-2fa)(\/|\?|$)/.test(value)) {
    return null
  }
  return value
}

/**
 * Where a navigation may go (§3.6 is the server's protection; this only spares the user dead ends):
 * signed out → login; a password to change → that screen first; 2FA required and not set up → its
 * setup; a role too low → the dashboard.
 */
export function decide(to: GuardTarget, state: GuardState): GuardResult {
  const returnUrl = { returnUrl: to.fullPath }
  if (to.public) {
    return state.signedIn && to.name === 'login' ? { name: 'dashboard' } : true
  }
  if (!state.signedIn) {
    return { name: 'login', query: safeReturnUrl(to.fullPath) ? returnUrl : {} }
  }
  if (state.flags?.mustChangePassword) {
    return to.name === 'change-password'
      ? true
      : { name: 'change-password', query: safeReturnUrl(to.fullPath) ? returnUrl : {} }
  }
  if (to.name === 'change-password') {
    return { name: 'account' }
  }
  if (state.flags?.totpSetupRequired) {
    return to.name === 'setup-2fa' ? true : { name: 'setup-2fa', query: safeReturnUrl(to.fullPath) ? returnUrl : {} }
  }
  if (to.role && !state.can(to.role)) {
    return { name: 'dashboard' }
  }
  return true
}

/**
 * After a complete sign-in or a forced password change (§4.5, §4.6): the password first, then a
 * required 2FA setup, then the prompt that suggests it (once per sign-in), then where the user wanted to go.
 */
export function nextAfterSignIn(
  flags: SessionFlags,
  returnUrl: string | null,
  prompt = true,
): { name: string; query: Record<string, string> } | string {
  const query: Record<string, string> = returnUrl ? { returnUrl } : {}
  if (flags.mustChangePassword) {
    return { name: 'change-password', query }
  }
  if (flags.totpSetupRequired) {
    return { name: 'setup-2fa', query }
  }
  if (flags.totpSetupSuggested && prompt) {
    return { name: 'setup-2fa', query: { ...query, prompt: '1' } }
  }
  return returnUrl ?? '/'
}
