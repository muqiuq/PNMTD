<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import DiffView from '@/components/DiffView.vue'
import ErrorState from '@/components/ErrorState.vue'
import { prettyJson } from '@/components/jsonTree/jsonTree'
import { lineDiff } from '@/components/markdown/lineDiff'
import { formatAbsolute } from '@/format'

/**
 * *Compare with the previous* (docs/advanced-sensor-plan.md §10.3): a report and the one received before it, both
 * pretty-printed in the app's order, as a line diff.
 */
const reportId = defineModel<string | null>('reportId', { required: true })
const props = defineProps<{ sensorId: string }>()
const open = computed({
  get: () => reportId.value !== null,
  set: (v: boolean) => {
    if (!v) reportId.value = null
  },
})

const report = useQuery({
  queryKey: computed(() => [...keys.advanced(props.sensorId), 'report', reportId.value]),
  queryFn: () => api.advanced.report(props.sensorId, reportId.value!),
  enabled: computed(() => reportId.value !== null),
})
const previousId = computed(() => report.data.value?.previousId ?? null)
const previous = useQuery({
  queryKey: computed(() => [...keys.advanced(props.sensorId), 'report', previousId.value]),
  queryFn: () => api.advanced.report(props.sensorId, previousId.value!),
  enabled: computed(() => previousId.value !== null),
})

const loading = computed(() => !report.data.value || (previousId.value !== null && !previous.data.value))
const error = computed(() => report.error.value ?? previous.error.value ?? null)
const missing = computed(() => {
  const r = report.data.value
  if (!r) return null
  if (!r.previousId) return 'This is the oldest report kept: there is none before it.'
  const p = previous.data.value
  if (!p) return null
  if (r.body === null || p.body === null) return 'A report too large to keep has no text to compare.'
  return null
})
const diff = computed(() => {
  const r = report.data.value
  const p = previous.data.value
  if (!r || !p || r.body === null || p.body === null) return null
  return lineDiff(prettyJson(p.body), prettyJson(r.body))
})
</script>

<template>
  <Dialog
    v-model:visible="open"
    header="Compared with the previous report"
    modal
    :style="{ width: 'min(64rem, 96vw)' }"
  >
    <ErrorState v-if="error && loading" :error="error" what="the reports" @retry="report.refetch()" />
    <div v-else-if="loading && !missing" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <Message v-else-if="missing" severity="secondary" :closable="false">{{ missing }}</Message>
    <template v-else-if="diff">
      <p class="muted small line">
        <span class="removed">−</span> {{ formatAbsolute(previous.data.value!.report.receivedAt) }} ·
        <span class="added">+</span> {{ formatAbsolute(report.data.value!.report.receivedAt) }}
        <template v-if="diff.added || diff.removed">
          · {{ diff.removed }} lines removed, {{ diff.added }} added</template
        >
      </p>
      <DiffView :diff="diff" />
    </template>
  </Dialog>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pnmt-space-4);
}

.line {
  margin-top: 0;
}

.small {
  font-size: 0.857rem;
}

.added {
  color: var(--pnmt-ok);
  font-weight: 700;
}

.removed {
  color: var(--pnmt-fail);
  font-weight: 700;
}
</style>
