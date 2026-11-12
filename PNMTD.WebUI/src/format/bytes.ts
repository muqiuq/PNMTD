/**
 * Bytes as people read them (docs/udp-iot-plan.md §6.3, §6.7): v1 sends them as Base64 in JSON,
 * the UI shows hex and ASCII. Nothing here knows the protocol: the server names every field.
 */

export type Bytes = Uint8Array

/** Base64 (standard alphabet, as System.Text.Json writes byte[]) → bytes; null or "" is empty. */
export function fromBase64(text: string | null | undefined): Bytes {
  if (!text) {
    return new Uint8Array(0)
  }
  const binary = atob(text)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i)
  }
  return bytes
}

export function toBase64(bytes: Bytes): string {
  let binary = ''
  for (const b of bytes) {
    binary += String.fromCharCode(b)
  }
  return btoa(binary)
}

/** One byte as two upper-case hex digits. */
export const hexByte = (b: number) => b.toString(16).toUpperCase().padStart(2, '0')

/** "44 03 07": upper case, spaced (or joined by another separator). */
export function toHex(bytes: Bytes | number[], separator = ' '): string {
  return Array.from(bytes, hexByte).join(separator)
}

/** "{0x44, 0x03, 0x07}", for firmware sources. */
export function toCArray(bytes: Bytes | number[]): string {
  return `{${Array.from(bytes, (b) => `0x${hexByte(b)}`).join(', ')}}`
}

/** The ASCII rendering of a byte: 0x20–0x7E as is, everything else "·". */
export function asciiChar(b: number): string {
  return b >= 0x20 && b <= 0x7e ? String.fromCharCode(b) : '·'
}

/** The ASCII rendering of bytes, cut after `max` with "…". */
export function asciiPreview(bytes: Bytes, max = Infinity): string {
  const shown = Array.from(bytes.subarray(0, Number.isFinite(max) ? max : bytes.length), asciiChar).join('')
  return bytes.length > max ? `${shown}…` : shown
}

/** Hex for a list cell: the first `max` bytes, then "…". */
export function hexPreview(bytes: Bytes, max = 12): string {
  const shown = toHex(bytes.subarray(0, max))
  return bytes.length > max ? `${shown} …` : shown
}

/** The bytes of a hex dump's line, for renderers that colour single bytes. */
export interface DumpLine {
  offset: number
  /** The index of the line's first byte in the whole input. */
  start: number
  bytes: number[]
}

export function dumpLines(bytes: Bytes, width = 16): DumpLine[] {
  const lines: DumpLine[] = []
  for (let start = 0; start < bytes.length; start += width) {
    lines.push({ offset: start, start, bytes: Array.from(bytes.subarray(start, start + width)) })
  }
  return lines
}

/** The offset column: 4 hex digits (more only past 64 KiB). */
export const dumpOffset = (offset: number) => offset.toString(16).toUpperCase().padStart(4, '0')

/** The gap after byte `i` of a line: a space, two between groups of 8, none after the last. */
export function dumpGap(i: number, width: number): string {
  if (i === width - 1) {
    return ''
  }
  return (i + 1) % 8 === 0 ? '  ' : ' '
}

/** The width of a full line's hex column, for padding the last one. */
export function dumpHexWidth(width: number): number {
  let length = 0
  for (let i = 0; i < width; i++) {
    length += 2 + dumpGap(i, width).length
  }
  return length
}

/**
 * A classic hex dump, one string per line:
 * `0000  44 03 07 00 2B 32 31 2E  35 43 35 0A B3 32 32 76  D···+21.5C5··22v`.
 * The offset in 4 hex digits, groups of 8, the ASCII column aligned on the last line too.
 */
export function hexDump(bytes: Bytes, width = 16): string[] {
  const full = dumpHexWidth(width)
  return dumpLines(bytes, width).map((line) => {
    const hex = line.bytes.map((b, i) => hexByte(b) + (i < line.bytes.length - 1 ? dumpGap(i, width) : '')).join('')
    return `${dumpOffset(line.offset)}  ${hex.padEnd(full)}  ${line.bytes.map(asciiChar).join('')}`
  })
}

export type ParsedHex = { bytes: Bytes; error: null } | { bytes: null; error: string }

/**
 * Hex as people type or paste it: case-insensitive; spaces, colons, dashes, commas, braces and a 0x
 * per byte or per value allowed ("02 02 58", "02:02:58", "0x020258", "{0x02, 0x02, 0x58}").
 * An odd number of digits or another character is an error.
 */
export function parseHexInput(text: string): ParsedHex {
  const digits = text
    .split(/[\s,:;\-{}]+/)
    .filter((t) => t !== '')
    .map((t) => (/^0x/i.test(t) ? t.slice(2) : t))
    .join('')
  const bad = digits.match(/[^0-9a-fA-F]/)
  if (bad) {
    return { bytes: null, error: `"${bad[0]}" is not a hex digit.` }
  }
  if (digits.length % 2 !== 0) {
    return { bytes: null, error: `${digits.length} hex digits: every byte needs two.` }
  }
  const bytes = new Uint8Array(digits.length / 2)
  for (let i = 0; i < bytes.length; i++) {
    bytes[i] = Number.parseInt(digits.slice(i * 2, i * 2 + 2), 16)
  }
  return { bytes, error: null }
}

export function utf8Encode(text: string): Bytes {
  return new TextEncoder().encode(text)
}

/** UTF-8 → text; invalid sequences become U+FFFD. */
export function utf8Decode(bytes: Bytes): string {
  return new TextDecoder('utf-8', { fatal: false }).decode(bytes)
}

/** The text of bytes that are valid UTF-8 without control characters (tab and newlines allowed), else null. */
export function printableText(bytes: Bytes): string | null {
  if (bytes.length === 0) {
    return null
  }
  let text: string
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  } catch {
    return null
  }
  return /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]/.test(text) ? null : text
}

/** Random bytes from the browser's CSPRNG, never one byte repeated (the server refuses 00…00). */
export function randomBytes(length: number): Bytes {
  const bytes = new Uint8Array(length)
  do {
    crypto.getRandomValues(bytes)
  } while (length > 1 && bytes.every((b) => b === bytes[0]))
  return bytes
}

/** 512 B, 1.5 KiB, 1.21 MiB. */
export function formatBytes(n: number | null | undefined): string {
  if (n === null || n === undefined) {
    return '–'
  }
  if (n < 1024) {
    return `${n} B`
  }
  const units = ['KiB', 'MiB', 'GiB']
  let value = n / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value >= 100 ? value.toFixed(0) : value.toFixed(value >= 10 ? 1 : 2)} ${units[unit]}`
}
