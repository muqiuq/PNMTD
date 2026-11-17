import { describe, expect, it } from 'vitest'
import {
  WILDCARD,
  codeTone,
  defaultGrace,
  expectLabel,
  formatFieldValue,
  formatPath,
  healthTone,
  kindOf,
  looksLikeTime,
  nameFromPath,
  naturalRule,
  operandProblem,
  operatorGroups,
  operatorInfo,
  parseDuration,
  parseTime,
  wildcardPath,
} from './advanced'

describe('paths from a position in a report (docs/advanced-sensor-plan.md §6.1)', () => {
  it('writes members, indexes and wildcards as the server reads them', () => {
    expect(formatPath(['jobs', 3, 'state'])).toBe('jobs[3].state')
    expect(formatPath(['disk', 'free_gb'])).toBe('disk.free_gb')
    expect(formatPath(['checks', 'db:ping', 0, 'status'])).toBe('checks["db:ping"][0].status')
    expect(formatPath(['free space'])).toBe('["free space"]')
    expect(formatPath(['a"b'])).toBe('["a\\"b"]')
    expect(formatPath(['größe'])).toBe('["größe"]')
    expect(formatPath(['jobs', WILDCARD, 'state'])).toBe('jobs[*].state')
    expect(formatPath([0, 'x'])).toBe('[0].x')
  })

  it('turns every index into [*] for a rule over every element', () => {
    expect(wildcardPath(['jobs', 3, 'state'])).toBe('jobs[*].state')
    expect(wildcardPath(['checks', 'disk', 0, 'status'])).toBe('checks.disk[*].status')
  })

  it('names a rule after its path', () => {
    expect(nameFromPath('disk.free_gb')).toBe('Disk free gb')
    expect(nameFromPath('jobs[*].state')).toBe('Jobs state')
    expect(nameFromPath('checks["db:ping"][0].status')).toBe('Checks db ping status')
  })
})

describe('kinds, health words and times', () => {
  it('knows the JSON kinds', () => {
    expect([1, 'a', true, null, [], {}, undefined].map(kindOf)).toEqual([
      'number',
      'string',
      'boolean',
      'null',
      'array',
      'object',
      'missing',
    ])
  })

  it('reads the IETF, Spring and ASP.NET status words as healthy does', () => {
    expect(['pass', 'UP', 'Healthy', 'ok', true].map(healthTone)).toEqual(['ok', 'ok', 'ok', 'ok', 'ok'])
    expect(['warn', 'Degraded', 'yellow'].map(healthTone)).toEqual(['warn', 'warn', 'warn'])
    expect(['fail', 'DOWN', 'Unhealthy', 'error'].map(healthTone)).toEqual(['fail', 'fail', 'fail', 'fail'])
    expect(healthTone('nightly')).toBeNull()
    expect(healthTone(3)).toBeNull()
  })

  it('takes ISO 8601 times, without an offset as UTC', () => {
    expect(looksLikeTime('2026-11-17T02:14:03+01:00')).toBe(true)
    expect(looksLikeTime('2026-11-17 02:14')).toBe(true)
    expect(looksLikeTime('2026-11-17')).toBe(false)
    expect(looksLikeTime('yesterday')).toBe(false)
    expect(parseTime('2026-11-17T02:14:03')?.toISOString()).toBe('2026-11-17T02:14:03.000Z')
    expect(parseTime('2026-11-17T02:14:03+01:00')?.toISOString()).toBe('2026-11-17T01:14:03.000Z')
  })

  it('parses durations as the rules take them', () => {
    expect(parseDuration('90s')).toBe(90)
    expect(parseDuration('15m')).toBe(900)
    expect(parseDuration('26h')).toBe(93_600)
    expect(parseDuration('7d')).toBe(604_800)
    expect(parseDuration('1h30m')).toBe(5_400)
    expect(parseDuration('PT26H')).toBe(93_600)
    expect(parseDuration('P1DT2H')).toBe(93_600)
    expect(parseDuration('a day')).toBeNull()
    expect(parseDuration('0s')).toBeNull()
    expect(parseDuration('')).toBeNull()
  })
})

