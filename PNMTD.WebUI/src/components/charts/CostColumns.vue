<script setup lang="ts">
import Button from 'primevue/button'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { money } from '@/api/licenses'
import {
  columnLayout,
  columnPath,
  columnTable,
  labelEvery,
  tickLabels,
  type ColumnMark,
  type CostColumn,
} from './chartScale'

/**
 * Cost per month, or per year (docs/licences-plan.md §8.2, §8.12): hand-made SVG columns, as MonthColumns.vue. One hue for the
 * money: what was spent in its full step, the forecast stacked on it in a lighter step of the same hue, a 2 px surface gap
 * between them; the legend names both. A rule marks the column today is in. Every column is a stop for Tab and shows its tooltip
 * on hover and on focus, its hit area the whole slot; with `selectable`, a click or Enter selects it. *Show as table* gives the
 * same figures as a table. One currency (§6.6); light and dark through the chart tokens, which have their own dark steps.
 */
const props = withDefaults(
  defineProps<{
    columns: readonly CostColumn[]
    currency: string
    /** Says what is plotted, with the currency: "Cost per month · EUR". */
    title: string
    /** What the first column of the table is: Month, Year. */
    unit?: string
    selectable?: boolean
    /** What a click does, for the accessible name: "Open its costs". */
    selectHint?: string
    height?: number
  }>(),
  { unit: 'Month', selectable: false, selectHint: '', height: 220 },
)
const emit = defineEmits<{ select: [index: number] }>()

const root = ref<HTMLElement | null>(null)
const width = ref(720)
let observer: ResizeObserver | undefined
function observe() {
  observer?.disconnect()
  if (!root.value) return
  width.value = root.value.clientWidth || width.value
  if (typeof ResizeObserver !== 'undefined') {
    observer = new ResizeObserver((entries) => (width.value = entries[0]?.contentRect.width || width.value))
    observer.observe(root.value)
  }
}
onMounted(observe)
onBeforeUnmount(() => observer?.disconnect())

const table = ref(false)
watch(table, (asTable) => {
  if (!asTable) void nextTick(observe)
})

const W = computed(() => Math.max(320, Math.round(width.value)))
const box = computed(() => ({ width: W.value, height: props.height, left: 44, right: 8, top: 18, bottom: 24 }))
const layout = computed(() => columnLayout(props.columns, box.value))
const ticks = computed(() => {
  const labels = tickLabels(layout.value.axis.ticks)
  return layout.value.axis.ticks.map((t, i) => ({ value: t, y: layout.value.y(t), label: labels[i] }))
})
const longest = computed(() => Math.max(0, ...props.columns.map((c) => c.label.length)))
const every = computed(() => labelEvery(layout.value.slot, longest.value * 6.5 + 8))
const current = computed(() => props.columns.findIndex((c) => c.current))

const hasSpent = computed(() => props.columns.some((c) => c.spent > 0))
const hasForecast = computed(() => props.columns.some((c) => c.forecast > 0))

function paths(mark: ColumnMark) {
  return {
    spent: mark.spent ? columnPath(mark.x, mark.spent.y, mark.width, mark.spent.height, mark.forecast ? 0 : 4) : '',
    forecast: mark.forecast ? columnPath(mark.x, mark.forecast.y, mark.width, mark.forecast.height) : '',
  }
}

const m = (amount: number) => money(amount, props.currency)

function describe(i: number): string {
  const c = props.columns[i]
  if (!c) return ''
  const total = c.spent + c.forecast
  const parts = [`${c.name}: ${m(total)}`]
  if (c.mixed) parts.push('spent so far and forecast')
  else {
    if (c.spent) parts.push(`spent ${m(c.spent)}`)
    if (c.forecast) parts.push(`forecast ${m(c.forecast)}`)
  }
  if (c.ifRenewed) parts.push(`of it if renewed ${m(c.ifRenewed)}`)
  let text = parts.join(', ')
  if (c.top?.length) text += `. Largest: ${c.top.map((t) => `${t.name} ${m(t.amount)}`).join(', ')}`
  return props.selectable && props.selectHint ? `${text}. ${props.selectHint}` : text
}

