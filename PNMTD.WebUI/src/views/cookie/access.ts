import type { S } from '@/api/resources'

/**
 * The access watch in words (docs/cookie-access-plan.md §5.4, §6.6, §7): the verdicts and how loud each looks, a reviewed
 * login's state, the answers, the kind groups, and the request an answer on a case sends.
 */

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary'

export type AccessVerdict = 'normal' | 'unusual' | 'suspicious' | 'alarming'

export const accessVerdicts: AccessVerdict[] = ['normal', 'unusual', 'suspicious', 'alarming']

/** "Alarming"; an unknown verdict as it is. */
export const accessVerdictLabel = (verdict: string | null | undefined) =>
  verdict ? verdict.charAt(0).toUpperCase() + verdict.slice(1) : '–'

/** Normal green, unusual blue, suspicious amber, alarming red. */
export const accessVerdictSeverity = (verdict: string | null | undefined): Severity =>
  verdict === 'normal'
    ? 'success'
    : verdict === 'unusual'
      ? 'info'
      : verdict === 'suspicious'
        ? 'warn'
        : verdict === 'alarming'
          ? 'danger'
          : 'secondary'

/** A reviewed login's state (§5.4, §6.5): waiting for a verdict, the verdict, or closed without one. */
export const reviewStateLabel = (state: S['AccessReviewState']) =>
  state === 'NotJudged' ? 'not judged' : state.toLowerCase()

export const reviewStateSeverity = (state: S['AccessReviewState']): Severity =>
  state === 'Waiting' || state === 'NotJudged' ? 'secondary' : accessVerdictSeverity(state.toLowerCase())

/** A contact's answer (§7). */
export const answerLabel = (answer: string | null | undefined) =>
  answer === 'expected' ? 'Expected' : answer === 'not-expected' ? 'Not expected' : (answer ?? '–')

export const answerSeverity = (answer: string | null | undefined): Severity =>
  answer === 'expected' ? 'success' : answer === 'not-expected' ? 'danger' : 'secondary'

/** The person's answer to Was this you? (§6.6). */
export const personAnswerLabel = (answer: string | null | undefined) =>
  answer === 'yes' ? 'Yes, it was me' : answer === 'no' ? "No, it wasn't me" : (answer ?? '–')

export const targetKindLabel: Record<S['AccessTargetKind'], string> = {
  Server: 'Server',
  Router: 'Router',
  Pnmt: 'PNMT',
}

/** The kind groups a login is compared by (§4.4), as the server names them. */
export const accessGroups = [
  'remote',
  'console',
  'network',
  'elevation',
  'file-transfer',
  'management',
  'web',
  'mcp',
] as const

const groupLabels: Record<string, string> = {
  remote: 'Remote',
  console: 'Console',
  network: 'Network logon',
  elevation: 'sudo and su',
  'file-transfer': 'File transfer',
  management: 'Management (WinBox, WebFig, API)',
  web: 'Web UI',
  mcp: 'MCP',
}

export const accessGroupLabel = (group: string) => groupLabels[group] ?? group

/** Where Remember this applies (§7): this target, its location, or every target. */
export type RememberScope = 'target' | 'location' | 'all'

/** For how long: 30 days, 90 days, a year, or without end (null). */
export const rememberDays: { label: string; value: number | null }[] = [
  { label: '30 days', value: 30 },
  { label: '90 days', value: 90 },
  { label: 'A year', value: 365 },
  { label: 'No end', value: null },
]

/** The answer dialog as filled in: Expected or Not expected, a comment, and Remember this for Expected. */
export interface AnswerForm {
  expected: boolean
  comment: string
  remember: boolean
  scope: RememberScope
  anySource: boolean
  anyWay: boolean
  days: number | null
}

export const answerForm = (expected: boolean): AnswerForm => ({
  expected,
  comment: '',
  remember: false,
  scope: 'target',
  anySource: false,
  anyWay: false,
  days: 90,
})

/** The request for the answer: the comment trimmed or null; Remember this only with Expected (the server refuses it else). */
export function answerRequest(f: AnswerForm): S['AccessAnswerRequest'] {
  return {
    expected: f.expected,
    comment: f.comment.trim() || null,
    remember:
      f.expected && f.remember ? { scope: f.scope, anySource: f.anySource, anyWay: f.anyWay, days: f.days } : null,
  }
}

/** Where a case is in Cookie's window. */
export const caseLink = (id: string) => ({ name: 'cookie', params: { tab: 'cases' }, query: { case: id } })

/**
 * A person's server accounts (§5.3, Admin → Users) from a text with one per line: trimmed, each once whatever its case,
 * as the server keeps them.
 */
export function serverAccountsFromText(text: string): string[] {
  const seen = new Set<string>()
  const list: string[] = []
  for (const line of text.split(/\r?\n/)) {
    const account = line.trim()
    if (account && !seen.has(account.toLowerCase())) {
      seen.add(account.toLowerCase())
      list.push(account)
    }
  }
  return list
}
