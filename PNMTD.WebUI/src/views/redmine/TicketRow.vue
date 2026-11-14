<script setup lang="ts">
import Tag from 'primevue/tag'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import TicketButtons from './TicketButtons.vue'

/** A ticket in a list (§6.3, §6.6): number, status, subject, who has it, when it changed, and its two buttons. */
defineProps<{ ticket: S['RedmineIssueItem']; note?: string | null }>()
defineSlots<{ actions?: () => unknown }>()
</script>

<template>
  <div class="ticket" :class="{ closed: ticket.isClosed }">
    <RouterLink :to="{ name: 'redmine-issue', params: { id: ticket.id } }" class="number mono"
      >#{{ ticket.id }}</RouterLink
    >
    <Tag :value="ticket.status" :severity="ticket.isClosed ? 'secondary' : 'info'" class="status" />
    <span class="main">
      <RouterLink
        :to="{ name: 'redmine-issue', params: { id: ticket.id } }"
        class="subject ellipsis"
        :title="ticket.subject"
        >{{ ticket.subject }}</RouterLink
      >
      <span class="meta muted">
        <template v-if="ticket.priority">{{ ticket.priority }} · </template>
        {{ ticket.assignedTo ?? 'nobody' }} · <RelativeTime :value="ticket.updatedOn" />
        <template v-if="note"> · {{ note }}</template>
      </span>
    </span>
    <TicketButtons :ticket="ticket" compact />
    <slot name="actions" />
  </div>
</template>

<style scoped>
.ticket {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.closed .subject {
  text-decoration: line-through;
  color: var(--pnmt-muted);
}

.number {
  flex-shrink: 0;
}

.status {
  flex-shrink: 0;
}

.main {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.subject {
  display: block;
}

.meta {
  font-size: 0.85em;
}
</style>
