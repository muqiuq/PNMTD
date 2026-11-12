import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import { mountWithUi } from '@/test/mount'
import ProtocolReference from './ProtocolReference.vue'
import { protocolMarkdown } from './protocolMarkdown'

// Vector #1 of docs/udp-iot-plan.md §3.14: a HEARTBEAT of device 7, HMAC-SHA256/32, the reference key.
const heartbeat: S['UdpExample'] = {
  title: 'HEARTBEAT, signed',
  description: 'The smallest signed packet: 9 bytes.',
  datagram: btoa(String.fromCharCode(0x40, 0x02, 0x07, 0x00, 0x01, 0x8c, 0x51, 0x2e, 0x0a)),
  fields: [
    { offset: 0, length: 1, name: 'VER · TYPE · IDL', value: 'version 1, HEARTBEAT (0x0), 1-byte id' },
    { offset: 1, length: 1, name: 'A · M · SUITE', value: 'HMAC-SHA256/32 (2)' },
    { offset: 2, length: 1, name: 'DEVICE ID', value: '7' },
    { offset: 3, length: 2, name: 'COUNTER', value: '1' },
    { offset: 5, length: 4, name: 'TAG', value: 'HMAC-SHA256/32, verified' },
  ],
  hex: '40 02 07 00 01 8C 51 2E 0A',
  cArray: '{ 0x40, 0x02, 0x07, 0x00, 0x01, 0x8C, 0x51, 0x2E, 0x0A }',
  shell: 'echo 40 02 07 00 01 8C 51 2E 0A | xxd -r -p | nc -u -w1 pnmt.example 5028',
}

function examples(patch: Partial<S['UdpProtocolExamples']> = {}): S['UdpProtocolExamples'] {
  return {
    referenceKey: false,
    keyHex: '000102030405060708090A0B0C0D0E0F',
    tokenHex: '5A17C39E',
    deviceId: 7,
    keyFamily: 'HmacSha256',
    allowedSuites: ['HmacSha256_32', 'HmacSha256_64'],
    endpoint: 'pnmt.example:5028',
    maxDatagram: 508,
    suites: [
      {
        suite: 'None',
        code: 0,
        name: 'NONE',
        tagLength: 0,
        family: null,
        keyed: false,
        encrypted: false,
        legacy: false,
      },
      {
        suite: 'HmacSha256_32',
        code: 2,
        name: 'HMAC-SHA256/32',
        tagLength: 4,
        family: 'HmacSha256',
        keyed: true,
        encrypted: false,
        legacy: false,
      },
      {
        suite: 'HmacMd5_32',
        code: 8,
        name: 'HMAC-MD5/32',
        tagLength: 4,
        family: 'HmacMd5',
        keyed: true,
        encrypted: false,
        legacy: true,
      },
    ],
    types: [
      { type: 'Heartbeat', code: 0, name: 'HEARTBEAT', downlink: false },
      { type: 'Ack', code: 8, name: 'ACK', downlink: true },
    ],
    examples: [heartbeat],
    vectors: [{ ...heartbeat, title: '#1 HEARTBEAT, HMAC-SHA256/32' }],
    ...patch,
  }
}

function render(data: S['UdpProtocolExamples'], forNode = true) {
  const reference = mountWithUi(ProtocolReference, { props: { examples: data } })
  return protocolMarkdown({
    examples: data,
    forNode,
    nodeName: forNode ? 'Climate node' : null,
    reference: reference.element as Element,
    pageUrl: 'https://pnmt.example/pnmt-admin/sensors/1/protocol',
    at: new Date(2026, 10, 12, 14, 5),
  })
}

/** The blocks of a Markdown text that are tables, as lists of lines. */
function tables(markdown: string): string[][] {
  return markdown
    .split('\n\n')
    .map((b) => b.split('\n'))
    .filter((lines) => lines[0]!.startsWith('| ') && lines[1]?.startsWith('| ---'))
}

describe('protocolMarkdown', () => {
  it('has the page: the introduction, every reference section, the examples, the vectors and the code', () => {
    const md = render(examples())
    expect(md.startsWith('# PNMT-UDP v1: Climate node\n\n')).toBe(true)
    expect(md).toContain('- **Endpoint:** `pnmt.example:5028` (UDP)')
    expect(md).toContain('- **Exported:** 2026-11-12 14:05 from <https://pnmt.example/pnmt-admin/sensors/1/protocol>')
    const headings = md.split('\n').filter((l) => l.startsWith('## '))
    expect(headings).toEqual([
      '## 1. Exchange model',
      '## 2. Packet layout',
      '## 3. Packet types',
      '## 4. Security suites',
      '## 5. Tags, encryption and nonces',
      '## 6. Counter, replays, JOIN, ACKs',
      '## 7. Commands',
      '## 8. Firmware updates (OTA)',
      '## 9. Device checklist',
      '## 10. Worked examples for this node',
      '## Reference vectors',
      '## 11. Code',
    ])
    expect(md).toContain('### Frame counter')
    expect(md).toContain('- [ ] Persist FCNT (block reservation), or JOIN at every start.')
    // The worked example: its hex dump, its fields, its shell line.
    expect(md).toContain('```text\n0000  40 02 07 00 01 8C 51 2E  0A')
    expect(md).toContain('| COUNTER | 3+2 | `00 01` | 1 |')
    expect(md).toContain('```sh\necho 40 02 07 00 01 8C 51 2E 0A | xxd -r -p | nc -u -w1 pnmt.example 5028\n```')
    expect(md).toContain('| #1 HEARTBEAT, HMAC-SHA256/32 | `40 02 07 00 01 8C 51 2E 0A` |')
    expect(md).toContain('### Python\n\n```python\n# PNMT-UDP v1 for node 7')
    expect(md).toContain('### C\n\n```c\n')
    expect(md).toContain('### Shell\n\n```sh\n')
    expect(md.endsWith('```\n')).toBe(true)
  })

  it('marks what the page shows with colour and badges in words', () => {
    const md = render(examples())
    expect(md).toContain('The suites this node allows are marked **allowed**.')
    expect(md).not.toContain('Highlighted')
    expect(md).toContain('| 2 | `HMAC-SHA256/32` · **allowed** | 4 B |')
    expect(md).toContain('| 8 | `HMAC-MD5/32` (legacy) | 4 B |')
    expect(md).toContain('| 0 | `NONE` (weak) | 0 B |')
  })

  it('writes every table with as many cells in each row as in its header', () => {
    const found = tables(render(examples()))
    expect(found.length).toBeGreaterThanOrEqual(7)
    for (const lines of found) {
      const cells = lines.map((l) => l.replace(/\\\|/g, '').split('|').length)
      expect(new Set(cells).size, lines[0]).toBe(1)
    }
  })

  it("warns when the node's key is in the file, and explains the reference key otherwise", () => {
    expect(render(examples())).toContain("**This file contains the node's key and token**")
    const viewer = render(examples({ referenceKey: true, keyHex: null, tokenHex: null }))
    expect(viewer).not.toContain('This file contains')
    expect(viewer).toContain("only User and Admin see this node's key")
    const reference = render(examples({ referenceKey: true, keyHex: null, tokenHex: null }), false)
    expect(reference.startsWith('# PNMT-UDP v1: the protocol of PNMT UDP nodes\n\n')).toBe(true)
    expect(reference).toContain('## 10. Worked examples\n')
    expect(reference).not.toContain('Device id:')
  })
})
