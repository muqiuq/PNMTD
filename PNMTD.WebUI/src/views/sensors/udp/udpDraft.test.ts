import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import { changeHints, draftFromNode, udpRequest } from './udpDraft'

const node = {
  deviceId: 40_001,
  deviceIdBytes: '9C 41',
  keyFamily: 'HmacSha256',
  allowedSuites: ['HmacSha256_32', 'HmacSha256_64'],
  responseSuite: null,
  sessionMode: 'None',
  ackMode: 'OnRequest',
  replayProtection: true,
} as unknown as S['UdpNodeDetail']

describe('editing a node', () => {
  it('starts from its settings and sends them back unchanged, without key or token', () => {
    const draft = draftFromNode(node)
    expect(draft.manual).toBe(false)
    expect(udpRequest(draft)).toEqual({
      deviceId: 40_001,
      keyFamily: 'HmacSha256',
      allowedSuites: ['HmacSha256_32', 'HmacSha256_64'],
      responseSuite: null,
      sessionMode: 'None',
      ackMode: 'OnRequest',
      replayProtection: true,
      key: null,
      token: null,
    })
    expect(changeHints(node, draft)).toEqual([])
  })

  it('says what a change means for the device', () => {
    const draft = {
      ...draftFromNode(node),
      deviceId: 7,
      keyFamily: 'Aes' as const,
      allowedSuites: ['AesCmac32' as const],
      sessionMode: 'JoinRequired' as const,
    }
    const hints = changeHints(node, draft)
    expect(hints).toHaveLength(4)
    expect(hints[0]).toContain('from 40001 to 7')
    expect(hints[1]).toContain('new key')
    expect(hints[2]).toBe('Packets in HMAC-SHA256/32, HMAC-SHA256/64 are rejected from now on.')
    expect(hints[3]).toContain('must JOIN')
  })

  it('does not announce a generated key when one is entered, but that it applies at once', () => {
    const draft = {
      ...draftFromNode(node),
      keyFamily: 'Aes' as const,
      manual: true,
      key: '000102030405060708090A0B0C0D0E0F',
    }
    const hints = changeHints(node, { ...draft, allowedSuites: [...node.allowedSuites] })
    expect(hints.some((h) => h.includes('gets a new key'))).toBe(false)
    expect(hints).toContain('The new key or token applies at once: sessions and the counter window are reset.')
    // An empty id keeps it: no hint.
    expect(changeHints(node, { ...draftFromNode(node), deviceId: null })).toEqual([])
  })
})
