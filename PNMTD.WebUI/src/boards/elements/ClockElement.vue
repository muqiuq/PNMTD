<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { boardTime, type BoardTime } from '../time'
import type { BoardElement } from '../types'

/** A large clock (§7.1) in PNMT's time zone, with the date and up to three more zones with their labels. */
const props = defineProps<{ element: BoardElement; time: BoardTime }>()
const now = ref(new Date())
const timer = setInterval(() => (now.value = new Date()), 1000)
onBeforeUnmount(() => clearInterval(timer))
const zones = computed(() =>
  (props.element.settings.zones ?? []).map((z) => ({ label: z.label, time: boardTime(z.zone) })),
)
</script>

<template>
  <div class="clock">
    <span class="board-big time">{{ time.time(now.toISOString(), element.settings.seconds) }}</span>
    <span v-if="element.settings.date" class="board-muted">{{ time.date(now) }}</span>
    <div v-if="zones.length" class="zones">
      <span v-for="z in zones" :key="z.label"
        ><span class="board-muted">{{ z.label }}</span> {{ z.time.time(now.toISOString()) }}</span
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

.zones {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 0.4em 1.2em;
}
</style>
