import type { S } from '@/api/resources'
import type { TagSeverity } from '@/views/admin/servers/agentFormat'

/** Labels, colours and checks for a Linux server's pages (docs/linux-sensor-plan.md §5, §6, §11). Pure, so they are tested. */

/**
 * A key's fingerprint short, as PNMTD words it in its messages (PNMTD.Lib's SshKeys.Short): the first 16 characters and the last
 * 4, "SHA256:Kq1f9hJ0x…rS2A". Names replace it once the key manager knows the key (§6.4).
 */
export function shortFingerprint(fingerprint: string | null | undefined): string {
  if (!fingerprint) return ''
  return fingerprint.length > 22 ? `${fingerprint.slice(0, 16)}…${fingerprint.slice(-4)}` : fingerprint
}

/**
 * The key a login used (§6.4): its registered name and owner, "Philipp, laptop (philipp)", as 264 words it; else the short
 * fingerprint.
 */
export function keyLabel(l: {
  keyName?: string | null
  keyOwner?: string | null
  keyFingerprint?: string | null
}): string {
  if (l.keyName) return l.keyOwner ? `${l.keyName} (${l.keyOwner})` : l.keyName
  return shortFingerprint(l.keyFingerprint)
}

/** A note on a setting, with how much it matters. */
export interface Note {
  text: string
  severity: TagSeverity
}

/** What root may do over SSH (PermitRootLogin), in words. */
export function rootLogin(value: string | null | undefined): Note {
  switch ((value ?? '').toLowerCase()) {
    case 'yes':
      return { text: 'root may log in with a password', severity: 'danger' }
    case 'prohibit-password':
    case 'without-password':
      return { text: 'root: key only', severity: 'secondary' }
    case 'forced-commands-only':
      return { text: 'root: forced commands only', severity: 'secondary' }
    case 'no':
      return { text: "root can't log in", severity: 'success' }
    default:
      return { text: 'root login: not known', severity: 'secondary' }
  }
}

/**
 * sshd's effective settings (§5.5) as notes, the risky ones first: root with a password, password and keyboard-interactive
 * authentication, forwarding, MaxAuthTries above the default, keys and certificates from outside the files, Match blocks, and a
 * LogLevel that doesn't log failed keys.
 */
export function sshdNotes(s: S['SshdSettings']): Note[] {
  const notes: Note[] = []
  const root = rootLogin(s.permitRootLogin)
  if (root.severity === 'danger') notes.push(root)
  if (s.passwordAuthentication) notes.push({ text: 'password authentication on', severity: 'warn' })
  if (s.kbdInteractiveAuthentication) notes.push({ text: 'keyboard-interactive on', severity: 'warn' })
  if (s.authorizedKeysCommand) notes.push({ text: 'keys from AuthorizedKeysCommand', severity: 'warn' })
  if (s.trustedUserCaKeys) notes.push({ text: 'certificates of a CA (TrustedUserCAKeys)', severity: 'warn' })
  if (s.x11Forwarding) notes.push({ text: 'X11 forwarding on', severity: 'warn' })
  if ((s.maxAuthTries ?? 0) > 6) notes.push({ text: `MaxAuthTries ${s.maxAuthTries}`, severity: 'warn' })
  if (root.severity !== 'danger') notes.push(root)
  if (!s.pubkeyAuthentication) notes.push({ text: 'public keys off', severity: 'secondary' })
  if (s.allowAgentForwarding) notes.push({ text: 'agent forwarding on', severity: 'secondary' })
  if (s.allowTcpForwarding && s.allowTcpForwarding.toLowerCase() !== 'no') {
    notes.push({
      text: `TCP forwarding ${s.allowTcpForwarding.toLowerCase() === 'yes' ? 'on' : s.allowTcpForwarding}`,
      severity: 'secondary',
    })
  }
  if (s.matchBlocks) notes.push({ text: 'Match blocks: settings differ per user or address', severity: 'info' })
  if (s.logLevel && !/^(VERBOSE|DEBUG\d?)$/i.test(s.logLevel)) {
    notes.push({ text: `LogLevel ${s.logLevel}: failed keys aren't logged`, severity: 'info' })
  }
  return notes
}

