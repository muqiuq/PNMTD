<script setup lang="ts">
import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import SelectButton from 'primevue/selectbutton'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import { formatAbsolute, formatTime } from '@/format'
import { formatFieldValue, operatorInfo } from './advanced'

/**
 * A charted field (docs/advanced-sensor-plan.md §5.5, §10.3): an SVG polyline of each step's last value with its min–max
 * band, over 24 h (per minute), 7 d or 30 d (per hour); the thresholds of rules on the same path as lines; the value under
 * the pointer (or the arrow keys). No chart library, as UplinkView's latency. `compact`: the overview's 24 h sparkline.
 */
type Range = '24h' | '7d' | '30d'
const props = withDefaults(defineProps<{ sensorId: string; field: S['AdvancedFieldItem']; compact?: boolean }>(), {
  compact: false,
})

const ranges: { label: string; value: Range }[] = [
  { label: '24 h', value: '24h' },
  { label: '7 d', value: '7d' },
  { label: '30 d', value: '30d' },
]
const range = ref<Range>('24h')
const samples = useQuery({
  queryKey: computed(() => [...keys.advanced(props.sensorId), 'samples', props.field.id, range.value]),
  queryFn: () => api.advanced.samples(props.sensorId, props.field.id, range.value),
  refetchInterval: 60_000,
  placeholderData: keepPreviousData,
})

const W = computed(() => (props.compact ? 120 : 600))
const H = computed(() => (props.compact ? 28 : 120))

interface Point {
  t: number
  min: number
  max: number
  last: number
}

const chart = computed(() => {
  const s = samples.data.value
  if (!s || !s.points.length) return null
  const from = Date.parse(s.from)
  const to = Math.max(Date.parse(s.to), from + 1)
  const points: Point[] = s.points
    .map((p) => ({ t: Date.parse(p.at), min: p.min, max: p.max, last: p.last }))
    .sort((a, b) => a.t - b.t)
  const thresholds = props.compact ? [] : s.thresholds
  const values = [...points.flatMap((p) => [p.min, p.max]), ...thresholds.map((t) => t.value)]
  let lo = Math.min(...values)
  let hi = Math.max(...values)
  const pad = hi > lo ? (hi - lo) * 0.08 : Math.abs(hi) * 0.1 || 1
  lo -= pad
  hi += pad
  const w = W.value
  const h = H.value
  const x = (t: number) => ((t - from) / (to - from)) * w
  const y = (v: number) => h - ((v - lo) / (hi - lo)) * h
  const xy = (t: number, v: number) => `${x(t).toFixed(1)},${y(v).toFixed(1)}`

  // A gap of more than a few steps breaks the line: no report then.
  const gap = Math.max(1, s.stepSeconds) * 1000 * 3
  const runs: Point[][] = []
  for (const p of points) {
    const run = runs[runs.length - 1]
    if (run && p.t - run[run.length - 1]!.t <= gap) run.push(p)
    else runs.push([p])
  }
  return {
    points,
    x,
    y,
    lines: runs.filter((r) => r.length > 1).map((r) => r.map((p) => xy(p.t, p.last)).join(' ')),
    dots: runs.filter((r) => r.length === 1).map((r) => ({ cx: x(r[0]!.t), cy: y(r[0]!.last) })),
    bands: runs
      .filter((r) => r.length > 1 && r.some((p) => p.max > p.min))
      .map((r) => [...r.map((p) => xy(p.t, p.max)), ...[...r].reverse().map((p) => xy(p.t, p.min))].join(' ')),
    thresholds: thresholds.map((t) => ({ ...t, y: y(t.value) })),
    min: Math.min(...points.map((p) => p.min)),
    max: Math.max(...points.map((p) => p.max)),
    last: points[points.length - 1]!,
    from,
    to,
  }
})

const fmt = (v: number) => formatFieldValue(v, props.field)
const when = (t: number) => (range.value === '24h' ? formatTime(t) : formatAbsolute(t))

// --- the value under the pointer -------------------------------------------------------------------------------------------
const hover = ref<number | null>(null)
const hovered = computed(() => (hover.value === null ? null : (chart.value?.points[hover.value] ?? null)))
function nearest(t: number): number {
  const points = chart.value?.points ?? []
  let best = 0
  for (let i = 1; i < points.length; i++) {
    if (Math.abs(points[i]!.t - t) < Math.abs(points[best]!.t - t)) best = i
  }
  return best
}
function onMove(event: PointerEvent) {
  const c = chart.value
  if (!c) return
  const rect = (event.currentTarget as Element).getBoundingClientRect()
  if (!rect.width) return
  hover.value = nearest(c.from + ((event.clientX - rect.left) / rect.width) * (c.to - c.from))
}
function onKey(event: KeyboardEvent) {
  const n = chart.value?.points.length ?? 0
  if (!n) return
  if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
    event.preventDefault()
    const step = event.key === 'ArrowLeft' ? -1 : 1
    hover.value = Math.min(n - 1, Math.max(0, (hover.value ?? n - 1) + (hover.value === null ? 0 : step)))
  } else if (event.key === 'Escape') {
    hover.value = null
  }
}
const readout = computed(() => {
  const p = hovered.value
  if (!p) return null
  const spread = p.max > p.min ? ` (min ${fmt(p.min)}, max ${fmt(p.max)})` : ''
  return { at: when(p.t), value: fmt(p.last), spread }
})
const marker = computed(() => {
  const c = chart.value
  const p = hovered.value
  if (!c || !p) return null
  return { left: `${(c.x(p.t) / W.value) * 100}%`, top: `${(c.y(p.last) / H.value) * 100}%`, x: c.x(p.t) }
})
const summary = computed(() => {
  const c = chart.value
  const span = ranges.find((r) => r.value === range.value)!.label
  if (!c) return `${props.field.name}: no values in the last ${span}`
  return `${props.field.name}, the last ${span}: from ${fmt(c.min)} to ${fmt(c.max)}, last ${fmt(c.last.last)}`
})
const thresholdLabel = (t: S['AdvancedThreshold']) => `${t.rule}: ${operatorInfo(t.operator).symbol} ${fmt(t.value)}`
</script>

