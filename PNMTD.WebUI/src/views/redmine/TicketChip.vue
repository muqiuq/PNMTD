<script setup lang="ts">
import type { S } from '@/api/resources'

/** An open ticket next to a problem (§6.6): someone is on it. Opens the ticket's page. */
defineProps<{ ticket: S['RedmineIssueItem'] }>()
</script>

<template>
  <RouterLink
    :to="{ name: 'redmine-issue', params: { id: ticket.id } }"
    class="ticket-chip"
    :title="`#${ticket.id} ${ticket.subject} · ${ticket.status}${ticket.assignedTo ? ' · ' + ticket.assignedTo : ''}`"
  >
    <i class="pi pi-ticket" aria-hidden="true" /> #{{ ticket.id }} {{ ticket.status
    }}<template v-if="ticket.assignedTo">, {{ ticket.assignedTo }}</template>
  </RouterLink>
</template>

<style scoped>
.ticket-chip {
  display: inline-flex;
  align-items: center;
  gap: var(--pnmt-space-1);
  padding: 0 var(--pnmt-space-2);
  border-radius: 999px;
  border: 1px solid var(--pnmt-border);
  font-size: 0.85em;
  white-space: nowrap;
}
</style>
