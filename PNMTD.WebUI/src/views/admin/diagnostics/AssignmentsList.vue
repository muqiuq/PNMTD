<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import InputNumber from 'primevue/inputnumber'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { api, keys, type S } from '@/api/resources'
import ListTable from '@/components/ListTable.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import { useActions } from '@/composables/useActions'
import { useServerTable } from '@/composables/useServerTable'
import AssignDialog from './AssignDialog.vue'
import RunDrawer from './RunDrawer.vue'
import { outcomeLabel, outcomeSeverity, schedule } from './diagnosticsFormat'

/**
 * Assignments (docs/windows-sensor-plan.md §10.3): set × server, the schedule, the state and last run, Run now (User), and
 * for an Admin the schedule, Assign… and delete (with the sensor and its history). One server's on its Diagnostics tab.
 */
const props = defineProps<{ targetSensorId?: string; canWrite: boolean; isAdmin: boolean }>()
const filters = computed(() => ({ targetSensorId: props.targetSensorId }))
const table = useServerTable<S['DiagnosticAssignmentItem']>({
  key: [...keys.diagnostics, 'assignments', props.targetSensorId ?? 'all'],
  fetch: (q) => api.diagnostics.assignments(q),
  pageSize: 50,
  filters,
  refetchInterval: 10_000,
})
const assigning = ref(false)
const runId = ref<string | null>(null)
const { run, ask, done } = useActions()
const invalidate = [keys.diagnostics, keys.windows, keys.sensors]
async function runNow(a: S['DiagnosticAssignmentItem']) {
  const started = await run(() => api.diagnostics.run(a.id), {
    success: 'Running: the result arrives in seconds',
    invalidate,
  })
  if (started) runId.value = started.id
}
async function save(a: S['DiagnosticAssignmentItem'], changes: Partial<S['DiagnosticAssignmentUpdate']>) {
  await run(
    () =>
      api.diagnostics.updateAssignment(a.id, {
        mode: a.mode,
        intervalMinutes: a.intervalMinutes,
        enabled: a.enabled,
        ...changes,
      }),
    {
      success: 'Saved',
      invalidate,
    },
  )
}
async function remove(a: S['DiagnosticAssignmentItem']) {
  if (
    !(await ask(
      'Delete the assignment?',
      `${a.set.name} on ${a.target.name}: its sensor "${a.sensor.name}" and that sensor's history go with it.`,
    ))
  )
    return
  await done(() => api.diagnostics.deleteAssignment(a.id), { success: 'Deleted', invalidate })
}
</script>

<template>
  <div class="tab">
    <div v-if="isAdmin" class="toolbar">
      <span class="spacer" />
      <Button label="Assign…" icon="pi pi-plus" size="small" @click="assigning = true" />
    </div>
    <ListTable :table="table" what="assignments" empty-text="No diagnostics assigned.">
      <Column header="Set">
        <template #body="{ data }">
          <span class="line">
            <StatusBadge :status="data.state" icon-only />
            <RouterLink :to="{ name: 'sensor', params: { id: data.sensor.id } }">{{ data.set.name }}</RouterLink>
          </span>
          <div v-if="!targetSensorId" class="muted small">
            <RouterLink :to="{ name: 'sensor', params: { id: data.target.id, tab: 'diagnostics' } }">{{
              data.target.name
            }}</RouterLink>
          </div>
        </template>
      </Column>
      <Column header="Schedule" style="width: 12rem">
        <template #body="{ data }">
          <template v-if="isAdmin && data.mode === 'Scheduled'">
            every
            <InputNumber
              :model-value="data.intervalMinutes"
              :min="1"
              :max="1440"
              size="small"
              input-class="num"
              @update:model-value="
                (v: number | null) => v && v !== data.intervalMinutes && save(data, { intervalMinutes: v })
              "
            />
            min
          </template>
          <template v-else>{{ schedule(data) }}</template>
        </template>
      </Column>
      <Column header="Last run">
        <template #body="{ data }">
          <template v-if="data.lastRun">
            <div class="run-line">
              <Tag :value="outcomeLabel(data.lastRun.outcome)" :severity="outcomeSeverity(data.lastRun.outcome)" />
              <a href="#" class="small" @click.prevent="runId = data.lastRun.id">
                <RelativeTime :value="data.lastRun.startedAt"
              /></a>
            </div>
            <div class="small ellipsis summary">{{ data.lastRun.summary }}</div>
          </template>
          <span v-else class="muted">none yet</span>
        </template>
      </Column>
      <Column style="width: 14rem">
        <template #body="{ data }">
          <Button
            v-if="canWrite"
            v-tooltip.top="data.agentConnected ? undefined : 'The agent is offline'"
            label="Run now"
            icon="pi pi-play"
            size="small"
            text
            :disabled="!data.agentConnected"
            @click="runNow(data)"
          />
          <template v-if="isAdmin">
            <Button
              :label="data.enabled ? 'Pause' : 'Resume'"
              size="small"
              text
              severity="secondary"
              @click="save(data, { enabled: !data.enabled })"
            />
            <Button icon="pi pi-trash" size="small" text severity="danger" aria-label="Delete" @click="remove(data)" />
          </template>
        </template>
      </Column>
      <template #mobile="{ item }">
        <div class="line">
          <StatusBadge :status="item.state" icon-only />
          {{ item.set.name }} · {{ item.target.name }}
        </div>
        <div class="small muted">{{ schedule(item) }} · {{ item.lastRun?.summary ?? 'none yet' }}</div>
      </template>
    </ListTable>
    <AssignDialog v-model:visible="assigning" :target-sensor-id="targetSensorId" />
    <RunDrawer :run-id="runId" @close="runId = null" />
  </div>
</template>

<style scoped>
.run-line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.summary {
  margin-top: var(--pnmt-space-1);
}

.tab {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.toolbar,
.line {
  display: flex;
  align-items: center;
  gap: var(--pnmt-space-2);
}

.spacer {
  flex: 1;
}

.line :deep(.num),
:deep(.num) {
  width: 4rem;
}

.small {
  font-size: 0.857rem;
}
</style>
