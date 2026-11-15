import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import { installLine, joinLine, pnmtAvailability, sudoWrap } from './installLine'

const hash = '3f2a'.padEnd(64, '0')
const pnmt = 'https://pnmt.example.com/agent/v1/download/pnmt-agent-linux-x64'
const gitHub = 'https://github.com/muqiuq/PNMTD/releases/download/sensor-1.3.0/pnmt-agent-linux-x64'
const line = (tool: string, url: string) =>
  `${tool} /tmp/pnmt-agent ${url} && echo "${hash}  /tmp/pnmt-agent" | sha256sum -c --quiet && install -m 0755 /tmp/pnmt-agent /usr/local/sbin/pnmt-agent && rm /tmp/pnmt-agent && pnmt-agent join --url https://pnmt.example.com --token xxxx-xxxx`

const file = (state: S['AgentDownloadState'], error: string | null = null): S['AgentDownloadFile'] => ({
  name: 'pnmt-agent-linux-x64',
  platform: 'Linux',
  architecture: 'x64',
  pnmtUrl: pnmt,
  gitHubUrl: gitHub,
  sha256: hash,
  size: 40_470_683,
  state,
  error,
  fetchedAt: null,
  version: '1.3.0',
})

const downloads = (state: S['AgentDownloadState'] = 'Here', error: string | null = null) => ({
  serveDownloads: true,
  tokenPlaceholder: 'xxxx-xxxx',
  assets: [file(state, error)],
  linux: [
    {
      architecture: 'x64',
      fileName: 'pnmt-agent-linux-x64',
      sha256: hash,
      pnmt: line('wget -qO', pnmt),
      pnmtCurl: line('curl -fsSLo', pnmt),
      gitHub: line('wget -qO', gitHub),
      gitHubCurl: line('curl -fsSLo', gitHub),
    },
  ],
})

describe('installLine', () => {
  it('picks the source and the tool, and fills in the token', () => {
    const d = downloads()
    const x = installLine(d, { source: 'PNMT', architecture: 'x64', tool: 'wget', user: 'root' }, 'k7mq-3xtp')!
    expect(x.startsWith(`wget -qO /tmp/pnmt-agent ${pnmt} && echo "${hash}  /tmp/pnmt-agent"`)).toBe(true)
    expect(x.endsWith('--token k7mq-3xtp')).toBe(true)
    const y = installLine(d, { source: 'GitHub', architecture: 'x64', tool: 'curl', user: 'root' }, 'k7mq-3xtp')!
    expect(y.startsWith(`curl -fsSLo /tmp/pnmt-agent ${gitHub}`)).toBe(true)
    // The same hash from either source.
    expect(y).toContain(hash)
  })

  it('keeps the placeholder without a token', () => {
    expect(installLine(downloads(), { source: 'PNMT', architecture: 'x64', tool: 'wget', user: 'root' })).toContain(
      '--token xxxx-xxxx',
    )
  })

  it('runs it all as root through sudo', () => {
    const x = installLine(
      downloads(),
      { source: 'PNMT', architecture: 'x64', tool: 'wget', user: 'sudo' },
      'k7mq-3xtp',
    )!
    expect(x.startsWith("sudo sh -c 'wget -qO")).toBe(true)
    expect(x.endsWith("--token k7mq-3xtp'")).toBe(true)
  })

  it("is null for an architecture or a hash it doesn't know", () => {
    expect(installLine(downloads(), { source: 'PNMT', architecture: 'arm64', tool: 'wget', user: 'root' })).toBeNull()
    const d = downloads()
    d.linux[0]!.gitHub = null as unknown as string
    expect(installLine(d, { source: 'GitHub', architecture: 'x64', tool: 'wget', user: 'root' })).toBeNull()
  })
})

describe('sudoWrap', () => {
  it('escapes single quotes', () => {
    expect(sudoWrap("echo 'a'")).toBe(`sudo sh -c 'echo '\\''a'\\'''`)
  })
})

describe('joinLine', () => {
  it('adds --allow-http for plain http only', () => {
    expect(joinLine('https://pnmt.example.com', 'k7mq-3xtp')).toBe(
      'pnmt-agent join --url https://pnmt.example.com --token k7mq-3xtp',
    )
    expect(joinLine('http://10.0.0.5:7327', 'k7mq-3xtp')).toBe(
      'pnmt-agent join --url http://10.0.0.5:7327 --token k7mq-3xtp --allow-http',
    )
  })
})

describe('pnmtAvailability', () => {
  it("offers PNMT's file when it is here or being fetched", () => {
    expect(pnmtAvailability(downloads('Here'), 'x64')).toEqual({ available: true, note: null })
    expect(pnmtAvailability(downloads('Fetching'), 'x64')).toMatchObject({ available: true })
  })

  it('says why not, and so GitHub is preselected', () => {
    expect(pnmtAvailability(downloads('Failed', 'Read-only file system'), 'x64')).toMatchObject({
      available: false,
      note: expect.stringContaining('Read-only file system'),
    })
    expect(pnmtAvailability(downloads('NotFetched'), 'x64').available).toBe(false)
    expect(pnmtAvailability({ ...downloads(), serveDownloads: false }, 'x64').available).toBe(false)
    expect(pnmtAvailability(downloads(), 'arm64').available).toBe(false)
  })
})
