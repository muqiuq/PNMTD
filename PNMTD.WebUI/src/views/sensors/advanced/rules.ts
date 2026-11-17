import type { S } from '@/api/resources'
import {
  actualText,
  nameFromPath,
  naturalRule,
  operandProblem,
  operatorInfo,
  outcomeLabel,
  reasonLabel,
  type JsonKind,
  type Operator,
  type Quantifier,
  type Severity,
  type WhenMissing,
} from './advanced'

/**
 * The Rules tab's helpers (docs/advanced-sensor-plan.md §6, §10.3): a rule as the editor holds it and as the API takes it,
 * which fields an operator needs, the choices with their explanations, and an outcome in one line. The server validates
 * and judges; these only prepare and show.
 */

/** A rule as the editor holds it: every text a string, so the inputs bind to it. */
export interface RuleDraft {
  name: string
  enabled: boolean
  note: string
  path: string
  operator: Operator
  operand: string
  operand2: string
  ignoreCase: boolean
  quantifier: Quantifier
  whenMissing: WhenMissing
  severity: Severity
}

export const MAX_NAME = 200
export const MAX_NOTE = 1000

/** A new rule: on, failing the sensor when its value is missing (§6.2). */
export function blankDraft(): RuleDraft {
  return {
    name: '',
    enabled: true,
    note: '',
    path: '',
    operator: 'Exists',
    operand: '',
    operand2: '',
    ignoreCase: false,
    quantifier: 'All',
    whenMissing: 'Fail',
    severity: 'Fail',
  }
}

/** The editor's draft from a saved rule, or from the Data tab's *Add rule…* (a partial request). */
export function draftOf(
  source: Partial<S['AdvancedRuleRequest']> | S['AdvancedRuleItem'] | null | undefined,
): RuleDraft {
  const blank = blankDraft()
  if (!source) return blank
  return {
    name: source.name ?? (source.path ? nameFromPath(source.path) : ''),
    enabled: source.enabled ?? blank.enabled,
    note: source.note ?? '',
    path: source.path ?? '',
    operator: source.operator ?? blank.operator,
    operand: source.operand ?? '',
    operand2: source.operand2 ?? '',
    ignoreCase: source.ignoreCase ?? false,
    quantifier: source.quantifier ?? blank.quantifier,
    whenMissing: source.whenMissing ?? blank.whenMissing,
    severity: source.severity ?? blank.severity,
  }
}

/** Whether the operator takes an operand, and a second one (`between`, `outside`), as the server's AdvancedRules. */
export const takesOperand = (op: Operator) => operatorInfo(op).operand !== null
export const takesOperand2 = (op: Operator) => operatorInfo(op).operand === 'range'

/** Whether *ignore case* means anything for the operator: the text-ish ones (the server's UsesCase). */
export const usesCase = (op: Operator) =>
  [
    'Eq',
    'Ne',
    'Contains',
    'NotContains',
    'StartsWith',
    'EndsWith',
    'Matches',
    'NotMatches',
    'OneOf',
    'NotOneOf',
  ].includes(op)

const isCount = (op: Operator) => operatorInfo(op).group === 'Count'
const decidesMissing = (op: Operator) => op === 'Exists' || op === 'Absent'

