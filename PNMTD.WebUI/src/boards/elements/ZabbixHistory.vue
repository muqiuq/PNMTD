<script setup lang="ts">
import { severityLabel } from '@/shared/zabbix'
import FitList from '../FitList.vue'
import { between, since, type BoardTime } from '../time'
import type { BoardElementData } from '../types'

/**
 * The latest Zabbix problems, history style (§7.5): newest first, open ones with how long, resolved ones muted with their span; an
 * estimate (its recovery unseen) as a range.
 */
defineProps<{ data: BoardElementData; fit: boolean; time: BoardTime; now: number }>()
</script>

<template>
  <p v-if="!data.problems?.length" class="board-good">✓ No Zabbix problems</p>
  <FitList v-else :items="data.problems" :fit="fit" :more="data.more" :key-of="(p) => p.eventId">
    <template #default="{ item }">
      <span class="board-sev" :data-sev="item.severity">{{ severityLabel(item.severity) }}</span>
      <span class="board-grow" :class="{ resolved: item.resolved }">
        <strong>{{ item.host }}</strong> · {{ item.name }}
      </span>
      <span v-if="!item.resolved" class="board-muted"
        >since {{ time.when(item.started) }} ({{ since(item.started, now) }})</span
      >
      <span v-else-if="item.resolvedFrom" class="board-muted"
        >✓ {{ time.when(item.started) }}–{{ time.time(item.resolved) }} (resolved {{ time.time(item.resolvedFrom) }}–{{
          time.time(item.resolved)
        }})</span
      >
      <span v-else class="board-muted"
        >✓ {{ time.when(item.started) }}–{{ time.time(item.resolved) }} ({{
          between(item.started, item.resolved)
        }})</span
      >
    </template>
  </FitList>
</template>

<style scoped>
.resolved {
  opacity: 0.65;
}
</style>
