import type { RouteLocationRaw } from 'vue-router'

/**
 * The charts' arithmetic (docs/licences-plan.md §8.12): nice axis maxima and ticks, the columns' and bars' sizes, and the rows a
 * chart's table shows. Each chart draws its marks and its table from the same rows, so nothing is readable only as a shape. Pure,
 * so that chartScale.test.ts checks it without a browser.
 */

/** A step of 1, 2 or 5 times a power of ten, at least `raw`. */
export function niceStep(raw: number): number {
  if (!(raw > 0) || !Number.isFinite(raw)) return 1
  const power = 10 ** Math.floor(Math.log10(raw))
  const fraction = raw / power
  const nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10
  return round(nice * power)
}

export interface Axis {
  /** The axis' maximum: a whole number of steps, at least the largest value. */
  top: number
  step: number
  ticks: number[]
}

/** About `target` intervals from 0 to a round top. Nothing to show: only the baseline, over a top of 1. */
export function axis(max: number, target = 4): Axis {
  if (!(max > 0) || !Number.isFinite(max)) return { top: 1, step: 1, ticks: [0] }
  const step = niceStep(max / Math.max(1, target))
  const top = round(Math.ceil(round(max / step)) * step)
  const ticks: number[] = []
  for (let i = 0; i * step <= top + step / 1000; i++) ticks.push(round(i * step))
  return { top, step, ticks }
}

/** Floating point's 0.30000000000000004 away. */
function round(value: number): number {
  return Math.round(value * 1e9) / 1e9
}

const whole = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 0 })
const thousands = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 1 })

/** One style per axis: in full below 10,000 (0, 2,500, 5,000), in thousands from it (0, 20k, 40k), as §8.12 writes them. */
export function tickLabels(ticks: readonly number[]): string[] {
  const top = Math.max(0, ...ticks.map(Math.abs))
  if (top >= 1_000_000) return ticks.map((t) => (t === 0 ? '0' : `${thousands.format(t / 1_000_000)}M`))
  if (top >= 10_000) return ticks.map((t) => (t === 0 ? '0' : `${thousands.format(t / 1000)}k`))
  return ticks.map((t) => whole.format(t))
}

/** A column's width in a slot: at most 24 px, never the whole slot, at least 2 px. */
export function barWidth(slot: number, cap = 24): number {
  return Math.max(2, Math.min(cap, Math.floor(slot * 0.62)))
}

/** Every how many columns a label fits under them: 1 for each, 2 for every other, … */
export function labelEvery(slot: number, labelWidth: number): number {
  if (!(slot > 0)) return 1
  return Math.max(1, Math.ceil(labelWidth / slot))
}

/** A column's outline: its data end (the top) rounded by up to 4 px, square at the baseline. */
export function columnPath(x: number, y: number, width: number, height: number, radius = 4): string {
  if (height <= 0 || width <= 0) return ''
  const r = Math.max(0, Math.min(radius, width / 2, height))
  const f = (n: number) => Math.round(n * 100) / 100
  return [
    `M${f(x)},${f(y + height)}`,
    `V${f(y + r)}`,
    `Q${f(x)},${f(y)} ${f(x + r)},${f(y)}`,
    `H${f(x + width - r)}`,
    `Q${f(x + width)},${f(y)} ${f(x + width)},${f(y + r)}`,
    `V${f(y + height)}`,
    'Z',
  ].join(' ')
}

// --- columns: spent and forecast, stacked in one hue ---------------------------------------------------------------------

/** One column: a month or a year, what was spent and what is forecast in it. */
export interface ColumnDatum {
  key: string
  label: string
  spent: number
  forecast: number
}

/** A column of CostColumns.vue: its figures and what its tooltip says besides. */
export interface CostColumn extends ColumnDatum {
  /** The tooltip's and the table's name: "March 2026". */
  name: string
  /** Of the forecast, what depends on a renewal by hand (§6.4). */
  ifRenewed?: number
  /** Its largest categories, for the tooltip. */
  top?: readonly { name: string; amount: number }[]
  /** The column today is in: the rule marks it. */
  current?: boolean
  /** Spent so far and the forecast for the rest, not told apart (the costs' current month): drawn as forecast. */
  mixed?: boolean
}

/** The column chart's frame: the whole size and the plot's margins. */
export interface ColumnBox {
  width: number
  height: number
  left: number
  right: number
  top: number
  bottom: number
}

export interface Segment {
  y: number
  height: number
}

export interface ColumnMark {
  key: string
  index: number
  /** The slot: the hit area, wider than the mark. */
  slotX: number
  slotWidth: number
  /** The mark. */
  x: number
  width: number
  spent: Segment | null
  forecast: Segment | null
  /** The column's top, for the tooltip. */
  top: number
  total: number
}

export interface ColumnLayout {
  axis: Axis
  slot: number
  barWidth: number
  plotTop: number
  plotBottom: number
  y: (value: number) => number
  marks: ColumnMark[]
}

/**
 * The columns in a box: spent from the baseline, the forecast on top of it, a 2 px surface gap between the two, so that a
 * column's height is its total. A value below a pixel still shows as one; credits beyond the charges draw nothing (the table
 * has the figure).
 */
