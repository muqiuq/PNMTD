import { describe, expect, it } from 'vitest'
import type { Role, SessionFlags } from '@/api/auth'
import { decide, nextAfterSignIn, safeReturnUrl } from './guard'

const ranks: Record<Role, number> = { Viewer: 1, User: 2, Admin: 3 }
const flags = (f: Partial<SessionFlags> = {}): SessionFlags => ({
  mustChangePassword: false,
  totpSetupRequired: false,
  totpSetupSuggested: false,
  ...f,
})
const as = (role: Role | null, f?: Partial<SessionFlags>) => ({
  signedIn: role !== null,
  flags: role ? flags(f) : null,
  can: (min: Role) => role !== null && ranks[role] >= ranks[min],
})
const to = (name: string, fullPath: string, extra: { public?: boolean; role?: Role } = {}) => ({
  name,
  fullPath,
  ...extra,
})

describe('the route guard', () => {
  it('sends a signed-out user to the login, with the page as returnUrl', () => {
    expect(decide(to('host', '/hosts/1'), as(null))).toEqual({ name: 'login', query: { returnUrl: '/hosts/1' } })
    expect(decide(to('login', '/login', { public: true }), as(null))).toBe(true)
  })

  it('sends a signed-in user away from the login', () => {
    expect(decide(to('login', '/login', { public: true }), as('Viewer'))).toEqual({ name: 'dashboard' })
  })

  it('holds everything until the password is changed, then until 2FA is set up', () => {
    expect(decide(to('hosts', '/hosts'), as('User', { mustChangePassword: true, totpSetupRequired: true }))).toEqual({
      name: 'change-password',
      query: { returnUrl: '/hosts' },
    })
    expect(decide(to('setup-2fa', '/account/setup-2fa'), as('User', { mustChangePassword: true }))).toMatchObject({
      name: 'change-password',
    })
    expect(decide(to('hosts', '/hosts'), as('User', { totpSetupRequired: true }))).toEqual({
      name: 'setup-2fa',
      query: { returnUrl: '/hosts' },
    })
    expect(decide(to('setup-2fa', '/account/setup-2fa'), as('User', { totpSetupRequired: true }))).toBe(true)
    expect(decide(to('change-password', '/change-password'), as('User'))).toEqual({ name: 'account' })
  })

  it('keeps a role away from pages above it', () => {
    expect(decide(to('users', '/admin/users', { role: 'Admin' }), as('User'))).toEqual({ name: 'dashboard' })
    expect(decide(to('users', '/admin/users', { role: 'Admin' }), as('Admin'))).toBe(true)
    expect(decide(to('mail-log', '/mail-log', { role: 'User' }), as('Viewer'))).toEqual({ name: 'dashboard' })
  })

  it('accepts only return URLs inside the app', () => {
    expect(safeReturnUrl('/hosts/1?x=2')).toBe('/hosts/1?x=2')
    for (const bad of [
      '//evil.example',
      'https://evil.example',
      '/\\evil',
      'hosts',
      '/login',
      '/login/2fa',
      42,
      null,
    ]) {
      expect(safeReturnUrl(bad)).toBeNull()
    }
  })

  it('orders what follows a sign-in: password, required 2FA, the prompt, the page', () => {
    expect(nextAfterSignIn(flags({ mustChangePassword: true, totpSetupSuggested: true }), '/x')).toEqual({
      name: 'change-password',
      query: { returnUrl: '/x' },
    })
    expect(nextAfterSignIn(flags({ totpSetupRequired: true }), null)).toEqual({ name: 'setup-2fa', query: {} })
    expect(nextAfterSignIn(flags({ totpSetupSuggested: true }), '/x')).toEqual({
      name: 'setup-2fa',
      query: { returnUrl: '/x', prompt: '1' },
    })
    expect(nextAfterSignIn(flags(), '/x')).toBe('/x')
    expect(nextAfterSignIn(flags(), null)).toBe('/')
  })
})
