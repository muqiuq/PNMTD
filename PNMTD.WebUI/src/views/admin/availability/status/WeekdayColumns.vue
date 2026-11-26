<script setup lang="ts">
import { computed } from 'vue'

/**
 * The office by weekday (docs/availability-extensions-plan.md §10, part 8): a count column's average per weekday, one hue,
 * each column labelled with its value; the column's minimum as a line. It answers "which day is the office day".
 */
const props = defineProps<{
  values: readonly { day: number; label: string; average: number; days: number }[]
  column: string
  minimum: number | null | undefined
}>()

const W = 360
const H = 170
const pad = { left: 8, right: 8, top: 18, bottom: 22 }
const plotH = H - pad.top - pad.bottom
const slot = computed(() => (W - pad.left - pad.right) / Math.max(1, props.values.length))
const barW = computed(() => Math.min(44, slot.value - 14))
const max = computed(() => Math.max(1, props.minimum ?? 0, ...props.values.map((v) => v.average)))
const y = (v: number) => pad.top + plotH - (v / max.value) * plotH
const one = (v: number) => v.toLocaleString('en-GB', { maximumFractionDigits: 1 })
</script>

<template>
  <figure class="chart">
    <svg :viewBox="`0 0 ${W} ${H}`" role="img" :aria-label="`${column}: the average per weekday`" class="svg">
      <line :x1="pad.left" :x2="W - pad.right" :y1="y(0)" :y2="y(0)" class="base" />
      <g v-for="(v, i) in values" :key="v.day">
        <title>{{ v.label }}: {{ one(v.average) }} on average, over {{ v.days }} days</title>
        <rect
          :x="pad.left + i * slot + (slot - barW) / 2"
          :y="y(v.average)"
          :width="barW"
          :height="Math.max(0, y(0) - y(v.average))"
          rx="4"
          class="bar"
        />
        <text :x="pad.left + i * slot + slot / 2" :y="y(v.average) - 5" text-anchor="middle" class="value">
          {{ one(v.average) }}
        </text>
        <text :x="pad.left + i * slot + slot / 2" :y="H - 6" text-anchor="middle" class="axis">{{ v.label }}</text>
      </g>
      <g v-if="minimum">
        <line :x1="pad.left" :x2="W - pad.right" :y1="y(minimum)" :y2="y(minimum)" class="minimum" />
        <text :x="W - pad.right" :y="y(minimum) - 4" text-anchor="end" class="axis">minimum {{ minimum }}</text>
      </g>
    </svg>
  </figure>
</template>

<style scoped>
.chart {
  margin: 0;
}

.svg {
  width: 100%;
  max-width: 420px;
  height: auto;
  display: block;
}

.bar {
  fill: var(--pnmt-chart-seq-5);
}

.base {
  stroke: var(--pnmt-border);
}

.minimum {
  stroke: var(--pnmt-muted);
  stroke-width: 1.5;
  stroke-dasharray: 4 3;
}

.value {
  fill: var(--pnmt-text);
  font-size: 12px;
  font-weight: 600;
}

.axis {
  fill: var(--pnmt-muted);
  font-size: 11px;
}
</style>
