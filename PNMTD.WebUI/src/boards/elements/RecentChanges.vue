<script setup lang="ts">
import FitList from '../FitList.vue'
import type { BoardTime } from '../time'
import type { BoardElementData } from '../types'

/** State changes of the last 24 hours, newest first (§7.1). */
defineProps<{ data: BoardElementData; fit: boolean; time: BoardTime }>()
</script>

<template>
  <p v-if="!data.changes?.length" class="board-muted">No changes in the last 24 hours.</p>
  <FitList v-else :items="data.changes" :fit="fit" :more="data.more" :key-of="(c) => `${c.at}${c.host}${c.sensor}`">
    <template #default="{ item }">
      <span class="board-muted when">{{ time.when(item.at) }}</span>
      <span
        class="board-dot"
        :data-tone="item.success ? 'ok' : 'fail'"
        :title="item.success ? 'OK again' : 'failing'"
      />
      <span class="board-grow">{{ item.host }} / {{ item.sensor }}</span>
    </template>
  </FitList>
</template>

<style scoped>
.when {
  flex: 0 0 auto;
  font-variant-numeric: tabular-nums;
}
</style>
