<script setup lang="ts">
import Tag from 'primevue/tag'
import { kbNumber, kbStatusLabel, kbStatusSeverity, type KbListItem } from '@/api/kb'

/** A page in a panel or a short list: its number, title, a lock and a clip, and its status unless published. */
defineProps<{ page: KbListItem }>()
</script>

<template>
  <span class="page-row">
    <span class="mono muted number">{{ kbNumber(page.number) }}</span>
    <RouterLink :to="{ name: 'kb-page', params: { number: page.number } }" class="title ellipsis">{{
      page.title
    }}</RouterLink>
    <i v-if="page.sensitive" class="pi pi-lock muted" title="Sensitive: never shown to AI or MCP" />
    <i v-if="page.hasAttachments" class="pi pi-paperclip muted" title="Has attachments" />
    <Tag
      v-if="page.status !== 'Published'"
      :value="kbStatusLabel[page.status]"
      :severity="kbStatusSeverity(page.status)"
      class="status"
    />
  </span>
</template>

<style scoped>
.page-row {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
  min-width: 0;
}

.number {
  white-space: nowrap;
  font-size: 0.857rem;
}

.title {
  font-weight: 500;
}

.status {
  font-size: 0.75rem;
}
</style>
