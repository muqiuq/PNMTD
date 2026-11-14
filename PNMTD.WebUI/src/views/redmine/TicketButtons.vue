<script setup lang="ts">
import Button from 'primevue/button'

/**
 * Every ticket's two buttons (docs/audit-redmine-zabbix-plan.md §6.3): *Details*, PNMT's page of the ticket (a full page),
 * and *Redmine*, the ticket in Redmine in a new tab.
 */
defineProps<{ ticket: { id: number; url: string }; compact?: boolean }>()
</script>

<template>
  <span class="ticket-buttons">
    <Button
      v-tooltip.top="compact ? 'Details' : undefined"
      as="router-link"
      :to="{ name: 'redmine-issue', params: { id: ticket.id } }"
      :label="compact ? undefined : 'Details'"
      icon="pi pi-file"
      size="small"
      severity="secondary"
      text
      :aria-label="`Details of #${ticket.id}`"
    />
    <Button
      v-tooltip.top="compact ? 'Open in Redmine' : undefined"
      as="a"
      :href="ticket.url"
      target="_blank"
      rel="noopener noreferrer"
      :label="compact ? undefined : 'Redmine'"
      icon="pi pi-external-link"
      size="small"
      severity="secondary"
      text
      :aria-label="`#${ticket.id} in Redmine`"
    />
  </span>
</template>

<style scoped>
.ticket-buttons {
  display: inline-flex;
  gap: var(--pnmt-space-1);
  flex-shrink: 0;
}
</style>
