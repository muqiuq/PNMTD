import { describe, expect, it } from 'vitest'
import type { SessionFlags } from '@/api/auth'
import { holds, type Area, type Level, type Right } from '@/api/rights'
import { decide, nextAfterSignIn, safeReturnUrl } from './guard'

type Who = 'admin' | Partial<Record<Area, Level>>
// The groups the update makes from the roles (docs/audit-redmine-zabbix-plan.md §5.3), and an admin.
const viewers: Who = { Monitoring: 'View', Locations: 'View', KnowledgeBase: 'View' }
const operators: Who = {
  Monitoring: 'Edit',
  Mail: 'Read',
  Decisions: 'Decide',
  Locations: 'Edit',
  KnowledgeBase: 'Edit',
}
const flags = (f: Partial<SessionFlags> = {}): SessionFlags => ({
  mustChangePassword: false,
  totpSetupRequired: false,
  totpSetupSuggested: false,
  ...f,
})
const as = (who: Who | null, f?: Partial<SessionFlags>) => ({
  signedIn: who !== null,
  flags: who ? flags(f) : null,
  allows: (right: Right) =>
    who !== null && (who === 'admin' || (right !== 'admin' && holds(false, who, right[0], right[1]))),
})
const to = (name: string, fullPath: string, extra: { public?: boolean; right?: Right } = {}) => ({
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
    expect(decide(to('login', '/login', { public: true }), as(viewers))).toEqual({ name: 'dashboard' })
  })

  it('holds everything until the password is changed, then until 2FA is set up', () => {
    expect(decide(to('hosts', '/hosts'), as(operators, { mustChangePassword: true, totpSetupRequired: true }))).toEqual(
      {
        name: 'change-password',
        query: { returnUrl: '/hosts' },
      },
    )
    expect(decide(to('setup-2fa', '/account/setup-2fa'), as(operators, { mustChangePassword: true }))).toMatchObject({
      name: 'change-password',
    })
    expect(decide(to('hosts', '/hosts'), as(operators, { totpSetupRequired: true }))).toEqual({
      name: 'setup-2fa',
      query: { returnUrl: '/hosts' },
    })
    expect(decide(to('setup-2fa', '/account/setup-2fa'), as(operators, { totpSetupRequired: true }))).toBe(true)
    expect(decide(to('change-password', '/change-password'), as(operators))).toEqual({ name: 'account' })
  })

  it('keeps a user away from pages they lack the right for', () => {
    expect(decide(to('users', '/admin/users', { right: 'admin' }), as(operators))).toEqual({ name: 'dashboard' })
    expect(decide(to('users', '/admin/users', { right: 'admin' }), as('admin'))).toBe(true)
    expect(decide(to('mail-log', '/mail-log', { right: ['Mail', 'Read'] }), as(viewers))).toEqual({ name: 'dashboard' })
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
