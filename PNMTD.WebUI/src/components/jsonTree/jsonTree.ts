import { toRaw, type ComputedRef, type InjectionKey, type Ref } from 'vue'
import { formatPath, type Outcome, type Segment } from '@/views/sensors/advanced/advanced'

/**
 * The pure part of JsonTree (docs/advanced-sensor-plan.md §10.3, *The report*): parsing in the app's key order, the
 * tables a report's lists and objects make, the filter over keys and values, the changes since a previous report, and the
 * raw text pretty-printed token by token. Nothing here renders; the components only show what these compute.
 */

// --- parsing in the app's order ---------------------------------------------------------------------------------------

/**
 * JavaScript lists integer-like keys first ("10", "200"), whatever their place in the text. For an object that has one, the
 * order as written is kept here, and `keysOf` reads it.
 */
const keyOrder = new WeakMap<object, string[]>()
const integerLike = /^(0|[1-9]\d*)$/

/** An object's keys in the app's order. */
export function keysOf(value: object): string[] {
  const raw = toRaw(value)
  return keyOrder.get(raw) ?? Object.keys(raw)
}

class Reader {
  private i = 0
  constructor(private readonly s: string) {}

  read(): unknown {
    const value = this.value()
    this.space()
    if (this.i < this.s.length) throw new SyntaxError('Unexpected text after the JSON')
    return value
  }

  private space() {
    const s = this.s
    while (this.i < s.length) {
      const c = s.charCodeAt(this.i)
      if (c !== 32 && c !== 9 && c !== 10 && c !== 13) return
      this.i++
    }
  }

  private value(): unknown {
    this.space()
    const c = this.s[this.i]
    if (c === '{') return this.object()
    if (c === '[') return this.array()
    if (c === '"') return this.string()
    for (const [word, value] of [
      ['true', true],
      ['false', false],
      ['null', null],
    ] as const) {
      if (this.s.startsWith(word, this.i)) {
        this.i += word.length
        return value
      }
    }
    const number = /-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?/y
    number.lastIndex = this.i
    const m = number.exec(this.s)
    if (!m || !m[0]) throw new SyntaxError(`Unexpected character at ${this.i}`)
    this.i += m[0].length
    return Number(m[0])
  }

  private string(): string {
    const s = this.s
    let j = this.i + 1
    while (j < s.length && s.charCodeAt(j) !== 34) j += s.charCodeAt(j) === 92 ? 2 : 1
    if (j >= s.length) throw new SyntaxError('Unterminated string')
    const raw = s.slice(this.i, j + 1)
    this.i = j + 1
    return raw.includes('\\') ? (JSON.parse(raw) as string) : raw.slice(1, -1)
  }

  private expect(c: string) {
    this.space()
    if (this.s[this.i] !== c) throw new SyntaxError(`'${c}' expected at ${this.i}`)
    this.i++
  }

  private object(): Record<string, unknown> {
    this.i++
    const out: Record<string, unknown> = {}
    const keys: string[] = []
    let reordered = false
    this.space()
    if (this.s[this.i] === '}') {
      this.i++
      return out
    }
    for (;;) {
      this.space()
      const key = this.string()
      this.expect(':')
      const value = this.value()
      if (!Object.prototype.hasOwnProperty.call(out, key)) keys.push(key)
      // An own property, never the prototype, as JSON.parse does.
      Object.defineProperty(out, key, { value, enumerable: true, writable: true, configurable: true })
      if (integerLike.test(key)) reordered = true
      this.space()
      const c = this.s[this.i++]
      if (c === '}') break
      if (c !== ',') throw new SyntaxError(`',' or '}' expected at ${this.i - 1}`)
    }
    if (reordered) keyOrder.set(out, keys)
    return out
  }

  private array(): unknown[] {
    this.i++
    const out: unknown[] = []
    this.space()
    if (this.s[this.i] === ']') {
      this.i++
      return out
    }
    for (;;) {
      out.push(this.value())
      this.space()
      const c = this.s[this.i++]
      if (c === ']') break
      if (c !== ',') throw new SyntaxError(`',' or ']' expected at ${this.i - 1}`)
    }
    return out
  }
}

/**
 * A report's text as values, in the app's key order. JSON.parse checks it and gives its error; the reader that keeps the
 * order only runs when an object has an integer-like key, the one case where JavaScript reorders.
 */
