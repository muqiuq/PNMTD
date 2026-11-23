<script setup lang="ts">
import FitList from '../FitList.vue'
import { since, type BoardTime } from '../time'
import type { BoardElementData } from '../types'

/** Failing sensors (§7.7): the most important first, then the longest failing; the message only where the element shows it. */
defineProps<{ data: BoardElementData; fit: boolean; time: BoardTime; now: number }>()
const tone = (importance: string) => (importance === 'High' ? 'fail' : importance === 'Low' ? 'none' : 'warn')
</script>

<template>
  <p v-if="!data.sensors?.length" class="board-good">✓ Nothing is failing</p>
  <FitList v-else :items="data.sensors" :fit="fit" :more="data.more" :key-of="(s) => s.id">
    <template #default="{ item }">
      <span class="board-dot" :data-tone="tone(item.importance)" :title="`${item.importance} importance`" />
      <span class="board-grow">
        <strong>{{ item.host }}</strong> / {{ item.sensor }}
        <span v-if="item.message" class="board-muted message"> · {{ item.message }}</span>
      </span>
      <span class="board-muted" :title="time.when(item.since)">{{ since(item.since, now) }}</span>
    </template>
  </FitList>
</template>
