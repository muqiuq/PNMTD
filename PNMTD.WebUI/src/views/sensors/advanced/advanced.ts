import type { S } from '@/api/resources'
import { formatAbsolute, formatDuration } from '@/format'
import { formatBytes } from '@/format/bytes'

/**
 * Advanced sensors in the web UI (docs/advanced-sensor-plan.md §10.1): paths from a position in a report, the operators with
 * their labels and operands, the fields' formats, the health words and the outcomes' tones. The server decides every
 * outcome; these only show and suggest.
 */

export type Operator = S['AdvancedOperator']
export type Outcome = S['AdvancedOutcome']
export type FieldFormat = S['AdvancedFieldFormat']
export type Quantifier = S['AdvancedQuantifier']
export type WhenMissing = S['AdvancedWhenMissing']
export type Severity = S['AdvancedSeverity']

/** A step in a report: a key, an index, or every element or member. */
export type Segment = string | number | { wildcard: true }

export const WILDCARD: Segment = { wildcard: true }

/** A name that needs no quotes in a path: `[A-Za-z_][A-Za-z0-9_-]*`, as the server's AdvancedPath. */
export const isPlainName = (name: string) => /^[A-Za-z_][A-Za-z0-9_-]*$/.test(name)

/** `formatPath(['jobs', 3, 'state'])` → `jobs[3].state`; keys that aren't plain names in `["…"]`. */
export function formatPath(segments: readonly Segment[]): string {
  let path = ''
  for (const segment of segments) {
    if (typeof segment === 'number') {
      path += `[${segment}]`
    } else if (typeof segment === 'object') {
      path += '[*]'
    } else if (isPlainName(segment)) {
      path += path ? `.${segment}` : segment
    } else {
      path += `[${JSON.stringify(segment)}]`
    }
  }
  return path
}

/** The same path with every index as `[*]`: `jobs[3].state` → `jobs[*].state`, for a rule over every element. */
export function wildcardPath(segments: readonly Segment[]): string {
  return formatPath(segments.map((s) => (typeof s === 'number' ? WILDCARD : s)))
}

/** The JSON kinds a value can have, and `missing` when a path matches nothing. */
export type JsonKind = 'string' | 'number' | 'boolean' | 'null' | 'array' | 'object' | 'missing'

export function kindOf(value: unknown): JsonKind {
  if (value === undefined) return 'missing'
  if (value === null) return 'null'
  if (Array.isArray(value)) return 'array'
  switch (typeof value) {
    case 'string':
      return 'string'
    case 'number':
    case 'bigint':
      return 'number'
    case 'boolean':
      return 'boolean'
    default:
      return 'object'
  }
}

// --- health words and times (§6.3) ------------------------------------------------------------------------------------

const healthy = new Set(['ok', 'pass', 'passed', 'up', 'healthy', 'success', 'green', 'true'])
const warning = new Set(['warn', 'warning', 'degraded', 'yellow'])
const failing = new Set(['fail', 'failed', 'failure', 'down', 'unhealthy', 'error', 'critical', 'red', 'false'])

/** The tone of a status word, as `healthy` reads it: ok, warn, or fail for the usual failure words; null for other text. */
export function healthTone(value: unknown): 'ok' | 'warn' | 'fail' | null {
  if (value === true) return 'ok'
  if (typeof value !== 'string') return null
  const word = value.trim().toLowerCase()
  return healthy.has(word) ? 'ok' : warning.has(word) ? 'warn' : failing.has(word) ? 'fail' : null
}

/** A status word the starter rule and `healthy` know. */
export const isHealthWord = (value: unknown) => healthTone(value) !== null

const isoTime = /^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}(:\d{2}(\.\d{1,9})?)?\s*([Zz]|[+-]\d{2}(:?\d{2})?)?$/

/** ISO 8601 with a time: shown as a time in the browser's zone. */
export const looksLikeTime = (value: unknown): boolean => typeof value === 'string' && isoTime.test(value.trim())

