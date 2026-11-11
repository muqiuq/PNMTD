<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge, { type Status } from '@/components/StatusBadge.vue'
import { formatDuration } from '@/format'
import { useSession } from '@/stores/session'

/**
 * System health (§5.6), new in 1.0: every background task's last run against its interval, the
 * uplink check, and what this instance is.
 */
const session = useSession()
const tasks = useQuery({ queryKey: [...keys.system, 'tasks'], queryFn: api.system.tasks, refetchInterval: 15_000 })
const uplink = useQuery({ queryKey: [...keys.system, 'uplink'], queryFn: api.system.uplink, refetchInterval: 30_000 })

const taskState: Record<string, { status: Status; label: string }> = {
  ok: { status: 'Ok', label: 'Running' },
  late: { status: 'Degraded', label: 'Late' },
  notRun: { status: 'NoData', label: 'Not run yet' },
  off: { status: 'Disabled', label: 'Off' },
}
const info = computed(() => session.info)
</script>

<template>
  <div class="page">
    <PageHeader title="System health" />
    <div class="two-columns">
      <section class="panel" aria-labelledby="tasks-title">
        <div id="tasks-title" class="panel-header">Background tasks</div>
        <DataTable :value="tasks.data.value ?? []" size="small" data-key="key" :loading="tasks.isLoading.value">
          <Column header="" style="width: 7.5rem">
            <template #body="{ data }"><StatusBadge v-bind="taskState[data.state] ?? taskState.notRun" /></template>
          </Column>
          <Column field="name" header="Task" />
          <Column header="Every" style="width: 6rem">
            <template #body="{ data }">{{ formatDuration(data.intervalSeconds * 1000) }}</template>
          </Column>
          <Column header="Last run" style="width: 8rem">
            <template #body="{ data }"><RelativeTime :value="data.lastRun" /></template>
          </Column>
        </DataTable>
      </section>
      <div class="column">
        <section class="panel" aria-labelledby="uplink-title">
          <div id="uplink-title" class="panel-header">Uplink</div>
          <dl v-if="uplink.data.value" class="panel-body facts">
            <dt>State</dt>
            <dd>
              <span v-if="!uplink.data.value.configured" class="muted">Not configured (Uplink settings)</span>
              <StatusBadge
                v-else
                :status="uplink.data.value.isUp ? 'Ok' : 'Degraded'"
                :label="uplink.data.value.isUp ? 'Up' : 'Down'"
              />
            </dd>
            <dt>Last check</dt>
            <dd><RelativeTime :value="uplink.data.value.lastRun" /></dd>
            <dt>Last success</dt>
            <dd><RelativeTime :value="uplink.data.value.lastSuccessfulRun" /></dd>
          </dl>
        </section>
        <section class="panel" aria-labelledby="instance-title">
          <div id="instance-title" class="panel-header">Instance</div>
          <dl v-if="info" class="panel-body facts">
            <dt>Identity</dt>
            <dd>{{ info.identity }}</dd>
            <dt>Version</dt>
            <dd class="mono">{{ info.version }}</dd>
            <dt>Public URL</dt>
            <dd class="mono">{{ info.publicBaseUrl || "(not set: URLs use the browser's address)" }}</dd>
            <dt>Time zone</dt>
            <dd>{{ info.timeZone || 'UTC' }}</dd>
            <dt>2FA policy</dt>
            <dd>{{ info.totpPolicy }}</dd>
          </dl>
        </section>
      </div>
    </div>
  </div>
</template>

<style scoped>
.column {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}
</style>
