<script setup lang="ts">
import { outcomeLabel, outcomeMark, outcomeTone } from './advanced'
import type { OutcomeRow } from './dataTab'

/**
 * Rules and what they make of a report (docs/advanced-sensor-plan.md §3.2, §10.3): ✓ holds, ✗ fails, ! warns, with the
 * condition, the reason, and the deciding values where the viewer may see them.
 */
withDefaults(defineProps<{ rows: OutcomeRow[]; label?: string }>(), { label: 'The rules' })
</script>

<template>
  <ul class="outcomes" :aria-label="label">
    <li v-for="r in rows" :key="r.key">
      <span class="mark" :class="`tone-${outcomeTone(r.outcome)}`" aria-hidden="true">{{
        outcomeMark(r.outcome)
      }}</span>
      <div class="text">
        <div>
          <span class="name">{{ r.name }}</span>
          <span class="sr">: {{ outcomeLabel(r.outcome) }}</span>
          <span class="condition">{{ r.condition }}</span>
        </div>
        <div class="small" :class="`tone-${outcomeTone(r.outcome)}`">
          {{ outcomeLabel(r.outcome) }}<template v-if="r.reason">: {{ r.reason }}</template>
          <span v-if="r.values" class="values"> · {{ r.values }}</span>
        </div>
      </div>
    </li>
  </ul>
</template>

<style scoped>
.outcomes {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.outcomes li {
  display: flex;
  align-items: flex-start;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.mark {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex: none;
  width: 1.4rem;
  height: 1.4rem;
  border-radius: 999px;
  font-weight: 700;
  font-size: 0.857rem;
  background: var(--pnmt-none-soft);
}

.text {
  min-width: 0;
  overflow-wrap: anywhere;
}

.name {
  font-weight: 600;
  margin-right: var(--pnmt-space-2);
}

.condition {
  font-family: var(--pnmt-mono);
  font-size: 0.857rem;
  color: var(--pnmt-muted);
}

.small {
  font-size: 0.857rem;
}

.values {
  color: var(--pnmt-text);
  font-family: var(--pnmt-mono);
}

.sr {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}

.tone-ok {
  color: var(--pnmt-ok);
}

.mark.tone-ok {
  background: var(--pnmt-ok-soft);
}

.tone-warn {
  color: var(--pnmt-warn);
}

.mark.tone-warn {
  background: var(--pnmt-warn-soft);
}

.tone-fail {
  color: var(--pnmt-fail);
}

.mark.tone-fail {
  background: var(--pnmt-fail-soft);
}

.tone-muted {
  color: var(--pnmt-muted);
}
</style>
