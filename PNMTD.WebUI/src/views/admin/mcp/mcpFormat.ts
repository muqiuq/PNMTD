import type { Schemas } from '@/api/client'

type Token = Schemas['McpTokenItem']

/** A token's state as people read it (docs/mcp-plan.md §4.5), on Account → Security and on Admin → MCP → Tokens. */
export function tokenStateText(t: Token): string {
  switch (t.state) {
    case 'on':
      return 'On'
    case 'blocked':
      return 'Blocked after refused requests'
    case 'expired':
      return t.kind === 'Write' ? 'Off: its 12 hours are over' : "Off: its user wasn't here for a week"
    case 'stamp':
      return 'Off: the password, 2FA or sessions changed'
    case 'totp':
      return 'Needs 2FA on the account'
    case 'inactive':
      return 'Off: the user is deactivated'
    case 'mcp_off':
      return 'MCP is off'
    default:
      return t.endedWhy === 'Admin' ? 'Turned off by an admin' : 'Off'
  }
}

export const tokenSeverity = (t: Token) => (t.on ? 'success' : t.state === 'blocked' ? 'danger' : 'secondary')

export const tokenKindLabel = (kind: Schemas['McpTokenKind']) => (kind === 'Write' ? 'Read & write' : 'Read-only')
