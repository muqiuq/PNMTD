<script setup lang="ts">
import Button from 'primevue/button'
import Textarea from 'primevue/textarea'
import { ref } from 'vue'
import { ApiError } from '@/api/client'
import type { LicenseDetail } from '@/api/licenses'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import { useNotify } from '@/composables/useNotify'
import { useItemSave } from './useItemSave'

/**
 * The item's note (docs/licences-plan.md §3.3, §8.7): Markdown, rendered for reading as a responsibility's description, edited in a
 * plain text field with Edit. A note that looks like it holds a key asks before it is saved anyway.
 */
const props = defineProps<{ item: LicenseDetail; readonly: boolean }>()
const { save } = useItemSave(() => props.item)
const notify = useNotify()
const editing = ref(false)
const draft = ref('')
const error = ref<string | null>(null)
const saving = ref(false)

function begin() {
  draft.value = props.item.note ?? ''
  error.value = null
  editing.value = true
}

async function store() {
  saving.value = true
  error.value = null
  try {
    await save({ note: draft.value.trim() ? draft.value : null }, 'note')
    editing.value = false
  } catch (e) {
    if (e instanceof ApiError && e.field('note')) error.value = e.field('note')!
    else notify.error(e, 'Note not saved')
  } finally {
    saving.value = false
  }
}

function onKey(event: KeyboardEvent) {
  if (event.key === 'Escape') editing.value = false
  else if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) void store()
}
</script>

<template>
  <section class="panel" aria-labelledby="note-title">
    <div class="panel-header">
      <span id="note-title">Note</span>
      <span class="spacer" />
      <Button
        v-if="!readonly && !editing"
        :label="item.note ? 'Edit' : 'Write one'"
        icon="pi pi-pencil"
        size="small"
        text
        data-test="edit-note"
        @click="begin"
      />
    </div>
    <div class="panel-body">
      <form v-if="editing" class="form" novalidate @submit.prevent="store" @keydown="onKey">
        <Textarea
          v-model="draft"
          rows="8"
          auto-resize
          class="mono"
          aria-label="Note"
          :invalid="!!error"
          placeholder="Markdown: what to know about it. Keys belong in the key fields."
          autofocus
        />
        <small v-if="error" class="field-error">{{ error }}</small>
        <div class="form-actions">
          <Button type="submit" label="Save" icon="pi pi-check" size="small" :loading="saving" data-test="note-save" />
          <Button label="Discard" severity="secondary" text size="small" :disabled="saving" @click="editing = false" />
          <span class="muted small">Ctrl+Enter saves, Esc discards</span>
        </div>
      </form>
      <MarkdownView v-else-if="item.note" :source="item.note" />
      <p v-else class="muted none">No note.</p>
    </div>
  </section>
</template>

<style scoped>
.spacer {
  flex: 1;
}

.none {
  margin: 0;
}

.small {
  font-size: 0.857rem;
}
</style>
