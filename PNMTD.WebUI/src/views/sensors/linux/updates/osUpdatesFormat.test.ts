import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import {
  acknowledgeDays,
  assessedByTag,
  cleanLog,
  countChips,
  coveredByTrackers,
  defaultAcknowledgeDays,
  diskText,
  downloadText,
  installBlocked,
  isShortenedMarker,
  kernelText,
  listsFooter,
  modeBlocked,
  modePackages,
  needsRestartText,
  osRightsRows,
  packagesText,
  pendingText,
  percentileText,
  planLine,
  planNotes,
  refreshBlocked,
  runCommand,
  runDuration,
  runStateSeverity,
  runWhat,
  safeLink,
  searchLines,
  startMode,
  summaryChips,
  triggerLabel,
} from './osUpdatesFormat'

const plan = (patch: Partial<S['AptPlan']> = {}): S['AptPlan'] => ({
  upgrade: 3,
  install: 1,
  remove: 0,
  keptBack: 0,
  downloadBytes: 1024 * 1024,
  diskBytes: 2048,
  removals: [],
  removalsHash: 'h1',
  ...patch,
})

const counts = (patch: Partial<S['OsUpdateCounts']> = {}): S['OsUpdateCounts'] => ({
  upgraded: 0,
  installed: 0,
  removed: 0,
  warnings: 0,
  errors: 0,
  servicesToRestart: 0,
  rebootRequired: false,
  repositoriesReached: 0,
  repositoriesFailed: 0,
  configFilesKept: 0,
  ...patch,
})

const run = (patch: Partial<S['OsUpdateRunItem']> = {}): S['OsUpdateRunItem'] => ({
  id: 'r1',
  kind: 'Upgrade',
  mode: 'Upgrade',
  trigger: 'Person',
  requestedByName: 'Philipp Albrecht',
  requestedAt: '2026-11-20T10:00:00Z',
  startedAt: '2026-11-20T10:00:00Z',
  finishedAt: '2026-11-20T10:12:00Z',
  state: 'Succeeded',
  exitCode: 0,
  message: null,
  command: null,
  logBytes: 100,
  logTruncated: false,
  logIncomplete: false,
  takingLong: false,
  dismissed: false,
  counts: counts(),
  waiting: false,
  ...patch,
})

const pkg = (name: string, patch: Partial<S['OsPendingPackageView']> = {}): S['OsPendingPackageView'] => ({
  name,
  arch: 'amd64',
  installed: '1.0',
  candidate: '1.1',
  source: name,
  sourceVersion: '1.1',
  origin: 'Debian:12.8/stable',
  security: false,
  inUpgrade: true,
  phased: false,
  firstSeenAt: '2026-11-20T10:00:00Z',
  cves: [],
  rating: null,
  hasChangelog: false,
  ...patch,
})

const view = (patch: Partial<S['OsUpdatesView']> = {}): S['OsUpdatesView'] => ({
  sensorId: 's1',
  hostname: 'web-01',
  enabled: true,
  checkEnabled: true,
  connected: true,
  capable: true,
  unavailable: null,
  rights: { update: true, upgrade: true, fullUpgrade: true, changelogs: true },
  rightsRefused: false,
  distribution: 'debian:bookworm',
  proxmox: false,
  listsUpdatedAt: '2026-11-21T04:37:00Z',
  nextUpdateAt: '2026-11-22T04:37:00Z',
  dailyUpdate: true,
  windowStart: '04:00',
  windowMinutes: 120,
  pending: 4,
  security: 1,
  highRisk: 0,
  highRiskAcknowledged: 0,
  important: 0,
  dpkgInterrupted: false,
  rebootRequired: false,
  rebootPackages: [],
  needsRestart: null,
  warnings: [],
  phasedDeferred: [],
  upgrade: plan(),
  fullUpgrade: plan({ remove: 1, removals: ['old-lib'], removalsHash: 'h2' }),
  defaultMode: 'Upgrade',
  current: null,
  lastUpdate: null,
  lastUpgrade: null,
  packages: [],
  findings: [],
  settings: { enabled: true, highRiskEvents: true, updateFailedEvents: true },
  updateFailedMessage: null,
  highRiskMessage: null,
  upgradeWarnMinutes: 60,
  acknowledgeMaxDays: 30,
  ...patch,
})

