import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import {
  asRequest,
  canFetch,
  chooseOptions,
  chosenSummary,
  FOLLOW,
  fileState,
  fromText,
  isVersion,
  proposedName,
} from './versions'

const file = (over: Partial<S['AgentVersionFile']>): S['AgentVersionFile'] => ({
  name: 'pnmt-agent-linux-x64',
  platform: 'Linux',
  architecture: 'x64',
  state: 'NotFetched',
  inRelease: null,
  sha256: null,
  size: null,
  error: null,
  source: null,
  addedAt: null,
  addedBy: null,
  ...over,
})

const version = (over: Partial<S['AgentVersionItem']>): S['AgentVersionItem'] => ({
  version: '1.0.0',
  preRelease: false,
  onGitHub: false,
  releasedAt: null,
  releaseUrl: null,
  here: false,
  files: [],
  windowsAgents: 0,
  linuxAgents: 0,
  chosenForWindows: false,
  chosenForLinux: false,
  isPnmtVersion: false,
  newerThanPnmt: false,
  keptUntilDeleted: false,
  deleteRefused: null,
  ...over,
})

const chosen = (over: Partial<S['AgentChosenVersion']>): S['AgentChosenVersion'] => ({
  platform: 'Linux',
  version: '1.8.0',
  followsPnmt: true,
  newerThanPnmt: false,
  preRelease: false,
  agents: 15,
  running: 12,
  older: 3,
  newer: 0,
  ...over,
})

describe('the versions tab', () => {
  it('offers PNMT’s version first, then what can be chosen for the platform, with what to know', () => {
    const list = {
      pnmtVersion: '1.8.0',
      versions: [
        version({ version: '1.9.0', newerThanPnmt: true, files: [file({ state: 'Here' })] }),
        version({ version: '1.8.0', isPnmtVersion: true, onGitHub: true }),
        version({ version: '1.8.0-rc1', preRelease: true, onGitHub: true }),
        version({
          version: '1.7.0',
          files: [file({ platform: 'Windows', name: 'pnmt-sensor-x64.exe', state: 'Here' })],
        }),
      ],
    } as S['AgentVersionList']
    expect(chooseOptions(list, 'Linux').map((o) => [o.value, o.label])).toEqual([
      [FOLLOW, "PNMT's version (1.8.0)"],
      ['1.9.0', '1.9.0 (newer than PNMT)'],
      ['1.8.0', "1.8.0 (PNMT's, fixed, fetched when chosen)"],
      ['1.8.0-rc1', '1.8.0-rc1 (pre-release, fetched when chosen)'],
    ])
    expect(chooseOptions(list, 'Windows').map((o) => o.value)).toEqual([FOLLOW, '1.8.0', '1.8.0-rc1', '1.7.0'])
  })

  it('says how many agents run the chosen version', () => {
    expect(chosenSummary(chosen({}))).toBe('12 of 15 Linux agents run 1.8.0 · 3 older')
    expect(chosenSummary(chosen({ older: 0, newer: 2, running: 13 }))).toBe('13 of 15 Linux agents run 1.8.0 · 2 newer')
    expect(chosenSummary(chosen({ agents: 0, platform: 'Windows' }))).toBe('No Windows agents yet.')
  })

  it('says where a version came from', () => {
    expect(fromText(version({ onGitHub: true }))).toBe('GitHub')
    expect(fromText(version({ files: [file({ state: 'Here', source: 'Upload', addedBy: 'alice' })] }))).toBe(
      'uploaded by alice',
    )
    expect(fromText(version({ files: [file({ state: 'Here', source: 'GitHub' })] }))).toBe('fetched from GitHub')
    expect(fromText(version({ files: [file({ state: 'Here', source: 'Folder' })] }))).toBe(
      'in the download folder itself',
    )
    expect(fromText(version({}))).toBe('only chosen, nothing here')
  })

  it('names a file’s state, and fetches only what the release has', () => {
    expect(fileState(file({ state: 'Here', source: 'Upload' })).label).toBe('uploaded')
    expect(fileState(file({ inRelease: false })).label).toBe('not in the release')
    expect(fileState(file({ state: 'Failed' })).severity).toBe('danger')
    expect(canFetch(version({ onGitHub: true, files: [file({ inRelease: true })] }))).toBe(true)
    expect(canFetch(version({ onGitHub: true, files: [file({ inRelease: false })] }))).toBe(false)
    expect(canFetch(version({ files: [file({})] }))).toBe(false)
  })

  it('takes versions and file names as PNMT does', () => {
    expect(isVersion('1.8.0')).toBe(true)
    expect(isVersion('1.8.0-rc1')).toBe(true)
    expect(isVersion('1.8')).toBe(false)
    expect(isVersion('../1.8.0')).toBe(false)
    expect(proposedName('pnmt-agent-linux-arm64')).toBe('pnmt-agent-linux-arm64')
    expect(proposedName('pnmt-agent (1)')).toBeNull()
    expect(asRequest(FOLLOW)).toBeNull()
    expect(asRequest('1.8.0')).toBe('1.8.0')
    expect(isVersion(FOLLOW)).toBe(false)
  })
})