/** A path with `[*]` or `.*` outside quoted keys: its rule has *several matches* (§6.2). */
export function pathHasWildcard(path: string): boolean {
  const unquoted = path.replace(/\["(?:[^"\\]|\\.)*"\]/g, '[""]')
  return /\[\*\]|(^|\.)\*(?=$|[.[])/.test(unquoted.trim())
}

/** *For several matches* means something: a path with a wildcard, and an operator that looks at each match. */
export const showsQuantifier = (draft: Pick<RuleDraft, 'path' | 'operator'>) =>
  pathHasWildcard(draft.path) && !isCount(draft.operator) && !decidesMissing(draft.operator)

/** *When missing* means something: not for `exists` and `absent`, nor a count over matches (0 is a count). */
export const showsWhenMissing = (draft: Pick<RuleDraft, 'path' | 'operator'>) =>
  !decidesMissing(draft.operator) && !(isCount(draft.operator) && pathHasWildcard(draft.path))

/** The draft as the API takes it: only the operands and options its operator uses, texts trimmed where the server trims. */
export function ruleRequest(draft: RuleDraft, id?: string | null): S['AdvancedRuleRequest'] {
  const op = draft.operator
  return {
    ...(id ? { id } : {}),
    name: draft.name.trim(),
    enabled: draft.enabled,
    note: draft.note.trim() || null,
    path: draft.path.trim(),
    operator: op,
    operand: takesOperand(op) ? draft.operand : null,
    operand2: takesOperand2(op) ? draft.operand2.trim() : null,
    ignoreCase: usesCase(op) && draft.ignoreCase,
    quantifier: showsQuantifier(draft) ? draft.quantifier : 'All',
    whenMissing: draft.whenMissing,
    severity: draft.severity,
  }
}

/** A saved rule as a request, with a change: switching it on or off, a copy for *Duplicate*. */
export function itemRequest(rule: S['AdvancedRuleItem'], patch: Partial<RuleDraft> = {}): S['AdvancedRuleRequest'] {
  return ruleRequest({ ...draftOf(rule), ...patch })
}

/** `Disk space (copy)`, within the name's 200 characters. */
export const copyName = (name: string) => `${name.slice(0, MAX_NAME - 7)} (copy)`

/**
 * The operand checked as the server will, with the field it belongs to: `operand2` only for a range whose first number
 * fits. Null when it fits.
 */
export function operandError(draft: RuleDraft): { field: 'operand' | 'operand2'; message: string } | null {
  if (!takesOperand(draft.operator)) return null
  const message = operandProblem(draft.operator, draft.operand, takesOperand2(draft.operator) ? draft.operand2 : null)
  if (!message) return null
  const firstFits = operandProblem('Ge', draft.operand) === null
  return { field: takesOperand2(draft.operator) && firstFits ? 'operand2' : 'operand', message }
}

/** What the editor can say before the server does: an empty name or path, a name or note too long, the operand. */
export function draftErrors(draft: RuleDraft): Record<string, string> {
  const errors: Record<string, string> = {}
  const name = draft.name.trim()
  if (!name || name.length > MAX_NAME) errors.name = `A name of 1 to ${MAX_NAME} characters.`
  if (!draft.path.trim()) errors.path = 'A path, such as disk.free_gb or jobs[*].state.'
  if (draft.note.length > MAX_NOTE) errors.note = `At most ${MAX_NOTE} characters.`
  const operand = operandError(draft)
  if (operand) errors[operand.field] = operand.message
  return errors
}

/** The fields of a rule request: a 400's errors go under them (`rules[0].operand` from the test is `operand`). */
export const RULE_FIELDS = [
  'name',
  'path',
  'operator',
  'operand',
  'operand2',
  'quantifier',
  'whenMissing',
  'severity',
  'note',
] as const

export function fieldErrors(errors: Record<string, string[]>, prefix = ''): Record<string, string> {
  const out: Record<string, string> = {}
  for (const [key, messages] of Object.entries(errors)) {
    if (!messages?.length) continue
    const name = prefix && key.startsWith(prefix) ? key.slice(prefix.length) : key
    out[(RULE_FIELDS as readonly string[]).includes(name) ? name : '_'] ??= messages[0]!
  }
  return out
}

// --- the paths of the latest report (GET paths) -----------------------------------------------------------------------

/** The server's JsonValueKind in lower case (`true`, `false`, …) as the editor's kinds. */
export function pathKind(kind: string | null | undefined): JsonKind | null {
  switch (kind) {
    case 'string':
    case 'number':
    case 'null':
    case 'array':
    case 'object':
      return kind
    case 'true':
    case 'false':
    case 'boolean':
      return 'boolean'
    default:
      return null
  }
}

/** A path's entry among the latest report's: as written, or with its indexes as `[*]` (`jobs[3].state`). */
export function findPath(paths: readonly S['AdvancedPathItem'][], path: string): S['AdvancedPathItem'] | null {
  const text = path.trim().replace(/^\$\.?/, '')
  if (!text) return null
  return paths.find((p) => p.path === text) ?? paths.find((p) => p.path === text.replace(/\[-?\d+\]/g, '[*]')) ?? null
}

/** A path's preview (`"ok"`, `3.2`, `true`) as a value, for the operator it suggests. */
export function previewValue(item: S['AdvancedPathItem']): unknown {
  switch (pathKind(item.kind)) {
    case 'array':
      return []
    case 'object':
      return {}
    case 'null':
      return null
    case 'boolean':
      return item.kind === 'true'
    default: {
      const preview = item.preview ?? ''
      try {
        return JSON.parse(preview)
      } catch {
        // A long text is cut with …: drop its opening quote.
        return preview.replace(/^"/, '').replace(/…$/, '')
      }
    }
  }
}

/** The operator and operand a path's current value suggests, as *Add rule…* does on the Data tab. */
export function suggestionFor(item: S['AdvancedPathItem']): { operator: Operator; operand: string | null } {
  if (!item.preview && !['array', 'object', 'null', 'true', 'false'].includes(item.kind)) {
    // A [*] path from a list of objects: the kind without a value.
    const kind = pathKind(item.kind)
    if (kind === 'number') return { operator: 'Ge', operand: '0' }
    if (kind === 'boolean') return { operator: 'IsTrue', operand: null }
    return { operator: 'Exists', operand: null }
  }
  return naturalRule(previewValue(item))
}

const kindWords: Record<JsonKind, string> = {
  string: 'text',
  number: 'a number',
  boolean: 'true or false',
  null: 'null',
  array: 'a list',
  object: 'an object',
  missing: 'missing',
}

/** `a number`, `text`, `a list`: the kind in words. */
export const kindLabel = (kind: JsonKind | null) => (kind ? kindWords[kind] : 'unknown')

// --- the choices, explained (§6.2) ------------------------------------------------------------------------------------

export const QUANTIFIERS: readonly { value: Quantifier; label: string; hint: string }[] = [
  { value: 'All', label: 'All', hint: 'every match must hold' },
  { value: 'Any', label: 'Any', hint: 'at least one match must hold' },
  { value: 'None', label: 'None', hint: 'no match may hold, as in “none of the jobs is failed”' },
]

export const WHEN_MISSING: readonly { value: WhenMissing; label: string; hint: string }[] = [
  { value: 'Fail', label: 'Fail', hint: 'a value that disappears is usually a problem' },
  { value: 'Warn', label: 'Warn', hint: 'shown in amber; the sensor stays OK' },
  { value: 'Pass', label: 'Pass', hint: 'for optional parts of the report' },
]

export const SEVERITIES: readonly { value: Severity; label: string; hint: string }[] = [
  { value: 'Fail', label: 'Fail', hint: 'the sensor fails (495) when the rule doesn’t hold' },
  { value: 'Warn', label: 'Warn', hint: 'a warning (296): the sensor stays OK, shown in amber' },
]

/** A word on what an operator compares with, where it isn't obvious (§6.3). */
export function operatorNote(op: Operator): string | null {
  switch (operatorInfo(op).group) {
    case 'Age':
      return "Compared with PNMT's clock now, and checked again every 30 s between reports. A time is ISO 8601 or Unix seconds; one without an offset is read as UTC."
    case 'Change':
      return 'Compared with the same path in the previous report; the first report passes.'
    case 'Count':
      return 'For a path with *, the number of matches; for a list or an object, its items; for text, its length.'
    case 'State':
      return 'Healthy: ok, pass, passed, up, healthy, success, green, true. The words warn, warning, degraded and yellow warn, even on a failing rule; anything else fails. Case doesn’t matter.'
    default:
      switch (op) {
        case 'Eq':
        case 'Ne':
          return 'A number compares as a number (5 equals 5.0), true, false and null as themselves, anything else as text.'
        case 'Exists':
        case 'Absent':
          return 'Decides by itself when nothing matches: “when missing” doesn’t apply.'
        case 'IsTrue':
        case 'IsFalse':
          return 'The boolean only: the text "true" is not.'
        default:
          return null
      }
  }
}

// --- an outcome in one line -------------------------------------------------------------------------------------------

/**
 * `disk.free_gb = 3.2 → fails (must be at least 5)`: the values (or the path, or that it is missing), the outcome, and why.
 * Without `values` (a sensor whose data names people, viewed without Details) only the path and why.
 */
export function outcomeLine(result: S['AdvancedRuleResult'], values = true): string {
  const { what, label, why } = outcomeParts(result, values)
  return `${what} → ${label}${why ? ` (${why})` : ''}`
}

/** The pieces of outcomeLine, for showing them each in its own style. */
export function outcomeParts(
  result: S['AdvancedRuleResult'],
  values = true,
): { what: string; label: string; why: string | null } {
  const missing = result.reason === 'Missing'
  const what = values ? actualText(result) || result.path : missing ? `${result.path} missing` : result.path
  const why =
    result.outcome === 'Off'
      ? 'the rule is off'
      : missing
        ? reasonLabel(result.reason)
        : result.detail || reasonLabel(result.reason)
  return { what, label: outcomeLabel(result.outcome), why }
}
