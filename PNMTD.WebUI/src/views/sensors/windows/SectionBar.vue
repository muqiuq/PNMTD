<script setup lang="ts">
import Button from 'primevue/button'
import Message from 'primevue/message'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'

/** When a section was collected, its collector's errors, and Collect now (docs/windows-sensor-plan.md §10.2). */
const props = defineProps<{
  sensorId: string
  section: string
  view: S['WindowsSectionView'] | null
  canWrite: boolean
  connected: boolean
  loading?: boolean
  error?: string | null
}>()
const { run, busy } = useActions()
const collect = () =>
  run(
    async () => {
      const r = await api.windows.collect(props.sensorId, [props.section])
      if (!r.ok) throw new Error(r.error ?? 'The agent refused.')
      return r
    },
    { success: 'Collected', invalidate: [keys.windowsServer(props.sensorId)] },
  )
</script>

<template>
  <div class="bar">
    <span v-if="view" class="muted small"
      >Collected <RelativeTime :value="view.collectedAt" />, changed <RelativeTime :value="view.changedAt"
    /></span>
    <span v-else-if="loading" class="muted small">Loading…</span>
    <span v-else-if="error" class="small fail">PNMT could not read this section: {{ error }}</span>
    <span v-else class="muted small">Not reported yet.</span>
    <span class="spacer" />
    <Button
      v-if="canWrite"
      label="Collect now"
      icon="pi pi-refresh"
      size="small"
      severity="secondary"
      :disabled="!connected"
      :loading="busy"
      @click="collect"
    />
  </div>
  <Message v-if="view?.errors.length" severity="warn" :closable="false" size="small">
    <div v-for="(e, i) in view.errors" :key="i">{{ e }}</div>
  </Message>
</template>

<style scoped>
.bar {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.spacer {
  flex: 1;
}

.fail {
  color: var(--pnmt-fail);
}

.small {
  font-size: 0.857rem;
}
</style>
