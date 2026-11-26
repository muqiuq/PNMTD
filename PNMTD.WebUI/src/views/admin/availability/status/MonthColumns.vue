<script setup lang="ts">
import { computed, ref } from 'vue'
import CodeChip from '../CodeChip.vue'
import { monthShort, type StatsType } from './stats'

/**
 * The team by month (docs/availability-extensions-plan.md §10, part 5): a column per month, stacked by type in person-days,
 * each type in its own colour as in the plan (the colour follows the type), with a thin outline so pale cell colours stay
 * visible. The admin picks the colours, so they can't be checked for colour blindness: the legend names the codes, each
 * month says what it holds on hover, and the tables beside it have the figures. A line marks today's month.
 */
const props = defineProps<{
  months: readonly { month: number; parts: readonly { typeId: string; days: number }[] }[]
  types: readonly StatsType[]
  /** 1 to 12: today's month in the year shown, null when the year is past or to come. */
  current: number | null
}>()

const W = 720
const H = 220
const pad = { left: 36, right: 8, top: 10, bottom: 24 }
const plotW = W - pad.left - pad.right
const plotH = H - pad.top - pad.bottom
const slot = plotW / 12
const barW = Math.min(36, slot - 10)

const order = computed(() => new Map(props.types.map((t, i) => [t.id, i])))
const typeById = computed(() => new Map(props.types.map((t) => [t.id, t])))
const totals = computed(() => props.months.map((m) => m.parts.reduce((n, p) => n + p.days, 0)))
const max = computed(() => Math.max(1, ...totals.value))
/** A round top for the axis: 10, 20, 50, 100, 200 … */
const top = computed(() => {
  const steps = [5, 10, 20, 25, 50, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 3000, 5000]
  return steps.find((s) => s >= max.value) ?? Math.ceil(max.value / 1000) * 1000
})
const y = (v: number) => pad.top + plotH - (v / top.value) * plotH
const ticks = computed(() => [0, top.value / 2, top.value])

/** Each month's segments, bottom up in the types' order, with a 2 px gap between them. */
const columns = computed(() =>
  props.months.map((m, i) => {
    const x = pad.left + i * slot + (slot - barW) / 2
    let base = 0
    const parts = [...m.parts]
      .sort((a, b) => (order.value.get(a.typeId) ?? 99) - (order.value.get(b.typeId) ?? 99))
      .map((p) => {
        const y0 = y(base)
        base += p.days
        const y1 = y(base)
        return { ...p, x, y: y1, h: Math.max(0, y0 - y1 - 2), colour: typeById.value.get(p.typeId)?.color ?? '#cccccc' }
      })
    return { month: m.month, x, parts, total: totals.value[i] }
  }),
)

const hovered = ref<number | null>(null)
const describe = (i: number) => {
  const c = columns.value[i]
  if (!c || !c.total) return `${monthShort[i]}: no days`
  const parts = c.parts.map((p) => `${typeById.value.get(p.typeId)?.code ?? '?'} ${p.days}`).join(' · ')
  return `${monthShort[i]}: ${c.total} person-days. ${parts}`
}
const readout = computed(() => (hovered.value === null ? 'Point at a month for its days.' : describe(hovered.value)))
</script>

<template>
  <figure class="chart">
    <svg :viewBox="`0 0 ${W} ${H}`" role="img" aria-label="Person-days per month, by type" class="svg">
      <g class="grid">
        <g v-for="t in ticks" :key="t">
          <line :x1="pad.left" :x2="W - pad.right" :y1="y(t)" :y2="y(t)" />
          <text :x="pad.left - 6" :y="y(t) + 4" text-anchor="end">{{ t }}</text>
        </g>
      </g>
      <line
        v-if="current"
        class="today"
        :x1="pad.left + (current - 1) * slot + slot / 2"
        :x2="pad.left + (current - 1) * slot + slot / 2"
        :y1="pad.top - 4"
        :y2="pad.top + plotH"
      />
      <g
        v-for="(c, i) in columns"
        :key="c.month"
        class="month"
        :class="{ dim: hovered !== null && hovered !== i }"
        @mouseenter="hovered = i"
        @mouseleave="hovered = null"
      >
        <title>{{ describe(i) }}</title>
        <rect class="hit" :x="pad.left + i * slot" :y="pad.top" :width="slot" :height="plotH" />
        <rect
          v-for="p in c.parts"
          :key="p.typeId"
          :x="p.x"
          :y="p.y"
          :width="barW"
          :height="p.h"
          :fill="p.colour"
          rx="2"
          class="segment"
        />
        <text :x="pad.left + i * slot + slot / 2" :y="H - 6" text-anchor="middle" class="axis">
          {{ monthShort[i] }}
        </text>
      </g>
    </svg>
    <figcaption class="readout muted small" aria-live="polite">{{ readout }}</figcaption>
    <div class="legend">
      <span v-for="t in types" :key="t.id" class="legend-item">
        <CodeChip :code="t.code" :colour="t.color" />
        <span class="muted small">{{ t.name }}</span>
      </span>
      <span v-if="current" class="legend-item muted small"><span class="today-key" /> today</span>
    </div>
  </figure>
</template>

<style scoped>
.chart {
  margin: 0;
}

.svg {
  width: 100%;
  max-width: 900px;
  height: auto;
  display: block;
}

.grid line {
  stroke: var(--pnmt-border);
  stroke-width: 1;
}

.grid text,
.axis {
  fill: var(--pnmt-muted);
  font-size: 11px;
}

.segment {
  stroke: color-mix(in srgb, var(--pnmt-text) 35%, transparent);
  stroke-width: 1;
}

.hit {
  fill: transparent;
}

.month.dim .segment {
  opacity: 0.45;
}

.today {
  stroke: var(--pnmt-text);
  stroke-width: 2;
  stroke-dasharray: 4 3;
}

.readout {
  margin-top: var(--pnmt-space-1);
  min-height: 1.3em;
}

.legend {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pnmt-space-1) var(--pnmt-space-3);
  margin-top: var(--pnmt-space-1);
}

.legend-item {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.today-key {
  display: inline-block;
  width: 14px;
  border-top: 2px dashed var(--pnmt-text);
}

.small {
  font-size: 0.857rem;
}
</style>
