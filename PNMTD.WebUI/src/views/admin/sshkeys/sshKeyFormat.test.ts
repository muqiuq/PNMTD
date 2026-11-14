import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import {
  canRegister,
  confirmMatches,
  keyType,
  namesByIndex,
  noteLabel,
  parseLineState,
  removalStateSeverity,
  targetsOf,
} from './sshKeyFormat'

const line = (patch: Partial<S['SshKeyParseLine']>): S['SshKeyParseLine'] => ({
  line: 1,
  index: 0,
  fingerprint: 'SHA256:a',
  type: 'ssh-ed25519',
  bits: 256,
  comment: 'philipp@laptop',
  weakness: null,
  suggestedName: 'philipp@laptop',
  registered: null,
  duplicate: false,
  servers: 0,
  problem: 'None',
  problemText: null,
  ...patch,
})

describe('keyType', () => {
  it('shortens the type and adds the bits', () => {
    expect(keyType('ssh-ed25519', 256)).toBe('ed25519 256')
    expect(keyType('ecdsa-sha2-nistp256', 256)).toBe('ecdsa-nistp256 256')
    expect(keyType('sk-ssh-ed25519@openssh.com', null)).toBe('sk-ssh-ed25519')
  })
})

describe('parseLineState', () => {
  it('passes a new key and skips a blank line', () => {
    expect(parseLineState(line({}))).toEqual({ ok: true, label: null, severity: 'success' })
    expect(parseLineState(line({ problem: 'Blank', index: null }))).toBeNull()
  })

  it('refuses a private key, a registered key, a duplicate and a line that is no key', () => {
    expect(parseLineState(line({ problem: 'PrivateKey', index: null }))).toMatchObject({
      ok: false,
      severity: 'danger',
    })
    expect(parseLineState(line({ registered: { id: 'k', name: 'Philipp, laptop' } }))?.label).toBe(
      'registered: Philipp, laptop',
    )
    expect(parseLineState(line({ duplicate: true }))?.ok).toBe(false)
    expect(parseLineState(line({ problem: 'Unreadable', problemText: 'Not a public key.', index: null }))?.label).toBe(
      'Not a public key.',
    )
  })
})

describe('canRegister', () => {
  it('needs keys, nothing refused and no private key', () => {
    expect(canRegister({ lines: [], keys: 2, refused: 0, privateKey: false })).toBe(true)
    expect(canRegister({ lines: [], keys: 0, refused: 0, privateKey: false })).toBe(false)
    expect(canRegister({ lines: [], keys: 2, refused: 1, privateKey: false })).toBe(false)
    expect(canRegister({ lines: [], keys: 1, refused: 0, privateKey: true })).toBe(false)
    expect(canRegister(null)).toBe(false)
  })
})

describe('namesByIndex', () => {
  it("orders the names by the keys' index, typed first, then the suggestion", () => {
    const lines = [
      line({ line: 3, index: 1, suggestedName: 'ci@runner' }),
      line({ line: 1, index: 0, suggestedName: 'philipp@laptop' }),
      line({ line: 2, index: null, problem: 'Blank' }),
      line({ line: 4, index: 2, suggestedName: null, type: 'ssh-rsa', bits: 4096 }),
    ]
    expect(namesByIndex(lines, { 1: 'Philipp, laptop' })).toEqual(['Philipp, laptop', 'ci@runner', 'rsa 4096 (line 4)'])
  })
})

describe('confirmMatches', () => {
  it('wants exactly the number', () => {
    expect(confirmMatches('3', 3)).toBe(true)
    expect(confirmMatches(' 3 ', 3)).toBe(true)
    expect(confirmMatches(3, 3)).toBe(true)
    expect(confirmMatches('2', 3)).toBe(false)
    expect(confirmMatches('', 0)).toBe(false)
  })
})

describe('labels', () => {
  it('words the notes and colours the states', () => {
    expect(noteLabel('unused180')).toBe('unused for 180 days')
    expect(noteLabel('other')).toBe('other')
    expect(removalStateSeverity('Removed')).toBe('success')
    expect(removalStateSeverity('Failed')).toBe('danger')
  })

  it('makes removal targets of authorizations', () => {
    expect(
      targetsOf([
        { server: { id: 's', name: 'web-01' }, account: 'root', path: '/root/.ssh/authorized_keys', fingerprint: 'f' },
      ]),
    ).toEqual([{ sensorId: 's', account: 'root', path: '/root/.ssh/authorized_keys', fingerprint: 'f' }])
  })
})
