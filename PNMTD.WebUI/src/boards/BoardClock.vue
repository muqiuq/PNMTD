<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import type { BoardTime } from './time'
import type { BoardTimeFormat } from './types'

/**
 * The time in the bar or the header line, in PNMT's zone; ticking every second when seconds show, else every ten. Its own hour cycle
 * when the header sets one (docs/board-messages-plan.md §9.1); the date in the board's format, with the week where the board shows it.
 */
const props = defineProps<{ time: BoardTime; seconds: boolean; date: boolean; timeFormat?: BoardTimeFormat | null }>()
const now = ref(new Date())
const timer = setInterval(() => (now.value = new Date()), props.seconds ? 1000 : 10_000)
onBeforeUnmount(() => clearInterval(timer))
const clock = computed(() => (props.timeFormat ? props.time.with({ timeFormat: props.timeFormat }) : props.time))
const text = computed(() => {
  const t = clock.value.time(now.value, props.seconds)
  if (!props.date) return t
  const week = clock.value.options.weekNumber ? ` · Week ${clock.value.week(now.value)}` : ''
  return `${clock.value.date(now.value, true)}${week} ${t}`
})
</script>

<template>
  <span class="board-clock">{{ text }}</span>
</template>