<template>
  <div class="sample-chart" :class="{ compact }">
    <div v-if="!compact" class="top">
      <SelectButton
        v-model="range"
        :options="ranges"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        :aria-label="`Range of the chart of ${field.name}`"
      />
      <span v-if="readout" class="readout" aria-live="polite">
        <strong>{{ readout.value }}</strong>
        <span class="muted">{{ readout.spread }} · {{ readout.at }}</span>
      </span>
      <span v-else-if="chart" class="readout muted"> min {{ fmt(chart.min) }} · max {{ fmt(chart.max) }} </span>
    </div>
    <div
      v-if="chart"
      class="plot"
      :tabindex="compact ? undefined : 0"
      role="img"
      :aria-label="summary"
      @pointermove="onMove"
      @pointerleave="hover = null"
      @keydown="onKey"
      @blur="hover = null"
    >
      <svg :viewBox="`0 0 ${W} ${H}`" preserveAspectRatio="none" aria-hidden="true">
        <title v-if="compact">{{ summary }}</title>
        <polygon v-for="(b, i) in chart.bands" :key="`b${i}`" :points="b" class="band" />
        <line
          v-for="(t, i) in chart.thresholds"
          :key="`t${i}`"
          x1="0"
          :x2="W"
          :y1="t.y"
          :y2="t.y"
          class="threshold"
          :class="t.severity === 'Warn' ? 'warn' : 'fail'"
          vector-effect="non-scaling-stroke"
        />
        <polyline
          v-for="(l, i) in chart.lines"
          :key="`l${i}`"
          :points="l"
          class="line"
          vector-effect="non-scaling-stroke"
        />
        <line
          v-if="marker"
          :x1="marker.x"
          :x2="marker.x"
          y1="0"
          :y2="H"
          class="crosshair"
          vector-effect="non-scaling-stroke"
        />
      </svg>
      <span
        v-for="(d, i) in chart.dots"
        :key="`d${i}`"
        class="dot"
        :style="{ left: `${(d.cx / W) * 100}%`, top: `${(d.cy / H) * 100}%` }"
      />
      <span v-if="marker" class="dot hover" :style="{ left: marker.left, top: marker.top }" />
    </div>
    <p v-else-if="!compact" class="empty muted">
      {{ samples.isPending.value ? 'Loading…' : 'No values in this range yet.' }}
    </p>
    <div v-if="chart && !compact" class="axis muted">
      <span>{{ when(chart.from) }}</span>
      <span>{{ when(chart.to) }}</span>
    </div>
    <ul v-if="chart && chart.thresholds.length" class="legend">
      <li v-for="(t, i) in chart.thresholds" :key="i">
        <span class="key" :class="t.severity === 'Warn' ? 'warn' : 'fail'" aria-hidden="true" />
        {{ thresholdLabel(t) }}
      </li>
    </ul>
  </div>
</template>

<style scoped>
.sample-chart {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  min-width: 0;
}

.top {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.readout {
  font-size: 0.857rem;
  min-width: 0;
}

.plot {
  position: relative;
  height: 120px;
  border-bottom: 1px solid var(--pnmt-border);
  cursor: crosshair;
  touch-action: pan-y;
}

.plot:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 2px;
}

.compact .plot {
  height: 28px;
  border-bottom: 0;
  cursor: default;
}

svg {
  display: block;
  width: 100%;
  height: 100%;
  overflow: visible;
}

.line {
  fill: none;
  stroke: var(--p-primary-color);
  stroke-width: 2;
  stroke-linejoin: round;
}

.compact .line {
  stroke-width: 1.5;
}

.band {
  fill: var(--p-primary-color);
  opacity: 0.15;
}

.threshold {
  stroke-width: 1;
  stroke-dasharray: 5 4;
}

.threshold.fail {
  stroke: var(--pnmt-fail);
}

.threshold.warn {
  stroke: var(--pnmt-warn);
}

.crosshair {
  stroke: var(--pnmt-muted);
  stroke-width: 1;
}

.dot {
  position: absolute;
  width: 6px;
  height: 6px;
  margin: -3px 0 0 -3px;
  border-radius: 50%;
  background: var(--p-primary-color);
  pointer-events: none;
}

.dot.hover {
  width: 8px;
  height: 8px;
  margin: -4px 0 0 -4px;
  box-shadow: 0 0 0 2px var(--pnmt-panel);
}

.axis {
  display: flex;
  justify-content: space-between;
  font-size: 0.786rem;
}

.empty {
  margin: 0;
  font-size: 0.857rem;
}

.legend {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-wrap: wrap;
  gap: 0 var(--pnmt-space-3);
  font-size: 0.786rem;
  color: var(--pnmt-muted);
}

.legend li {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.key {
  display: inline-block;
  width: 1rem;
  border-top: 1px dashed;
}

.key.fail {
  border-color: var(--pnmt-fail);
}

.key.warn {
  border-color: var(--pnmt-warn);
}
</style>
