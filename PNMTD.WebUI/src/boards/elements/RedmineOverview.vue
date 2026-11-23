<script setup lang="ts">
import FitList from '../FitList.vue'
import type { BoardElementData } from '../types'
import TicketRow from './TicketRow.vue'

/** The Redmine overview (§7.4): its counts as tiles, then the chosen lists of the important tickets. */
defineProps<{ data: BoardElementData; fit: boolean; now: number }>()
</script>

<template>
  <template v-if="data.redmine">
    <div v-if="data.redmine.tiles.length" class="board-tiles tiles">
      <div v-for="t in data.redmine.tiles" :key="t.tile" class="board-tile" :data-tone="t.tone ?? undefined">
        <span class="board-tile-value">{{ t.value }}</span>
        <span class="board-tile-label">{{ t.label }}</span>
      </div>
    </div>
    <div v-for="list in data.redmine.lists" :key="list.list" class="list">
      <div class="board-element-title sub">{{ list.title }}</div>
      <p v-if="!list.tickets.length" class="board-muted none">None.</p>
      <FitList v-else :items="list.tickets" :fit="fit" :key-of="(t) => t.id">
        <template #default="{ item }"><TicketRow :ticket="item" :now="now" /></template>
      </FitList>
    </div>
  </template>
</template>

<style scoped>
.tiles {
  grid-template-columns: repeat(auto-fit, minmax(6.5em, 1fr));
  flex: 0 0 auto;
}

.list {
  display: flex;
  flex-direction: column;
  flex: 1 1 0;
  min-height: 0;
}

.sub {
  padding: 0.2em 0 0;
}

.none {
  margin: 0;
}
</style>
