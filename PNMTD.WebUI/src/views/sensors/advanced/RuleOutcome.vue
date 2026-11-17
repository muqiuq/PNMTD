<script setup lang="ts">
import { computed } from 'vue'
import type { S } from '@/api/resources'
import { outcomeMark, outcomeTone } from './advanced'
import { outcomeParts } from './rules'

/**
 * A rule's outcome (docs/advanced-sensor-plan.md §3.2): ✓ holds, ✗ fails, ! warns, with the values that decided it and why,
 * as `disk.free_gb = 3.2 → fails (must be at least 5)`. As a `cell`, the mark and the word on a line, the values below.
 * Without `values` (data that names people, viewed without Details) the path and why only. Values are the app's text.
 */
const props = withDefaults(
  defineProps<{ result: S['AdvancedRuleResult'] | null | undefined; values?: boolean; cell?: boolean }>(),
  { values: true, cell: false },
)
const tone = computed(() => outcomeTone(props.result?.outcome))
const parts = computed(() => (props.result ? outcomeParts(props.result, props.values) : null))
const below = computed(() => (parts.value ? [parts.value.what, parts.value.why].filter(Boolean).join(' · ') : ''))
</script>

<template>
  <span v-if="!result || !parts" class="muted">not judged yet</span>
  <span v-else-if="cell" class="outcome cell">
    <span class="head" :class="`tone-${tone}`"
      ><span class="mark" aria-hidden="true">{{ outcomeMark(result.outcome) }}</span> {{ parts.label }}</span
    >
    <span v-if="below && result.outcome !== 'Off'" class="muted small ellipsis" :title="below">{{ below }}</span>
    <span v-if="result.assumedUtc" class="muted small">a time without an offset, read as UTC</span>
  </span>
  <span v-else class="outcome line">
    <span class="mark" :class="`tone-${tone}`" aria-hidden="true">{{ outcomeMark(result.outcome) }}</span>
    <span class="text"
      ><span class="mono what">{{ parts.what }}</span> →
      <strong :class="`tone-${tone}`">{{ parts.label }}</strong><template v-if="parts.why">
        ({{ parts.why }})</template
      ></span
    >
    <span v-if="result.assumedUtc" class="muted small note">A time without an offset was read as UTC.</span>
  </span>
</template>

<style scoped>
.outcome {
  min-width: 0;
}

.cell {
  display: flex;
  flex-direction: column;
  max-width: 100%;
}

.head {
  font-weight: 600;
  white-space: nowrap;
}

.line {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 0 var(--pnmt-space-1);
}

.line .text {
  overflow-wrap: anywhere;
}

.line .note {
  flex-basis: 100%;
}

.mark {
  font-weight: 700;
  display: inline-block;
  min-width: 1ch;
  text-align: center;
}

.what {
  overflow-wrap: anywhere;
}

.small {
  font-size: 0.857rem;
}

.tone-ok {
  color: var(--pnmt-ok);
}

.tone-warn {
  color: var(--pnmt-warn);
}

.tone-fail {
  color: var(--pnmt-fail);
}

.tone-muted {
  color: var(--pnmt-muted);
}
</style>