const summary = computed(() => {
  const list = props.columns
  if (!list.length) return `${props.title}: nothing to show`
  const spent = list.reduce((n, c) => n + c.spent, 0)
  const forecast = list.reduce((n, c) => n + c.forecast, 0)
  const highest = list.reduce((best, c) => (c.spent + c.forecast > best.spent + best.forecast ? c : best), list[0]!)
  const sums = [spent ? `spent ${m(spent)}` : '', forecast ? `forecast ${m(forecast)}` : ''].filter(Boolean).join(', ')
  return `${props.title}, ${list[0]!.name} to ${list[list.length - 1]!.name}: ${sums || 'nothing'}; the highest ${m(
    highest.spent + highest.forecast,
  )} in ${highest.name}. Tab through the columns for each one, or show them as a table.`
})

// --- the tooltip -----------------------------------------------------------------------------------------------------------
const active = ref<number | null>(null)
const tip = computed(() => {
  const i = active.value
  const mark = i === null ? undefined : layout.value.marks[i]
  const c = i === null ? undefined : props.columns[i]
  if (!mark || !c) return null
  // Beside the column, never over it: to its right, or to its left near the right edge.
  const right = mark.slotX + mark.slotWidth + 4
  const flip = right + 230 > W.value
  return {
    c,
    total: c.spent + c.forecast,
    style: {
      left: `${flip ? mark.slotX - 4 : right}px`,
      top: `${layout.value.plotTop}px`,
      transform: flip ? 'translateX(-100%)' : undefined,
    },
  }
})
function select(i: number) {
  if (props.selectable) emit('select', i)
}

// --- the table -------------------------------------------------------------------------------------------------------------
// The same rows as the marks; a month told only as a total (spent so far and forecast) has no split.
const rows = computed(() =>
  columnTable(props.columns).map((r, i) => {
    const c = props.columns[i]
    return {
      ...r,
      name: c?.name ?? r.label,
      spent: c?.mixed ? null : r.spent,
      forecast: c?.mixed ? null : r.forecast,
      ifRenewed: c?.ifRenewed ?? 0,
      mixed: !!c?.mixed,
    }
  }),
)
const withRenewed = computed(() => rows.value.some((r) => r.ifRenewed))
const totals = computed(() => {
  const list = rows.value
  const split = !list.some((r) => r.mixed)
  return {
    spent: split ? list.reduce((n, r) => n + (r.spent ?? 0), 0) : null,
    forecast: split ? list.reduce((n, r) => n + (r.forecast ?? 0), 0) : null,
    total: list.reduce((n, r) => n + r.total, 0),
    ifRenewed: list.reduce((n, r) => n + r.ifRenewed, 0),
  }
})
const cell = (amount: number | null) => (amount === null ? '' : m(amount))
</script>

