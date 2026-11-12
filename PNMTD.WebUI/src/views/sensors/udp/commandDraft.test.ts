import { describe, expect, it } from 'vitest'
import type { SecuritySuite } from '@/format/udp'
import { commandBytes, commandSuiteOptions, effectiveSuite, weakWarning, wireSummary } from './commandDraft'
import { changeFamily, draftHints, newUdpDraft, secretError, udpRequest } from './udpDraft'

const node = {
  deviceId: 7,
  keyFamily: 'HmacSha256' as const,
  allowedSuites: ['HmacSha256_32', 'HmacSha256_64'] as ('HmacSha256_32' | 'HmacSha256_64' | 'Token32')[],
  responseSuite: null,
  lastSuite: 'HmacSha256_32' as const,
}

describe('the command dialog', () => {
  it('reads text as UTF-8 and hex with spaces', () => {
    expect(Array.from(commandBytes('text', 'OK').bytes!)).toEqual([0x4f, 0x4b])
    expect(Array.from(commandBytes('text', '°').bytes!)).toEqual([0xc2, 0xb0])
    expect(Array.from(commandBytes('hex', '02 02 58').bytes!)).toEqual([0x02, 0x02, 0x58])
    expect(commandBytes('hex', '').bytes!.length).toBe(0)
  })

  it('says what is wrong with hex', () => {
    expect(commandBytes('hex', '02 2').error).toMatch(/3 hex digits/)
    expect(commandBytes('hex', 'xyz').error).toMatch(/not a hex digit/)
  })

  it('refuses more than 480 bytes', () => {
    expect(commandBytes('text', 'a'.repeat(480)).error).toBeNull()
    expect(commandBytes('text', 'a'.repeat(481)).error).toMatch(/at most 480/)
  })

  it('previews the size on the wire with the suite it goes out in', () => {
    // Vector #5: COMMAND 02 02 58 for node 7 with HMAC-SHA256/32 is 13 bytes.
    expect(wireSummary(3, null, node)).toBe(
      '13 bytes on the wire with HMAC-SHA256/32 (as received, like the last uplink)',
    )
    expect(wireSummary(3, 'HmacSha256_64', node)).toBe(
      "17 bytes on the wire with HMAC-SHA256/64 (the command's own suite)",
    )
    expect(wireSummary(3, null, { ...node, responseSuite: 'HmacSha256_64' })).toMatch(/the node's response suite/)
    expect(wireSummary(500, 'HmacSha256_64', { ...node, deviceId: 70_000 })).toMatch(/516 bytes .* more than the 508/)
  })

  it('guesses the suite of a node that never reported', () => {
    expect(effectiveSuite(null, { ...node, lastSuite: null, allowedSuites: ['Token32', 'HmacSha256_32'] }).suite).toBe(
      'HmacSha256_32',
    )
  })

  it('warns when a command could go out unsigned or with only the token', () => {
    expect(weakWarning(null, node)).toBeNull()
    expect(weakWarning('Token32', node)).toMatch(/forge/)
    expect(weakWarning(null, { ...node, allowedSuites: ['Token32', 'HmacSha256_32'] })).toMatch(/NONE or TOKEN32/)
    expect(weakWarning('HmacSha256_64', { ...node, allowedSuites: ['Token32'] })).toBeNull()
  })

  it('offers the node default and the family suites', () => {
    const options = commandSuiteOptions(node)
    expect(options[0]).toEqual({ label: 'Node default (as received)', value: null })
    expect(options.map((o) => o.value)).toEqual([null, 'None', 'Token32', 'HmacSha256_32', 'HmacSha256_64'])
  })
})

describe('the UDP section of the sensor form', () => {
  it('validates a key and a token as they are typed; empty means generated', () => {
    expect(secretError('', 16)).toBeNull()
    expect(secretError('00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F', 16)).toBeNull()
    expect(secretError('000102', 16)).toMatch(/32 hex digits \(16 bytes\); this is 6/)
    expect(secretError('00'.repeat(16), 16)).toBe('Not one byte repeated.')
    expect(secretError('5A17C39', 4)).toMatch(/hex digits: every byte needs two/)
    expect(secretError('5A 17 C3 9E', 4)).toBeNull()
  })

  it('keeps only the suites of a new family, and a signature', () => {
    const draft = {
      ...newUdpDraft(),
      allowedSuites: ['Token32', 'HmacSha256_32'] as SecuritySuite[],
      responseSuite: 'HmacSha256_64' as SecuritySuite,
    }
    const aes = changeFamily(draft, 'Aes')
    expect(aes.allowedSuites).toEqual(['Token32', 'AesCmac32', 'AesCmac64'])
    expect(aes.responseSuite).toBeNull()
  })

  it('hints at commands that would go out unsigned and at a JOIN without its suite', () => {
    expect(draftHints(newUdpDraft())).toEqual([])
    expect(draftHints({ ...newUdpDraft(), allowedSuites: ['Token32', 'HmacSha256_32'] })[0]).toMatch(/unsigned/)
    expect(draftHints({ ...newUdpDraft(), allowedSuites: ['HmacSha256_32'], sessionMode: 'JoinRequired' })[0]).toMatch(
      /JOIN uses HMAC-SHA256\/64/,
    )
  })

  it('sends a key only when entered by hand', () => {
    const generated = udpRequest({ ...newUdpDraft(7), key: 'ignored' })
    expect(generated.key).toBeNull()
    expect(generated.deviceId).toBe(7)
    const manual = udpRequest({ ...newUdpDraft(), manual: true, key: ' 00 01 ', token: '' })
    expect(manual.key).toBe('00 01')
    expect(manual.token).toBeNull()
  })
})
