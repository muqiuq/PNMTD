import type { S } from '@/api/resources'
import { formatDuration, toDate } from '@/format'
import { formatBytes } from '@/format/bytes'
import type { TagSeverity } from '@/views/admin/servers/agentFormat'

/**
 * Labels, colours, reasons and texts for OS updates on Linux servers (docs/linux-os-updates-plan.md §10): a server's tab, the
 * install dialog, the dashboard's card and Admin → Servers → Advisories. Pure, so they are tested.
 */

type View = S['OsUpdatesView']
type Run = S['OsUpdateRunItem']
type Package = S['OsPendingPackageView']

export interface Chip {
  label: string
  severity: TagSeverity
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`

// --- runs (§10.1 5) -------------------------------------------------------------------------------------------

const stateLabels: Record<S['OsUpdateState'], string> = {
  Requested: 'requested',
  Refused: 'refused',
  Running: 'running',
  Succeeded: 'succeeded',
  Warnings: 'with warnings',
  Failed: 'failed',
  Lost: 'lost',
}

export const runStateLabel = (state: S['OsUpdateState']) => stateLabels[state] ?? state.toLowerCase()

export function runStateSeverity(state: S['OsUpdateState']): TagSeverity {
  switch (state) {
    case 'Succeeded':
      return 'success'
    case 'Warnings':
    case 'Refused':
      return 'warn'
    case 'Failed':
      return 'danger'
    case 'Running':
    case 'Requested':
      return 'info'
    default:
      return 'secondary'
  }
}

/** What a run did, in words: "update", "upgrade", "full upgrade". */
export const runWhat = (r: Pick<Run, 'kind' | 'mode'>) =>
  r.kind === 'Update' ? 'update' : r.mode === 'FullUpgrade' ? 'full upgrade' : 'upgrade'

/** The command PNMT's agent runs for it (§4.3), as the log's title. */
export const runCommand = (r: Pick<Run, 'kind' | 'mode'>) =>
  r.kind === 'Update' ? 'apt-get update' : r.mode === 'FullUpgrade' ? 'apt-get full-upgrade' : 'apt-get upgrade'

export const modeLabel = (mode: S['OsUpgradeMode']) => (mode === 'FullUpgrade' ? 'Full upgrade' : 'Upgrade')

/**
 * Who or what started a run: scheduled, the person, on the server, or outside PNMT (unattended-upgrades, or apt by its
 * Requested-By). The API leaves the person's name out without Monitoring: Details.
 */
export function triggerLabel(r: Pick<Run, 'trigger' | 'requestedByName' | 'command'>): string {
  if (r.trigger === 'Schedule') return 'scheduled'
  if (r.trigger === 'Server') return 'on the server'
  if (r.trigger === 'Person') return r.requestedByName ?? 'a person'
  if (/unattended-upgrade/.test(r.command ?? '')) return 'outside PNMT: unattended-upgrades'
  return r.requestedByName ? `outside PNMT: apt by ${r.requestedByName}` : 'outside PNMT'
}

/** How long a run took, or has been running ("12 min"); "–" before it started. */
export function runDuration(r: Pick<Run, 'startedAt' | 'finishedAt' | 'state'>, now: number): string {
  const start = toDate(r.startedAt)
  if (!start) return '–'
  const end = toDate(r.finishedAt)?.getTime() ?? (r.state === 'Running' ? now : null)
  return end === null ? '–' : formatDuration(end - start.getTime())
}

/** A run's result as chips (§10.1 5): "23 upgraded", "1 installed", "1 removed", "2 warnings", and what needs a look. */
export function countChips(c: S['OsUpdateCounts']): Chip[] {
  const chips: Chip[] = []
  if (c.upgraded) chips.push({ label: `${c.upgraded} upgraded`, severity: 'success' })
  if (c.installed) chips.push({ label: `${c.installed} installed`, severity: 'info' })
  if (c.removed) chips.push({ label: `${c.removed} removed`, severity: 'warn' })
  if (c.errors) chips.push({ label: plural(c.errors, 'error'), severity: 'danger' })
  if (c.warnings) chips.push({ label: plural(c.warnings, 'warning'), severity: 'warn' })
  if (c.repositoriesFailed) {
    chips.push({ label: `${plural(c.repositoriesFailed, 'repository', 'repositories')} failed`, severity: 'warn' })
  }
  if (c.configFilesKept) {
    chips.push({ label: `${plural(c.configFilesKept, 'configuration file')} kept`, severity: 'secondary' })
  }
  if (c.servicesToRestart) {
    chips.push({ label: `${plural(c.servicesToRestart, 'service')} to restart`, severity: 'secondary' })
  }
  if (c.rebootRequired) chips.push({ label: 'reboot required', severity: 'warn' })
  return chips
}

// --- what may run, and why not (§4.6, §10.1 1) --------------------------------------------------------------------

export const CONFIG_YAML = '/etc/pnmt-agent/config.yaml'
const notAllowed = (keys: string) => `Not allowed on the server: ${keys} in ${CONFIG_YAML}.`
const notAnnounced = "The server hasn't said yet what its config.yaml allows."

/** Why *Refresh lists* is disabled, or null. */
export function refreshBlocked(v: View): string | null {
  if (v.unavailable) return v.unavailable
  if (!v.rights) return notAnnounced
  if (!v.rights.update) return notAllowed('os_updates.update: false')
  if (v.current) return 'A run is in progress.'
  return null
}

/** One line of config.yaml's os_updates block (§3): what it allows, and when off the line that would allow it. */
export interface OsRight {
  label: string
  on: boolean
  grant: string | null
}

/** config.yaml's os_updates block, line by line (§10.1 6). A missing key means on (D1); an explicit false is root's to change. */
export function osRightsRows(r: S['OsUpdatesRightsSummary']): OsRight[] {
  const row = (label: string, key: string, on: boolean): OsRight => ({
    label,
    on,
    grant: on ? null : `os_updates: { ${key}: true }`,
  })
  return [
    row('apt-get update, daily and when asked', 'update', r.update),
    row('apt-get upgrade --with-new-pkgs', 'upgrade', r.upgrade),
    row('apt-get full-upgrade, which may remove packages', 'full_upgrade', r.fullUpgrade),
    row("the pending packages' changelogs", 'changelogs', r.changelogs),
  ]
}

const planOf = (v: View, mode: S['OsUpgradeMode']) => (mode === 'FullUpgrade' ? v.fullUpgrade : v.upgrade)

/** Why a mode of *Install updates…* can't be chosen (§10.2), or null: config.yaml, no plan yet, nothing to do. */
export function modeBlocked(v: View, mode: S['OsUpgradeMode']): string | null {
  if (v.rights && !(mode === 'FullUpgrade' ? v.rights.fullUpgrade : v.rights.upgrade)) {
    return notAllowed(mode === 'FullUpgrade' ? 'os_updates.full_upgrade: false' : 'os_updates.upgrade: false')
  }
  const plan = planOf(v, mode)
  if (!plan) return 'No plan from the server yet: it simulates the upgrade with its next collection.'
  if (plan.upgrade + plan.install + plan.remove === 0) {
    return plan.keptBack ? `Nothing to install this way: ${plan.keptBack} kept back.` : 'Nothing to install this way.'
  }
  return null
}

/** Why *Install updates…* is disabled, or null: the server, config.yaml, dpkg, a run in progress, nothing pending. */
export function installBlocked(v: View): string | null {
  if (v.unavailable) return v.unavailable
  if (!v.rights) return notAnnounced
  if (!v.rights.upgrade && !v.rights.fullUpgrade) {
    return notAllowed('os_updates.upgrade: false and os_updates.full_upgrade: false')
  }
  if (v.dpkgInterrupted) return 'dpkg was interrupted: run dpkg --configure -a on the server.'
  if (v.current) return 'A run is in progress.'
  const upgrade = modeBlocked(v, 'Upgrade')
  const full = modeBlocked(v, 'FullUpgrade')
  if (upgrade && full) return v.pending === 0 ? 'No updates pending.' : v.defaultMode === 'FullUpgrade' ? full : upgrade
  return null
}

/** The mode the dialog starts with (D3): the server's default (full upgrade on Proxmox VE), else the other when it's the only one. */
export function startMode(v: View): S['OsUpgradeMode'] {
  const other: S['OsUpgradeMode'] = v.defaultMode === 'FullUpgrade' ? 'Upgrade' : 'FullUpgrade'
  return modeBlocked(v, v.defaultMode) && !modeBlocked(v, other) ? other : v.defaultMode
}

// --- the plan (§10.2) -----------------------------------------------------------------------------------------------

/** The pending packages a mode installs: upgrade leaves out those only a full upgrade brings. */
export const modePackages = (packages: Package[], mode: S['OsUpgradeMode']) =>
  mode === 'FullUpgrade' ? packages : packages.filter((p) => p.inUpgrade)

/** "23 upgraded · 1 new · 2 kept back". */
export function planLine(plan: S['AptPlan']): string {
  const parts = [`${plan.upgrade} upgraded`, `${plan.install} new`]
  if (plan.keptBack) parts.push(`${plan.keptBack} kept back`)
  return parts.join(' · ')
}

/** The download, "41.2 MiB", or "unknown". */
export const downloadText = (bytes: number | null | undefined) => (bytes == null ? 'unknown' : formatBytes(bytes))

/** The disk space after the run: "120 MiB more", "frees 12.5 MiB", or "unknown". */
export function diskText(bytes: number | null | undefined): string {
  if (bytes == null) return 'unknown'
  return bytes < 0 ? `frees ${formatBytes(-bytes)}` : `${formatBytes(bytes)} more`
}

const kernel = /^(linux-image-|linux-(generic|virtual|signed|lowlatency)|proxmox-kernel-|pve-kernel-)/

/** The notes of §10.2 where they apply: a new kernel, openssh-server, and Proxmox VE without a full upgrade. */
export function planNotes(packages: Pick<Package, 'name'>[], mode: S['OsUpgradeMode'], proxmox: boolean): string[] {
  const notes: string[] = []
  if (packages.some((p) => kernel.test(p.name))) {
    notes.push("A new kernel: the server needs a reboot afterwards, which PNMT doesn't do.")
  }
  if (packages.some((p) => p.name === 'openssh-server')) {
    notes.push('openssh-server is upgraded: SSH sessions stay, sshd restarts.')
  }
  if (proxmox && mode === 'Upgrade') {
    notes.push('Proxmox VE needs a full upgrade: an upgrade keeps back what would install or remove packages.')
  }
  return notes
}

// --- the state (§10.1 1) ---------------------------------------------------------------------------------------------

/** needrestart's report, only reported (D4): "3 services need a restart: nginx, php8.2-fpm, ssh". */
export function needsRestartText(n: S['NeedsRestartInfo'] | null | undefined): string | null {
  if (!n?.services.length) return null
  return `${plural(n.services.length, 'service needs', 'services need')} a restart: ${n.services.join(', ')}`
}

/** A pending kernel: "the running kernel is 6.1.0-26, 6.1.0-28 is installed". */
export function kernelText(n: S['NeedsRestartInfo'] | null | undefined): string | null {
  if (!n?.kernelPending) return null
  if (n.runningKernel && n.expectedKernel) {
    return `the running kernel is ${n.runningKernel}, ${n.expectedKernel} is installed`
  }
  return 'a newer kernel is installed than the one running'
}

/** The counts' line: "23 updates pending · 5 security". High risk and acknowledged ones are shown apart, in colour. */
export function pendingText(v: Pick<View, 'pending' | 'security'>): string {
  const parts = [`${plural(v.pending, 'update')} pending`]
  if (v.security) parts.push(`${v.security} security`)
  return parts.join(' · ')
}

// --- findings (§6.4, §6.5, §10.1 3) ----------------------------------------------------------------------------------

export const ratingLabel = (r: S['OsFindingRating'] | null | undefined) =>
  r === 'HighRisk' ? 'high risk' : r === 'Important' ? 'important' : ''

export const ratingSeverity = (r: S['OsFindingRating'] | null | undefined): TagSeverity =>
  r === 'HighRisk' ? 'danger' : r === 'Important' ? 'warn' : 'secondary'

/** The durations an acknowledgement may have (§6.5): 1, 7 or 30 days, never more than AcknowledgeMaxDays. */
export function acknowledgeDays(max: number): number[] {
  const limit = Math.max(1, Math.floor(max))
  const days = [1, 7, 30].filter((d) => d <= limit)
  if (limit < 30 && !days.includes(limit)) days.push(limit)
  return days
}

/** 7 days by default, or the longest allowed below it. */
export const defaultAcknowledgeDays = (days: number[]) => (days.includes(7) ? 7 : days[days.length - 1]!)

/** A link from a finding or an advisory text, only when it is http(s): their texts are untrusted (§7.2). */
export function safeLink(url: string | null | undefined): string | null {
  if (!url) return null
  try {
    const parsed = new URL(url)
    return parsed.protocol === 'https:' || parsed.protocol === 'http:' ? parsed.href : null
  } catch {
    return null
  }
}

// --- pending packages (§10.1 4) ------------------------------------------------------------------------------------------

/**
 * Whether Debian's or Ubuntu's trackers cover a package's origin (§6.3): packages from other repositories (Proxmox, Docker,
 * PGDG) are matched by their changelogs only.
 */
export const coveredByTrackers = (origin: string | null | undefined) => /^(debian|ubuntu)/i.test(origin ?? '')

/** *Copy*: the pending packages as text, one per line. */
export function packagesText(packages: Package[]): string {
  return packages
    .map((p) => {
      const marks = [
        p.security ? 'security' : null,
        p.inUpgrade ? null : 'full upgrade only',
        p.phased ? 'phased' : null,
      ]
        .filter(Boolean)
        .join(', ')
      return `${p.name}:${p.arch} ${p.installed ?? '(new)'} -> ${p.candidate}${marks ? ` [${marks}]` : ''}`
    })
    .join('\n')
}

// --- logs (§10.1 5, §7.2) ---------------------------------------------------------------------------------------------------

/**
 * A log as text for a <pre>: ANSI sequences, control and invisible characters removed, line ends as \n. PNMTD cleans the stored
 * log already; this is the UI's own guard, since apt's output is untrusted (repositories write parts of it).
 */
export function cleanLog(text: string): string {
  return text
    .replace(/\u001b\[[0-?]*[ -/]*[@-~]/g, '')
    .replace(/\u001b\][^\u0007\u001b]*(\u0007|\u001b\\)/g, '')
    .replace(/\u001b[@-_]?/g, '')
    .replace(/\r\n/g, '\n')
    .replace(/\r/g, '\n')
    .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F\u200B-\u200F\u202A-\u202E\u2066-\u2069\uFEFF]/g, '')
}

/** The lines that contain a search, case-insensitive, with their numbers (1-based); at most `max`. */
export function searchLines(text: string, query: string, max = 2000): { n: number; text: string }[] {
  const q = query.trim().toLowerCase()
  if (!q) return []
  const found: { n: number; text: string }[] = []
  const lines = text.split('\n')
  for (let i = 0; i < lines.length && found.length < max; i++) {
    if (lines[i]!.toLowerCase().includes(q)) found.push({ n: i + 1, text: lines[i]! })
  }
  return found
}

/** The marker PNMTD puts where the middle of a long log was left out (§5.3). */
export const isShortenedMarker = (line: string) => /^\[… .* left out: PNMT keeps the start and the end/.test(line)

// --- the dashboard's card (§10.3) ----------------------------------------------------------------------------------------

/**
 * The card's header chips: "14 of 22 servers", "312 packages", "41 security", "2 high risk" (red) and "1 acknowledged" (grey),
 * "1 failed" (red), "3 lists stale" (amber), "1 running".
 */
export function summaryChips(s: S['OsUpdatesSummary']): Chip[] {
  const chips: Chip[] = [{ label: `${s.serversWithUpdates} of ${plural(s.servers, 'server')}`, severity: 'secondary' }]
  if (s.packages) chips.push({ label: plural(s.packages, 'package'), severity: 'secondary' })
  if (s.security) chips.push({ label: `${s.security} security`, severity: 'info' })
  if (s.highRisk) chips.push({ label: `${s.highRisk} high risk`, severity: 'danger' })
  if (s.highRiskAcknowledged) chips.push({ label: `${s.highRiskAcknowledged} acknowledged`, severity: 'secondary' })
  if (s.failed) chips.push({ label: `${s.failed} failed`, severity: 'danger' })
  if (s.listsStale) chips.push({ label: `${s.listsStale} lists stale`, severity: 'warn' })
  if (s.running) chips.push({ label: `${s.running} running`, severity: 'info' })
  return chips
}

/** The card's footer: every server's lists fresh, or the servers whose lists are stale. */
export function listsFooter(s: S['OsUpdatesSummary']): string {
  const stale = s.byServer.filter((r) => r.listsStale).map((r) => r.hostName)
  if (!stale.length)
    return s.listsStale ? `${s.listsStale} lists stale` : 'Every server refreshed its lists within 2 days.'
  const shown = stale.slice(0, 5).join(', ')
  return `Lists older than 2 days: ${shown}${stale.length > 5 ? ` and ${stale.length - 5} more` : ''}`
}

// --- Admin → Servers → Advisories (§6.1, §10.4) -----------------------------------------------------------------------------

const kindLabels: Record<S['OsAdvisorySourceKind'], string> = {
  DebianTracker: 'Debian Security Tracker',
  UbuntuNotices: 'Ubuntu security notices',
  CisaKev: 'CISA KEV',
  Epss: 'EPSS',
  Rss: 'RSS or Atom (texts)',
}

export const sourceKindLabel = (kind: S['OsAdvisorySourceKind']) => kindLabels[kind] ?? kind

export const sourceKinds = Object.keys(kindLabels) as S['OsAdvisorySourceKind'][]

/** Each kind's interval when PNMT creates it (OsAdvisorySourceDefaults), the start for a new source. */
const kindMinutes: Record<S['OsAdvisorySourceKind'], number> = {
  DebianTracker: 360,
  UbuntuNotices: 720,
  CisaKev: 360,
  Epss: 1440,
  Rss: 60,
}

export const defaultMinutes = (kind: S['OsAdvisorySourceKind']) => kindMinutes[kind] ?? 360

/** A source's role: what an update fixes, how dangerous a CVE is, or texts for the AI (§6.1, §7). */
export function roleLabel(role: string | null | undefined): string {
  if (role === 'fixes') return 'what updates fix'
  if (role === 'risk') return 'how dangerous'
  if (role === 'changelogs') return 'changelogs'
  return 'texts'
}

/** A text's assessment (§7.1): code only, waiting, AI without a claim, verified, disputed, or stopped as suspicious. */
export function assessedByTag(assessedBy: string): Chip {
  switch (assessedBy) {
    case 'pending':
      return { label: 'waiting for the AI', severity: 'info' }
    case 'ai':
      return { label: 'AI: nothing exploited', severity: 'secondary' }
    case 'verified':
      return { label: 'verified', severity: 'danger' }
    case 'disputed':
      return { label: 'disputed', severity: 'warn' }
    case 'suspicious':
      return { label: 'suspicious', severity: 'danger' }
    case 'code':
      return { label: 'code only', severity: 'secondary' }
    default:
      return { label: assessedBy, severity: 'secondary' }
  }
}

/** An EPSS percentile threshold: "0.99 (the top 1 %)". */
export function percentileText(p: number): string {
  const top = Math.round((1 - p) * 1000) / 10
  return `${p} (the top ${top} %)`
}

/** A source's interval: "6 h", "1 d", "5 min". */
export const intervalText = (minutes: number) => formatDuration(minutes * 60_000)
