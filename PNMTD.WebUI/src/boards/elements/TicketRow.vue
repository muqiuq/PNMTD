<script setup lang="ts">
import { since } from '../time'
import type { BoardTicket } from '../types'

/** A ticket on a board (§7.4): a private one without subject or assignee; New on a recent one. */
defineProps<{ ticket: BoardTicket; now: number }>()
</script>

<template>
  <span class="board-id">#{{ ticket.id }}</span>
  <span class="board-grow">
    <template v-if="ticket.private"><span class="board-muted">Private ticket</span></template>
    <template v-else-if="ticket.subject">{{ ticket.subject }}</template>
    <template v-else
      ><span class="board-muted">{{ ticket.tracker }}</span></template
    >
    <span class="board-muted meta">
      · {{ ticket.project }}<template v-if="ticket.assignedTo"> · {{ ticket.assignedTo }}</template></span
    >
  </span>
  <span v-if="ticket.new" class="board-badge">New</span>
  <span class="board-muted">{{ since(ticket.createdOn ?? ticket.updatedOn, now) }}</span>
</template>
