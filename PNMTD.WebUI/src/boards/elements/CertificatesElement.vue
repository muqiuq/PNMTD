<script setup lang="ts">
import FitList from '../FitList.vue'
import type { BoardTime } from '../time'
import type { BoardElementData } from '../types'

/** SSL certificates that expire soon (§7.1), the soonest first: red within a week or expired, yellow within a month. */
defineProps<{ data: BoardElementData; fit: boolean; time: BoardTime }>()
const tone = (days: number) => (days < 7 ? 'fail' : days < 30 ? 'warn' : 'ok')
</script>

<template>
  <p v-if="!data.certificates?.length" class="board-good">✓ No certificate expires soon</p>
  <FitList v-else :items="data.certificates" :fit="fit" :more="data.more" :key-of="(c) => `${c.host}/${c.sensor}`">
    <template #default="{ item }">
      <span class="board-dot" :data-tone="tone(item.days)" />
      <span class="board-grow"
        >{{ item.host }} / {{ item.sensor }}<span v-if="item.name" class="board-muted"> · {{ item.name }}</span></span
      >
      <span :class="item.days < 0 ? 'expired' : 'board-muted'">{{
        item.days < 0 ? 'expired' : item.days === 0 ? 'today' : `${item.days} d`
      }}</span>
    </template>
  </FitList>
</template>

<style scoped>
.expired {
  color: var(--board-fail);
  font-weight: 700;
}
</style>