describe('runs', () => {
  it('names what a run did and the command behind it', () => {
    expect(runWhat({ kind: 'Update', mode: null })).toBe('update')
    expect(runWhat({ kind: 'Upgrade', mode: 'Upgrade' })).toBe('upgrade')
    expect(runWhat({ kind: 'Upgrade', mode: 'FullUpgrade' })).toBe('full upgrade')
    expect(runCommand({ kind: 'Upgrade', mode: 'FullUpgrade' })).toBe('apt-get full-upgrade')
    expect(runCommand({ kind: 'Update', mode: null })).toBe('apt-get update')
  })

  it('names the trigger: scheduled, the person, on the server, outside PNMT (§10.1 5)', () => {
    expect(triggerLabel(run({ trigger: 'Schedule', requestedByName: null }))).toBe('scheduled')
    expect(triggerLabel(run())).toBe('Philipp Albrecht')
    expect(triggerLabel(run({ requestedByName: null }))).toBe('a person')
    expect(triggerLabel(run({ trigger: 'Server' }))).toBe('on the server')
    expect(
      triggerLabel(run({ trigger: 'External', requestedByName: null, command: '/usr/bin/unattended-upgrade' })),
    ).toBe('outside PNMT: unattended-upgrades')
    expect(triggerLabel(run({ trigger: 'External', requestedByName: 'philipp', command: 'apt upgrade' }))).toBe(
      'outside PNMT: apt by philipp',
    )
    expect(triggerLabel(run({ trigger: 'External', requestedByName: null }))).toBe('outside PNMT')
  })

  it('measures a finished run, a running one up to now, and none before it started', () => {
    expect(runDuration(run(), 0)).toBe('12 min')
    const now = Date.parse('2026-11-20T10:05:00Z')
    expect(runDuration(run({ state: 'Running', finishedAt: null }), now)).toBe('5 min')
    expect(runDuration(run({ startedAt: null, finishedAt: null, state: 'Requested' }), now)).toBe('–')
  })

  it('colours the states', () => {
    expect(runStateSeverity('Succeeded')).toBe('success')
    expect(runStateSeverity('Warnings')).toBe('warn')
    expect(runStateSeverity('Failed')).toBe('danger')
    expect(runStateSeverity('Running')).toBe('info')
    expect(runStateSeverity('Lost')).toBe('secondary')
  })

  it('shows only the counts that happened, in their order', () => {
    expect(countChips(counts())).toEqual([])
    expect(
      countChips(counts({ upgraded: 23, installed: 1, removed: 1, warnings: 2, rebootRequired: true })).map(
        (c) => c.label,
      ),
    ).toEqual(['23 upgraded', '1 installed', '1 removed', '2 warnings', 'reboot required'])
    expect(countChips(counts({ errors: 1, repositoriesFailed: 1, servicesToRestart: 3 })).map((c) => c.label)).toEqual([
      '1 error',
      '1 repository failed',
      '3 services to restart',
    ])
  })
})

describe('why a button is disabled (§10.1 1)', () => {
  it('lets both run on a connected server that allows them', () => {
    expect(refreshBlocked(view())).toBeNull()
    expect(installBlocked(view())).toBeNull()
  })

  it("says the server's own reason first", () => {
    const offline = view({ unavailable: 'The server is offline.', connected: false })
    expect(refreshBlocked(offline)).toBe('The server is offline.')
    expect(installBlocked(offline)).toBe('The server is offline.')
  })

  it('names the line of config.yaml that refuses', () => {
    const v = view({ rights: { update: false, upgrade: false, fullUpgrade: true, changelogs: true } })
    expect(refreshBlocked(v)).toBe(
      'Not allowed on the server: os_updates.update: false in /etc/pnmt-agent/config.yaml.',
    )
    expect(modeBlocked(v, 'Upgrade')).toBe(
      'Not allowed on the server: os_updates.upgrade: false in /etc/pnmt-agent/config.yaml.',
    )
    expect(modeBlocked(v, 'FullUpgrade')).toBeNull()
    expect(installBlocked(v)).toBeNull()
    expect(
      installBlocked(view({ rights: { update: true, upgrade: false, fullUpgrade: false, changelogs: true } })),
    ).toBe(
      'Not allowed on the server: os_updates.upgrade: false and os_updates.full_upgrade: false in /etc/pnmt-agent/config.yaml.',
    )
  })

  it("lists config.yaml's os_updates block, with the line that allows what is off", () => {
    const rows = osRightsRows({ update: true, upgrade: true, fullUpgrade: false, changelogs: true })
    expect(rows.map((r) => r.on)).toEqual([true, true, false, true])
    expect(rows.map((r) => r.grant)).toEqual([null, null, 'os_updates: { full_upgrade: true }', null])
  })

  it('refuses while dpkg is interrupted or a run is in progress', () => {
    expect(installBlocked(view({ dpkgInterrupted: true }))).toBe(
      'dpkg was interrupted: run dpkg --configure -a on the server.',
    )
    expect(installBlocked(view({ current: run({ state: 'Running' }) }))).toBe('A run is in progress.')
    expect(refreshBlocked(view({ current: run({ state: 'Running' }) }))).toBe('A run is in progress.')
  })

  it('has nothing to install when no mode has work, and says so', () => {
    const empty = view({
      pending: 0,
      upgrade: plan({ upgrade: 0, install: 0 }),
      fullUpgrade: plan({ upgrade: 0, install: 0 }),
    })
    expect(installBlocked(empty)).toBe('No updates pending.')
    expect(modeBlocked(view({ upgrade: plan({ upgrade: 0, install: 0, keptBack: 2 }) }), 'Upgrade')).toBe(
      'Nothing to install this way: 2 kept back.',
    )
    expect(modeBlocked(view({ upgrade: null }), 'Upgrade')).toMatch(/^No plan from the server yet/)
  })

  it("starts with the server's default mode, or the other when only it works (D3)", () => {
    expect(startMode(view())).toBe('Upgrade')
    expect(startMode(view({ defaultMode: 'FullUpgrade', proxmox: true }))).toBe('FullUpgrade')
    expect(startMode(view({ rights: { update: true, upgrade: false, fullUpgrade: true, changelogs: true } }))).toBe(
      'FullUpgrade',
    )
  })
})

