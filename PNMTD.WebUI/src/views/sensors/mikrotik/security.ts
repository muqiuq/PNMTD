import type { S } from '@/api/resources'

/**
 * RouterOS security fixes (docs/ai-operations-plan.md §10): the changelog lines PNMTD reads as security lines, the
 * same words as the server's code assessment, and how a release's severity is shown.
 */
const securityWords =
  /\bCVE-\d{4}-\d{4,}\b|\b(security|vulnerab\w*|exploit\w*|remote code|authentication bypass|privilege escalation|denial of service|zero-day|0-day)\b/i

export const isSecurityLine = (line: string) => securityWords.test(line)

export const fixLabel = (s: S['ReleaseSeverity']) => (s === 'Critical' ? 'security fix' : 'security fix (important)')
export const fixSeverity = (s: S['ReleaseSeverity']) => (s === 'Critical' ? 'danger' : 'warn')