export function parseJson(text: string): unknown {
  const body = text.replace(/^\uFEFF/, '')
  const parsed: unknown = JSON.parse(body)
  if (!/"(0|[1-9]\d*)"\s*:/.test(body)) return parsed
  try {
    return new Reader(body).read()
  } catch {
    return parsed
  }
}

/**
 * The text as received, pretty-printed token by token: the app's order, its numbers as written (`3.20`, `1e3`), two
 * spaces per level. For valid JSON; anything else comes back unchanged.
 */
export function prettyJson(text: string): string {
  const s = text.replace(/^\uFEFF/, '')
  try {
    JSON.parse(s)
  } catch {
    return text
  }
  const indent = (depth: number) => '\n' + '  '.repeat(depth)
  const parts: string[] = []
  let depth = 0
  let i = 0
  const n = s.length
  const space = (c: string) => c === ' ' || c === '\n' || c === '\r' || c === '\t'
  while (i < n) {
    const c = s[i]!
    if (c === '"') {
      let j = i + 1
      while (j < n && s[j] !== '"') j += s[j] === '\\' ? 2 : 1
      parts.push(s.slice(i, j + 1))
      i = j + 1
    } else if (space(c)) {
      i++
    } else if (c === '{' || c === '[') {
      let j = i + 1
      while (j < n && space(s[j]!)) j++
      if (s[j] === (c === '{' ? '}' : ']')) {
        parts.push(c + s[j])
        i = j + 1
      } else {
        depth++
        parts.push(c + indent(depth))
        i++
      }
    } else if (c === '}' || c === ']') {
      depth--
      parts.push(indent(depth) + c)
      i++
    } else if (c === ',') {
      parts.push(',' + indent(depth))
      i++
    } else if (c === ':') {
      parts.push(': ')
      i++
    } else {
      let j = i
      while (j < n && !',:{}[]" \n\r\t'.includes(s[j]!)) j++
      parts.push(s.slice(i, j))
      i = j
    }
  }
  return parts.join('')
}

// --- kinds ------------------------------------------------------------------------------------------------------------

export const isObject = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null && !Array.isArray(v)

/** An object or a list: what JsonTreeNode shows. */
export type Container = Record<string, unknown> | unknown[]

export const isContainer = (v: unknown): v is Container => typeof v === 'object' && v !== null

export const isScalar = (v: unknown) => !isContainer(v)

/** `3 keys`, `1 item`, `empty`: a collapsed container in brief. */
export function summaryOf(value: unknown): string {
  if (Array.isArray(value)) {
    return value.length ? `${value.length} item${value.length === 1 ? '' : 's'}` : 'empty list'
  }
  if (isObject(value)) {
    const n = keysOf(value).length
    return n ? `${n} key${n === 1 ? '' : 's'}` : 'empty object'
  }
  return ''
}

/** A container with something in it, shown as a section or a table; an empty one shows as a value. */
export const hasChildren = (v: unknown) =>
  Array.isArray(v) ? v.length > 0 : isObject(v) ? keysOf(v).length > 0 : false

/** A value as JSON, two spaces per level, its objects in the app's order: *Copy value*. */
export function stringifyOrdered(value: unknown, depth = 0): string {
  const pad = '  '.repeat(depth + 1)
  const end = '  '.repeat(depth)
  if (Array.isArray(value)) {
    if (!value.length) return '[]'
    return `[\n${value.map((v) => pad + stringifyOrdered(v, depth + 1)).join(',\n')}\n${end}]`
  }
  if (isObject(value)) {
    const keys = keysOf(value)
    if (!keys.length) return '{}'
    return `{\n${keys.map((k) => `${pad}${JSON.stringify(k)}: ${stringifyOrdered(value[k], depth + 1)}`).join(',\n')}\n${end}}`
  }
  return JSON.stringify(value) ?? 'null'
}

/** A scalar as text, for the filter, copying and the changes' "before". */
export function scalarText(value: unknown): string {
  if (value === undefined) return ''
  if (typeof value === 'string') return value
  return JSON.stringify(value) ?? String(value)
}

/** The value at a position, or undefined when the report has nothing there. */
export function valueAt(root: unknown, segments: readonly Segment[]): unknown {
  let node: unknown = root
  for (const s of segments) {
    if (typeof s === 'number') {
      if (!Array.isArray(node)) return undefined
      node = node[s < 0 ? node.length + s : s]
    } else if (typeof s === 'string') {
      if (!isObject(node) || !Object.prototype.hasOwnProperty.call(node, s)) return undefined
      node = node[s]
    } else {
      return undefined
    }
  }
  return node
}

