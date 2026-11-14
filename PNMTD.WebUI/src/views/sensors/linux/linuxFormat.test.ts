import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import {
  invalidLinesText,
  keyGlance,
  priorityTag,
  rightsRows,
  rightsSource,
  rootLogin,
  shortFingerprint,
  sshdNotes,
} from './linuxFormat'

describe('shortFingerprint', () => {
  it('shortens as PNMTD does in its messages', () => {
    expect(shortFingerprint('SHA256:Kq1f9hJ0x0mRk8xPZ3oWJ6gW5p0rPj5n8l2pHq0rS2A')).toBe('SHA256:Kq1f9hJ0x…rS2A')
  })

  it('keeps a short one, and nothing stays nothing', () => {
    expect(shortFingerprint('SHA256:abc')).toBe('SHA256:abc')
    expect(shortFingerprint(null)).toBe('')
  })
})

const sshd = (patch: Partial<S['SshdSettings']> = {}): S['SshdSettings'] => ({
  ports: [22],
  listenAddresses: ['0.0.0.0', '::'],
  permitRootLogin: 'prohibit-password',
  passwordAuthentication: false,
  kbdInteractiveAuthentication: false,
  pubkeyAuthentication: true,
  allowUsers: [],
  allowGroups: [],
  denyUsers: [],
  denyGroups: [],
  allowAgentForwarding: false,
  allowTcpForwarding: 'no',
  x11Forwarding: false,
  maxAuthTries: 6,
  logLevel: 'VERBOSE',
  matchBlocks: false,
  authorizedKeysCommand: null,
  trustedUserCaKeys: false,
  socketActivated: false,
  version: 'OpenSSH_9.2p1',
  ...patch,
})

describe('sshdNotes', () => {
  it('has only root key-only for a careful configuration', () => {
    expect(sshdNotes(sshd())).toEqual([{ text: 'root: key only', severity: 'secondary' }])
  })

  it('puts root with a password and password authentication first', () => {
    const notes = sshdNotes(sshd({ permitRootLogin: 'yes', passwordAuthentication: true, logLevel: 'INFO' }))
    expect(notes.slice(0, 2)).toEqual([
      { text: 'root may log in with a password', severity: 'danger' },
      { text: 'password authentication on', severity: 'warn' },
    ])
    expect(notes.at(-1)).toEqual({ text: "LogLevel INFO: failed keys aren't logged", severity: 'info' })
  })

  it('notes forwarding, MaxAuthTries above the default, and keys from elsewhere', () => {
    const texts = sshdNotes(
      sshd({
        allowTcpForwarding: 'yes',
        x11Forwarding: true,
        maxAuthTries: 10,
        authorizedKeysCommand: '/usr/bin/sss_ssh_authorizedkeys',
        trustedUserCaKeys: true,
      }),
    ).map((n) => n.text)
    expect(texts).toContain('TCP forwarding on')
    expect(texts).toContain('X11 forwarding on')
    expect(texts).toContain('MaxAuthTries 10')
    expect(texts).toContain('keys from AuthorizedKeysCommand')
    expect(texts).toContain('certificates of a CA (TrustedUserCAKeys)')
  })

  it('words PermitRootLogin', () => {
    expect(rootLogin('without-password').text).toBe('root: key only')
    expect(rootLogin('no').severity).toBe('success')
    expect(rootLogin(null).text).toBe('root login: not known')
  })
})

describe('priorityTag', () => {
  it('colours by priority', () => {
    expect(priorityTag(2)).toMatchObject({ label: 'crit', severity: 'danger', icon: expect.any(String) })
    expect(priorityTag(3)).toEqual({ label: 'err', severity: 'danger' })
    expect(priorityTag(4)).toEqual({ label: 'warning', severity: 'warn' })
  })
})

const key = (fingerprint: string, notes: string[] = []): S['AuthorizedKey'] => ({
  line: 1,
  type: 'ssh-ed25519',
  bits: 256,
  fingerprint,
  comment: null,
  options: [],
  key: 'AAAA',
  notes,
  protected: false,
})