describe('operators (§6.3)', () => {
  it('has a label, a symbol and an operand for each', () => {
    expect(operatorInfo('Ge')).toMatchObject({ symbol: '≥', operand: 'number', group: 'Compare' })
    expect(operatorInfo('Healthy').operand).toBeNull()
    expect(operatorInfo('NewerThan').operand).toBe('duration')
  })

  it('lists the operators that suit the value first, and keeps every one', () => {
    const groups = operatorGroups('number')
    expect(groups.map((g) => g.label)).toEqual(['Compare', 'Text', 'Kind', 'State', 'Age', 'Count', 'Change'])
    expect(groups.flatMap((g) => g.items)).toHaveLength(35)
    const text = groups.find((g) => g.label === 'Text')!.items
    expect(text[0]!.suits).toBe(true)
    expect(text.at(-1)!.suits).toBe(false)
  })

  it('checks operands as the server will', () => {
    expect(operandProblem('Ge', '5')).toBeNull()
    expect(operandProblem('Ge', 'five')).toBe('A number, such as 5 or 0.25.')
    expect(operandProblem('Between', '5', '1')).toBe('The second number must be at least the first.')
    expect(operandProblem('Matches', '(')).toMatch(/^Not a valid pattern/)
    expect(operandProblem('OlderThan', '26h')).toBeNull()
    expect(operandProblem('OlderThan', 'soon')).toMatch(/^A duration/)
    expect(operandProblem('CountGe', '-1')).toMatch(/whole number/)
    expect(operandProblem('OneOf', '\n \n')).toMatch(/one per line/)
    expect(operandProblem('Eq', '')).toBeNull()
    expect(operandProblem('Healthy', null)).toBeNull()
  })

  it('suggests the natural rule for a value', () => {
    expect(naturalRule(3.2)).toEqual({ operator: 'Ge', operand: '3.2' })
    expect(naturalRule('ok')).toEqual({ operator: 'Healthy', operand: null })
    expect(naturalRule('2026-11-17T02:14:03Z')).toEqual({ operator: 'NewerThan', operand: '1d' })
    expect(naturalRule(false)).toEqual({ operator: 'IsFalse', operand: null })
    expect(naturalRule([1, 2])).toEqual({ operator: 'CountGe', operand: '2' })
    expect(naturalRule('v2.1')).toEqual({ operator: 'Eq', operand: 'v2.1' })
    expect(naturalRule('12')).toEqual({ operator: 'Ge', operand: '12' })
    expect(naturalRule(null)).toEqual({ operator: 'IsNull', operand: null })
  })
})

describe('fields (§5.3) and verdicts', () => {
  it('formats values by the field', () => {
    expect(formatFieldValue(48_213_307_392, { format: 'Bytes' })).toBe('44.9 GiB')
    expect(formatFieldValue(3_712, { format: 'Duration' })).toBe('1 h 1 min')
    expect(formatFieldValue(64.25, { format: 'Percent', decimals: 1 })).toBe(`${(64.3).toLocaleString()} %`)
    expect(formatFieldValue(true, { format: 'Boolean' })).toBe('yes')
    expect(formatFieldValue(null, { format: 'Auto' })).toBe('–')
    expect(formatFieldValue(undefined, { format: 'Number' })).toBe('–')
    expect(formatFieldValue('3.2', { format: 'Number', unit: 'GB', decimals: 1 })).toBe(
      `${(3.2).toLocaleString(undefined, { minimumFractionDigits: 1 })} GB`,
    )
    expect(formatFieldValue([1, 2, 3], { format: 'Auto' })).toBe('3 items')
    expect(formatFieldValue('ok', { format: 'Status' })).toBe('ok')
  })

  it('tones codes and intervals', () => {
    expect([295, 296, 495, 496, 460, null].map(codeTone)).toEqual(['ok', 'warn', 'fail', 'fail', 'fail', 'muted'])
    expect(defaultGrace(300)).toBe(60)
    expect(defaultGrace(86_400)).toBe(17_280)
    expect(defaultGrace(0)).toBe(0)
    expect(expectLabel(300)).toBe('every 5 minutes')
    expect(expectLabel(0)).toBe('no expectation')
    expect(expectLabel(120)).toBe('every 2 min')
  })
})
