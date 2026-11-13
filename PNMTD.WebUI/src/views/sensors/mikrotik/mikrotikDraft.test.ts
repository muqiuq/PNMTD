import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import { changeHints, draftErrors, draftFromRouter, mikrotikRequestFromDraft, newMikrotikDraft } from './mikrotikDraft'

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
