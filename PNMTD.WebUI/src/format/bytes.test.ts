import { describe, expect, it } from 'vitest'
import {
  asciiChar,
  asciiPreview,
  dumpGap,
  dumpHexWidth,
  formatBytes,
  fromBase64,
  hexDump,
  hexPreview,
  parseHexInput,
  printableText,
  randomBytes,
  toBase64,
  toCArray,
  toHex,
  utf8Decode,
  utf8Encode,
} from './bytes'

const bytes = (...values: number[]) => new Uint8Array(values)
// Vector #3 of docs/udp-iot-plan.md §3.14: DATA "21.5C", HMAC-SHA256/64, FCNT 43.
const vector3 = bytes(
  0x44,
  0x03,
  0x07,
  0x00,
  0x2b,
  0x32,
  0x31,
  0x2e,
  0x35,
  0x43,
  0x35,
  0x0a,
  0xb3,
  0x32,
  0x32,
  0x76,
  0xbb,
  0x45,
)

describe('Base64', () => {
  it('reads what System.Text.Json writes for byte[] and back', () => {
    expect(Array.from(fromBase64('QIIHACrfJ4h+'))).toEqual([0x40, 0x82, 0x07, 0x00, 0x2a, 0xdf, 0x27, 0x88, 0x7e])
    expect(toBase64(bytes(0x40, 0x82, 0x07, 0x00, 0x2a, 0xdf, 0x27, 0x88, 0x7e))).toBe('QIIHACrfJ4h+')
  })

  it('treats null and empty as no bytes', () => {
    expect(fromBase64(null).length).toBe(0)
    expect(fromBase64(undefined).length).toBe(0)
    expect(fromBase64('').length).toBe(0)
    expect(toBase64(bytes())).toBe('')
  })

  it('round-trips every byte value', () => {
    const all = new Uint8Array(256).map((_, i) => i)
    expect(Array.from(fromBase64(toBase64(all)))).toEqual(Array.from(all))
  })
})

describe('hex', () => {
  it('is upper case and spaced', () => {
    expect(toHex(bytes(0x02, 0x02, 0x58, 0xab))).toBe('02 02 58 AB')
    expect(toHex(bytes(0x0a, 0xff), '')).toBe('0AFF')
    expect(toHex(bytes())).toBe('')
  })

  it('makes a C array', () => {
    expect(toCArray(bytes(0x00, 0x01, 0xfe))).toBe('{0x00, 0x01, 0xFE}')
    expect(toCArray(bytes())).toBe('{}')
  })

  it('previews the first bytes', () => {
    expect(hexPreview(bytes(1, 2, 3), 2)).toBe('01 02 …')
    expect(hexPreview(bytes(1, 2), 2)).toBe('01 02')
  })
})

describe('ASCII', () => {
  it('shows 0x20–0x7E as they are and everything else as ·', () => {
    expect(asciiChar(0x20)).toBe(' ')
    expect(asciiChar(0x41)).toBe('A')
    expect(asciiChar(0x7e)).toBe('~')
    for (const b of [0x00, 0x09, 0x0a, 0x0d, 0x1f, 0x7f, 0x80, 0xb3, 0xff]) {
      expect(asciiChar(b)).toBe('·')
    }
  })

  it('previews with a cut', () => {
    expect(asciiPreview(vector3)).toBe('D···+21.5C5··22v·E')
    expect(asciiPreview(utf8Encode('hello world'), 5)).toBe('hello…')
    expect(asciiPreview(bytes())).toBe('')
  })

  it('renders control characters (0x00–0x1F) and high bytes (0x7F–0xFF) as dots', () => {
    const low = new Uint8Array(32).map((_, i) => i)
    const high = new Uint8Array(129).map((_, i) => 0x7f + i)
    expect(asciiPreview(low)).toBe('·'.repeat(32))
    expect(asciiPreview(high)).toBe('·'.repeat(129))
  })
})

