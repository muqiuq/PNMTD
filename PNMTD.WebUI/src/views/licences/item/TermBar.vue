<script setup lang="ts">
import { computed } from 'vue'
import { formatDay } from '@/format'
import type { TermBar } from './item'

/**
 * The term as a bar (docs/licences-plan.md §8.7), as a certificate's validity: from the term's first day to the expiry, today marked,
 * the part since the warning's start in warn colours, the cancellation deadline and the end of life when they fall within; all of it
 * in fail colours once expired. The marks have words too, never the colour alone.
 */
const props = defineProps<{ bar: TermBar }>()
const warn = computed(() => props.bar.warn ?? 100)
const showWarn = computed(
  () => !!props.bar.warnFrom && props.bar.warn !== null && props.bar.warn > 0 && props.bar.warn < 100,
)
const label = computed(
  () =>
    `The term from ${formatDay(props.bar.start)} to ${formatDay(props.bar.end)}; today at ${Math.round(props.bar.today)} %` +
    (props.bar.warnFrom ? `; the warning from ${formatDay(props.bar.warnFrom)}` : ''),
)
</script>

<template>
  <div class="term" role="img" :aria-label="label" data-test="term-bar">
    <div class="bar">
      <div class="part ok" :style="{ width: `${warn}%` }" />
      <div v-if="bar.warn !== null" class="part warn" :style="{ left: `${warn}%`, width: `${100 - warn}%` }" />
      <div v-if="bar.expired" class="part fail" />
      <div
        v-if="bar.cancelBy !== null"
        class="mark cancel"
        :style="{ left: `${bar.cancelBy}%` }"
        title="The cancellation deadline"
      />
      <div
        v-if="bar.endOfLife !== null"
        class="mark eol"
        :style="{ left: `${bar.endOfLife}%` }"
        title="The end of life"
      />
      <div class="mark today" :style="{ left: `${bar.today}%` }" title="Today" />
    </div>
    <div class="scale muted">
      <span>{{ formatDay(bar.start) }}</span>
      <span>{{ formatDay(bar.end) }}</span>
    </div>
    <!-- The warning's start in the legend, not on the scale, where it ran into the end's day. -->
    <div v-if="showWarn || bar.cancelBy !== null || bar.endOfLife !== null" class="legend muted">
      <span v-if="showWarn" class="warn-label"
        ><span class="key warn-key" aria-hidden="true" /> warning from {{ formatDay(bar.warnFrom!) }}</span
      >
      <span v-if="bar.cancelBy !== null"><span class="key cancel" aria-hidden="true" /> cancellation deadline</span>
      <span v-if="bar.endOfLife !== null"><span class="key eol" aria-hidden="true" /> end of life</span>
    </div>
  </div>
</template>

<style scoped>
.term {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
  max-width: 40rem;
}

.bar {
  position: relative;
  height: 10px;
  border-radius: 5px;
  background: var(--pnmt-none-soft);
}

.part {
  position: absolute;
  top: 0;
  bottom: 0;
  border-radius: 5px;
}

.part.ok {
  left: 0;
  background: var(--pnmt-ok-soft);
  border: 1px solid var(--pnmt-ok);
}

.part.warn {
  background: var(--pnmt-warn-soft);
  border: 1px solid var(--pnmt-warn);
}

.part.fail {
  left: 0;
  width: 100%;
  background: var(--pnmt-fail-soft);
  border: 1px solid var(--pnmt-fail);
}

.mark {
  position: absolute;
  top: -4px;
  bottom: -4px;
  width: 2px;
  margin-left: -1px;
}

.mark.today {
  background: var(--pnmt-text);
}

.mark.cancel,
.key.cancel {
  background: var(--pnmt-warn);
}

.mark.eol,
.key.eol {
  background: var(--pnmt-fail);
}

.scale {
  position: relative;
  display: flex;
  justify-content: space-between;
  font-size: 0.786rem;
}

.warn-label {
  white-space: nowrap;
  color: var(--pnmt-warn);
}

.warn-key {
  background: var(--pnmt-warn);
}

.legend {
  display: flex;
  gap: var(--pnmt-space-3);
  font-size: 0.786rem;
}

.key {
  display: inline-block;
  width: 2px;
  height: 0.8rem;
  vertical-align: middle;
}
</style>