/** The firewall's verdict on a port (§5.5): open, filtered, or unknown (a ruleset too complex to judge). */
export const verdictSeverity = (verdict: string): TagSeverity =>
  verdict === 'open' ? 'warn' : verdict === 'filtered' ? 'success' : 'secondary'

// --- services (§5.4) ----------------------------------------------------------------------------------------

export const serviceClassLabel = (c: S['LinuxServiceClass']) =>
  c === 'ThirdParty' ? 'third-party' : c === 'Pnmt' ? 'PNMT' : c.toLowerCase()

export const serviceClassSeverity = (c: S['LinuxServiceClass']): TagSeverity =>
  c === 'Unpackaged' ? 'danger' : c === 'ThirdParty' ? 'warn' : c === 'Distribution' ? 'info' : 'secondary'

/** Special (§5.4): third-party and unpackaged programs, and services that should run and don't. */
export const specialService = (s: S['LinuxServiceEntry']) =>
  s.class === 'ThirdParty' || s.class === 'Unpackaged' || s.enabledNotRunning || s.failed

/** The order of the list: failed, then unpackaged, third-party, the rest; by name within each. */
export function serviceRank(s: S['LinuxServiceEntry']): number {
  if (s.failed || s.enabledNotRunning) return 0
  if (s.class === 'Unpackaged') return 1
  if (s.class === 'ThirdParty') return 2
  return 3
}

// --- the journal (§5.7) -------------------------------------------------------------------------------------

export const priorityNames = ['emerg', 'alert', 'crit', 'err', 'warning', 'notice', 'info', 'debug']

/** A journal entry's priority as a tag: emerg, alert and crit in red with a mark, err in red, warning amber. */
export function priorityTag(priority: number): { label: string; severity: TagSeverity; icon?: string } {
  const label = priorityNames[priority] ?? String(priority)
  if (priority <= 2) return { label, severity: 'danger', icon: 'pi pi-exclamation-circle' }
  if (priority === 3) return { label, severity: 'danger' }
  if (priority === 4) return { label, severity: 'warn' }
  return { label, severity: 'secondary' }
}

/** Where an entry comes from: its unit, else its identifier (the kernel has no unit). */
export const entrySource = (e: Pick<S['JournalEntry'], 'unit' | 'identifier'>) => e.unit ?? e.identifier ?? '–'

// --- SSH keys (§6) ------------------------------------------------------------------------------------------

/** A weak or refused key (§6.1): its notes say so ("RSA 2048 bits: weak", "DSA: refused by OpenSSH"). */
export const weakKey = (k: Pick<S['AuthorizedKey'], 'notes'>) => k.notes.some((n) => /weak|refused/i.test(n))

/** root's key without from= (§6.1): anyone with the key gets in as root from anywhere. */
export const rootWithoutFrom = (k: Pick<S['AuthorizedKey'], 'notes'>) =>
  k.notes.some((n) => n.includes('without from='))

/** The SSH keys at a glance (§11.2): how many, in how many files, how many different, weak ones, root's without from=, problems. */
export function keyGlance(section: S['SshKeysSection']) {
  const keys = section.files.flatMap((f) => f.keys)
  return {
    keys: section.totalKeys,
    files: section.files.filter((f) => f.exists).length,
    distinct: new Set(keys.map((k) => k.fingerprint)).size,
    weak: keys.filter(weakKey).length,
    rootWithoutFrom: keys.filter(rootWithoutFrom).length,
    problems: section.files.reduce((n, f) => n + f.problems.length, 0),
    invalidLines: section.files.reduce((n, f) => n + f.invalidLines.length, 0),
  }
}

/** An option's tag colour: restrictions are good news, command= and cert-authority need a look. */
export function optionSeverity(option: string): TagSeverity {
  const name = option.split('=')[0]!.toLowerCase()
  if (name === 'from' || name === 'restrict' || name.startsWith('no-') || name === 'expiry-time') return 'success'
  if (name === 'cert-authority' || name === 'environment' || name === 'permitopen' || name === 'permitlisten')
    return 'warn'
  return 'secondary'
}

