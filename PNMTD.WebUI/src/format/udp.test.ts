import { describe, expect, it } from 'vitest'
import {
  commandWireSize,
  defaultSuites,
  deliveryEstimate,
  endpointParts,
  idBytes,
  idLength,
  joinSuite,
  kindLabel,
  otaEstimate,
  resultStatusText,
  secretSource,
  suiteChoices,
  suiteMarks,
  suiteName,
  SUITES,
  toggleRow,
  typeName,
  words,
} from './udp'

describe('suites', () => {
  it('has the twelve suites of §3.5 with their codes and tags', () => {
    expect(SUITES.map((s) => s.code)).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11])
    expect(SUITES.filter((s) => s.tag === 8).map((s) => s.code)).toEqual([3, 5, 7, 9, 11])
    expect(suiteName('HmacSha256_32')).toBe('HMAC-SHA256/32')
    expect(suiteName('AesCcm64')).toBe('AES-CCM/64')
    expect(suiteName(null)).toBe('–')
  })

  it('offers NONE, TOKEN32 and the family suites', () => {
    expect(suiteChoices('HmacSha256')).toEqual(['None', 'Token32', 'HmacSha256_32', 'HmacSha256_64'])
    expect(suiteChoices('Aes')).toEqual(['None', 'Token32', 'AesCmac32', 'AesCmac64', 'AesCcm32', 'AesCcm64'])
    expect(defaultSuites('Aes')).toEqual(['AesCmac32', 'AesCmac64'])
    expect(joinSuite('HmacMd5')).toBe('HmacMd5_64')
  })

  it('marks weak, encrypted and legacy suites', () => {
    expect(suiteMarks('Token32')).toEqual(['weak'])
    expect(suiteMarks('AesCcm32')).toEqual(['encrypted'])
    expect(suiteMarks('HmacSha1_64')).toEqual(['legacy'])
    expect(suiteMarks('HmacSha256_32')).toEqual([])
  })
})

describe('sizes', () => {
  it('takes the shortest id length, as the header carries it', () => {
    expect([1, 255, 256, 65_535, 65_536, 16_777_216, 4_294_967_295].map(idLength)).toEqual([1, 1, 2, 2, 3, 4, 4])
    expect(idBytes(7)).toBe('07')
    expect(idBytes(300)).toBe('01 2C')
    expect(idBytes(4_294_967_295)).toBe('FF FF FF FF')
  })

  it('counts a COMMAND on the wire: header, CMD_ID, command, tag', () => {
    // Vector #5: 64 02 07 00 2C 01 02 02 58 E6 B0 5E F4.
    expect(commandWireSize(7, 3, 'HmacSha256_32')).toBe(13)
    expect(commandWireSize(300, 3, 'HmacSha256_64')).toBe(18)
    expect(commandWireSize(7, 0, 'None')).toBe(6)
  })

  it('estimates an update as §3.13 does: 1 MiB in 448-byte chunks', () => {
    const e = otaEstimate(1_048_576, 448)
    expect(e.roundTrips).toBe(2341)
    expect(e.bytesOnTheWire / 1_048_576).toBeCloseTo(1.21, 2)
    expect(Math.round(e.secondsAt100Ms / 60)).toBe(4)
    expect(Math.round(e.secondsAt2s / 60)).toBe(78)
  })
})

describe('labels', () => {
  it('names types and kinds as the specification does', () => {
    expect(typeName('CmdResult')).toBe('CMD_RESULT')
    expect(typeName('JoinAccept')).toBe('JOIN_ACCEPT')
    expect(kindLabel('ResultReceived')).toBe('Result received')
    expect(kindLabel('OtaApplied')).toBe('Update applied')
    expect(words('SuiteNotAllowed')).toBe('Suite not allowed')
    expect(resultStatusText(0)).toBe('OK')
    expect(resultStatusText(4)).toBe('busy')
    expect(resultStatusText(9)).toBe('reserved 9')
    expect(resultStatusText(200)).toBe('device-specific 200')
  })

  it('says where a secret came from', () => {
    expect(secretSource('Generated', '2026-11-12T10:00:00Z', 'alice')).toMatch(/^generated on .*2026$/)
    expect(secretSource('Manual', '2026-11-12T10:00:00Z', 'alice')).toMatch(/^entered manually on .*2026 by alice$/)
    expect(secretSource('Manual', null, null)).toBe('entered manually')
  })

  it('splits an endpoint into host and port, IPv6 too', () => {
    expect(endpointParts('pnmt.example.com:5028')).toEqual({ host: 'pnmt.example.com', port: '5028' })
    expect(endpointParts('[2001:db8::1]:6000')).toEqual({ host: '2001:db8::1', port: '6000' })
  })
})

describe('deliveryEstimate', () => {
  const now = Date.parse('2026-11-12T14:25:00Z')

  it('expects the command with the next packet', () => {
    const text = deliveryEstimate('2026-11-12T14:22:00Z', 600, now)
    expect(text).toMatch(/^Delivered with the node's next packet\. The last arrived 3 min ago, every 10 min → around /)
  })

  it('says when the node is overdue or never reported', () => {
    expect(deliveryEstimate('2026-11-12T13:00:00Z', 600, now)).toMatch(/overdue/)
    expect(deliveryEstimate(null, 600, now)).toMatch(/has not reported yet/)
  })
})

describe('toggleRow', () => {
  it('adds a row and removes it again (a collapsed row must leave the map)', () => {
    const open = toggleRow({}, 7)
    expect(open).toEqual({ '7': true })
    expect(toggleRow(open, 7)).toEqual({})
  })
})
