import type { S } from '@/api/resources'
import { parseHexInput, utf8Encode, type ParsedHex } from '@/format/bytes'
import {
  commandWireSize,
  isWeak,
  MAX_COMMAND,
  MAX_DATAGRAM,
  suiteChoices,
  suiteInfo,
  suiteName,
  type SecuritySuite,
} from '@/format/udp'

/**
 * The "New command" dialog without its markup (docs/udp-iot-plan.md §6.4): the input as text
 * (UTF-8) or hex, the suite a command goes out in, and its size on the wire.
 */
export type InputMode = 'text' | 'hex'

/** The command's bytes: UTF-8 text, or hex with spaces; at most 480 bytes (the datagram stays ≤ 508). */
export function commandBytes(mode: InputMode, input: string): ParsedHex {
  const parsed: ParsedHex = mode === 'text' ? { bytes: utf8Encode(input), error: null } : parseHexInput(input)
  if (parsed.error === null && parsed.bytes.length > MAX_COMMAND) {
    return { bytes: null, error: `${parsed.bytes.length} bytes: a command is at most ${MAX_COMMAND}.` }
  }
  return parsed
}

type Node = Pick<S['UdpNodeDetail'], 'responseSuite' | 'lastSuite' | 'allowedSuites' | 'deviceId'>

/**
 * The suite a command goes out in (§3.10): its own, else the node's response suite, else the suite
 * of the uplink it answers, which the UI can only guess from the last one.
 */
export function effectiveSuite(suite: SecuritySuite | null, node: Node): { suite: SecuritySuite | null; why: string } {
  if (suite) return { suite, why: "the command's own suite" }
  if (node.responseSuite) return { suite: node.responseSuite, why: "the node's response suite" }
  if (node.lastSuite) return { suite: node.lastSuite, why: 'as received, like the last uplink' }
  const guess = node.allowedSuites.find((s) => !isWeak(s)) ?? node.allowedSuites[0] ?? null
  return { suite: guess, why: 'as received; the node has not reported yet' }
}

/** "13 bytes on the wire with HMAC-SHA256/32 (the node's response suite)". */
export function wireSummary(length: number, suite: SecuritySuite | null, node: Node): string {
  const chosen = effectiveSuite(suite, node)
  if (!chosen.suite) return `${length} command bytes`
  const total = commandWireSize(node.deviceId, length, chosen.suite)
  const over = total > MAX_DATAGRAM ? `, more than the ${MAX_DATAGRAM} a datagram may have` : ''
  return `${total} bytes on the wire with ${suiteName(chosen.suite)} (${chosen.why})${over}`
}

/** The warning of §6.4: a command that could go out unsigned or with only the token. */
export function weakWarning(suite: SecuritySuite | null, node: Node): string | null {
  const chosen = suite ?? node.responseSuite
  if (chosen && isWeak(chosen)) {
    return `${suiteName(chosen)}: anyone who sees this command can forge it, or spoof the node to receive it. Choose a keyed suite.`
  }
  if (!chosen && node.allowedSuites.some(isWeak)) {
    return 'The node may report with NONE or TOKEN32, and the command then goes out the same way: anyone who sees it can forge it. Choose a keyed suite for the command, or set a response suite on the node.'
  }
  return null
}

/** The suites a command may choose: NONE, TOKEN32 and the node's family (§3.10). */
export function commandSuiteOptions(node: Node & Pick<S['UdpNodeDetail'], 'keyFamily'>) {
  const label = node.responseSuite ? `Node default (${suiteName(node.responseSuite)})` : 'Node default (as received)'
  return [
    { label, value: null as SecuritySuite | null },
    ...suiteChoices(node.keyFamily).map((s) => ({
      label: `${suiteName(s)}${suiteInfo(s)?.encrypted ? ' (encrypted)' : ''}${isWeak(s) ? ' (weak)' : ''}`,
      value: s as SecuritySuite | null,
    })),
  ]
}

export const EXPIRY_OPTIONS = [
  { label: '1 h', value: 1 },
  { label: '24 h', value: 24 },
  { label: '7 d', value: 168 },
  { label: 'Never', value: 0 },
]