/** "Lines 3 and 7 aren't keys", by number only: their text is never sent (§6.6). */
export function invalidLinesText(lines: number[]): string {
  if (!lines.length) return ''
  if (lines.length === 1) return `Line ${lines[0]} isn't a key`
  return `Lines ${lines.slice(0, -1).join(', ')} and ${lines[lines.length - 1]} aren't keys`
}

// --- the rights (§4.6, §11.3) -------------------------------------------------------------------------------

/** One right the server gives PNMT: what it is, whether it is on, and the line of config.yaml that would grant it when off. */
export interface Right {
  label: string
  value: string
  on: boolean
  grant: string | null
}

/** Where the rights come from: config.yaml, the defaults (no file: everything off), or a refused file (everything off). */
export function rightsSource(r: Pick<S['AgentRightsSummary'], 'source'>): { label: string; severity: TagSeverity } {
  if (r.source === 'config.yaml') return { label: 'config.yaml', severity: 'success' }
  if (r.source === 'refused') return { label: 'config.yaml refused', severity: 'danger' }
  if (r.source === 'defaults') return { label: 'defaults: no config.yaml', severity: 'secondary' }
  return { label: r.source, severity: 'secondary' }
}

const list = (items: string[]) => items.join(', ')

/**
 * The rights of config.yaml (§4.6) as rows, each with the line that would grant what is off: key removal and for whom, the
 * protected keys, the writable paths, scripts and as whom, the allowed hashes and the longest run, and what is collected.
 */
export function rightsRows(r: S['AgentRightsSummary']): Right[] {
  const rows: Right[] = [
    {
      label: 'Key removal',
      on: r.keyRemoval,
      value: r.keyRemoval
        ? r.keyRemovalUsers.length
          ? `allowed for ${list(r.keyRemovalUsers)}`
          : 'allowed for every account'
        : 'off',
      grant: r.keyRemoval ? null : 'ssh_keys: { remove: true }',
    },
  ]
  if (r.keyRemoval) {
    rows.push(
      {
        label: 'Protected keys',
        on: r.protectedKeys > 0,
        value: r.protectedKeys
          ? `${r.protectedKeys} by fingerprint, never removed`
          : 'none named (cluster nodes are protected anyway)',
        grant: null,
      },
      {
        label: 'Key files under /etc',
        on: r.writablePaths.length > 0,
        value: r.writablePaths.length ? list(r.writablePaths) : 'none writable',
        grant: null,
      },
    )
  }
  rows.push({
    label: 'Scripts',
    on: r.scripts && r.scriptRunAs.length > 0,
    value: r.scripts ? (r.scriptRunAs.length ? `as ${list(r.scriptRunAs)}` : 'on, but no account to run as') : 'off',
    grant: !r.scripts
      ? 'scripts: { enabled: true, run_as: [root] }'
      : r.scriptRunAs.length
        ? null
        : 'scripts: { run_as: [root] }',
  })
  if (r.scripts) {
    rows.push(
      {
        label: 'Allowed scripts',
        on: true,
        value: r.allowedScriptHashes ? `${r.allowedScriptHashes} by hash` : "any script of PNMT's library",
        grant: null,
      },
      { label: 'Longest run', on: true, value: `${r.maxScriptTimeoutMinutes} min`, grant: null },
    )
  }
  rows.push(
    {
      label: 'sudo commands',
      on: r.sudoCommands,
      value: r.sudoCommands ? 'collected with the logins' : 'not collected',
      grant: r.sudoCommands ? null : 'collect: { sudo_commands: true }',
    },
    {
      label: 'cron commands',
      on: r.cronCommands,
      value: r.cronCommands ? 'collected in full' : 'only the program each line runs',
      grant: r.cronCommands ? null : 'collect: { cron_commands: true }',
    },
  )
  return rows
}
