<script setup lang="ts">
import { uptimeTone } from '@/components/uptime'
import { formatPercent } from '@/format'
import FitList from '../FitList.vue'
import type { BoardElement, BoardElementData } from '../types'

/** Hosts with their 30-day uptime and their 90-day bar (§7.1), as rows or tiles. */
defineProps<{ element: BoardElement; data: BoardElementData; fit: boolean }>()
const tone = (state: string) => (state === 'Failing' ? 'fail' : state === 'Ok' ? 'ok' : 'none')
</script>

<template>
  <p v-if="!data.hosts?.length" class="board-muted">No hosts.</p>
  <div v-else-if="element.settings.tiles" class="board-tiles hosts">
    <div v-for="h in data.hosts" :key="h.id" class="board-tile" :data-tone="tone(h.state)">
      <span class="board-tile-label">{{ h.name }}</span>
      <span class="board-tile-value">{{ formatPercent(h.uptime30Days) }}</span>
    </div>
  </div>
  <FitList v-else :items="data.hosts" :fit="fit" :more="data.more" :key-of="(h) => h.id">
    <template #default="{ item }">
      <span class="board-dot" :data-tone="tone(item.state)" />
      <span class="board-grow name">{{ item.name }}</span>
      <span class="strip" aria-hidden="true">
        <span v-for="(v, i) in item.days.values.slice(-30)" :key="i" :data-tone="uptimeTone(v)" />
      </span>
      <span class="percent">{{ formatPercent(item.uptime30Days) }}</span>
    </template>
  </FitList>
</template>

<style scoped>
.hosts {
  grid-template-columns: repeat(auto-fill, minmax(8em, 1fr));
}

.name {
  flex: 0 1 10em;
}

.strip {
  flex: 1 1 auto;
  display: flex;
  gap: 1px;
  height: 0.9em;
  align-self: center;
}

.strip span {
  flex: 1;
  border-radius: 1px;
  background: var(--board-unknown-soft);
}

.strip [data-tone='ok'] {
  background: var(--board-ok);
}

.strip [data-tone='warn'] {
  background: var(--board-warn);
}

.strip [data-tone='fail'] {
  background: var(--board-fail);
}

.percent {
  flex: 0 0 4.5em;
  text-align: right;
  font-variant-numeric: tabular-nums;
}
</style>
