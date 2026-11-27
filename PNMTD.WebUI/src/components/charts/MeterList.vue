<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { meter, type MeterRow } from './chartScale'

/**
 * A list of meters (docs/licences-plan.md §8.2, §8.12): one row per item whose seats are counted on hosts, its name, a bar of
 * what is used against what is covered (the track a lighter step of the same hue), and the figures as text. Over-assigned shows
 * in the warn colour with ⚠ and *over by 2*, never by colour alone. Each row is a stop for Tab, with its tooltip on hover and on
 * focus; the figures are always printed, so the list is its own table.
 */
const props = withDefaults(defineProps<{ rows: readonly MeterRow[]; emptyText?: string }>(), {
  emptyText: 'Nothing to show.',
})

const meters = computed(() => props.rows.map((r) => ({ ...r, ...meter(r.used, r.total) })))
const active = ref<number | null>(null)

const figures = (r: MeterRow) => `${r.used}/${r.total} ${r.unit}`
function describe(r: (typeof meters.value)[number]): string {
  const base = `${r.label}: ${r.used} of ${r.total} ${r.unit} used`
  if (r.over) return `${base}, over-assigned: over by ${r.over}`
  return `${base}, ${r.free} free`
}
</script>

<template>
  <p v-if="!rows.length" class="muted empty">{{ emptyText }}</p>
  <ul v-else class="meter-list" data-test="meter-list">
    <li v-for="(r, i) in meters" :key="r.key" class="meter-row" :class="{ active: active === i, over: r.over > 0 }">
      <component
        :is="r.to ? RouterLink : 'div'"
        :to="r.to"
        class="hit"
        :tabindex="r.to ? undefined : 0"
        :aria-label="describe(r)"
        data-test="meter"
        @pointerenter="active = i"
        @pointerleave="active = null"
        @focus="active = i"
        @blur="active = null"
      >
        <span class="name ellipsis" :title="r.label">{{ r.label }}</span>
        <span class="track" aria-hidden="true">
          <span class="fill" :style="{ width: `${r.fraction * 100}%` }" />
        </span>
        <span class="figures" aria-hidden="true">
          <template v-if="r.over">
            <i class="pi pi-exclamation-triangle warn" />
            {{ figures(r) }} · <span class="warn">over by {{ r.over }}</span>
          </template>
          <template v-else>{{ figures(r) }}</template>
        </span>
      </component>
      <div v-if="active === i" class="tooltip" aria-hidden="true">
        <div class="tip-name">{{ r.label }}</div>
        <div class="tip-total">{{ r.used }} of {{ r.total }} {{ r.unit }} used</div>
        <div v-if="r.over" class="warn"><i class="pi pi-exclamation-triangle" /> over by {{ r.over }}</div>
        <div v-else class="muted">{{ r.free }} free</div>
      </div>
    </li>
  </ul>
</template>

<style scoped>
.meter-list {
  /* The fill the full step, the track a lighter one of the same hue; over-assigned the warn colour, with ⚠ and words. */
  --fill: var(--pnmt-chart-seq-5);
  --track: var(--pnmt-chart-seq-1);
  list-style: none;
  margin: 0;
  padding: 0;
}

.empty {
  margin: var(--pnmt-space-2) 0;
}

.meter-row {
  position: relative;
}

.hit {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 2px var(--pnmt-space-2);
  padding: 3px var(--pnmt-space-1);
  border-radius: var(--p-border-radius-sm);
  color: var(--pnmt-text);
  outline: none;
}

a.hit:hover {
  text-decoration: none;
}

.meter-row.active .hit {
  background: color-mix(in srgb, var(--pnmt-text) 6%, transparent);
}

.hit:focus-visible {
  box-shadow: 0 0 0 2px var(--p-primary-color);
}

.name {
  flex: 1 1 12rem;
  font-size: 0.929rem;
}

.track {
  flex: 1 1 8rem;
  height: 8px;
  border-radius: 4px;
  background: var(--track);
  overflow: hidden;
}

.fill {
  display: block;
  height: 100%;
  border-radius: 4px;
  background: var(--fill);
}

.over .fill {
  background: var(--pnmt-warn);
}

.figures {
  flex: none;
  min-width: 7rem;
  text-align: right;
  font-size: 0.857rem;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.warn {
  color: var(--pnmt-warn);
}

.tooltip {
  position: absolute;
  z-index: 2;
  right: 0;
  top: calc(100% + 2px);
  min-width: 10rem;
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
  font-weight: 600;
}
</style>
