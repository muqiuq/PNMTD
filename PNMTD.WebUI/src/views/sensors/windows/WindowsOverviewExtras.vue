<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import Tag from 'primevue/tag'
import { computed, toRef } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import AgentOverviewPanels from '../agent/AgentOverviewPanels.vue'
import { useAgentSection } from '../agent/useAgentSection'

/** The overview's remote access, recent logins (User role), diagnostics and last changes (docs/windows-sensor-plan.md §10.2). */
const props = defineProps<{ server: S['AgentServerDetail']; canWrite: boolean }>()
const id = toRef(() => props.server.server.sensorId)
const remote = useAgentSection(id, 'remote-access')
const admins = useQuery({
  queryKey: computed(() => [...keys.agentServer(id.value), 'logins', 'overview-admins']),
  queryFn: () => api.agents.logins(id.value, { admin: true, outcome: 'Success', pageSize: 5 }),
  enabled: computed(() => props.canWrite),
  refetchInterval: 30_000,
})
const failed = useQuery({
  queryKey: computed(() => [...keys.agentServer(id.value), 'logins', 'overview-failed']),
  queryFn: () =>
    api.agents.logins(id.value, {
      outcome: 'Failure',
      from: new Date(Date.now() - 86_400_000).toISOString(),
      pageSize: 5,
    }),
  enabled: computed(() => props.canWrite),
  refetchInterval: 30_000,
})
const active = computed(() => (remote.data.value?.remoteAccess?.features ?? []).filter((f) => f.active))
const who = (l: S['AgentLoginItem']) => (l.domain ? `${l.domain}\\${l.user}` : l.user)
</script>

<template>
  <section class="panel">
    <div class="panel-header">Remote access</div>
    <div class="panel-body">
      <p>{{ remote.data.value?.remoteAccess?.summary ?? (remote.loading.value ? 'Loading…' : 'Not reported yet.') }}</p>
      <div class="tags">
        <Tag
          v-for="f in active"
          :key="f.key"
          :value="`${f.name}${f.firewallOpen ? ' · firewall open' : ''}`"
          :severity="f.firewallOpen ? 'warn' : 'secondary'"
        />
      </div>
    </div>
  </section>
  <section v-if="canWrite" class="panel">
    <div class="panel-header">Admin logins</div>
    <ul class="panel-body plain">
      <li v-for="l in admins.data.value?.items ?? []" :key="l.id">
        <RelativeTime :value="l.lastAt ?? l.at" /> · <strong>{{ who(l) }}</strong> · {{ l.kind }}
        <span v-if="l.sourceAddress" class="mono muted"> from {{ l.sourceAddress }}</span>
      </li>
      <li v-if="admins.isPending.value" class="muted">Loading…</li>
      <li v-else-if="!admins.data.value?.items.length" class="muted">None recorded.</li>
    </ul>
    <div class="panel-body small">
      Failed logins in the last 24 hours:
      <strong>{{ (failed.data.value?.items ?? []).reduce((sum, l) => sum + l.count, 0) }}</strong><template
        v-if="(failed.data.value?.totalCount ?? 0) > 5"
      >
        (and more)</template
      >
    </div>
  </section>
  <AgentOverviewPanels :server="server" />
</template>

<style scoped>
.plain {
  list-style: none;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.tags {
  display: flex;
  gap: var(--pnmt-space-1);
  flex-wrap: wrap;
}

.small {
  font-size: 0.857rem;
}
</style>
