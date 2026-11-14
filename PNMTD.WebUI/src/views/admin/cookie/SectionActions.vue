<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import { computed } from 'vue'

/**
 * A section's Save and Undo (docs/cookie-plan.md §9.3): a save is a new version. The server's messages that no field of
 * the tab shows (placed) are listed here, so none is lost.
 */
const props = withDefaults(
  defineProps<{
    busy: boolean
    dirty: boolean
    errors: Record<string, string[]>
    /** The fields the tab shows its messages at. */
    placed?: (field: string) => boolean
    label?: string
  }>(),
  { placed: () => false, label: 'Save' },
)
const emit = defineEmits<{ save: []; reset: [] }>()
const rest = computed(() =>
  Object.entries(props.errors)
    .filter(([field]) => !props.placed(field))
    .flatMap(([field, messages]) => messages.map((m) => (field ? `${field}: ${m}` : m))),
)
const any = computed(() => Object.keys(props.errors).length > 0)
</script>

<template>
  <div class="section-actions">
    <Message v-if="any" severity="error" :closable="false" size="small">
      Not saved.
      <template v-if="rest.length">
        <ul class="messages">
          <li v-for="m in rest" :key="m">{{ m }}</li>
        </ul>
      </template>
      <template v-else> See the marked fields.</template>
    </Message>
    <div class="form-actions">
      <Button :label="label" icon="pi pi-check" :loading="busy" :disabled="!dirty" @click="emit('save')" />
      <Button v-if="dirty" label="Undo changes" severity="secondary" text :disabled="busy" @click="emit('reset')" />
      <span v-if="dirty" class="muted small">Not saved yet.</span>
    </div>
  </div>
</template>

<style scoped>
.section-actions {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-2);
}

.messages {
  margin: var(--pnmt-space-1) 0 0;
  padding-left: 1.2rem;
}

.small {
  font-size: 0.857rem;
}
</style>
