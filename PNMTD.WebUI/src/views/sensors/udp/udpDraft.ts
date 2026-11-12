import type { S } from '@/api/resources'
import { parseHexInput } from '@/format/bytes'
import {
  defaultSuites,
  isWeak,
  joinSuite,
  suiteChoices,
  suiteName,
  type KeyFamily,
  type SecuritySuite,
  type UdpAckMode,
  type UdpSessionMode,
} from '@/format/udp'

/** The UDP section of the sensor form (docs/udp-iot-plan.md §6.1), before it becomes SensorRequest.udp. */
export interface UdpDraft {
  /** Null: a random free id when created, the node's id when edited. */
  deviceId: number | null
  keyFamily: KeyFamily
  allowedSuites: SecuritySuite[]
  /** Null: answer in the suite the uplink came with. */
  responseSuite: SecuritySuite | null
  sessionMode: UdpSessionMode
  ackMode: UdpAckMode
  replayProtection: boolean
  /** Enter the key and the token by hand; otherwise both are generated when saved. */
  manual: boolean
  key: string
  token: string
}

export const KEY_BYTES = 16
export const TOKEN_BYTES = 4

export function newUdpDraft(deviceId: number | null = null): UdpDraft {
  return {
    deviceId,
    keyFamily: 'HmacSha256',
    allowedSuites: defaultSuites('HmacSha256'),
    responseSuite: null,
    sessionMode: 'None',
    ackMode: 'OnRequest',
    replayProtection: true,
    manual: false,
    key: '',
    token: '',
  }
}

/** An existing node's settings, to edit; the key and token stay unless entered. */
export function draftFromNode(u: S['UdpNodeDetail']): UdpDraft {
  return {
    deviceId: u.deviceId,
    keyFamily: u.keyFamily,
    allowedSuites: [...u.allowedSuites],
    responseSuite: u.responseSuite,
    sessionMode: u.sessionMode,
    ackMode: u.ackMode,
    replayProtection: u.replayProtection,
    manual: false,
    key: '',
    token: '',
  }
}

/** What an edit means for the device out there, before it is saved. */
export function changeHints(u: S['UdpNodeDetail'], draft: UdpDraft): string[] {
  const hints: string[] = []
  if (draft.deviceId !== null && draft.deviceId !== u.deviceId) {
    hints.push(
      `The device id changes from ${u.deviceId} to ${draft.deviceId}: until its firmware sends the new id, its packets come from an unknown device.`,
    )
  }
  if (draft.keyFamily !== u.keyFamily && !(draft.manual && draft.key.trim())) {
    hints.push(
      'A new key family gets a new key when saved (a key is never used with two families): the device needs it.',
    )
  }
  if (draft.manual && (draft.key.trim() || draft.token.trim())) {
    hints.push('The new key or token applies at once: sessions and the counter window are reset.')
  }
  const dropped = u.allowedSuites.filter((s) => !draft.allowedSuites.includes(s))
  if (dropped.length) {
    hints.push(`Packets in ${dropped.map(suiteName).join(', ')} are rejected from now on.`)
  }
  if (u.sessionMode === 'JoinRequired' && draft.sessionMode === 'None') {
    hints.push("Without JOIN the session ends: from its next packet on, the device signs with the node's key.")
  } else if (u.sessionMode === 'None' && draft.sessionMode === 'JoinRequired') {
    hints.push('With JOIN required, the device must JOIN before its next packet is accepted.')
  }
  return hints
}

/** Another family: the suites that do not belong to it go, and a node always keeps a signature. */
export function changeFamily(draft: UdpDraft, family: KeyFamily): UdpDraft {
  const choices = suiteChoices(family)
  const kept = draft.allowedSuites.filter((s) => choices.includes(s))
  return {
    ...draft,
    keyFamily: family,
    allowedSuites: kept.some((s) => !isWeak(s)) ? kept : [...kept, ...defaultSuites(family)],
    responseSuite: draft.responseSuite && choices.includes(draft.responseSuite) ? draft.responseSuite : null,
  }
}

/**
 * The problem with a hex secret as it is typed, or null: exactly `bytes` bytes, not one byte
 * repeated (the server refuses 00…00 and FF…FF). Empty is fine: it is generated.
 */
export function secretError(text: string, bytes: number): string | null {
  if (!text.trim()) {
    return null
  }
  const parsed = parseHexInput(text)
  if (parsed.error !== null) {
    return parsed.error
  }
  if (parsed.bytes.length !== bytes) {
    return `${bytes * 2} hex digits (${bytes} bytes); this is ${parsed.bytes.length * 2}.`
  }
  if (parsed.bytes.every((b) => b === parsed.bytes[0])) {
    return 'Not one byte repeated.'
  }
  return null
}

/** What the form warns about before saving: commands that could go out unsigned, a JOIN without its suite. */
export function draftHints(draft: UdpDraft): string[] {
  const hints: string[] = []
  if (draft.responseSuite && isWeak(draft.responseSuite)) {
    hints.push('Commands go out unsigned or with only the token: anyone who sees a packet can forge one.')
  } else if (!draft.responseSuite && draft.allowedSuites.some(isWeak)) {
    hints.push(
      'Responses use the suite the uplink came with: an uplink with NONE or TOKEN32 gets its commands unsigned. Choose a keyed response suite for commands.',
    )
  }
  if (draft.sessionMode === 'JoinRequired' && !draft.allowedSuites.includes(joinSuite(draft.keyFamily))) {
    hints.push(`A JOIN uses ${suiteName(joinSuite(draft.keyFamily))}: allow it, or the node cannot start a session.`)
  }
  if (!draft.allowedSuites.some((s) => !isWeak(s))) {
    hints.push('No keyed suite: firmware updates and replay protection need one.')
  }
  return hints
}

/** SensorRequest.udp: on create a missing key or token is generated (§4.3). */
export function udpRequest(draft: UdpDraft): S['UdpNodeRequest'] {
  return {
    deviceId: draft.deviceId ?? null,
    keyFamily: draft.keyFamily,
    allowedSuites: [...draft.allowedSuites],
    responseSuite: draft.responseSuite,
    sessionMode: draft.sessionMode,
    ackMode: draft.ackMode,
    replayProtection: draft.replayProtection,
    key: draft.manual && draft.key.trim() ? draft.key.trim() : null,
    token: draft.manual && draft.token.trim() ? draft.token.trim() : null,
  }
}
