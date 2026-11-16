<script setup lang="ts">
import Button from 'primevue/button'
import type { S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { caseLink } from './access'

/**
 * Cookie's notes on a host or a sensor (docs/cookie-object-notes-plan.md §6): one row per note, newest first, with its date,
 * a link to its case for readers with Cookie: Use, and Remove for Monitoring: Edit. Hidden when there is nothing to show.
 */
defineProps<{
  notes: S['CookieObjectNoteItem'][]
  canRemove: boolean
  canLinkCase: boolean
}>()
const emit = defineEmits<{ remove: [note: S['CookieObjectNoteItem']] }>()
</script>

<template>
  <section v-if="notes.length" class="panel" aria-labelledby="cookie-notes-title">
    <div id="cookie-notes-title" class="panel-header">Cookie's notes</div>
    <ul class="panel-body notes">
      <li v-for="note in notes" :key="note.id" class="note">
        <div class="meta muted small">
          <RelativeTime :value="note.at" />
          <template v-if="note.caseId && canLinkCase">
            ·
            <RouterLink :to="caseLink(note.caseId)">{{
              note.caseTitle ? `Case: ${note.caseTitle}` : 'Its case'
            }}</RouterLink>
          </template>
          <Button
            v-if="canRemove"
            icon="pi pi-times"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="`Remove Cookie's note`"
            class="remove"
            @click="emit('remove', note)"
          />
        </div>
        <p class="text">{{ note.text }}</p>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.notes {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.note {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.meta {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-1);
}

.remove {
  margin-left: auto;
}

.text {
  margin: 0;
  white-space: pre-wrap;
}
</style>
