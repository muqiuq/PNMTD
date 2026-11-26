<script setup lang="ts">
import { computed, ref } from 'vue'
import { formatPlanDate } from '../plan'
import { heatStep, monthShort, type CalendarDay } from './stats'

/**
 * The office, day by day (docs/availability-extensions-plan.md §10, part 7): the year in weeks, each open day shaded in one
 * hue by a count column's count (--pnmt-chart-seq-1 to -7), a day below the minimum outlined, closed days left blank. A
 * click opens the plan at that day.
 */
const props = defineProps<{
  weekdays: readonly { day: number; label: string }[]
  weeks: readonly (readonly (CalendarDay | null)[])[]
  column: string
  minimum: number | null | undefined
}>()
const emit = defineEmits<{ open: [date: string] }>()

const max = computed(() => Math.max(0, ...props.weeks.flat().map((d) => d?.value ?? 0)))
/** The month's label above the week its 1st falls in. */
const monthAt = computed(() =>
  props.weeks.map((w) => {
    const first = w.find((d) => d && d.date.endsWith('-01'))
    return first ? monthShort[Number(first.date.slice(5, 7)) - 1] : ''
  }),
)
const hovered = ref<CalendarDay | null>(null)
const describe = (d: CalendarDay) =>
  d.value === null
    ? `${formatPlanDate(d.date)}: closed`
    : `${formatPlanDate(d.date)}: ${props.column} ${d.value}${d.below ? `, below the minimum of ${props.minimum}` : ''}${d.future ? ' (planned)' : ''}`
const readout = computed(() =>
  hovered.value ? describe(hovered.value) : 'Point at a day for its count; a click opens it in the plan.',
)
</script>

<template>
  <figure class="calendar-wrap">
    <div class="calendar" :style="{ gridTemplateColumns: `2rem repeat(${weeks.length}, 13px)` }">
      <span />
      <span v-for="(m, i) in monthAt" :key="`m${i}`" class="month muted">{{ m }}</span>
      <template v-for="(w, row) in weekdays" :key="w.day">
        <span class="weekday muted">{{ w.label }}</span>
        <template v-for="(week, col) in weeks" :key="`${row}-${col}`">
          <button
            v-if="week[row]"
            type="button"
            class="day"
            :class="[
              `step-${week[row]!.value === null ? 'closed' : heatStep(week[row]!.value!, max)}`,
              { below: week[row]!.below, future: week[row]!.future },
            ]"
            :aria-label="describe(week[row]!)"
            :title="describe(week[row]!)"
            :disabled="week[row]!.value === null"
            @mouseenter="hovered = week[row]"
            @mouseleave="hovered = null"
            @focus="hovered = week[row]"
            @click="emit('open', week[row]!.date)"
          />
          <span v-else />
        </template>
      </template>
    </div>
    <figcaption class="readout muted small" aria-live="polite">{{ readout }}</figcaption>
    <div class="legend muted small" aria-hidden="true">
      <span>0</span>
      <span v-for="s in 7" :key="s" class="key" :class="`step-${s}`" />
      <span>{{ max }}</span>
      <span class="gap"><span class="key below step-1" /> below the minimum</span>
      <span class="gap"><span class="key future step-4" /> planned</span>
    </div>
  </figure>
</template>

<style scoped>
.calendar-wrap {
  margin: 0;
  overflow-x: auto;
}

.calendar {
  display: grid;
  gap: 2px;
  align-items: center;
  width: max-content;
}

.month,
.weekday {
  font-size: 10px;
  line-height: 1;
  white-space: nowrap;
  overflow: visible;
}

.day,
.key {
  display: inline-block;
  width: 13px;
  height: 13px;
  padding: 0;
  border: none;
  border-radius: 2px;
  background: var(--pnmt-chart-mid);
  cursor: pointer;
}

.day:disabled {
  cursor: default;
}

.day:focus-visible {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 1px;
}

.step-closed {
  background: transparent;
  box-shadow: inset 0 0 0 1px var(--pnmt-border);
}

.step-0 {
  background: var(--pnmt-chart-mid);
}

.step-1 {
  background: var(--pnmt-chart-seq-1);
}

.step-2 {
  background: var(--pnmt-chart-seq-2);
}

.step-3 {
  background: var(--pnmt-chart-seq-3);
}

.step-4 {
  background: var(--pnmt-chart-seq-4);
}

.step-5 {
  background: var(--pnmt-chart-seq-5);
}

.step-6 {
  background: var(--pnmt-chart-seq-6);
}

.step-7 {
  background: var(--pnmt-chart-seq-7);
}

.below {
  box-shadow: inset 0 0 0 2px var(--pnmt-warn);
}

.future {
  opacity: 0.55;
}

.readout {
  margin-top: var(--pnmt-space-1);
  min-height: 1.3em;
}

.legend {
  display: flex;
  align-items: center;
  gap: 3px;
  margin-top: var(--pnmt-space-1);
}

.gap {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-left: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}
</style>