// --- tables (§10.3) ---------------------------------------------------------------------------------------------------

export interface TableRow {
  /** The member's name, or the element's index. */
  key: string | number
  value: Record<string, unknown>
  segments: Segment[]
  path: string
  /** The member's own path: for an IETF check, the list around the row's object. */
  memberPath: string
}

export interface TableShape {
  rows: TableRow[]
  columns: string[]
  /** An object's members: the first column is their name. */
  members: boolean
  /** IETF's `observedUnit`, shown next to `observedValue` instead of in a column of its own. */
  unitBeside: boolean
}

const MAX_SCALAR_COLUMNS = 12
const MAX_COLUMNS = 16

/**
 * A list of objects, or an object whose members are objects of the same shape (IETF `checks` hold a list of one object
 * each), as a table: at most 12 scalar keys among them, nested values expandable in their cells. The report itself stays
 * sections, and anything else is null.
 */
export function tableShape(value: unknown, segments: readonly Segment[]): TableShape | null {
  if (segments.length === 0) return null
  const rows: TableRow[] = []
  if (Array.isArray(value)) {
    if (!value.length || !value.every(isObject)) return null
    value.forEach((item, i) => {
      const at = [...segments, i]
      const path = formatPath(at)
      rows.push({ key: i, value: item as Record<string, unknown>, segments: at, path, memberPath: path })
    })
  } else if (isObject(value)) {
    let listsOfOne = false
    for (const key of keysOf(value)) {
      const member = value[key]
      if (isObject(member)) {
        const at = [...segments, key]
        const path = formatPath(at)
        rows.push({ key, value: member, segments: at, path, memberPath: path })
      } else if (Array.isArray(member) && member.length === 1 && isObject(member[0])) {
        const at = [...segments, key, 0]
        rows.push({
          key,
          value: member[0],
          segments: at,
          path: formatPath(at),
          memberPath: formatPath([...segments, key]),
        })
        listsOfOne = true
      } else {
        return null
      }
    }
    // One object in an object reads better as its members, unless it is a check of IETF's format.
    if (rows.length < (listsOfOne ? 1 : 2)) return null
  } else {
    return null
  }

  const columns: string[] = []
  const seen = new Set<string>()
  const counts = new Map<string, number>()
  for (const row of rows) {
    for (const key of keysOf(row.value)) {
      if (!seen.has(key)) {
        seen.add(key)
        columns.push(key)
      }
      counts.set(key, (counts.get(key) ?? 0) + 1)
    }
  }
  const scalar = columns.filter((c) => rows.every((r) => isScalar(r.value[c])))
  if (!scalar.length || scalar.length > MAX_SCALAR_COLUMNS || columns.length > MAX_COLUMNS) return null
  if (!Array.isArray(value)) {
    // The same shape: most keys in every member, so `{ backup: {…}, disk: {…} }` stays sections.
    const common = columns.filter((c) => counts.get(c) === rows.length).length
    if (common === 0 || common * 2 < columns.length) return null
  }
  const unitBeside = seen.has('observedValue') && seen.has('observedUnit')
  return {
    rows,
    columns: unitBeside ? columns.filter((c) => c !== 'observedUnit') : columns,
    members: !Array.isArray(value),
    unitBeside,
  }
}

/** Sorting a column: numbers as numbers, text naturally, booleans false first; missing and null last. */
export function compareCells(a: unknown, b: unknown): number {
  const empty = (v: unknown) => v === undefined || v === null
  if (empty(a) || empty(b)) return empty(a) === empty(b) ? 0 : empty(a) ? 1 : -1
  if (typeof a === 'number' && typeof b === 'number') return a - b
  if (typeof a === 'boolean' && typeof b === 'boolean') return Number(a) - Number(b)
  return scalarText(a).localeCompare(scalarText(b), undefined, { numeric: true, sensitivity: 'base' })
}

// --- the filter (§10.3) -----------------------------------------------------------------------------------------------

export interface FilterResult {
  /** The positions to show: every match and the way to it. */
  show: Set<string>
  /** Positions whose key matched: everything below them shows. */
  whole: Set<string>
  matches: number
}

