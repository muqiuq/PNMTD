<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import type { BoardTime } from '../time'
import type { BoardElement } from '../types'

/**
 * A large clock (§7.1) in PNMT's time zone, with the date and up to three more zones with their labels. Its own time and date format
 * where it sets them, else the board's (docs/board-messages-plan.md §9.1, §9.2); "PM" smaller than the time.
 */
const props = defineProps<{ element: BoardElement; time: BoardTime }>()
const now = ref(new Date())
const timer = setInterval(() => (now.value = new Date()), 1000)
onBeforeUnmount(() => clearInterval(timer))
const clock = computed(() => {
  const s = props.element.settings
  return props.time.with({
    ...(s.timeFormat ? { timeFormat: s.timeFormat } : {}),
    ...(s.dateFormat ? { dateFormat: s.dateFormat } : {}),
  })
})
const big = computed(() => clock.value.timeParts(now.value, props.element.settings.seconds))
/** The board's week number goes with the date; the element can turn it on or off for itself. */
const week = computed(
  () => props.element.settings.weekNumber ?? (props.element.settings.date && clock.value.options.weekNumber),
)
const zones = computed(() =>
  (props.element.settings.zones ?? []).map((z) => ({ label: z.label, time: clock.value.with({ zone: z.zone }) })),
)
</script>

<template>
  <div class="clock">
    <span class="board-big time"
      >{{ big.time }}<span v-if="big.period" class="period">&nbsp;{{ big.period }}</span></span
    >
    <span v-if="element.settings.date" class="board-muted"
      >{{ clock.date(now) }}<template v-if="week"> · Week {{ clock.week(now) }}</template></span
    >
    <span v-else-if="week" class="board-muted">Week {{ clock.week(now) }}</span>
    <div v-if="zones.length" class="zones">
      <span v-for="z in zones" :key="z.label"
        ><span class="board-muted">{{ z.label }}</span> {{ z.time.time(now) }}</span
      >
    </div>
  </div>
</template>

<style scoped>
.clock {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 0.2em;
  padding: 0.2em 0;
}

.time {
  font-size: 3.6em;
}

.period {
  font-size: 0.6em;
}

.zones {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 0.4em 1.2em;
}
</style>