export function columnLayout(data: readonly ColumnDatum[], box: ColumnBox, gap = 2): ColumnLayout {
  const plotTop = box.top
  const plotBottom = box.height - box.bottom
  const plotHeight = Math.max(1, plotBottom - plotTop)
  const plotWidth = Math.max(1, box.width - box.left - box.right)
  const scale = axis(Math.max(0, ...data.map((d) => Math.max(0, d.spent) + Math.max(0, d.forecast))))
  const slot = plotWidth / Math.max(1, data.length)
  const bw = barWidth(slot)
  const size = (v: number) => (v > 0 ? Math.max(1, (v / scale.top) * plotHeight) : 0)
  const y = (v: number) => plotBottom - (Math.max(0, v) / scale.top) * plotHeight
  const marks = data.map((d, index) => {
    const slotX = box.left + index * slot
    const spentHeight = size(d.spent)
    const forecastHeight = size(d.forecast)
    const spent = spentHeight ? { y: plotBottom - spentHeight, height: spentHeight } : null
    const below = spent ? spent.y - gap : plotBottom
    const forecastShown = spent ? Math.max(1, forecastHeight - gap) : forecastHeight
    const forecast = forecastHeight ? { y: below - forecastShown, height: forecastShown } : null
    return {
      key: d.key,
      index,
      slotX,
      slotWidth: slot,
      x: slotX + (slot - bw) / 2,
      width: bw,
      spent,
      forecast,
      top: forecast?.y ?? spent?.y ?? plotBottom,
      total: d.spent + d.forecast,
    }
  })
  return { axis: scale, slot, barWidth: bw, plotTop, plotBottom, y, marks }
}

export interface ColumnRow {
  key: string
  label: string
  spent: number
  forecast: number
  total: number
}

/** The table beside the columns: the same columns, in the same order, with the same figures. */
export function columnTable(data: readonly ColumnDatum[]): ColumnRow[] {
  return data.map((d) => ({
    key: d.key,
    label: d.label,
    spent: d.spent,
    forecast: d.forecast,
    total: d.spent + d.forecast,
  }))
}

// --- ranked bars ----------------------------------------------------------------------------------------------------------

export interface BarDatum {
  key: string
  label: string
  amount: number
}

export type RankedBar<T extends BarDatum> = T & {
  /** The bar's length against the largest, 0 to 1. */
  fraction: number
}

/**
 * Largest first; the rows `pinned` says (*Other providers (14)*) stay last, in their order. Each bar's length is its share of the
 * largest; nothing below zero is drawn.
 */
export function rankedLayout<T extends BarDatum>(
  rows: readonly T[],
  pinned: (row: T) => boolean = () => false,
): RankedBar<T>[] {
  const free = rows.filter((r) => !pinned(r))
  const last = rows.filter((r) => pinned(r))
  const ordered = [...free].sort((a, b) => b.amount - a.amount)
  const max = Math.max(0, ...rows.map((r) => r.amount))
  return [...ordered, ...last].map((r) => ({ ...r, fraction: max > 0 ? Math.max(0, r.amount) / max : 0 }))
}

// --- meters -----------------------------------------------------------------------------------------------------------------

export interface Meter {
  /** How much of the track the fill takes, 0 to 1. */
  fraction: number
  /** Used beyond what is covered. */
  over: number
  /** Covered and not used. */
  free: number
}

/** A row of MeterList.vue. */
export interface MeterRow {
  key: string
  label: string
  used: number
  total: number
  /** devices, users, cores… */
  unit: string
  to?: RouteLocationRaw
}

/** Used against covered: full when over, with how much over. */
export function meter(used: number, total: number): Meter {
  const safeUsed = Math.max(0, used)
  if (!(total > 0)) return { fraction: safeUsed > 0 ? 1 : 0, over: safeUsed, free: 0 }
  return {
    fraction: Math.min(1, safeUsed / total),
    over: Math.max(0, safeUsed - total),
    free: Math.max(0, total - safeUsed),
  }
}

// --- months ------------------------------------------------------------------------------------------------------------------

const shortMonths = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const longMonths = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
]

/** "Mar", or "Mar 26" with the year; a year ("2026") stays as it is. */
export function monthLabel(key: string, withYear: boolean): string {
  const m = /^(\d{4})-(\d{2})$/.exec(key)
  if (!m) return key
  const name = shortMonths[Number(m[2]) - 1] ?? m[2]
  return withYear ? `${name} ${m[1]!.slice(2)}` : name
}

/** "March 2026"; a year stays as it is. */
export function monthLong(key: string): string {
  const m = /^(\d{4})-(\d{2})$/.exec(key)
  if (!m) return key
  return `${longMonths[Number(m[2]) - 1] ?? m[2]} ${m[1]}`
}

/** Whether the months of these columns span more than one year, so their labels need it. */
export function spansYears(keys: readonly string[]): boolean {
  return new Set(keys.map((k) => k.slice(0, 4))).size > 1
}