<template>
  <figure class="cost-columns" data-test="cost-columns">
    <figcaption class="head">
      <span class="title">{{ title }}</span>
      <span class="legend">
        <span v-if="hasSpent" class="key"><span class="swatch spent" aria-hidden="true" />Spent</span>
        <span v-if="hasForecast" class="key"><span class="swatch forecast" aria-hidden="true" />Forecast</span>
        <span v-if="current >= 0 && !table" class="key"><span class="rule-key" aria-hidden="true" />today</span>
      </span>
      <Button
        :label="table ? 'Show as chart' : 'Show as table'"
        :icon="table ? 'pi pi-chart-bar' : 'pi pi-table'"
        size="small"
        text
        :aria-pressed="table"
        data-test="columns-table-toggle"
        @click="table = !table"
      />
    </figcaption>

    <div v-if="!table" ref="root" class="plot">
      <svg :width="W" :height="height" :viewBox="`0 0 ${W} ${height}`" role="group" :aria-label="summary" class="svg">
        <g class="grid" aria-hidden="true">
          <g v-for="t in ticks" :key="t.value">
            <line :x1="box.left" :x2="W - box.right" :y1="t.y" :y2="t.y" />
            <text :x="box.left - 6" :y="t.y + 4" text-anchor="end">{{ t.label }}</text>
          </g>
        </g>
        <g v-if="current >= 0 && layout.marks[current]" class="today" aria-hidden="true">
          <line
            :x1="layout.marks[current]!.slotX + layout.marks[current]!.slotWidth / 2"
            :x2="layout.marks[current]!.slotX + layout.marks[current]!.slotWidth / 2"
            :y1="layout.plotTop - 6"
            :y2="layout.plotBottom"
          />
          <text
            :x="layout.marks[current]!.slotX + layout.marks[current]!.slotWidth / 2"
            :y="layout.plotTop - 9"
            text-anchor="middle"
          >
            today
          </text>
        </g>
        <g
          v-for="mark in layout.marks"
          :key="mark.key"
          class="column"
          :class="{ active: active === mark.index, selectable }"
          tabindex="0"
          :role="selectable ? 'link' : 'img'"
          :aria-label="describe(mark.index)"
          data-test="cost-column"
          @pointerenter="active = mark.index"
          @pointerleave="active = null"
          @focus="active = mark.index"
          @blur="active = null"
          @click="select(mark.index)"
          @keydown.enter.prevent="select(mark.index)"
          @keydown.space.prevent="select(mark.index)"
        >
          <rect
            class="hit"
            :x="mark.slotX"
            :y="layout.plotTop - 2"
            :width="mark.slotWidth"
            :height="layout.plotBottom - layout.plotTop + 2"
            rx="3"
          />
          <path v-if="mark.spent" class="spent" :d="paths(mark).spent" />
          <path v-if="mark.forecast" class="forecast" :d="paths(mark).forecast" />
        </g>
        <line
          class="baseline"
          :x1="box.left"
          :x2="W - box.right"
          :y1="layout.plotBottom"
          :y2="layout.plotBottom"
          aria-hidden="true"
        />
        <g class="x-axis" aria-hidden="true">
          <template v-for="mark in layout.marks" :key="mark.key">
            <text
              v-if="mark.index % every === 0 || mark.index === current"
              :x="mark.slotX + mark.slotWidth / 2"
              :y="height - 7"
              text-anchor="middle"
              :class="{ now: mark.index === current }"
            >
              {{ columns[mark.index]?.label }}
            </text>
          </template>
        </g>
      </svg>
      <div v-if="tip" class="tooltip" :style="tip.style" aria-hidden="true">
        <div class="tip-name">{{ tip.c.name }}</div>
        <div class="tip-total">{{ m(tip.total) }}</div>
        <template v-if="tip.c.mixed">
          <div class="tip-row"><span class="line-key forecast" />spent so far and forecast</div>
        </template>
        <template v-else>
          <div v-if="tip.c.spent" class="tip-row">
            <span class="line-key spent" /><strong>{{ m(tip.c.spent) }}</strong
            ><span class="muted">spent</span>
          </div>
          <div v-if="tip.c.forecast" class="tip-row">
            <span class="line-key forecast" /><strong>{{ m(tip.c.forecast) }}</strong
            ><span class="muted">forecast</span>
          </div>
        </template>
        <div v-if="tip.c.ifRenewed" class="tip-row muted">of it if renewed: {{ m(tip.c.ifRenewed) }}</div>
        <ul v-if="tip.c.top?.length" class="tip-top">
          <li v-for="t in tip.c.top" :key="t.name">
            <strong>{{ m(t.amount) }}</strong> <span class="muted">{{ t.name }}</span>
          </li>
        </ul>
        <div v-if="selectable && selectHint" class="tip-hint muted">{{ selectHint }}</div>
      </div>
    </div>

    <div v-else class="table-wrap">
      <table class="chart-table" data-test="columns-table">
        <caption class="sr-only">
          {{
            title
          }}
        </caption>
        <thead>
          <tr>
            <th scope="col">{{ unit }}</th>
            <th scope="col" class="num">Spent</th>
            <th scope="col" class="num">Forecast</th>
            <th scope="col" class="num">Total</th>
            <th v-if="withRenewed" scope="col" class="num">Of it if renewed</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(r, i) in rows" :key="r.key" :class="{ now: i === current }">
            <th scope="row">{{ r.name }}<span v-if="r.mixed" class="muted"> · spent so far and forecast</span></th>
            <td class="num">{{ cell(r.spent) }}</td>
            <td class="num">{{ cell(r.forecast) }}</td>
            <td class="num">{{ m(r.total) }}</td>
            <td v-if="withRenewed" class="num">{{ r.ifRenewed ? m(r.ifRenewed) : '' }}</td>
          </tr>
        </tbody>
        <tfoot>
          <tr>
            <th scope="row">Total</th>
            <td class="num">{{ cell(totals.spent) }}</td>
            <td class="num">{{ cell(totals.forecast) }}</td>
            <td class="num">{{ m(totals.total) }}</td>
            <td v-if="withRenewed" class="num">{{ m(totals.ifRenewed) }}</td>
          </tr>
        </tfoot>
      </table>
    </div>
  </figure>
