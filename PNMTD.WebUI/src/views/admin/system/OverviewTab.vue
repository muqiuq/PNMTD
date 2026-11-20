<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import { computed } from 'vue'
import { api, keys } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge, { type Status } from '@/components/StatusBadge.vue'
import { formatDuration } from '@/format'
import { useSession } from '@/stores/session'
import IntegrationsPanel from '../IntegrationsPanel.vue'

/**
 * System health's *Overview* (§5.6), new in 1.0: every background task's last run against its interval, the
 * uplink check, the UDP listener (docs/udp-iot-plan.md §6.7), the integrations (docs/audit-redmine-zabbix-plan.md §6.1),
 * and what this instance is. The whole page until it got tabs (docs/redmine-queue-outbound-plan.md §7.1); now its first, unchanged.
 */
const session = useSession()
const tasks = useQuery({ queryKey: [...keys.system, 'tasks'], queryFn: api.system.tasks, refetchInterval: 15_000 })
const uplink = useQuery({ queryKey: [...keys.system, 'uplink'], queryFn: api.system.uplink, refetchInterval: 30_000 })
const udp = useQuery({ queryKey: [...keys.udp, 'status'], queryFn: api.udp.status, refetchInterval: 15_000 })
const coverage = useQuery({ queryKey: [...keys.notifications, 'coverage'], queryFn: api.notifications.coverage })
const udpBadge = computed(() => {
  const s = udp.data.value
  if (!s) return null
  return !s.enabled
    ? { status: 'Disabled' as Status, label: 'Off' }
    : s.listening
      ? { status: 'Ok' as Status, label: 'Listening' }
      : { status: 'Degraded' as Status, label: 'Not listening' }
})
const udpDropped = computed(() => udp.data.value?.rejected.find((r) => r.reason === 'QueueFull')?.count ?? 0)

const taskState: Record<string, { status: Status; label: string }> = {
  ok: { status: 'Ok', label: 'Running' },
  late: { status: 'Degraded', label: 'Late' },
  notRun: { status: 'NoData', label: 'Not run yet' },
  off: { status: 'Disabled', label: 'Off' },
}
const info = computed(() => session.info)
</script>

<template>
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
        <div id="uplink-title" class="panel-header">
          Uplink
          <RouterLink :to="{ name: 'uplink' }" class="more">Uplink page ›</RouterLink>
        </div>
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
      <section class="panel" aria-labelledby="coverage-title">
        <div id="coverage-title" class="panel-header">
          Notifications
          <RouterLink :to="{ name: 'notifications', query: { tab: 'coverage' } }" class="more">Coverage ›</RouterLink>
          <RouterLink :to="{ name: 'notifications', query: { tab: 'pushover' } }" class="more">Pushover ›</RouterLink>
        </div>
        <dl v-if="coverage.data.value" class="panel-body facts">
          <dt>Covered</dt>
          <dd>{{ coverage.data.value.covered }} of {{ coverage.data.value.total }} sensors</dd>
          <dt>High, no rule</dt>
          <dd :class="{ error: coverage.data.value.uncoveredHigh > 0 }">
            {{ coverage.data.value.uncoveredHigh }}
          </dd>
          <dt>Rules reaching nobody</dt>
          <dd :class="{ error: coverage.data.value.rulesReachingNobody.length > 0 }">
            {{ coverage.data.value.rulesReachingNobody.length }}
          </dd>
        </dl>
      </section>
      <section class="panel" aria-labelledby="udp-title">
        <div id="udp-title" class="panel-header">
          UDP listener
          <RouterLink :to="{ name: 'udp-nodes' }" class="more">UDP nodes ›</RouterLink>
        </div>
        <dl v-if="udp.data.value && udpBadge" class="panel-body facts">
          <dt>State</dt>
          <dd><StatusBadge v-bind="udpBadge" /></dd>
          <dt>Endpoint</dt>
          <dd class="mono">{{ udp.data.value.endpoint }}/udp</dd>
          <template v-if="udp.data.value.error">
            <dt>Error</dt>
            <dd class="error">{{ udp.data.value.error }}</dd>
          </template>
          <dt>Queue</dt>
          <dd>{{ udp.data.value.queueDepth.toLocaleString() }} waiting · {{ udpDropped.toLocaleString() }} dropped</dd>
          <dt>Traffic</dt>
          <dd>
            {{ udp.data.value.packetsLastMinute.toLocaleString() }} packets in the last minute ·
            {{ udp.data.value.nodes.toLocaleString() }} nodes
          </dd>
          <template v-if="udp.data.value.warnings.length">
            <dt>Warnings</dt>
            <dd class="error">
              {{ udp.data.value.warnings.length }} node{{ udp.data.value.warnings.length === 1 ? '' : 's' }} with many
              rejected packets
            </dd>
          </template>
        </dl>
        <div v-else-if="udp.isError.value" class="panel-body muted">Could not load the listener's state.</div>
      </section>
      <IntegrationsPanel />
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
</template>

<style scoped>
.column {
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-3);
}

.more {
  margin-left: auto;
  font-weight: 400;
}

.error {
  color: var(--pnmt-fail);
}
</style>
