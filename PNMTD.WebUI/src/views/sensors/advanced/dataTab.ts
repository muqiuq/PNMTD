import type { S } from '@/api/resources'
import type { JsonMark, JsonPick } from '@/components/jsonTree/jsonTree'
import { formatAbsolute, formatDuration, formatTime } from '@/format'
import {
  actualText,
  isHealthWord,
  kindOf,
  looksLikeTime,
  nameFromPath,
  naturalRule,
  outcomeLabel,
  reasonLabel,
  type FieldFormat,
  type Outcome,
} from './advanced'

/**
 * The Data tab's helpers (docs/advanced-sensor-plan.md §10.3): the rule marks a report's outcomes put on its values, the
 * outcomes as a list, what *Add rule…* and *Add field…* suggest for a value, and the headline's texts.
 */

/**
 * The marks of a report's outcomes, by concrete path (§3.2): the values that decided a rule carry its outcome; the other
 * matches carry ✓ when the rule holds, and otherwise a plain mark (the rule looks at them, others decided it).
 */
export function marksFrom(results: readonly S['AdvancedRuleResult'][] | null | undefined): Map<string, JsonMark[]> {
  const marks = new Map<string, JsonMark[]>()
  const add = (path: string, mark: JsonMark) => {
    const list = marks.get(path) ?? []
    if (!list.some((m) => m.name === mark.name && m.outcome === mark.outcome)) list.push(mark)
    marks.set(path, list)
  }
  for (const r of results ?? []) {
    if (r.outcome === 'Off') continue
    const detail = r.outcome === 'Pass' ? r.condition : (r.detail ?? r.condition)
    const decided = new Set(r.actual.map((a) => a.path))
    for (const a of r.actual) add(a.path, { outcome: r.outcome, name: r.name, detail })
    for (const m of r.matches) {
      if (decided.has(m)) continue
      add(
        m,
        r.outcome === 'Pass'
          ? { outcome: 'Pass', name: r.name, detail }
          : { outcome: null, name: r.name, detail: `${outcomeLabel(r.outcome)} at other values` },
      )
    }
  }
  return marks
}

/** A rule's outcome as a line of a list. */
export interface OutcomeRow {
  key: string
  name: string
  condition: string
  outcome: Outcome | null
  reason: string | null
  /** The values that decided it; null where they mustn't show (§13). */
  values: string | null
}

export function outcomeRows(results: readonly S['AdvancedRuleResult'][], withValues: boolean): OutcomeRow[] {
  return results.map((r) => ({
    key: r.ruleId,
    name: r.name,
    condition: r.condition,
    outcome: r.outcome,
    reason: reasonLabel(r.reason) ?? (r.outcome === 'Pass' || r.outcome === 'Off' ? null : r.detail),
    values: withValues ? actualText(r) || null : null,
  }))
}

/** The rules with their latest outcomes, without values: for a viewer without Monitoring: Details (§10.3). */
export function ruleRows(rules: readonly S['AdvancedRuleItem'][]): OutcomeRow[] {
  return rules.map((r) => ({
    key: r.id,
    name: r.name,
    condition: r.condition,
    outcome: r.enabled ? (r.last?.outcome ?? null) : 'Off',
    reason: r.last ? reasonLabel(r.last.reason) : null,
    values: null,
  }))
}

/** *Add rule…* (§10.3): the path, a name from it, the value's natural operator; over every element: all must hold. */
export function ruleDraft(pick: JsonPick): Partial<S['AdvancedRuleRequest']> {
  return {
    name: nameFromPath(pick.path),
    path: pick.path,
    ...naturalRule(pick.value),
    ...(pick.every ? { quantifier: 'All' as const } : {}),
  }
}

const numeric = (v: unknown) =>
  typeof v === 'number' || (typeof v === 'string' && v.trim() !== '' && Number.isFinite(Number(v)))

