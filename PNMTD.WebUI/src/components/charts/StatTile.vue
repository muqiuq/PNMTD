<script setup lang="ts">
import { RouterLink, type RouteLocationRaw } from 'vue-router'

/**
 * A stat tile (docs/licences-plan.md §8.12): a headline number is a tile, not a chart. A label, the figure large (proportional
 * figures, as standalone numbers read best), lines under it (other currencies, a comparison), and, with `to`, a link to the list
 * it sums. The `lines` slot takes richer lines (an icon and a word for a state).
 */
defineProps<{ label: string; value: string; lines?: readonly string[]; to?: RouteLocationRaw; linkLabel?: string }>()
</script>

<template>
  <component
    :is="to ? RouterLink : 'div'"
    :to="to"
    class="stat-tile panel"
    :class="{ link: !!to }"
    :aria-label="to && linkLabel ? linkLabel : undefined"
    data-test="stat-tile"
  >
    <span class="label">{{ label }}</span>
    <span class="value">{{ value }}</span>
    <span v-for="(line, i) in lines ?? []" :key="i" class="line muted">{{ line }}</span>
    <slot name="lines" />
  </component>
</template>

<style scoped>
.stat-tile {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: var(--pnmt-space-3);
  min-width: 0;
  color: var(--pnmt-text);
}

.stat-tile.link:hover {
  text-decoration: none;
  border-color: var(--p-primary-color);
}

.stat-tile.link:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 2px;
}

.label {
  color: var(--pnmt-muted);
  font-size: 0.929rem;
}

.value {
  font-size: 1.5rem;
  font-weight: 600;
  line-height: 1.25;
  /* Proportional figures for a standalone number; the body sets tabular ones. */
  font-feature-settings:
    'cv11',
    'tnum' 0;
  overflow-wrap: anywhere;
}

.line {
  font-size: 0.857rem;
}
</style>