/** A time without an offset is read as UTC, as the server reads it. */
export function parseTime(value: string): Date | null {
  const text = value.trim()
  const withZone = /([Zz]|[+-]\d{2}(:?\d{2})?)$/.test(text) ? text : `${text}Z`
  const date = new Date(withZone.replace(' ', 'T'))
  return Number.isNaN(date.getTime()) ? null : date
}

/** A duration as the rules take it (§6.3): 90s, 15m, 26h, 7d, 2w, combinations as 1h30m, or ISO 8601 (PT26H). Seconds, or null. */
export function parseDuration(text: string | null | undefined): number | null {
  const s = (text ?? '').trim().replace(/\s+/g, '')
  if (!s) return null
  const iso = /^P(?:(\d+(?:\.\d+)?)D)?(?:T(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?)?$/i.exec(s)
  if (iso && s.length > 1 && !/T$/i.test(s)) {
    const [, d, h, m, sec] = iso
    const total = Number(d ?? 0) * 86_400 + Number(h ?? 0) * 3_600 + Number(m ?? 0) * 60 + Number(sec ?? 0)
    return total > 0 ? total : null
  }
  if (!/^(\d+(\.\d+)?(w|d|h|min|m|s))+$/i.test(s)) return null
  const units: Record<string, number> = { w: 604_800, d: 86_400, h: 3_600, m: 60, min: 60, s: 1 }
  let total = 0
  for (const part of s.matchAll(/(\d+(?:\.\d+)?)(w|d|h|min|m|s)/gi)) {
    total += Number(part[1]) * units[part[2]!.toLowerCase()]!
  }
  return total > 0 ? total : null
}

// --- operators (§6.3) -------------------------------------------------------------------------------------------------

export type OperatorGroup = 'Compare' | 'Text' | 'Kind' | 'State' | 'Age' | 'Count' | 'Change'

/** What an operator's operand is: the editor's fields and hints follow it. */
export type OperandKind = 'value' | 'number' | 'range' | 'text' | 'pattern' | 'list' | 'duration' | 'count' | null

export interface OperatorInfo {
  op: Operator
  label: string
  /** As the condition reads: `≥ 5`, `contains "x"`. */
  symbol: string
  group: OperatorGroup
  operand: OperandKind
  /** The JSON kinds it suits; the editor lists these first for a value of that kind. */
  kinds: JsonKind[]
}

const scalar: JsonKind[] = ['string', 'number', 'boolean']
const any: JsonKind[] = ['string', 'number', 'boolean', 'null', 'array', 'object', 'missing']

export const OPERATORS: readonly OperatorInfo[] = [
  { op: 'Eq', label: 'equals', symbol: '=', group: 'Compare', operand: 'value', kinds: [...scalar, 'null'] },
  { op: 'Ne', label: 'does not equal', symbol: '≠', group: 'Compare', operand: 'value', kinds: [...scalar, 'null'] },
  { op: 'Lt', label: 'less than', symbol: '<', group: 'Compare', operand: 'number', kinds: ['number', 'string'] },
  { op: 'Le', label: 'at most', symbol: '≤', group: 'Compare', operand: 'number', kinds: ['number', 'string'] },
  { op: 'Gt', label: 'more than', symbol: '>', group: 'Compare', operand: 'number', kinds: ['number', 'string'] },
  { op: 'Ge', label: 'at least', symbol: '≥', group: 'Compare', operand: 'number', kinds: ['number', 'string'] },
  {
    op: 'Between',
    label: 'between',
    symbol: 'between',
    group: 'Compare',
    operand: 'range',
    kinds: ['number', 'string'],
  },
  {
    op: 'Outside',
    label: 'outside',
    symbol: 'outside',
    group: 'Compare',
    operand: 'range',
    kinds: ['number', 'string'],
  },
  { op: 'Contains', label: 'contains', symbol: 'contains', group: 'Text', operand: 'text', kinds: ['string', 'array'] },
  {
    op: 'NotContains',
    label: 'does not contain',
    symbol: "doesn't contain",
    group: 'Text',
    operand: 'text',
    kinds: ['string', 'array'],
  },
  { op: 'StartsWith', label: 'starts with', symbol: 'starts with', group: 'Text', operand: 'text', kinds: ['string'] },
  { op: 'EndsWith', label: 'ends with', symbol: 'ends with', group: 'Text', operand: 'text', kinds: ['string'] },
  {
    op: 'Matches',
    label: 'matches the pattern',
    symbol: 'matches',
    group: 'Text',
    operand: 'pattern',
    kinds: ['string'],
  },
  {
    op: 'NotMatches',
    label: 'does not match the pattern',
    symbol: "doesn't match",
    group: 'Text',
    operand: 'pattern',
    kinds: ['string'],
  },
  { op: 'OneOf', label: 'is one of', symbol: 'is one of', group: 'Text', operand: 'list', kinds: scalar },
  { op: 'NotOneOf', label: 'is none of', symbol: 'is none of', group: 'Text', operand: 'list', kinds: scalar },
  { op: 'IsTrue', label: 'is true', symbol: 'is true', group: 'Kind', operand: null, kinds: ['boolean'] },
  { op: 'IsFalse', label: 'is false', symbol: 'is false', group: 'Kind', operand: null, kinds: ['boolean'] },
  { op: 'Exists', label: 'exists', symbol: 'exists', group: 'Kind', operand: null, kinds: any },
  { op: 'Absent', label: 'is absent', symbol: 'is absent', group: 'Kind', operand: null, kinds: any },
  { op: 'IsNull', label: 'is null', symbol: 'is null', group: 'Kind', operand: null, kinds: ['null'] },
  { op: 'NotNull', label: 'is not null', symbol: "isn't null", group: 'Kind', operand: null, kinds: any },
  {
    op: 'Empty',
    label: 'is empty',
    symbol: 'is empty',
    group: 'Kind',
    operand: null,
    kinds: ['string', 'array', 'object', 'null'],
  },
  {
    op: 'NotEmpty',
    label: 'is not empty',
    symbol: "isn't empty",
    group: 'Kind',
    operand: null,
    kinds: ['string', 'array', 'object'],
  },
  {
    op: 'Healthy',
    label: 'is healthy',
    symbol: 'is healthy',
    group: 'State',
    operand: null,
    kinds: ['string', 'boolean'],
  },
  {
    op: 'OlderThan',
    label: 'is older than',
    symbol: 'older than',
    group: 'Age',
    operand: 'duration',
    kinds: ['string', 'number'],
  },
  {
    op: 'NewerThan',
    label: 'is newer than',
    symbol: 'newer than',
    group: 'Age',
    operand: 'duration',
    kinds: ['string', 'number'],
  },
  {
    op: 'CountGe',
    label: 'count at least',
    symbol: 'count ≥',
    group: 'Count',
    operand: 'count',
    kinds: ['array', 'object', 'string'],
  },
  {
    op: 'CountLe',
    label: 'count at most',
    symbol: 'count ≤',
    group: 'Count',
    operand: 'count',
    kinds: ['array', 'object', 'string'],
  },
  {
    op: 'CountEq',
    label: 'count exactly',
    symbol: 'count =',
    group: 'Count',
    operand: 'count',
    kinds: ['array', 'object', 'string'],
  },
  {
    op: 'Changed',
    label: 'changed since the previous report',
    symbol: 'changed',
    group: 'Change',
    operand: null,
    kinds: any,
  },
  {
    op: 'Unchanged',
    label: 'unchanged since the previous report',
    symbol: 'unchanged',
    group: 'Change',
    operand: null,
    kinds: any,
  },
  {
    op: 'Increased',
    label: 'increased',
    symbol: 'increased',
    group: 'Change',
    operand: null,
    kinds: ['number', 'string'],
  },
  {
    op: 'Decreased',
    label: 'decreased',
    symbol: 'decreased',
    group: 'Change',
    operand: null,
    kinds: ['number', 'string'],
  },
  {
    op: 'NotDecreased',
    label: 'did not decrease',
    symbol: "didn't decrease",
    group: 'Change',
    operand: null,
    kinds: ['number', 'string'],
  },
]

const byOperator = new Map(OPERATORS.map((o) => [o.op, o]))

export function operatorInfo(op: Operator): OperatorInfo {
  return byOperator.get(op) ?? { op, label: op, symbol: op, group: 'Compare', operand: 'value', kinds: any }
}

export const GROUPS: readonly OperatorGroup[] = ['Compare', 'Text', 'Kind', 'State', 'Age', 'Count', 'Change']

/**
 * The operators grouped as in §6.3, those that suit the value's kind first in each group (and marked). Every operator stays
 * reachable: the value of today isn't always the value of tomorrow.
 */
export function operatorGroups(
  kind: JsonKind | null,
): { label: OperatorGroup; items: (OperatorInfo & { suits: boolean })[] }[] {
  return GROUPS.map((group) => {
    const items = OPERATORS.filter((o) => o.group === group).map((o) => ({
      ...o,
      suits: !kind || o.kinds.includes(kind),
    }))
    return { label: group, items: [...items.filter((i) => i.suits), ...items.filter((i) => !i.suits)] }
  })
}

/** The hint under an operand field: what it must be. Matches the server's messages (§6.3). */
export function operandHint(kind: OperandKind): string {
  switch (kind) {
    case 'value':
      return 'A number, true, false, null, or text (compared as written).'
    case 'number':
    case 'range':
      return 'A number, such as 5 or 0.25.'
    case 'text':
      return 'The text to look for.'
    case 'pattern':
      return 'A regular expression (.NET), such as ^v2\\. — at most 100 ms per value.'
    case 'list':
      return 'The values, one per line.'
    case 'duration':
      return 'A duration: 90s, 15m, 26h or 7d (or ISO 8601, such as PT26H).'
    case 'count':
      return 'A whole number, 0 or more.'
    default:
      return ''
  }
}

/** The operand checked as the server will (so the editor can say it before saving); null when it fits. */
export function operandProblem(
  op: Operator,
  operand: string | null | undefined,
  operand2?: string | null,
): string | null {
  const kind = operatorInfo(op).operand
  const value = operand ?? ''
  const number = (v: string | null | undefined) => (v ?? '').trim() !== '' && Number.isFinite(Number((v ?? '').trim()))
  switch (kind) {
    case 'value':
      return operand === null || operand === undefined ? operandHint(kind) : null
    case 'number':
      return number(value) ? null : operandHint(kind)
    case 'range':
      if (!number(value) || !number(operand2)) return operandHint(kind)
      return Number(value) <= Number(operand2) ? null : 'The second number must be at least the first.'
    case 'text':
      return value ? null : operandHint(kind)
    case 'pattern':
      if (!value) return operandHint(kind)
      try {
        // JavaScript's syntax is close to .NET's; the server has the last word.
        new RegExp(value)
        return null
      } catch (e) {
        return `Not a valid pattern: ${(e as Error).message}`
      }
    case 'list':
      return value.split('\n').some((l) => l.trim()) ? null : operandHint(kind)
    case 'duration':
      return parseDuration(value) ? null : operandHint(kind)
    case 'count':
      return /^\d+$/.test(value.trim()) ? null : operandHint(kind)
    default:
      return null
  }
}

/** A value as an operand: numbers, booleans and null as written, text as it is. */
export function operandOf(value: unknown): string {
  if (typeof value === 'string') return value
  if (value === null) return 'null'
  if (typeof value === 'number' || typeof value === 'boolean') return String(value)
  return ''
}

/**
 * The rule a value suggests (*Add rule…*, §10.3): a number at least its value, a status word healthy, a time newer than a
 * day, a boolean as it is, a list at least as long, other text equal to it, an object that it exists.
 */
export function naturalRule(value: unknown): { operator: Operator; operand: string | null } {
  switch (kindOf(value)) {
    case 'number':
      return { operator: 'Ge', operand: String(value) }
    case 'boolean':
      return { operator: value ? 'IsTrue' : 'IsFalse', operand: null }
    case 'null':
      return { operator: 'IsNull', operand: null }
    case 'array':
      return { operator: 'CountGe', operand: String((value as unknown[]).length) }
    case 'object':
      return { operator: 'Exists', operand: null }
    case 'string': {
      const text = value as string
      if (isHealthWord(text)) return { operator: 'Healthy', operand: null }
      if (looksLikeTime(text)) return { operator: 'NewerThan', operand: '1d' }
      if (text.trim() !== '' && Number.isFinite(Number(text))) return { operator: 'Ge', operand: text.trim() }
      return { operator: 'Eq', operand: text }
    }
    default:
      return { operator: 'Exists', operand: null }
  }
}

/** A short name for a rule made from a path: `disk.free_gb` → `Disk free gb`, `jobs[*].state` → `Jobs state`. */
export function nameFromPath(path: string): string {
  const words = path
    .replace(/\[\*\]|\[-?\d+\]/g, ' ')
    .replace(/\["([^"]*)"\]/g, ' $1 ')
    .replace(/[._:\-[\]"]+/g, ' ')
    .trim()
    .replace(/\s+/g, ' ')
  const text = words || path
  return (text.charAt(0).toUpperCase() + text.slice(1)).slice(0, 200)
}

// --- outcomes and verdicts --------------------------------------------------------------------------------------------

export type Tone = 'ok' | 'warn' | 'fail' | 'muted'

export function outcomeTone(outcome: Outcome | null | undefined): Tone {
  switch (outcome) {
    case 'Pass':
      return 'ok'
    case 'Warn':
      return 'warn'
    case 'Fail':
      return 'fail'
    default:
      return 'muted'
  }
}

/** ✓ holds, ✗ fails, ! warns, – off. */
export function outcomeMark(outcome: Outcome | null | undefined): string {
  switch (outcome) {
    case 'Pass':
      return '✓'
    case 'Warn':
      return '!'
    case 'Fail':
      return '✗'
    default:
      return '–'
  }
}

export function outcomeLabel(outcome: Outcome | null | undefined): string {
  switch (outcome) {
    case 'Pass':
      return 'holds'
    case 'Warn':
      return 'warns'
    case 'Fail':
      return 'fails'
    case 'Off':
      return 'off'
    default:
      return 'not judged yet'
  }
}

/** The reason in words (§3.2). */
export function reasonLabel(reason: S['AdvancedReason'] | null | undefined): string | null {
  switch (reason) {
    case 'Missing':
      return 'nothing matched'
    case 'WrongKind':
      return "the value can't be compared"
    case 'Timeout':
      return 'the pattern took too long'
    case 'TooManyMatches':
      return 'more than 1000 matches'
    default:
      return null
  }
}

/** The tone of a verdict's code: 295 ok, 296 warn, anything else from 400 fail. */
export function codeTone(code: number | null | undefined): Tone {
  if (code === null || code === undefined) return 'muted'
  if (code === 296) return 'warn'
  return code <= 399 ? 'ok' : 'fail'
}

/** `Disk space ✗ disk.free_gb = 3.2`: a result's values in one line. */
export function actualText(result: S['AdvancedRuleResult'] | null | undefined): string {
  if (!result) return ''
  if (result.reason === 'Missing') return `${result.path} missing`
  const values = result.actual.map((a) => `${a.path} = ${a.value}`)
  if (result.matchCount > result.actual.length && result.actual.length > 1) {
    values.push(`${result.matchCount - result.actual.length} more`)
  }
  return values.join(', ')
}

// --- fields (§5.3) ----------------------------------------------------------------------------------------------------

export const FIELD_FORMATS: readonly { value: FieldFormat; label: string }[] = [
  { value: 'Auto', label: 'Automatic' },
  { value: 'Number', label: 'Number' },
  { value: 'Percent', label: 'Percent' },
  { value: 'Bytes', label: 'Bytes' },
  { value: 'Duration', label: 'Duration (seconds)' },
  { value: 'DateTime', label: 'Date and time' },
  { value: 'Text', label: 'Text' },
  { value: 'Boolean', label: 'Yes / no' },
  { value: 'Status', label: 'Status word' },
]

const numberOf = (value: unknown): number | null => {
  if (typeof value === 'number') return value
  if (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value))) return Number(value)
  return null
}

function number(n: number, decimals: number | null | undefined): string {
  return decimals === null || decimals === undefined
    ? n.toLocaleString(undefined, { maximumFractionDigits: 6 })
    : n.toLocaleString(undefined, { minimumFractionDigits: decimals, maximumFractionDigits: decimals })
}

/**
 * A field's value as a person reads it (§5.3): bytes as KiB/MiB/GiB, durations from seconds, percent, dates in the browser's
 * zone, yes/no; the unit after a number. Lists and objects in brief; null and a missing value as –.
 */
export function formatFieldValue(
  value: unknown,
  field: { format: FieldFormat; unit?: string | null; decimals?: number | null },
): string {
  if (value === undefined || value === null) return '–'
  const unit = field.unit ? ` ${field.unit}` : ''
  const n = numberOf(value)
  switch (field.format) {
    case 'Bytes':
      return n === null ? String(value) : formatBytes(n)
    case 'Duration':
      return n === null ? String(value) : formatDuration(n * 1000)
    case 'Percent':
      return n === null ? String(value) : `${number(n, field.decimals ?? 1)} %`
    case 'DateTime': {
      if (typeof value === 'string') {
        const date = parseTime(value)
        return date ? formatAbsolute(date) : value
      }
      if (n !== null) return formatAbsolute(new Date((Math.abs(n) > 1e11 ? n : n * 1000) as number))
      return String(value)
    }
    case 'Boolean':
      return value === true || value === 'true' ? 'yes' : value === false || value === 'false' ? 'no' : String(value)
    case 'Number':
      return n === null ? String(value) : `${number(n, field.decimals)}${unit}`
    case 'Text':
    case 'Status':
      return typeof value === 'string' ? value : JSON.stringify(value)
    default: {
      if (typeof value === 'number') return `${number(value, field.decimals)}${unit}`
      if (typeof value === 'boolean') return value ? 'yes' : 'no'
      if (typeof value === 'string') {
        if (looksLikeTime(value)) {
          const date = parseTime(value)
          if (date) return formatAbsolute(date)
        }
        return `${value}${n !== null ? unit : ''}`
      }
      if (Array.isArray(value)) return `${value.length} items`
      return `${Object.keys(value as object).length} keys`
    }
  }
}

/** The tone a field's value takes for a Status field, or when rules on its path don't hold (the server's outcome). */
export function fieldTone(field: S['AdvancedFieldItem']): Tone {
  if (field.outcome === 'Fail' || field.outcome === 'Warn') return outcomeTone(field.outcome)
  if (field.format === 'Status') return healthTone(field.value) ?? 'muted'
  return field.outcome === 'Pass' ? 'ok' : 'muted'
}

// --- intervals (§10.2) ------------------------------------------------------------------------------------------------

/** *Expect a report every*: the presets, and none. */
export const EXPECT_EVERY: readonly { label: string; value: number }[] = [
  { label: 'No expectation', value: 0 },
  { label: 'Every minute', value: 60 },
  { label: 'Every 5 minutes', value: 300 },
  { label: 'Every 15 minutes', value: 900 },
  { label: 'Hourly', value: 3_600 },
  { label: 'Daily', value: 86_400 },
]

/** The grace for an interval: a fifth of it, at least a minute. */
export const defaultGrace = (interval: number) => (interval > 0 ? Math.max(60, Math.round(interval / 5)) : 0)

/** `every 5 min`, `daily`, `no expectation`. */
export function expectLabel(interval: number): string {
  const preset = EXPECT_EVERY.find((e) => e.value === interval)
  if (preset) return preset.label.toLowerCase()
  return interval > 0 ? `every ${formatDuration(interval * 1000)}` : 'no expectation'
}
