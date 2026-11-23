<script setup lang="ts">
import { computed } from 'vue'
import type { BoardElementData } from '../types'

/** Tickets received or solved today (§7.4): a large number, its colour from the element's thresholds, yesterday's below. */
const props = defineProps<{ data: BoardElementData }>()
const tone = computed(() => ({ Green: 'ok', Yellow: 'warn', Red: 'fail', Grey: 'none' })[props.data.colour ?? 'Grey'])
</script>

<template>
  <div v-if="data.number" class="number">
    <span class="board-big" :data-tone="data.colour ? tone : undefined">{{ data.number.value }}</span>
    <span v-if="data.number.yesterday != null" class="board-muted">yesterday {{ data.number.yesterday }}</span>
  </div>
</template>

<style scoped>
.number {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 0.2em;
  padding: 0.2em 0;
}

.board-big[data-tone='ok'] {
  color: var(--board-ok);
}

.board-big[data-tone='warn'] {
  color: var(--board-warn);
}

.board-big[data-tone='fail'] {
  color: var(--board-fail);
}
</style>
