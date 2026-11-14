<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import ErrorState from '@/components/ErrorState.vue'

/**
 * A prompt as the model receives it (docs/cookie-plan.md §9.3): Cookie's identity, goals and rules first, then the
 * prompt or the kind of case's instruction, as saved. What code fills in at the time of a call shows in [brackets].
 */
const key = defineModel<string | null>({ required: true })
defineProps<{ title: string; dirty?: boolean }>()
const preview = useQuery({
  queryKey: computed(() => [...keys.cookie, 'admin', 'preview', key.value]),
  queryFn: () => api.cookie.preview(key.value ?? ''),
  enabled: computed(() => !!key.value),
  staleTime: 0,
})
const visible = computed({
  get: () => key.value !== null,
  set: (v: boolean) => {
    if (!v) key.value = null
  },
})
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="`Preview: ${title}`"
    modal
    :style="{ width: '52rem' }"
    :breakpoints="{ '880px': '95vw' }"
  >
    <Message v-if="dirty" severity="info" :closable="false" size="small" class="note">
      As saved: your changes that aren't saved yet aren't in it.
    </Message>
    <ErrorState
      v-if="preview.isError.value"
      :error="preview.error.value"
      what="the preview"
      @retry="preview.refetch()"
    />
    <div v-else-if="!preview.data.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <pre v-else class="text mono">{{ preview.data.value.text }}</pre>
    <template #footer>
      <Button label="Close" text @click="key = null" />
    </template>
  </Dialog>
</template>

<style scoped>
.note {
  margin-bottom: var(--pnmt-space-2);
}

.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.text {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 65vh;
  overflow: auto;
  font-size: 0.8rem;
  padding: var(--pnmt-space-2);
  border: 1px solid var(--pnmt-border);
  border-radius: var(--p-border-radius-md);
}
</style>
