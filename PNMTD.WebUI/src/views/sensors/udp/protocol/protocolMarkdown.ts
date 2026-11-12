import type { S } from '@/api/resources'
import { fromBase64, hexDump, hexPreview } from '@/format/bytes'
import { htmlToMarkdown, mdCode, mdFence, mdTable, mdText } from '@/format/markdown'
import { familyLabel, idBytes, suiteName } from '@/format/udp'
import { codeSnippets, codeSuite } from './protocolCode'

type Examples = S['UdpProtocolExamples']

export interface ProtocolDocument {
  examples: Examples
  /** The examples are a node's (/sensors/:id/protocol), not the reference page's. */
  forNode: boolean
  nodeName?: string | null
  /** The rendered reference sections (ProtocolReference), converted as they are on the page. */
  reference: Element | null
  /** Where the page is, for the builder that only exists there. */
  pageUrl?: string
  at?: Date
}

/** A packet: its hex dump, its fields, and the line that sends it. */
function packet(e: S['UdpExample'], heading: string): string[] {
  const bytes = fromBase64(e.datagram)
  const parts = [heading]
  if (e.description) parts.push(mdText(e.description))
  parts.push(mdFence(hexDump(bytes).join('\n'), 'text'))
  if (e.fields.length) {
    parts.push(
      mdTable(
        ['Field', 'Offset', 'Bytes', 'Value'],
        e.fields.map((f) => [
          mdText(f.name),
          f.length > 1 ? `${f.offset}+${f.length}` : `${f.offset}`,
          mdCode(hexPreview(bytes.subarray(f.offset, f.offset + f.length), 16)),
          mdText(f.value),
        ]),
      ),
    )
  }
  if (e.shell) parts.push(mdFence(e.shell, 'sh'))
  return parts
}

const stamp = (d: Date) => {
  const two = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${two(d.getMonth() + 1)}-${two(d.getDate())} ${two(d.getHours())}:${two(d.getMinutes())}`
}

/**
 * The protocol page as one Markdown file (docs/udp-iot-plan.md §6.5): the introduction, the
 * reference sections as rendered, the worked examples with hex dumps and field tables, the
 * reference vectors and the code. The packet builder is interactive and stays on the page.
 */
export function protocolMarkdown(doc: ProtocolDocument): string {
  const e = doc.examples
  const node = doc.forNode
  const out: string[] = []

  out.push(
    node
      ? `# PNMT-UDP v1: ${mdText(doc.nodeName?.trim() || `device ${e.deviceId}`)}`
      : '# PNMT-UDP v1: the protocol of PNMT UDP nodes',
  )
  out.push(
    `**PNMT-UDP v1**: one datagram up, at most one down. A heartbeat is 5 to 13 bytes. The examples were built by ` +
      (node
        ? `PNMTD's own codec for this node: device id ${e.deviceId} (${mdCode(idBytes(e.deviceId))}), ` +
          `${familyLabel(e.keyFamily)}, ${e.allowedSuites.map(suiteName).join(', ')}.`
        : "PNMTD's own codec with device id 7 and the reference key."),
  )
  const facts = [`- **Endpoint:** ${mdCode(e.endpoint)} (UDP)`, `- **Largest datagram:** ${e.maxDatagram} bytes`]
  if (node) {
    facts.push(
      `- **Device id:** ${e.deviceId} (header bytes ${mdCode(idBytes(e.deviceId))})`,
      `- **Key family:** ${familyLabel(e.keyFamily)}`,
      `- **Allowed suites:** ${e.allowedSuites.map(suiteName).join(', ')}`,
    )
  }
  facts.push(`- **Exported:** ${stamp(doc.at ?? new Date())}${doc.pageUrl ? ` from <${doc.pageUrl}>` : ''}`)
  out.push(facts.join('\n'))
  if (e.referenceKey) {
    out.push(
      node
        ? '> The examples use the reference key `00 01 … 0F` and token `5A 17 C3 9E`: only User and Admin see ' +
            "this node's key. The structure is the same; the tags differ."
        : "> The reference key `00 01 02 … 0F` and token `5A 17 C3 9E`, as in the specification's test vectors.",
    )
  } else if (e.keyHex) {
    out.push(
      "> **This file contains the node's key and token** (in the examples and the code). Keep it like a password.",
    )
  }

  if (doc.reference) {
    out.push(htmlToMarkdown(doc.reference))
  }

  out.push(`## 10. Worked examples${node ? ' for this node' : ''}`)
  for (const example of e.examples) out.push(...packet(example, `### ${mdText(example.title)}`))

  if (e.vectors.length) {
    out.push('## Reference vectors')
    out.push(
      'Device id 7, key `00 01 … 0F`, token `5A 17 C3 9E`, built live; a test pins them to the specification. ' +
        '#9–#12 are #1 with another suite: the suite is in byte 1, which the MAC covers.',
    )
    out.push(
      mdTable(
        ['Vector', 'Datagram'],
        e.vectors.map((v) => [mdText(v.title), mdCode(v.hex)]),
      ),
    )
  }

  out.push('## 11. Code')
  out.push(
    `With this ${node ? "node's" : 'reference'} values filled in. The suite is ${suiteName(codeSuite(e))}. ` +
      `Any other packet: the packet builder on the protocol page${doc.pageUrl ? ` (<${doc.pageUrl}>)` : ''}, or ` +
      '`pnmtd udp send`.',
  )
  for (const snippet of codeSnippets(e)) {
    out.push(`### ${snippet.label}`, mdFence(snippet.code, snippet.lang))
  }

  return `${out.join('\n\n')}\n`
}
