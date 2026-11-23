<script setup lang="ts">
import FitList from '../FitList.vue'
import type { BoardElementData } from '../types'
import TicketRow from './TicketRow.vue'

/** The newest tickets (§7.4), the newest first. */
defineProps<{ data: BoardElementData; fit: boolean; now: number }>()
</script>

<template>
  <p v-if="!data.tickets?.length" class="board-muted">No tickets.</p>
  <FitList v-else :items="data.tickets" :fit="fit" :key-of="(t) => t.id">
    <template #default="{ item }"><TicketRow :ticket="item" :now="now" /></template>
  </FitList>
</template>
