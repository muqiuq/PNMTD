<script setup lang="ts">
import { computed } from 'vue'
import { since } from '../time'
import type { BoardElement, BoardElementData } from '../types'

/** A large tile per chosen host or sensor (§7.1), coloured by its state, with since when it fails. */
const props = defineProps<{ element: BoardElement; data: BoardElementData; now: number }>()
const tone = { Ok: 'ok', Failing: 'fail', NoData: 'none', Off: 'none', Missing: 'none' } as const
const words = { Ok: 'OK', Failing: 'failing', NoData: 'no data', Off: 'off', Missing: 'removed' } as const
const columns = computed(() => `repeat(${props.element.settings.perRow ?? 3}, minmax(0, 1fr))`)
</script>

<template>
  <p v-if="!data.tiles?.length" class="board-muted">No hosts or sensors chosen.</p>
  <div v-else class="board-tiles status" :style="{ gridTemplateColumns: columns }">
    <div v-for="t in data.tiles" :key="t.id" class="board-tile" :data-tone="tone[t.state]" :data-state="t.state">
      <span class="board-tile-label name">{{ t.name }}</span>
      <span class="board-tile-value">{{ words[t.state] }}</span>
      <span v-if="t.since" class="board-tile-label">for {{ since(t.since, now) }}</span>
    </div>
  </div>
</template>

<style scoped>
.status {
  flex: 1 1 auto;
  align-content: stretch;
}

.name {
  font-weight: 650;
}
</style>