describe('hexDump', () => {
  it('matches the layout of the plan (§6.3), the ASCII column aligned on the last line', () => {
    expect(hexDump(vector3)).toEqual([
      '0000  44 03 07 00 2B 32 31 2E  35 43 35 0A B3 32 32 76  D···+21.5C5··22v',
      '0010  BB 45                                             ·E',
    ])
  })

  it('has nothing for no bytes', () => {
    expect(hexDump(bytes())).toEqual([])
  })

  it('keeps every line the same width up to the ASCII column', () => {
    const lines = hexDump(new Uint8Array(37).map((_, i) => i))
    expect(lines).toHaveLength(3)
    expect(lines.map((l) => l.indexOf('  ', 54))).toEqual([54, 54, 54])
    expect(lines[2].startsWith('0020  20 21 22 23 24')).toBe(true)
    expect(lines[2].endsWith('   !"#$')).toBe(true)
  })

  it('groups by 8 for other widths', () => {
    expect(dumpHexWidth(16)).toBe(48)
    expect(dumpHexWidth(8)).toBe(23)
    expect(dumpGap(7, 16)).toBe('  ')
    expect(dumpGap(15, 16)).toBe('')
    expect(hexDump(bytes(0x41, 0x42, 0x43), 8)).toEqual([`0000  41 42 43${' '.repeat(15 + 2)}ABC`])
  })

  it('shows offsets past 0xFFFF with more digits', () => {
    const lines = hexDump(new Uint8Array(0x10010), 16)
    expect(lines.at(-1)!.startsWith('10000  ')).toBe(true)
  })
})

describe('parseHexInput', () => {
  it('takes hex as people type and paste it', () => {
    for (const text of [
      '02 02 58',
      '020258',
      '02:02:58',
      '02-02-58',
      '0x02 0x02 0x58',
      '0x020258',
      '{0x02, 0x02, 0x58}',
      ' 02\n02\t58 ',
    ]) {
      const parsed = parseHexInput(text)
      expect(parsed.error, text).toBeNull()
      expect(Array.from(parsed.bytes!), text).toEqual([0x02, 0x02, 0x58])
    }
    expect(Array.from(parseHexInput('aB cD').bytes!)).toEqual([0xab, 0xcd])
  })

  it('is empty for empty or whitespace input', () => {
    expect(parseHexInput('').bytes!.length).toBe(0)
    expect(parseHexInput('   \n ').bytes!.length).toBe(0)
  })

  it('refuses an odd number of digits', () => {
    const parsed = parseHexInput('02 025')
    expect(parsed.bytes).toBeNull()
    expect(parsed.error).toMatch(/5 hex digits/)
    expect(parseHexInput('0').error).toMatch(/two/)
  })

  it('refuses characters that are not hex', () => {
    expect(parseHexInput('02 0g').error).toBe('"g" is not a hex digit.')
    expect(parseHexInput('zz').bytes).toBeNull()
    expect(parseHexInput('0x').bytes!.length).toBe(0)
  })
})

describe('UTF-8', () => {
  it('encodes and decodes', () => {
    expect(Array.from(utf8Encode('21.5C'))).toEqual([0x32, 0x31, 0x2e, 0x35, 0x43])
    expect(Array.from(utf8Encode('°'))).toEqual([0xc2, 0xb0])
    expect(utf8Decode(bytes(0xc2, 0xb0))).toBe('°')
    expect(utf8Decode(bytes(0xff))).toBe('�')
  })

  it('finds printable text, and nothing in binary', () => {
    expect(printableText(utf8Encode('OK'))).toBe('OK')
    expect(printableText(utf8Encode('line 1\nline 2\t°C'))).toBe('line 1\nline 2\t°C')
    expect(printableText(bytes(0x02, 0x02, 0x58))).toBeNull()
    expect(printableText(bytes(0xff, 0xfe))).toBeNull()
    expect(printableText(bytes(0x7f))).toBeNull()
    expect(printableText(bytes())).toBeNull()
  })
})

describe('randomBytes', () => {
  it('has the length asked for and is never one byte repeated', () => {
    for (let i = 0; i < 20; i++) {
      const key = randomBytes(16)
      expect(key).toHaveLength(16)
      expect(key.every((b) => b === key[0])).toBe(false)
    }
  })
})

describe('formatBytes', () => {
  it('uses binary units', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(1023)).toBe('1023 B')
    expect(formatBytes(1536)).toBe('1.50 KiB')
    expect(formatBytes(1_266_289)).toBe('1.21 MiB')
    expect(formatBytes(16 * 1024 * 1024)).toBe('16.0 MiB')
    expect(formatBytes(null)).toBe('–')
  })
})