</template>

<style scoped>
.cost-columns {
  /* One hue: spent in its full step, the forecast lighter (on dark, the receding step). Validated on both surfaces. */
  --spent: var(--pnmt-chart-seq-5);
  --forecast: var(--pnmt-chart-seq-3);
  margin: 0;
  min-width: 0;
}

.head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
  margin-bottom: var(--pnmt-space-1);
}

.title {
  font-weight: 600;
}

.legend {
  display: inline-flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-3);
  margin-left: auto;
  color: var(--pnmt-muted);
  font-size: 0.857rem;
}

.key {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.swatch {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 2px;
}

.swatch.spent,
.line-key.spent {
  background: var(--spent);
}

.swatch.forecast,
.line-key.forecast {
  background: var(--forecast);
}

.rule-key {
  display: inline-block;
  width: 14px;
  border-top: 2px dashed var(--pnmt-text);
}

.plot {
  position: relative;
  min-width: 0;
}

.svg {
  display: block;
  overflow: visible;
  /* Until the observer has the new width, it scales rather than overflows. */
  max-width: 100%;
  height: auto;
}

.grid line,
.baseline {
  stroke: var(--pnmt-border);
  stroke-width: 1;
  shape-rendering: crispEdges;
}

.grid text,
.x-axis text,
.today text {
  fill: var(--pnmt-muted);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.x-axis text.now {
  fill: var(--pnmt-text);
  font-weight: 600;
}

.today line {
  stroke: var(--pnmt-text);
  stroke-width: 1.5;
  stroke-dasharray: 4 3;
}

.spent {
  fill: var(--spent);
}

.forecast {
  fill: var(--forecast);
}

.hit {
  fill: transparent;
}

.column {
  outline: none;
}

.column.selectable {
  cursor: pointer;
}

.column.active .hit {
  fill: color-mix(in srgb, var(--pnmt-text) 7%, transparent);
}

.column:focus-visible .hit {
  stroke: var(--p-primary-color);
  stroke-width: 2;
}

.tooltip {
  position: absolute;
  z-index: 2;
  min-width: 11rem;
  max-width: 15rem;
  padding: var(--pnmt-space-2) var(--pnmt-space-3);
  background: var(--pnmt-panel);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
  box-shadow: 0 4px 12px rgb(0 0 0 / 0.15);
  font-size: 0.857rem;
  pointer-events: none;
}

.tip-name {
  color: var(--pnmt-muted);
}

.tip-total {
  font-size: 1rem;
  font-weight: 600;
}

.tip-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.line-key {
  display: inline-block;
  width: 10px;
  height: 3px;
  border-radius: 2px;
  flex: none;
}

.tip-top {
  list-style: none;
  margin: var(--pnmt-space-1) 0 0;
  padding: var(--pnmt-space-1) 0 0;
  border-top: 1px solid var(--pnmt-border);
}

.tip-hint {
  margin-top: var(--pnmt-space-1);
}

.table-wrap {
  overflow-x: auto;
}

.chart-table {
  border-collapse: collapse;
  width: 100%;
  font-size: 0.929rem;
}

.chart-table th,
.chart-table td {
  padding: 3px var(--pnmt-space-2);
  border-bottom: 1px solid var(--pnmt-border);
  text-align: left;
  white-space: nowrap;
}

.chart-table thead th {
  color: var(--pnmt-muted);
  font-weight: 500;
}

.chart-table tbody th {
  font-weight: 400;
}

.chart-table .num {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.chart-table tr.now th {
  font-weight: 600;
}

.chart-table tfoot th,
.chart-table tfoot td {
  font-weight: 600;
  border-bottom: none;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}
</style>
