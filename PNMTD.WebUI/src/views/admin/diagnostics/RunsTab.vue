<script setup lang="ts">
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useServerTable } from '@/composables/useServerTable'
import RunDrawer from './RunDrawer.vue'
import { outcomeLabel, outcomeSeverity } from './diagnosticsFormat'

/** The most recent runs (docs/windows-sensor-plan.md §10.3), filtered by outcome, or one server's. */
const props = defineProps<{ targetSensorId?: string }>()
const outcome = ref<S['DiagnosticOutcome'] | null>(null)
const filters = computed(() => ({ outcome: outcome.value ?? undefined, targetSensorId: props.targetSensorId }))
const table = useServerTable<S['DiagnosticRunItem']>({
  key: [...keys.diagnostics, 'runs', props.targetSensorId ?? 'all'],
  fetch: (q) => api.diagnostics.runs(q),
  pageSize: 50,
  filters,
  refetchInterval: 15_000,
})
const runId = ref<string | null>(null)
</script>

<template>
  <div class="tab">
    <div class="toolbar">
      <Select
        v-model="outcome"
        :options="['Pass', 'Fail', 'Error']"
        show-clear
        size="small"
        placeholder="Every outcome"
      />
    </div>
    <ListTable :table="table" what="runs" selectable empty-text="No run yet." @select="(r) => (runId = r.id)">
      <Column header="When" style="width: 9rem">
        <template #body="{ data }"><RelativeTime :value="data.startedAt" /></template>
      </Column>
      <Column header="Outcome" style="width: 7rem">
        <template #body="{ data }"
          ><Tag :value="outcomeLabel(data.outcome)" :severity="outcomeSeverity(data.outcome)"
        /></template>
      </Column>
      <Column header="Set">
        <template #body="{ data }">
          {{ data.setName }}
          <div v-if="!targetSensorId" class="muted small">{{ data.target.name }}</div>
        </template>
      </Column>
      <Column header="Result">
        <template #body="{ data }"
          ><span class="small">{{ data.summary }}</span></template
        >
      </Column>
      <Column header="Trigger" style="width: 9rem">
        <template #body="{ data }"
          ><span class="small">{{ data.trigger }} {{ data.requestedBy ?? '' }}</span></template
        >
      </Column>
      <template #mobile="{ item }">
        <div class="small">
          <Tag :value="outcomeLabel(item.outcome)" :severity="outcomeSeverity(item.outcome)" /> {{ item.setName }}
        </div>
        <div class="small muted">{{ item.summary }}</div>
      </template>
    </ListTable>
    <RunDrawer :run-id="runId" @close="runId = null" />
  </div>
</template>

<style scoped>
.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.small {
  font-size: 0.857rem;
}
</style>
