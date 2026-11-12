import { describe, expect, it } from 'vitest'
import { toBase64 } from '@/format/bytes'
import type { PacketField } from '@/format/udp'
import { mountWithUi } from '@/test/mount'
import HexDump from './HexDump.vue'
import PacketInspector from './PacketInspector.vue'

// Vector #3 of docs/udp-iot-plan.md §3.14 with the fields as the server's codec describes them.
const datagram = new Uint8Array([
  0x44, 0x03, 0x07, 0x00, 0x2b, 0x32, 0x31, 0x2e, 0x35, 0x43, 0x35, 0x0a, 0xb3, 0x32, 0x32, 0x76, 0xbb, 0x45,
])
const fields: PacketField[] = [
  { offset: 0, length: 1, name: 'VER · TYPE · IDL', value: 'version 1, DATA (0x1), 1-byte id' },
  { offset: 1, length: 1, name: 'A · M · SUITE', value: 'HMAC-SHA256/64 (3)' },
  { offset: 2, length: 1, name: 'DEVICE ID', value: '7' },
  { offset: 3, length: 2, name: 'COUNTER', value: '43' },
  { offset: 5, length: 5, name: 'PAYLOAD', value: '21.5C' },
  { offset: 10, length: 8, name: 'TAG', value: 'HMAC-SHA256/64, verified' },
]

describe('PacketInspector', () => {
  it('lists the fields with their offsets, bytes and decoded values', () => {
    const inspector = mountWithUi(PacketInspector, { props: { datagram: toBase64(datagram), fields } })
    const rows = inspector.findAll('table.fields tbody tr')
    expect(rows).toHaveLength(6)
    expect(rows.map((r) => r.findAll('td').map((td) => td.text()))).toEqual([
      ['VER · TYPE · IDL', '0', '44', 'version 1, DATA (0x1), 1-byte id'],
      ['A · M · SUITE', '1', '03', 'HMAC-SHA256/64 (3)'],
      ['DEVICE ID', '2', '07', '7'],
      ['COUNTER', '3+2', '00 2B', '43'],
      ['PAYLOAD', '5+5', '32 31 2E 35 43', '21.5C'],
      ['TAG', '10+8', '35 0A B3 32 32 76 BB 45', 'HMAC-SHA256/64, verified'],
    ])
    expect(inspector.text()).toContain('18 bytes')
  })

  it('dumps the datagram as hex and ASCII, each field in its colour, with a legend', () => {
    const inspector = mountWithUi(PacketInspector, { props: { datagram, fields } })
    const lines = inspector.findAll('.hexdump')[0].findAll('.line')
    expect(lines.map((l) => l.text())).toEqual([
      '0000  44 03 07 00 2B 32 31 2E  35 43 35 0A B3 32 32 76  D···+21.5C5··22v',
      '0010  BB 45                                             ·E',
    ])
    // The counter's two bytes (and the gap between them) in its colour, hex and ASCII alike.
    const counter = lines[0].findAll('.field-slot-3').map((s) => s.element.textContent)
    expect(counter).toEqual(['00', ' ', '2B', '·', '+'])
    expect(lines[0].find('.b.field-slot-0').attributes('title')).toBe('VER · TYPE · IDL')
    expect(inspector.findAll('.legend li').map((li) => li.text())).toEqual(fields.map((f) => f.name))
  })

  it('shows the payload as hex and ASCII, and as text when it is text', () => {
    const inspector = mountWithUi(PacketInspector, { props: { datagram, fields } })
    const payload = inspector.find('.payload')
    expect(payload.text()).toContain('Payload · 5 bytes')
    expect(payload.find('.hexdump').text()).toBe('0000  32 31 2E 35 43' + ' '.repeat(34) + '  21.5C')
    expect(payload.find('.text code').text()).toBe('21.5C')
  })

  it('labels a separate plaintext payload as decrypted for AES-CCM', () => {
    const encrypted: PacketField[] = [
      ...fields.slice(0, 4),
      { offset: 5, length: 5, name: 'PAYLOAD', value: 'encrypted' },
      { offset: 10, length: 8, name: 'TAG', value: 'AES-CCM/64, verified' },
    ]
    const plain = new TextEncoder().encode('21.5C')
    const withPlain = mountWithUi(PacketInspector, { props: { datagram, fields: encrypted, payload: plain } })
    expect(withPlain.find('.payload').text()).toContain('Payload (decrypted) · 5 bytes')
    // Without it, the ciphertext is not passed off as the payload.
    const without = mountWithUi(PacketInspector, { props: { datagram, fields: encrypted } })
    expect(without.find('.payload').exists()).toBe(false)
    expect(without.text()).toContain('encrypted on the wire')
  })

  it('dumps bytes without fields, as unknown senders send them', () => {
    const inspector = mountWithUi(PacketInspector, { props: { datagram: new Uint8Array([0x00, 0xff]) } })
    expect(inspector.find('table.fields').exists()).toBe(false)
    expect(inspector.find('.legend').exists()).toBe(false)
    expect(inspector.find('.hexdump .line').text()).toBe('0000  00 FF' + ' '.repeat(43) + '  ··')
  })
})

describe('HexDump', () => {
  it('holds 8 bytes a line when asked', () => {
    const dump = mountWithUi(HexDump, { props: { bytes: datagram, width: 8 } })
    expect(dump.findAll('.line')).toHaveLength(3)
    expect(dump.findAll('.line')[1].text()).toBe('0008  35 43 35 0A B3 32 32 76  5C5··22v')
  })

  it('says when there are no bytes', () => {
    expect(mountWithUi(HexDump, { props: { bytes: new Uint8Array(0) } }).text()).toBe('(no bytes)')
  })
})
