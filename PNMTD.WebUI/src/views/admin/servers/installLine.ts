import type { S } from '@/api/resources'

/**
 * The Linux agent's one line (docs/linux-sensor-plan.md §4.4) in its variants: PNMTD answers it per architecture from PNMT and
 * from GitHub, with wget and with curl, and the token as a placeholder. The token, and running it through `sudo sh -c '…'`, are
 * the UI's. Pure, so they are tested.
 */

export type InstallSource = 'PNMT' | 'GitHub'
export type InstallTool = 'wget' | 'curl'
export type InstallUser = 'root' | 'sudo'

export interface InstallChoice {
  source: InstallSource
  architecture: string
  tool: InstallTool
  user: InstallUser
}

export const architectures = ['x64', 'arm64']

/** A line for a user with sudo: the whole line in one shell as root, its single quotes escaped. */
export function sudoWrap(line: string): string {
  return `sudo sh -c '${line.replaceAll("'", `'\\''`)}'`
}

/** The join alone, as PNMTD words it (AgentDownloads.JoinLine): for a binary installed another way, or while no hash is known. */
export function joinLine(url: string, token: string): string {
  return `pnmt-agent join --url ${url} --token ${token}${url.toLowerCase().startsWith('http://') ? ' --allow-http' : ''}`
}

/** The line for a choice, with the token filled in when there is one; null while the binary's hash isn't known. */
export function installLine(
  downloads: Pick<S['AgentDownloadLinks'], 'linux' | 'tokenPlaceholder'>,
  choice: InstallChoice,
  token?: string | null,
): string | null {
  const lines = downloads.linux.find((l) => l.architecture === choice.architecture)
  if (!lines) return null
  const raw =
    choice.source === 'PNMT'
      ? choice.tool === 'curl'
        ? lines.pnmtCurl
        : lines.pnmt
      : choice.tool === 'curl'
        ? lines.gitHubCurl
        : lines.gitHub
  if (!raw) return null
  const line = token ? raw.replaceAll(downloads.tokenPlaceholder, token) : raw
  return choice.user === 'sudo' ? sudoWrap(line) : line
}

/** The Linux file of an architecture in PNMT's download folder. */
export const linuxFile = (downloads: Pick<S['AgentDownloadLinks'], 'assets'>, architecture: string) =>
  downloads.assets.find((a) => a.platform === 'Linux' && a.architecture === architecture) ?? null

/**
 * Whether PNMT can hand out its own file for an architecture: it serves downloads, and the file is here or being fetched (a
 * download waits for the fetch). Otherwise the reason, and the dialog preselects GitHub.
 */
export function pnmtAvailability(
  downloads: Pick<S['AgentDownloadLinks'], 'assets' | 'linux' | 'serveDownloads'>,
  architecture: string,
): { available: boolean; note: string | null } {
  if (!downloads.serveDownloads) {
    return {
      available: false,
      note: "PNMT doesn't serve the agent's files (Agents__ServeDownloads is off): use GitHub.",
    }
  }
  const file = linuxFile(downloads, architecture)
  const line = downloads.linux.find((l) => l.architecture === architecture)
  if (file?.state === 'Failed') {
    return {
      available: false,
      note: `PNMT couldn't fetch ${file.name}${file.error ? `: ${file.error}` : ''}. Use GitHub, or Fetch again on Admin → Servers → Download.`,
    }
  }
  if (!file || !line?.pnmt) {
    return { available: false, note: "PNMT doesn't know the file's hash yet: use GitHub, or try again later." }
  }
  if (file.state === 'Here') return { available: true, note: null }
  if (file.state === 'Fetching') {
    return { available: true, note: `PNMT is fetching ${file.name} from GitHub; a download waits for it.` }
  }
  return {
    available: false,
    note: `${file.name} isn't in PNMT's download folder yet. Use GitHub, or Fetch again on Admin → Servers → Download.`,
  }
}

/** A file's state in PNMT's download folder, in words. */
export function fileStateLabel(state: S['AgentDownloadState']): string {
  return state === 'Here'
    ? 'here'
    : state === 'Fetching'
      ? 'being fetched'
      : state === 'Failed'
        ? 'failed'
        : 'not fetched'
}

export const fileStateSeverity = (state: S['AgentDownloadState']) =>
  state === 'Here' ? 'success' : state === 'Fetching' ? 'info' : state === 'Failed' ? 'danger' : 'secondary'
