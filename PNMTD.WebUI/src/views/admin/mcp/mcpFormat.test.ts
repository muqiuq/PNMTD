import { describe, expect, it } from 'vitest'
import type { Schemas } from '@/api/client'
import { tokenKindLabel, tokenSeverity, tokenStateText } from './mcpFormat'

const token = (patch: Partial<Schemas['McpTokenItem']>): Schemas['McpTokenItem'] =>
  ({
    kind: 'Read',
    on: false,
    state: 'off',
    hasValue: true,
    strikes: 0,
    deleteCount: 0,
    rights: {},
    ...patch,
  }) as Schemas['McpTokenItem']

describe('MCP tokens as people read them', () => {
  it('says why a token is off', () => {
    expect(tokenStateText(token({ state: 'on', on: true }))).toBe('On')
    expect(tokenStateText(token({ state: 'expired', kind: 'Write' }))).toBe('Off: its 12 hours are over')
    expect(tokenStateText(token({ state: 'expired' }))).toBe("Off: its user wasn't here for a week")
    expect(tokenStateText(token({ endedWhy: 'Admin' }))).toBe('Turned off by an admin')
    expect(tokenStateText(token({ state: 'blocked' }))).toBe('Blocked after refused requests')
    expect(tokenStateText(token({}))).toBe('Off')
  })

  it('colours a blocked token and names the kinds', () => {
    expect(tokenSeverity(token({ state: 'blocked' }))).toBe('danger')
    expect(tokenSeverity(token({ on: true, state: 'on' }))).toBe('success')
    expect(tokenKindLabel('Write')).toBe('Read & write')
    expect(tokenKindLabel('Read')).toBe('Read-only')
  })
})
