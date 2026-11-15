<script setup lang="ts">
import InlineField from '@/components/InlineField.vue'

/**
 * A sensor's notes in a panel of their own, edited where they are shown (the same field as in the configuration). A
 * reader sees the panel only when there are notes.
 */
defineProps<{
  notes: string | null
  canWrite: boolean
  save: (notes: string | null) => Promise<unknown>
}>()
</script>

<template>
  <section v-if="canWrite || notes" class="panel" aria-labelledby="notes-title">
    <div id="notes-title" class="panel-header">Notes</div>
    <div class="panel-body">
      <InlineField
        :value="notes"
        type="textarea"
        label="Notes"
        field="notes"
        placeholder="No notes"
        :readonly="!canWrite"
        :save="(v) => save(v ? String(v) : null)"
      />
    </div>
  </section>
</template>