describe('the plan (§10.2)', () => {
  it('counts and sizes', () => {
    expect(planLine(plan())).toBe('3 upgraded · 1 new')
    expect(planLine(plan({ keptBack: 2 }))).toBe('3 upgraded · 1 new · 2 kept back')
    expect(downloadText(null)).toBe('unknown')
    expect(downloadText(1024 * 1024)).toBe('1.00 MiB')
    expect(diskText(-2048)).toBe('frees 2.00 KiB')
    expect(diskText(2048)).toBe('2.00 KiB more')
  })

  it('leaves out what only a full upgrade installs', () => {
    const packages = [pkg('a'), pkg('b', { inUpgrade: false })]
    expect(modePackages(packages, 'Upgrade').map((p) => p.name)).toEqual(['a'])
    expect(modePackages(packages, 'FullUpgrade').map((p) => p.name)).toEqual(['a', 'b'])
  })

  it('notes a kernel, openssh-server, and Proxmox VE without a full upgrade', () => {
    expect(planNotes([pkg('curl')], 'Upgrade', false)).toEqual([])
    const notes = planNotes([pkg('linux-image-amd64'), pkg('openssh-server')], 'Upgrade', true)
    expect(notes).toHaveLength(3)
    expect(notes[0]).toMatch(/^A new kernel/)
    expect(notes[1]).toMatch(/^openssh-server is upgraded/)
    expect(notes[2]).toMatch(/^Proxmox VE needs a full upgrade/)
    expect(planNotes([pkg('proxmox-kernel-6.8')], 'FullUpgrade', true)).toHaveLength(1)
  })
})

describe('the state', () => {
  it("words needrestart's report and a pending kernel (D4)", () => {
    expect(needsRestartText(null)).toBeNull()
    const n = {
      services: ['nginx', 'php8.2-fpm', 'ssh'],
      kernelPending: true,
      runningKernel: '6.1.0-26',
      expectedKernel: '6.1.0-28',
    }
    expect(needsRestartText(n)).toBe('3 services need a restart: nginx, php8.2-fpm, ssh')
    expect(needsRestartText({ ...n, services: ['ssh'] })).toBe('1 service needs a restart: ssh')
    expect(kernelText(n)).toBe('the running kernel is 6.1.0-26, 6.1.0-28 is installed')
    expect(kernelText({ ...n, kernelPending: false })).toBeNull()
  })

  it('counts the pending updates', () => {
    expect(pendingText({ pending: 23, security: 5 })).toBe('23 updates pending · 5 security')
    expect(pendingText({ pending: 1, security: 0 })).toBe('1 update pending')
  })
})

describe('acknowledging (§6.5)', () => {
  it('offers 1, 7 or 30 days, never more than the maximum', () => {
    expect(acknowledgeDays(30)).toEqual([1, 7, 30])
    expect(acknowledgeDays(14)).toEqual([1, 7, 14])
    expect(acknowledgeDays(7)).toEqual([1, 7])
    expect(acknowledgeDays(3)).toEqual([1, 3])
    expect(acknowledgeDays(1)).toEqual([1])
    expect(acknowledgeDays(365)).toEqual([1, 7, 30])
  })

  it('starts with 7 days, or the longest allowed below', () => {
    expect(defaultAcknowledgeDays([1, 7, 30])).toBe(7)
    expect(defaultAcknowledgeDays([1, 3])).toBe(3)
  })
})

