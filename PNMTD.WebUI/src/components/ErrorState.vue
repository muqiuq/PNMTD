<script setup lang="ts">
import Button from 'primevue/button'
import { computed } from 'vue'
import { ApiError } from '@/api/client'

/** A failed load, with what failed and a retry, instead of a blank page (§5.7). */
const props = defineProps<{ error: unknown; what?: string }>()
const emit = defineEmits<{ retry: [] }>()

const message = computed(() => {
  if (props.error instanceof ApiError) {
    return props.error.status === 404 ? 'It does not exist (any more).' : props.error.message
  }
  return props.error instanceof Error ? props.error.message : 'Unknown error.'
})
</script>

<template>
  <div class="error-state" role="alert">
    <i class="pi pi-exclamation-circle" aria-hidden="true" />
    <div class="text">
      <div class="title">{{ what ? `Could not load ${what}` : 'Could not load this' }}</div>
      <div class="muted">{{ message }}</div>
    </div>
    <Button label="Retry" icon="pi pi-refresh" size="small" severity="secondary" outlined @click="emit('retry')" />
  </div>
</template>

<style scoped>
.error-state {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-3);
  padding: var(--pnmt-space-3);
  border: 1px solid var(--pnmt-fail-soft);
  border-radius: var(--p-border-radius-md);
  background: var(--pnmt-panel);
}

.error-state > i {
  color: var(--pnmt-fail);
  font-size: 1.25rem;
}

.text {
  flex: 1;
  min-width: 0;
}

.title {
  font-weight: 500;
}
</style>
