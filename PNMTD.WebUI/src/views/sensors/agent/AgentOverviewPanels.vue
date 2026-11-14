<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed, toRef } from 'vue'
import { api, keys, type S } from '@/api/resources'
import RelativeTime from '@/components/RelativeTime.vue'
import StatusBadge from '@/components/StatusBadge.vue'

/**
 * The overview's diagnostics and last changes (docs/windows-sensor-plan.md §10.2; docs/linux-sensor-plan.md §11.2), on either
 * platform: each assignment with its last run, and the last 5 changes.
 */
const props = defineProps<{ server: S['AgentServerDetail'] }>()
const id = toRef(() => props.server.server.sensorId)
const changes = useQuery({
  queryKey: computed(() => [...keys.agentServer(id.value), 'changes', 'overview']),
  queryFn: () => api.agents.changes(id.value, { pageSize: 5 }),
  refetchInterval: 60_000,
})
const diagnostics = useQuery({
  queryKey: computed(() => [...keys.diagnostics, 'assignments', id.value, 'overview']),
  queryFn: () => api.diagnostics.assignments({ targetSensorId: id.value, pageSize: 50 }),
  refetchInterval: 30_000,
})
</script>

<template>
  <section v-if="diagnostics.data.value?.items.length" class="panel">
    <div class="panel-header">Diagnostics</div>
    <ul class="panel-body plain">
      <li v-for="a in diagnostics.data.value.items" :key="a.id">
        <StatusBadge :status="a.state" icon-only />
        <RouterLink :to="{ name: 'sensor', params: { id: a.sensor.id } }">{{ a.set.name }}</RouterLink>
        <span class="muted small"> · {{ a.lastRun?.summary ?? 'no run yet' }}</span>
      </li>
    </ul>
  </section>
  <section class="panel">
    <div class="panel-header">Last changes</div>
    <ul class="panel-body plain">
      <li v-for="c in changes.data.value?.items ?? []" :key="c.id"><RelativeTime :value="c.at" /> · {{ c.summary }}</li>
      <li v-if="changes.isPending.value" class="muted">Loading…</li>
      <li v-else-if="!changes.data.value?.items.length" class="muted">None yet.</li>
    </ul>
  </section>
</template>

<style scoped>
.plain {
  list-style: none;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pnmt-space-1);
}

.small {
  font-size: 0.857rem;
}
</style>