describe('untrusted text', () => {
  it('links only http and https', () => {
    expect(safeLink('https://security-tracker.debian.org/tracker/CVE-2026-1234')).toBe(
      'https://security-tracker.debian.org/tracker/CVE-2026-1234',
    )
    expect(safeLink('javascript:alert(1)')).toBeNull()
    expect(safeLink('data:text/html,x')).toBeNull()
    expect(safeLink('not a url')).toBeNull()
    expect(safeLink(null)).toBeNull()
  })

  it('removes ANSI sequences, control and invisible characters from a log, and keeps tabs and line ends', () => {
    expect(cleanLog('\u001b[1;31mE:\u001b[0m failed\r\nnext\rline\tok\u0007')).toBe('E: failed\nnext\nline\tok')
    expect(cleanLog('a\u202Eb\u200Bc\uFEFF')).toBe('abc')
    expect(cleanLog('\u001b]0;title\u0007text')).toBe('text')
  })

  it('finds the lines of a search, with their numbers', () => {
    const text = 'Hit:1 http://deb.debian.org\nErr:2 https://enterprise.proxmox.com 401\nW: Failed to fetch'
    expect(searchLines(text, 'ERR')).toEqual([{ n: 2, text: 'Err:2 https://enterprise.proxmox.com 401' }])
    expect(searchLines(text, '  ')).toEqual([])
    expect(searchLines(text, 'http', 1)).toHaveLength(1)
  })

  it("recognises PNMTD's marker in a shortened log", () => {
    expect(
      isShortenedMarker('[… 1.2 MiB of this log were left out: PNMT keeps the start and the end of long logs …]'),
    ).toBe(true)
    expect(isShortenedMarker('Unpacking openssl')).toBe(false)
  })
})

describe('pending packages', () => {
  it("knows what the trackers cover: Debian's and Ubuntu's archives", () => {
    expect(coveredByTrackers('Debian-Security:12/stable-security')).toBe(true)
    expect(coveredByTrackers('Ubuntu:24.04/noble-updates')).toBe(true)
    expect(coveredByTrackers('Proxmox:bookworm/pve-no-subscription')).toBe(false)
    expect(coveredByTrackers(null)).toBe(false)
  })

  it('copies the list as text, one line each with its marks', () => {
    expect(
      packagesText([pkg('openssl', { security: true }), pkg('pve-manager', { inUpgrade: false, installed: null })]),
    ).toBe('openssl:amd64 1.0 -> 1.1 [security]\npve-manager:amd64 (new) -> 1.1 [full upgrade only]')
  })
})

describe("the dashboard's card (§10.3)", () => {
  const summary = (patch: Partial<S['OsUpdatesSummary']> = {}): S['OsUpdatesSummary'] => ({
    enabled: true,
    checkEnabled: true,
    servers: 22,
    serversWithUpdates: 14,
    packages: 312,
    security: 41,
    highRisk: 2,
    highRiskAcknowledged: 1,
    failed: 1,
    listsStale: 0,
    running: 0,
    byServer: [],
    byPackage: [],
    ...patch,
  })

  it('has chips for what there is, high risk and failures in red', () => {
    const chips = summaryChips(summary())
    expect(chips.map((c) => c.label)).toEqual([
      '14 of 22 servers',
      '312 packages',
      '41 security',
      '2 high risk',
      '1 acknowledged',
      '1 failed',
    ])
    expect(chips.find((c) => c.label === '2 high risk')?.severity).toBe('danger')
    expect(chips.find((c) => c.label === '1 acknowledged')?.severity).toBe('secondary')
    expect(
      summaryChips(
        summary({
          servers: 1,
          serversWithUpdates: 0,
          packages: 0,
          security: 0,
          highRisk: 0,
          highRiskAcknowledged: 0,
          failed: 0,
        }),
      ).map((c) => c.label),
    ).toEqual(['0 of 1 server'])
  })

  it('names the servers whose lists are stale', () => {
    expect(listsFooter(summary())).toBe('Every server refreshed its lists within 2 days.')
    const row = (hostName: string, listsStale: boolean): S['OsUpdatesServerRow'] => ({
      sensorId: hostName,
      name: 'Linux',
      hostName,
      pending: 1,
      security: 0,
      highRisk: 0,
      highRiskAcknowledged: 0,
      acknowledgedUntil: null,
      rebootRequired: false,
      listsStale,
      listsUpdatedAt: null,
      failed: false,
      running: false,
      failedMessage: null,
    })
    expect(listsFooter(summary({ listsStale: 1, byServer: [row('web-01', true), row('pve-01', false)] }))).toBe(
      'Lists older than 2 days: web-01',
    )
  })
})

describe('Admin → Servers → Advisories (§10.4)', () => {
  it('words the assessments and the thresholds', () => {
    expect(assessedByTag('verified')).toEqual({ label: 'verified', severity: 'danger' })
    expect(assessedByTag('suspicious').severity).toBe('danger')
    expect(assessedByTag('disputed').severity).toBe('warn')
    expect(assessedByTag('code').label).toBe('code only')
    expect(percentileText(0.99)).toBe('0.99 (the top 1 %)')
    expect(percentileText(0.9)).toBe('0.9 (the top 10 %)')
  })
})
