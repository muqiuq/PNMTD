<script setup lang="ts">
import type { BoardElementData } from '../types'

/** Who is on duty today (§7.1): the availability plan's people of the day, and as what. */
defineProps<{ data: BoardElementData }>()
</script>

<template>
  <template v-if="data.onDuty">
    <p v-if="data.onDuty.closed || !data.onDuty.people.length" class="board-muted">Nobody is on duty today.</p>
    <div v-else class="duty">
      <span v-for="p in data.onDuty.people" :key="p" class="person">{{ p }}</span>
      <span class="board-muted">{{ data.onDuty.as }}</span>
    </div>
  </template>
</template>

<style scoped>
.duty {
  display: flex;
  flex-direction: column;
  gap: 0.15em;
}

.person {
  font-size: 1.6em;
  font-weight: 700;
}
</style>
