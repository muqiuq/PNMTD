import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import {
  acknowledgedDifferences,
  changeHints,
  draftErrors,
  draftFromRouter,
  mayAcknowledge,
  mikrotikRequestFromDraft,
  newMikrotikDraft,
  openDifferences,
  rulesHint,
  scriptHasRules,
} from './mikrotikDraft'

const router = (patch: Partial<S['MikrotikRouterDetail']> = {}): S['MikrotikRouterDetail'] =>
  ({
    mode: 'DirectSsh',
    setupState: 'Ready',
    address: '192.0.2.1',
    sshPort: 22,
    sourceAddress: '203.0.113.5',
    backupIntervalHours: 24,
    managementAccess: false,
    includeSecrets: false,
    diagnostics: false,
    scriptOutdated: false,
    scriptDifferences: [],
    warnings: [],
    scriptOptions: { firewall: true, removals: true },
    differences: [],
    ...patch,
  }) as S['MikrotikRouterDetail']

describe('the router draft', () => {
  it('starts as Direct SSH on port 22 with daily backups', () => {
    const d = newMikrotikDraft()
    expect(d.mode).toBe('DirectSsh')
    expect(d.sshPort).toBe(22)
    expect(d.backupIntervalHours).toBe(24)
  })

  it('needs an address for Direct SSH, and a port in range', () => {
    const errors = draftErrors({ ...newMikrotikDraft(), sshPort: 70000 })
    expect(errors['mikrotik.address']).toBeTruthy()
    expect(errors['mikrotik.sshPort']).toBeTruthy()
    expect(Object.values(draftErrors({ ...newMikrotikDraft(), address: 'gw.example.org' })).filter(Boolean)).toEqual([])
    expect(draftErrors({ ...newMikrotikDraft(), address: 'two words' })['mikrotik.address']).toBeTruthy()
  })

  it('sends trimmed values, and no address in a WireGuard mode', () => {
    const request = mikrotikRequestFromDraft({ ...newMikrotikDraft(), address: ' 192.0.2.1 ', sourceAddress: ' ' })
    expect(request.address).toBe('192.0.2.1')
    expect(request.sourceAddress).toBeNull()
    const wg = mikrotikRequestFromDraft({
      ...newMikrotikDraft(),
      mode: 'WireGuardSsh',
      address: 'x',
      managementAccess: true,
    })
    expect(wg.address).toBeNull()
    expect(wg.managementAccess).toBe(true)
    expect(mikrotikRequestFromDraft({ ...newMikrotikDraft(), managementAccess: true }).managementAccess).toBe(false)
  })

  it('says what a change means on the router', () => {
    const r = router()
    expect(changeHints(r, draftFromRouter(r))).toEqual([])
    expect(changeHints(r, { ...draftFromRouter(r), diagnostics: true })[0]).toContain('pnmt-read')
    expect(changeHints(r, { ...draftFromRouter(r), address: '192.0.2.2' })[0]).toContain('host key')
  })
})

describe("the join script's options and acknowledged differences", () => {
  const difference = (patch: Partial<S['MikrotikScriptDifference']> = {}): S['MikrotikScriptDifference'] => ({
    id: 'ssh-key:0123456789abcdef',
    text: "The router does not have PNMTD's current SSH key.",
    admin: false,
    acknowledgement: null,
    ...patch,
  })

  it('offers the firewall option only where the script has rules', () => {
    expect(scriptHasRules(router({ mode: 'WireGuardSsh' }))).toBe(true)
    expect(scriptHasRules(router({ mode: 'WireGuardOnly' }))).toBe(false)
    expect(scriptHasRules(router({ mode: 'WireGuardOnly', managementAccess: true }))).toBe(true)
    expect(scriptHasRules(router({ mode: 'DirectSsh', managementAccess: true }))).toBe(false)
    expect(rulesHint(router({ mode: 'WireGuardSsh', managementAccess: true }))).toBe(
      "Accepts PNMTD's SSH and custom tunnels' management ports on the interface pnmt, first in the filter list.",
    )
    expect(rulesHint(router({ mode: 'WireGuardOnly', managementAccess: true }))).toContain(
      "custom tunnels' management ports",
    )
  })

  it('splits the differences into open and acknowledged, and lets a User acknowledge only the non-Admin ones', () => {
    const open = difference()
    const policy = difference({ id: 'policy-test:fedcba9876543210', admin: true })
    const done = difference({
      id: 'mtu:00112233445566ff',
      acknowledgement: { byName: 'alice', reason: 'LTE', at: '2026-11-23T10:00:00Z' },
    })
    expect(openDifferences([open, policy, done]).map((d) => d.id)).toEqual([open.id, policy.id])
    expect(acknowledgedDifferences([open, policy, done]).map((d) => d.id)).toEqual([done.id])
    expect(mayAcknowledge(open, false)).toBe(true)
    expect(mayAcknowledge(policy, false)).toBe(false)
    expect(mayAcknowledge(policy, true)).toBe(true)
  })
})