/** Walks a report for a filter over keys and values, case-insensitive. Null without a filter. */
export function filterTree(root: unknown, text: string): FilterResult | null {
  const needle = text.trim().toLowerCase()
  if (!needle) return null
  const show = new Set<string>()
  const whole = new Set<string>()
  let matches = 0
  const stack: string[] = []
  const reveal = (path: string) => {
    for (let i = stack.length - 1; i >= 0 && !show.has(stack[i]!); i--) show.add(stack[i]!)
    show.add(path)
  }
  const walk = (value: unknown, segments: Segment[], key: string | null) => {
    const path = formatPath(segments)
    if (key !== null && key.toLowerCase().includes(needle)) {
      matches++
      whole.add(path)
      reveal(path)
      return
    }
    if (isContainer(value)) {
      stack.push(path)
      if (Array.isArray(value)) value.forEach((v, i) => walk(v, [...segments, i], null))
      else for (const k of keysOf(value)) walk(value[k], [...segments, k], k)
      stack.pop()
    } else if (scalarText(value).toLowerCase().includes(needle)) {
      matches++
      reveal(path)
    }
  }
  walk(root, [], null)
  return { show, whole, matches }
}

// --- changes since the previous report (§10.3) ------------------------------------------------------------------------

export interface Changes {
  /** Each changed or new value's position, with what it was (undefined: not there before). */
  changed: Map<string, unknown>
  /** Positions with a change below them. */
  inside: Set<string>
  /** Values the previous report had and this one hasn't. */
  removed: number
}

const sameKind = (a: unknown, b: unknown) =>
  Array.isArray(a) ? Array.isArray(b) : isObject(a) ? isObject(b) : isScalar(b)

/** What differs from the previous report, value by value, by position. */
export function changesSince(current: unknown, previous: unknown): Changes {
  const changed = new Map<string, unknown>()
  const inside = new Set<string>()
  let removed = 0
  const stack: string[] = []
  const mark = (path: string, before: unknown) => {
    changed.set(path, before)
    for (const p of stack) inside.add(p)
  }
  const count = (v: unknown): number =>
    Array.isArray(v)
      ? v.reduce((n: number, x) => n + count(x), 0)
      : isObject(v)
        ? keysOf(v).reduce((n, k) => n + count(v[k]), 0)
        : 1
  const walk = (now: unknown, before: unknown, segments: Segment[]) => {
    const path = formatPath(segments)
    if (before === undefined || !sameKind(now, before)) {
      mark(path, before)
      if (isContainer(now)) inside.add(path)
      return
    }
    if (!isContainer(now)) {
      if (now !== before) mark(path, before)
      return
    }
    stack.push(path)
    if (Array.isArray(now)) {
      const was = before as unknown[]
      now.forEach((v, i) => walk(v, was[i], [...segments, i]))
      for (let i = now.length; i < was.length; i++) removed += count(was[i])
      if (was.length > now.length) for (const p of stack) inside.add(p)
    } else {
      const was = before as Record<string, unknown>
      for (const k of keysOf(now))
        walk(now[k], Object.prototype.hasOwnProperty.call(was, k) ? was[k] : undefined, [...segments, k])
      for (const k of keysOf(was)) {
        if (!Object.prototype.hasOwnProperty.call(now, k)) {
          removed += count(was[k])
          for (const p of stack) inside.add(p)
        }
      }
    }
    stack.pop()
  }
  walk(current, previous, [])
  return { changed, inside, removed }
}

// --- the tree's context ------------------------------------------------------------------------------------------------

/** A rule's mark on a value: its outcome (null: looked at, but not one of the values that decided it), name and why. */
export interface JsonMark {
  outcome: Outcome | null
  name: string
  detail?: string | null
}

/** A value picked from the tree: *Add rule…* (with `every`: the path over every element) and *Add field…*. */
export interface JsonPick {
  path: string
  value: unknown
  segments: Segment[]
  every: boolean
}

export interface JsonTreeContext {
  marks: ComputedRef<ReadonlyMap<string, readonly JsonMark[]>>
  changes: ComputedRef<Changes | null>
  filter: ComputedRef<FilterResult | null>
  now: Ref<number>
  openMenu: (event: Event, segments: Segment[], value: unknown) => void
}

export const jsonTreeKey: InjectionKey<JsonTreeContext> = Symbol('json-tree')

/** Rows per page in long lists and tables; chips before *+n more*. */
export const PAGE_SIZE = 100
export const CHIP_LIMIT = 50
