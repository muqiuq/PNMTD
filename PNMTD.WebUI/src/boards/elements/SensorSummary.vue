<script setup lang="ts">
import type { BoardElement, BoardElementData } from '../types'

/** How many sensors are OK, failing, without data; ignored and off where the element says so (§7.1). */
defineProps<{ element: BoardElement; data: BoardElementData }>()
</script>

<template>
  <div v-if="data.counts" class="board-tiles summary">
    <div class="board-tile" :data-tone="data.counts.failing > 0 ? 'fail' : 'ok'">
      <span class="board-tile-value">{{ data.counts.failing }}</span>
      <span class="board-tile-label">failing</span>
    </div>
    <div class="board-tile">
      <span class="board-tile-value">{{ data.counts.ok }}</span>
      <span class="board-tile-label">OK</span>
    </div>
    <div class="board-tile">
      <span class="board-tile-value">{{ data.counts.noData }}</span>
      <span class="board-tile-label">no data</span>
    </div>
    <template v-if="element.settings.ignored">
      <div class="board-tile">
        <span class="board-tile-value">{{ data.counts.ignored }}</span>
        <span class="board-tile-label">ignored</span>
      </div>
      <div class="board-tile">
        <span class="board-tile-value">{{ data.counts.disabled }}</span>
        <span class="board-tile-label">off</span>
      </div>
    </template>
  </div>
</template>

<style scoped>
.summary {
  grid-template-columns: repeat(auto-fit, minmax(5.5em, 1fr));
}
</style>
