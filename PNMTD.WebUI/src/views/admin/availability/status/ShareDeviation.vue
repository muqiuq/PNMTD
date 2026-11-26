<script setup lang="ts">
import { computed } from 'vue'
import CodeChip from '../CodeChip.vue'
import { deviationScale, percent, signedDays } from './stats'

/**
 * Days as the responsible against each person's share (docs/availability-extensions-plan.md §10, part 2): a diverging bar per
 * person, centred on their target, right for more days than their share and left for fewer, the figures beside it. Two
 * hues and a grey midpoint (--pnmt-chart-above, -below, -mid), not the status colours: being behind isn't an error.
 */
export interface ShareRow {
  userId: string
  name: string
  code: string
  color: string | null
  days: number
  actual: number
  target: number
  expected: number
  deviation: number
}
const props = defineProps<{ rows: readonly ShareRow[]; label: string }>()
const scale = computed(() => deviationScale(props.rows))
const width = (r: ShareRow) => `${Math.min(50, (Math.abs(r.deviation) / scale.value) * 50)}%`
const text = (r: ShareRow) =>
  `${r.name}: ${props.label} for ${r.days} ${r.days === 1 ? 'day' : 'days'}, ${percent(r.actual)} of all, target ${percent(
    r.target,
  )} (${r.expected.toLocaleString('en-GB', { maximumFractionDigits: 1 })} days): ${signedDays(r.deviation)}`
</script>

<template>
  <ul class="share-rows">
    <li v-for="r in rows" :key="r.userId" class="share-row" :title="text(r)">
      <span class="who">
        <CodeChip :code="r.code" :colour="r.color" />
        <span class="ellipsis">{{ r.name }}</span>
      </span>
      <span class="days">{{ label }} for {{ r.days }} {{ r.days === 1 ? 'day' : 'days' }}</span>
      <span class="track" role="img" :aria-label="text(r)">
        <span class="mid" aria-hidden="true" />
        <span
          v-if="Math.abs(r.deviation) >= 0.05"
          class="bar"
          :class="r.deviation > 0 ? 'above' : 'below'"
          :style="{ width: width(r) }"
          aria-hidden="true"
        />
      </span>
      <span class="figures mono small">
        {{ percent(r.actual) }} · target {{ percent(r.target) }} ·
        <strong>{{ signedDays(r.deviation) }}</strong>
      </span>
    </li>
  </ul>
  <div class="legend muted small" aria-hidden="true">
    <span><span class="swatch below" /> fewer days than the share</span>
    <span><span class="swatch above" /> more days than the share</span>
  </div>
</template>

<style scoped>
.share-rows {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.share-row {
  display: grid;
  grid-template-columns: minmax(8rem, 14rem) 11rem minmax(8rem, 1fr) 15rem;
  align-items: center;
  gap: var(--pnmt-space-3);
  min-height: 28px;
}

.who {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.track {
  position: relative;
  height: 14px;
  background: color-mix(in srgb, var(--pnmt-chart-mid) 70%, transparent);
  border-radius: 4px;
}

.mid {
  position: absolute;
  left: 50%;
  top: -3px;
  bottom: -3px;
  width: 2px;
  margin-left: -1px;
  background: var(--pnmt-muted);
}

.bar {
  position: absolute;
  top: 2px;
  bottom: 2px;
}

.bar.above {
  left: calc(50% + 2px);
  background: var(--pnmt-chart-above);
  border-radius: 0 4px 4px 0;
}

.bar.below {
  right: calc(50% + 2px);
  background: var(--pnmt-chart-below);
  border-radius: 4px 0 0 4px;
}

.figures {
  white-space: nowrap;
}

.legend {
  display: flex;
  gap: var(--pnmt-space-4);
  margin-top: var(--pnmt-space-2);
}

.swatch {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 2px;
  vertical-align: middle;
}

.swatch.above {
  background: var(--pnmt-chart-above);
}

.swatch.below {
  background: var(--pnmt-chart-below);
}

.small {
  font-size: 0.857rem;
}

@media (max-width: 900px) {
  .share-row {
    grid-template-columns: 1fr 1fr;
  }
}
</style>