/** Units read from a key's ending: `free_gb` → GB, `latency_ms` → ms. */
const unitEndings: [RegExp, string][] = [
  [/[_-]?tb$/i, 'TB'],
  [/[_-]?gb$/i, 'GB'],
  [/[_-]?mb$/i, 'MB'],
  [/[_-]?kb$/i, 'KB'],
  [/[_-]ms$|^ms$/i, 'ms'],
]

/**
 * *Add field…* (§10.3): a name from the path, a format from the value and its key (bytes, seconds, percent, a time, a
 * status word, a boolean), a unit from the key's ending, charted when it is a number and fewer than ten are.
 */
export function fieldDraft(pick: JsonPick, charted: number): Partial<S['AdvancedFieldRequest']> {
  const key =
    pick.path
      .replace(/\[[^\]]*\]$/, '')
      .split(/[.[\]"]+/)
      .filter(Boolean)
      .pop() ?? ''
  const value = pick.value
  let format: FieldFormat = 'Auto'
  let unit: string | null = null
  if (typeof value === 'boolean') format = 'Boolean'
  else if (typeof value === 'string' && looksLikeTime(value)) format = 'DateTime'
  else if (typeof value === 'string' && isHealthWord(value)) format = 'Status'
  else if (numeric(value)) {
    if (/bytes?$/i.test(key)) format = 'Bytes'
    else if (/(percent|pct)$/i.test(key)) format = 'Percent'
    else if (/([_-](s|sec|secs|seconds)$|^seconds$|duration$|uptime$)/i.test(key)) format = 'Duration'
    else unit = unitEndings.find(([re]) => re.test(key))?.[1] ?? null
  } else if (kindOf(value) === 'string') format = 'Text'
  return {
    name: nameFromPath(pick.path),
    path: pick.path,
    format,
    unit,
    decimals: null,
    pinned: false,
    chart: numeric(value) && charted < 10,
  }
}

/** The verdict's codes in words (§3). */
export function codeLabel(code: number | null | undefined): string {
  switch (code) {
    case 295:
      return 'all rules hold'
    case 296:
      return 'warning'
    case 495:
      return 'failed'
    case 496:
      return "couldn't be read"
    case 460:
      return 'missing'
    case null:
    case undefined:
      return 'not judged yet'
    default:
      return code <= 399 ? 'success' : 'failure'
  }
}

/** The same report again (§7.2): *×1 440 until 10:02*, the time with its date when that isn't today. */
export function repeatText(report: { repeats: number; lastReceivedAt: string }, now = Date.now()): string | null {
  if (report.repeats <= 0) return null
  const last = new Date(report.lastReceivedAt)
  const today = new Date(now).toDateString() === last.toDateString()
  return `×${(report.repeats + 1).toLocaleString()} until ${today ? formatTime(last) : formatAbsolute(last)}`
}

/** *The app's clock is 7 min behind* (§4.2): measured minus received, beyond 5 minutes. */
export function skewText(measuredAt: string | null | undefined, receivedAt: string): string | null {
  if (!measuredAt) return null
  const seconds = Math.round((Date.parse(measuredAt) - Date.parse(receivedAt)) / 1000)
  if (!Number.isFinite(seconds) || Math.abs(seconds) <= 300) return null
  return `the app's clock is ${formatDuration(Math.abs(seconds) * 1000)} ${seconds < 0 ? 'behind' : 'ahead'}`
}

/** What arrived of an invalid report, as text (§10.3): the first 2 KB. */
export const HEAD_LENGTH = 2048

export function headOf(body: string | null | undefined): string | null {
  if (!body) return null
  return body.length > HEAD_LENGTH ? `${body.slice(0, HEAD_LENGTH)}…` : body
}

/** `backup-01 2026-11-17 10-02-03.json`: a report's file name for *Download*. */
export function reportFileName(sensorName: string, receivedAt: string): string {
  const d = new Date(receivedAt)
  const pad = (n: number) => String(n).padStart(2, '0')
  const stamp = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}-${pad(d.getMinutes())}-${pad(d.getSeconds())}`
  const name = sensorName.replace(/[\\/:*?"<>|]+/g, '-').trim() || 'report'
  return `${name} ${stamp}.json`
}
