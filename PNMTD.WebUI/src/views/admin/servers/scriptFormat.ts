import type { S } from '@/api/resources'

/** Scripts' labels (docs/windows-sensor-plan.md §7; docs/linux-sensor-plan.md §8). Pure, so they are tested. */

export const runStateSeverity = (s: S['ScriptRunState']) =>
  s === 'Done' ? 'success' : s === 'Running' || s === 'Sent' ? 'info' : s === 'Offline' ? 'secondary' : 'danger'

export const runAsLabel = (k: S['RunAsKind']) =>
  k === 'Service'
    ? 'the service (LocalSystem)'
    : k === 'Stored'
      ? 'stored on the server'
      : k === 'TypedPerRun'
        ? 'typed per run'
        : k === 'User'
          ? 'a local account'
          : 'gMSA'

/** A library script's version, or a script registered on the server (version 0: it has no versions in PNMT). */
export const versionLabel = (n: number) => (n > 0 ? `v${n}` : 'registered on the server')

export const parseSeverity = (p: S['ScriptParseState']) =>
  p === 'Parsed' ? 'success' : p === 'Failed' ? 'danger' : 'warn'

/** The languages of the library (docs/linux-sensor-plan.md §8.1): PowerShell for Windows servers, bash for Linux servers. */
export const languages: S['ScriptLanguage'][] = ['PowerShell', 'Bash']

export const languageLabel = (l: S['ScriptLanguage']) => (l === 'Bash' ? 'bash' : 'PowerShell')

/** The platform a language's scripts run on. */
export const platformOf = (l: S['ScriptLanguage']): S['AgentPlatform'] => (l === 'Bash' ? 'Linux' : 'Windows')

/**
 * Whether a library script can be run: PowerShell once an agent parsed it; bash at once, as PNMTD reads its header at the save,
 * unless the header has errors (a Linux agent's syntax check is merged in later).
 */
export const runnable = (s: Pick<S['AgentScriptItem'], 'language' | 'parseState'>) =>
  s.language === 'Bash' ? s.parseState !== 'Failed' : s.parseState === 'Parsed'

/** A parameter as the script uses it: $Name in PowerShell, $PNMT_KEEP_DAYS in bash (§8.2). */
export const parameterName = (name: string, language: S['ScriptLanguage']) =>
  language === 'Bash' ? `$PNMT_${name.toUpperCase()}` : `$${name}`

/** A number field for PowerShell's numeric types and bash's int and number. */
export const numericParameter = (p: Pick<S['ScriptParameter'], 'type'>) =>
  ['int32', 'int64', 'double', 'decimal', 'byte', 'uint32', 'int', 'number', 'long'].includes(p.type.toLowerCase())

/** The account a run ran as: the account, or the service's (root on Linux, LocalSystem on Windows). */
export const runAccount = (r: Pick<S['AgentScriptRunItem'], 'account' | 'runAsKind' | 'runAsName'>) =>
  r.account ?? (r.runAsKind === 'Service' && r.runAsName.startsWith('root') ? 'root' : 'LocalSystem')
