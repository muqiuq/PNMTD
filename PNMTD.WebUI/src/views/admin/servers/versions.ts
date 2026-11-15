import type { S } from '@/api/resources'

/**
 * The Versions tab's words (docs/agent-update-plan.md §8.1): what a version can be chosen as, where it came from, its files'
 * short names, and how many agents run the chosen one.
 */

/** A choice: a version, or {@link FOLLOW} for PNMT's own (a select shows null and '' as nothing chosen). */
export type ChoiceOption = { value: string; label: string; disabled?: boolean }

/** The choice of PNMT's own version: no version looks like it. */
export const FOLLOW = 'pnmt'

/** "Windows x64", "Linux arm64": a file's platform and architecture. */
export const fileLabel = (f: Pick<S['AgentVersionFile'], 'platform' | 'architecture'>) =>
  `${f.platform} ${f.architecture}`

/** Whether a version can be chosen for a platform: a file of it here, or its release on GitHub. */
export function choosable(v: S['AgentVersionItem'], platform: S['AgentPlatform']): boolean {
  return v.onGitHub || v.files.some((f) => f.platform === platform && f.state === 'Here')
}

/**
 * The choices for a platform: PNMT's own version first ({@link FOLLOW}, it follows PNMT's updates), then every version that can be
 * chosen, newest first, with what to know about it.
 */
export function chooseOptions(list: S['AgentVersionList'], platform: S['AgentPlatform']): ChoiceOption[] {
  const options: ChoiceOption[] = [{ value: FOLLOW, label: `PNMT's version (${list.pnmtVersion})` }]
  for (const v of list.versions) {
    if (!choosable(v, platform)) continue
    const notes: string[] = []
    if (v.isPnmtVersion) notes.push("PNMT's, fixed")
    if (v.preRelease) notes.push('pre-release')
    if (v.newerThanPnmt) notes.push('newer than PNMT')
    if (!v.files.some((f) => f.platform === platform && f.state === 'Here')) notes.push('fetched when chosen')
    options.push({ value: v.version, label: notes.length ? `${v.version} (${notes.join(', ')})` : v.version })
  }
  return options
}

/** The stored choice: {@link FOLLOW} when the platform follows PNMT's version. */
export const choiceOf = (c: S['AgentChosenVersion']) => (c.followsPnmt ? FOLLOW : c.version)

/** A choice as the API takes it: null for PNMT's own. */
export const asRequest = (choice: string) => (choice === FOLLOW ? null : choice)

/** "12 of 15 Linux agents run 1.8.0 · 3 older · 1 newer". */
export function chosenSummary(c: S['AgentChosenVersion']): string {
  if (c.agents === 0) return `No ${c.platform} agents yet.`
  const parts = [`${c.running} of ${c.agents} ${c.platform} agents run ${c.version}`]
  if (c.older) parts.push(`${c.older} older`)
  if (c.newer) parts.push(`${c.newer} newer`)
  return parts.join(' · ')
}

/** Where a version's files came from, in a few words: GitHub's release, uploads, the folder, or nothing here yet. */
export function fromText(v: S['AgentVersionItem']): string {
  const sources = new Set(v.files.filter((f) => f.state === 'Here').map((f) => f.source))
  const parts: string[] = []
  if (sources.has('Upload')) {
    const by = [...new Set(v.files.filter((f) => f.source === 'Upload' && f.addedBy).map((f) => f.addedBy))]
    parts.push(by.length ? `uploaded by ${by.join(', ')}` : 'uploaded')
  }
  if (sources.has('Folder')) parts.push('in the download folder itself')
  if (v.onGitHub) parts.push('GitHub')
  else if (sources.has('GitHub')) parts.push('fetched from GitHub')
  return parts.length ? parts.join(' · ') : 'only chosen, nothing here'
}

/** A file's state as a short word, with the release's absence. */
export function fileState(f: S['AgentVersionFile']): {
  label: string
  severity: 'success' | 'info' | 'danger' | 'secondary'
} {
  if (f.state === 'Here') return { label: f.source === 'Upload' ? 'uploaded' : 'here', severity: 'success' }
  if (f.state === 'Fetching') return { label: 'fetching', severity: 'info' }
  if (f.state === 'Failed') return { label: 'failed', severity: 'danger' }
  return { label: f.inRelease === false ? 'not in the release' : 'not here', severity: 'secondary' }
}

/** Whether Fetch has something to do: a file not here that the release has (or may have). */
export const canFetch = (v: S['AgentVersionItem']) =>
  v.onGitHub && v.files.some((f) => f.state !== 'Here' && f.state !== 'Fetching' && f.inRelease !== false)

/** The file names the uploads take, by platform and architecture. */
export const uploadNames = [
  'pnmt-sensor-x64.exe',
  'pnmt-sensor-arm64.exe',
  'pnmt-agent-linux-x64',
  'pnmt-agent-linux-arm64',
] as const

/** The file name an upload proposes: one of the four when the picked file's name is one, else none. */
export function proposedName(fileName: string): string | null {
  return (uploadNames as readonly string[]).includes(fileName) ? fileName : null
}

/** A version as PNMT takes it: 1.8.0 or 1.8.0-rc1. */
export const isVersion = (v: string) =>
  /^\d{1,5}\.\d{1,5}\.\d{1,5}(-[0-9A-Za-z][0-9A-Za-z.-]{0,30})?$/.test(v) && !v.includes('..')
