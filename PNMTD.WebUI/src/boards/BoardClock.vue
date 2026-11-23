<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import type { BoardTime } from './time'

/** The time in the bar or the header line, in PNMT's zone; ticking every second when seconds show, else every ten. */
const props = defineProps<{ time: BoardTime; seconds: boolean; date: boolean }>()
const now = ref(new Date())
const timer = setInterval(() => (now.value = new Date()), props.seconds ? 1000 : 10_000)
onBeforeUnmount(() => clearInterval(timer))
const text = computed(() => {
  const t = props.time.time(now.value.toISOString(), props.seconds)
  return props.date ? `${props.time.date(now.value, false)} ${t}` : t
})
</script>

<template>
  <span class="board-clock">{{ text }}</span>
</template>