describe('keyGlance', () => {
  it('counts keys, different ones, weak ones and root keys without from=', () => {
    const file = (account: string, keys: S['AuthorizedKey'][], problems: string[] = []): S['AuthorizedKeysFile'] => ({
      account,
      path: `/home/${account}/.ssh/authorized_keys`,
      target: null,
      exists: true,
      owner: account,
      group: account,
      mode: '0600',
      size: 100,
      modifiedAt: null,
      sha256: null,
      clusterWide: false,
      problems,
      keys,
      invalidLines: [4],
      removable: false,
      notRemovable: null,
    })
    const glance = keyGlance({
      sshd: {} as S['SshdKeySettings'],
      caKeys: [],
      totalKeys: 5,
      truncated: false,
      notes: [],
      files: [
        file('root', [
          key('SHA256:a', ["root's key without from="]),
          key('SHA256:b'),
          key('SHA256:c', ['RSA 1024 bits: refused by OpenSSH', "root's key without from="]),
        ]),
        file('deploy', [key('SHA256:d'), key('SHA256:a', ['RSA 2048 bits: weak'])], ['writable by group']),
      ],
    })
    expect(glance).toEqual({
      keys: 5,
      files: 2,
      distinct: 4,
      weak: 2,
      rootWithoutFrom: 2,
      problems: 1,
      invalidLines: 2,
    })
  })
})

describe('invalidLinesText', () => {
  it('names the lines by number', () => {
    expect(invalidLinesText([])).toBe('')
    expect(invalidLinesText([3])).toBe("Line 3 isn't a key")
    expect(invalidLinesText([3, 7, 9])).toBe("Lines 3, 7 and 9 aren't keys")
  })
})

const rights = (patch: Partial<S['AgentRightsSummary']> = {}): S['AgentRightsSummary'] => ({
  source: 'defaults',
  problem: null,
  keyRemoval: false,
  keyRemovalUsers: [],
  protectedKeys: 0,
  writablePaths: [],
  scripts: false,
  scriptRunAs: [],
  allowedScriptHashes: 0,
  maxScriptTimeoutMinutes: 60,
  sudoCommands: false,
  cronCommands: false,
  ...patch,
})

describe('rightsRows', () => {
  it("gives config.yaml's line for everything that is off", () => {
    expect(rightsRows(rights()).map((r) => [r.label, r.grant])).toEqual([
      ['Key removal', 'ssh_keys: { remove: true }'],
      ['Scripts', 'scripts: { enabled: true, run_as: [root] }'],
      ['sudo commands', 'collect: { sudo_commands: true }'],
      ['cron commands', 'collect: { cron_commands: true }'],
    ])
  })

  it('names for whom and as whom, and needs no line for what is on', () => {
    const rows = rightsRows(
      rights({
        source: 'config.yaml',
        keyRemoval: true,
        keyRemovalUsers: ['deploy', 'www'],
        protectedKeys: 1,
        scripts: true,
        scriptRunAs: ['root', 'deploy'],
      }),
    )
    expect(rows.find((r) => r.label === 'Key removal')).toMatchObject({ value: 'allowed for deploy, www', grant: null })
    expect(rows.find((r) => r.label === 'Protected keys')?.value).toBe('1 by fingerprint, never removed')
    expect(rows.find((r) => r.label === 'Scripts')).toMatchObject({ value: 'as root, deploy', grant: null })
    expect(rows.find((r) => r.label === 'Allowed scripts')?.value).toBe("any script of PNMT's library")
  })

  it('asks for an account when scripts are on without one', () => {
    expect(rightsRows(rights({ scripts: true })).find((r) => r.label === 'Scripts')).toMatchObject({
      on: false,
      grant: 'scripts: { run_as: [root] }',
    })
  })

  it('says where the rights come from', () => {
    expect(rightsSource({ source: 'refused' }).severity).toBe('danger')
    expect(rightsSource({ source: 'defaults' }).label).toBe('defaults: no config.yaml')
  })
})
